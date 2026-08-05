using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>Owns immutable sampled RGBA8 textures and model descriptor sets.</summary>
internal unsafe sealed class PersistentTextureStore
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly Func<uint, MemoryPropertyFlags, uint> _findMemoryType;
    private readonly DescriptorSetLayout _descriptorLayout;
    private readonly DescriptorPool _descriptorPool;
    private readonly Dictionary<TextureHandle, Resource> _resources = [];
    private readonly List<Resource> _pendingUploads = [];

    /// <summary>Creates an empty texture store.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Logical device.</param>
    /// <param name="findMemoryType">Memory-type resolver.</param>
    /// <param name="descriptorLayout">Model texture descriptor layout.</param>
    /// <param name="descriptorPool">Model texture descriptor pool.</param>
    internal PersistentTextureStore(
        Vk vk,
        Device device,
        Func<uint, MemoryPropertyFlags, uint> findMemoryType,
        DescriptorSetLayout descriptorLayout,
        DescriptorPool descriptorPool)
    {
        _vk = vk;
        _device = device;
        _findMemoryType = findMemoryType;
        _descriptorLayout = descriptorLayout;
        _descriptorPool = descriptorPool;
    }

    /// <summary>Creates one sampled texture and queues its pixel upload.</summary>
    /// <param name="handle">Renderer-owned handle.</param>
    /// <param name="texture">Decoded RGBA8 pixels.</param>
    internal void Add(TextureHandle handle, TextureResource texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (texture.Width == 0 || texture.Height == 0 ||
            texture.Pixels.Length != checked((long)texture.Width * texture.Height * 4))
        {
            throw new ArgumentException("Texture must contain tightly packed RGBA8 pixels.",
                nameof(texture));
        }
        var format = texture.ColorSpace == TextureColorSpace.Srgb
            ? Format.R8G8B8A8Srgb : Format.R8G8B8A8Unorm;
        var resource = CreateResource(texture.Width, texture.Height, format,
            texture.Pixels.ToArray());
        _resources.Add(handle, resource);
        _pendingUploads.Add(resource);
    }

    /// <summary>Gets a texture descriptor.</summary>
    /// <param name="handle">Renderer-owned texture handle.</param>
    /// <returns>The combined image-sampler descriptor.</returns>
    internal DescriptorSet GetDescriptor(TextureHandle handle)
    {
        return _resources.TryGetValue(handle, out var resource)
            ? resource.DescriptorSet
            : throw new ArgumentOutOfRangeException(nameof(handle), handle,
                "Texture resource was not found.");
    }

    /// <summary>Removes a texture for deferred destruction.</summary>
    /// <param name="handle">Renderer-owned texture handle.</param>
    /// <returns>The removed texture resource.</returns>
    internal Resource Remove(TextureHandle handle)
    {
        if (!_resources.Remove(handle, out var resource))
            throw new ArgumentOutOfRangeException(nameof(handle), handle,
                "Texture resource was not found.");
        _pendingUploads.Remove(resource);
        return resource;
    }

    /// <summary>Records all pending image uploads and layout transitions.</summary>
    /// <param name="commandBuffer">Command buffer receiving transfer work.</param>
    /// <param name="arena">Active frame staging arena.</param>
    /// <param name="frameIndex">Active frame slot.</param>
    internal void RecordPendingUploads(
        CommandBuffer commandBuffer,
        FrameTransientArena arena,
        uint frameIndex)
    {
        foreach (var resource in _pendingUploads)
        {
            var staging = arena.Allocate(frameIndex, checked((uint)resource.Pixels.Length));
            fixed (byte* source = resource.Pixels)
                System.Buffer.MemoryCopy(source, staging.MappedPointer,
                    resource.Pixels.Length, resource.Pixels.Length);
            var range = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1
            };
            var toTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                DstAccessMask = AccessFlags.TransferWriteBit,
                Image = resource.Image,
                SubresourceRange = range
            };
            _vk.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toTransfer);
            var copy = new BufferImageCopy
            {
                BufferOffset = staging.ByteOffset,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LayerCount = 1
                },
                ImageExtent = new Extent3D
                {
                    Width = resource.Width,
                    Height = resource.Height,
                    Depth = 1
                }
            };
            _vk.CmdCopyBufferToImage(commandBuffer, staging.Buffer, resource.Image,
                ImageLayout.TransferDstOptimal, 1, &copy);
            var toShader = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                Image = resource.Image,
                SubresourceRange = range
            };
            _vk.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit,
                PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, &toShader);
        }
        _pendingUploads.Clear();
    }

    /// <summary>Destroys one retired texture.</summary>
    /// <param name="resource">Texture no longer used by in-flight work.</param>
    internal void Release(Resource resource) => resource.Destroy(_vk, _device, _descriptorPool);

    /// <summary>Destroys all owned textures.</summary>
    internal void Destroy()
    {
        foreach (var resource in _resources.Values)
            resource.Destroy(_vk, _device, _descriptorPool);
        _resources.Clear();
        _pendingUploads.Clear();
    }

    /// <summary>Creates the Vulkan image, view, sampler, and descriptor.</summary>
    /// <param name="width">Pixel width.</param>
    /// <param name="height">Pixel height.</param>
    /// <param name="format">Vulkan sample format.</param>
    /// <param name="pixels">Owned RGBA8 upload bytes.</param>
    /// <returns>The created texture resource.</returns>
    private Resource CreateResource(uint width, uint height, Format format, byte[] pixels)
    {
        var resource = new Resource(width, height, pixels);
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D { Width = width, Height = height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive
        };
        Check(_vk.CreateImage(_device, &imageInfo, null, out resource.Image), "create model texture");
        _vk.GetImageMemoryRequirements(_device, resource.Image, out var requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _findMemoryType(requirements.MemoryTypeBits,
                MemoryPropertyFlags.DeviceLocalBit)
        };
        Check(_vk.AllocateMemory(_device, &allocation, null, out resource.Memory),
            "allocate model texture memory");
        Check(_vk.BindImageMemory(_device, resource.Image, resource.Memory, 0),
            "bind model texture memory");
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = resource.Image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1
            }
        };
        Check(_vk.CreateImageView(_device, &viewInfo, null, out resource.View),
            "create model texture view");
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            MaxLod = 0f
        };
        Check(_vk.CreateSampler(_device, &samplerInfo, null, out resource.Sampler),
            "create model texture sampler");
        var layout = _descriptorLayout;
        var setInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };
        Check(_vk.AllocateDescriptorSets(_device, &setInfo, out resource.DescriptorSet),
            "allocate model texture descriptor");
        var imageDescriptor = new DescriptorImageInfo
        {
            Sampler = resource.Sampler,
            ImageView = resource.View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = resource.DescriptorSet,
            DstBinding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageDescriptor
        };
        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
        return resource;
    }

    /// <summary>Throws when Vulkan reports a failure.</summary>
    /// <param name="result">Vulkan result.</param>
    /// <param name="operation">Operation description.</param>
    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}");
    }

    /// <summary>Owns one Vulkan sampled texture.</summary>
    internal sealed class Resource
    {
        internal uint Width { get; }
        internal uint Height { get; }
        internal byte[] Pixels { get; }
        internal Image Image;
        internal DeviceMemory Memory;
        internal ImageView View;
        internal Sampler Sampler;
        internal DescriptorSet DescriptorSet;

        /// <summary>Creates one pending texture resource.</summary>
        /// <param name="width">Pixel width.</param>
        /// <param name="height">Pixel height.</param>
        /// <param name="pixels">Owned upload pixels.</param>
        internal Resource(uint width, uint height, byte[] pixels)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        /// <summary>Destroys Vulkan handles.</summary>
        /// <param name="vk">Vulkan API.</param>
        /// <param name="device">Owning device.</param>
        internal void Destroy(Vk vk, Device device, DescriptorPool descriptorPool)
        {
            if (DescriptorSet.Handle != 0)
            {
                var descriptorSet = DescriptorSet;
                vk.FreeDescriptorSets(device, descriptorPool, 1, &descriptorSet);
            }
            if (Sampler.Handle != 0) vk.DestroySampler(device, Sampler, null);
            if (View.Handle != 0) vk.DestroyImageView(device, View, null);
            if (Image.Handle != 0) vk.DestroyImage(device, Image, null);
            if (Memory.Handle != 0) vk.FreeMemory(device, Memory, null);
        }
    }
}
