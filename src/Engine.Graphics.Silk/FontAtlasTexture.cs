using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>Owns the Vulkan image and descriptor used by the cached glyph atlas.</summary>
internal unsafe sealed class FontAtlasTexture
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly TrueTypeFontRasterizer _rasterizer;
    private Image _image;
    private DeviceMemory _memory;
    private ImageView _view;
    private Sampler _sampler;
    private bool _initializedLayout;

    /// <summary>Gets the atlas texture descriptor.</summary>
    internal DescriptorSet DescriptorSet { get; private set; }

    /// <summary>Creates a device-local atlas image and its sampling descriptor.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Logical device.</param>
    /// <param name="rasterizer">CPU glyph atlas.</param>
    /// <param name="findMemoryType">Memory-type resolver.</param>
    /// <param name="descriptorSetLayout">Texture descriptor layout.</param>
    /// <param name="descriptorPool">Texture descriptor pool.</param>
    internal FontAtlasTexture(
        Vk vk,
        Device device,
        TrueTypeFontRasterizer rasterizer,
        Func<uint, MemoryPropertyFlags, uint> findMemoryType,
        DescriptorSetLayout descriptorSetLayout,
        DescriptorPool descriptorPool)
    {
        _vk = vk;
        _device = device;
        _rasterizer = rasterizer;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            Extent = new Extent3D
            {
                Width = TrueTypeFontRasterizer.AtlasWidth,
                Height = TrueTypeFontRasterizer.AtlasHeight,
                Depth = 1
            },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        Check(_vk.CreateImage(_device, &imageInfo, null, out _image), "create font atlas image");
        _vk.GetImageMemoryRequirements(_device, _image, out var requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = findMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit)
        };
        Check(_vk.AllocateMemory(_device, &allocation, null, out _memory), "allocate font atlas memory");
        Check(_vk.BindImageMemory(_device, _image, _memory, 0), "bind font atlas memory");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _image,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1
            }
        };
        Check(_vk.CreateImageView(_device, &viewInfo, null, out _view), "create font atlas view");
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MipmapMode = SamplerMipmapMode.Nearest,
            MaxLod = 0f
        };
        Check(_vk.CreateSampler(_device, &samplerInfo, null, out _sampler), "create font atlas sampler");

        var layout = descriptorSetLayout;
        var setInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };
        Check(_vk.AllocateDescriptorSets(_device, &setInfo, out var descriptorSet),
            "allocate font atlas descriptor");
        DescriptorSet = descriptorSet;
        var imageDescriptor = new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = _view,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = DescriptorSet,
            DstBinding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageDescriptor
        };
        _vk.UpdateDescriptorSets(_device, 1, &write, 0, null);
    }

    /// <summary>Uploads the atlas when newly rasterized glyphs changed its generation.</summary>
    /// <param name="commandBuffer">Command buffer receiving transfer commands.</param>
    /// <param name="transientArena">Mapped transient frame arena.</param>
    /// <param name="frameIndex">Active frame slot.</param>
    internal void RecordPendingUpload(
        CommandBuffer commandBuffer,
        FrameTransientArena transientArena,
        uint frameIndex)
    {
        if (!_rasterizer.TryTakeAtlasUpdate(out var update))
            return;
        var pixels = update.Pixels;
        var staging = transientArena.Allocate(frameIndex, checked((uint)pixels.Length));
        fixed (byte* source = pixels)
            System.Buffer.MemoryCopy(source, staging.MappedPointer, pixels.Length, pixels.Length);

        var toTransfer = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = _initializedLayout ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcAccessMask = _initializedLayout ? AccessFlags.ShaderReadBit : 0,
            DstAccessMask = AccessFlags.TransferWriteBit,
            Image = _image,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1
            }
        };
        _vk.CmdPipelineBarrier(commandBuffer,
            _initializedLayout ? PipelineStageFlags.FragmentShaderBit : PipelineStageFlags.TopOfPipeBit,
            PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &toTransfer);
        var copy = new BufferImageCopy
        {
            BufferOffset = staging.ByteOffset,
            ImageOffset = new Offset3D { X = update.X, Y = update.Y, Z = 0 },
            ImageSubresource = new ImageSubresourceLayers
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LayerCount = 1
            },
            ImageExtent = new Extent3D
            {
                Width = (uint)update.Width,
                Height = (uint)update.Height,
                Depth = 1
            }
        };
        _vk.CmdCopyBufferToImage(commandBuffer, staging.Buffer, _image,
            ImageLayout.TransferDstOptimal, 1, &copy);
        var toShader = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
            Image = _image,
            SubresourceRange = toTransfer.SubresourceRange
        };
        _vk.CmdPipelineBarrier(commandBuffer, PipelineStageFlags.TransferBit,
            PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, &toShader);
        _initializedLayout = true;
    }

    /// <summary>Destroys the atlas resources.</summary>
    internal void Destroy()
    {
        if (_sampler.Handle != 0)
            _vk.DestroySampler(_device, _sampler, null);
        if (_view.Handle != 0)
            _vk.DestroyImageView(_device, _view, null);
        if (_image.Handle != 0)
            _vk.DestroyImage(_device, _image, null);
        if (_memory.Handle != 0)
            _vk.FreeMemory(_device, _memory, null);
    }

    /// <summary>Throws for a failed Vulkan operation.</summary>
    /// <param name="result">Vulkan result.</param>
    /// <param name="operation">Operation description.</param>
    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}");
    }
}
