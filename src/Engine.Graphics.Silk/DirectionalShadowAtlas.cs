using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>GPU buffer layout shared by forward shaders for cascaded directional shadows.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuDirectionalShadowData
{
    public Matrix4x4 Cascade0;
    public Matrix4x4 Cascade1;
    public Matrix4x4 Cascade2;
    public Matrix4x4 Cascade3;
    public Vector4 SplitDistances;
    public Vector4 CameraForwardCascadeCount;
    public Vector4 ShadowParameters;
    public Vector4 WorldTexelSizes;

    /// <summary>Creates the GPU contract from renderer-independent cascade output.</summary>
    /// <param name="cascades">Computed cascade transforms and metrics.</param>
    /// <param name="settings">Active SRP shadow settings.</param>
    /// <returns>Fully populated shader data.</returns>
    public static GpuDirectionalShadowData Create(
        DirectionalShadowCascades cascades,
        DirectionalShadowSettings settings) => new()
        {
            Cascade0 = cascades.Cascade0,
            Cascade1 = cascades.Cascade1,
            Cascade2 = cascades.Cascade2,
            Cascade3 = cascades.Cascade3,
            SplitDistances = cascades.SplitDistances,
            CameraForwardCascadeCount = new Vector4(cascades.CameraForward, cascades.Count),
            ShadowParameters = new Vector4(settings.Strength, settings.CascadeBlend,
                settings.NormalBias, 1f / DirectionalShadowAtlas.CascadeResolution),
            WorldTexelSizes = cascades.WorldTexelSizes
        };
}

/// <summary>Owns one four-tile depth atlas and per-frame cascade parameter buffers.</summary>
internal unsafe sealed class DirectionalShadowAtlas
{
    public const int MaximumCascades = 4;
    public const uint CascadeResolution = 2048;
    public const uint AtlasResolution = CascadeResolution * 2;
    private const int FrameCount = 2;
    private readonly Silk.NET.Vulkan.Buffer[] _dataBuffers =
        new Silk.NET.Vulkan.Buffer[FrameCount];
    private readonly DeviceMemory[] _dataMemories = new DeviceMemory[FrameCount];
    private readonly nint[] _mappedData = new nint[FrameCount];
    private readonly DescriptorSet[] _descriptorSets = new DescriptorSet[FrameCount];

    public Image Image;
    public DeviceMemory Memory;
    public ImageView View;
    public Framebuffer Framebuffer;
    public Sampler Sampler;

    /// <summary>Gets whether the atlas has reached its shader-readable layout.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>Creates atlas, comparison sampler, frame buffers, and descriptors.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Owning logical device.</param>
    /// <param name="renderPass">Compatible depth-only render pass.</param>
    /// <param name="depthFormat">Selected depth format.</param>
    /// <param name="deviceLocalMemoryType">Compatible device-local memory type.</param>
    /// <param name="descriptorSetLayout">Shadow sampling descriptor layout.</param>
    /// <param name="descriptorPool">Pool allocating per-frame shadow sets.</param>
    /// <param name="findMemoryType">Device memory-type resolver.</param>
    public void Create(
        Vk vk,
        Device device,
        RenderPass renderPass,
        Format depthFormat,
        uint deviceLocalMemoryType,
        DescriptorSetLayout descriptorSetLayout,
        DescriptorPool descriptorPool,
        Func<uint, MemoryPropertyFlags, uint> findMemoryType)
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = depthFormat,
            Extent = new Extent3D
            {
                Width = AtlasResolution,
                Height = AtlasResolution,
                Depth = 1
            },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        Check(vk.CreateImage(device, &imageInfo, null, out Image), "create shadow atlas image");
        vk.GetImageMemoryRequirements(device, Image, out var imageRequirements);
        var imageAllocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = imageRequirements.Size,
            MemoryTypeIndex = deviceLocalMemoryType
        };
        Check(vk.AllocateMemory(device, &imageAllocation, null, out Memory),
            "allocate shadow atlas memory");
        Check(vk.BindImageMemory(device, Image, Memory, 0), "bind shadow atlas memory");
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = ImageViewType.Type2D,
            Format = depthFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0,
                LevelCount = 1,
                BaseArrayLayer = 0,
                LayerCount = 1
            }
        };
        Check(vk.CreateImageView(device, &viewInfo, null, out View),
            "create shadow atlas view");
        var attachment = View;
        var framebufferInfo = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = renderPass,
            AttachmentCount = 1,
            PAttachments = &attachment,
            Width = AtlasResolution,
            Height = AtlasResolution,
            Layers = 1
        };
        Check(vk.CreateFramebuffer(device, &framebufferInfo, null, out Framebuffer),
            "create shadow atlas framebuffer");
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            AddressModeU = SamplerAddressMode.ClampToBorder,
            AddressModeV = SamplerAddressMode.ClampToBorder,
            AddressModeW = SamplerAddressMode.ClampToBorder,
            BorderColor = BorderColor.FloatOpaqueWhite,
            MipmapMode = SamplerMipmapMode.Nearest,
            CompareEnable = new Bool32(true),
            CompareOp = CompareOp.LessOrEqual,
            MinLod = 0f,
            MaxLod = 0f
        };
        Check(vk.CreateSampler(device, &samplerInfo, null, out Sampler),
            "create shadow comparison sampler");
        for (var frameIndex = 0; frameIndex < FrameCount; frameIndex++)
            CreateFrameData(vk, device, descriptorSetLayout, descriptorPool,
                findMemoryType, frameIndex);
    }

    /// <summary>Uploads cascade parameters into the current frame's persistently mapped buffer.</summary>
    /// <param name="frameIndex">Frame-in-flight index.</param>
    /// <param name="data">Cascade data to upload.</param>
    public void Update(uint frameIndex, GpuDirectionalShadowData data)
    {
        if (frameIndex >= FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        *(GpuDirectionalShadowData*)_mappedData[frameIndex] = data;
    }

    /// <summary>Gets the shadow sampling set for one frame in flight.</summary>
    /// <param name="frameIndex">Frame-in-flight index.</param>
    /// <returns>Descriptor containing atlas, sampler, and immutable frame data.</returns>
    public DescriptorSet GetDescriptorSet(uint frameIndex)
    {
        if (frameIndex >= FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        return _descriptorSets[frameIndex];
    }

    /// <summary>Records that a completed render pass left the atlas shader-readable.</summary>
    public void MarkInitialized()
    {
        IsInitialized = true;
    }

    /// <summary>Releases every atlas resource and descriptor.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Owning logical device.</param>
    /// <param name="descriptorPool">Pool that allocated shadow sets.</param>
    public void Destroy(Vk vk, Device device, DescriptorPool descriptorPool)
    {
        for (var frameIndex = 0; frameIndex < FrameCount; frameIndex++)
        {
            if (_descriptorSets[frameIndex].Handle != 0)
            {
                var descriptor = _descriptorSets[frameIndex];
                vk.FreeDescriptorSets(device, descriptorPool, 1, &descriptor);
                _descriptorSets[frameIndex] = default;
            }
            if (_mappedData[frameIndex] != 0)
            {
                vk.UnmapMemory(device, _dataMemories[frameIndex]);
                _mappedData[frameIndex] = 0;
            }
            if (_dataBuffers[frameIndex].Handle != 0)
                vk.DestroyBuffer(device, _dataBuffers[frameIndex], null);
            if (_dataMemories[frameIndex].Handle != 0)
                vk.FreeMemory(device, _dataMemories[frameIndex], null);
            _dataBuffers[frameIndex] = default;
            _dataMemories[frameIndex] = default;
        }
        if (Framebuffer.Handle != 0)
            vk.DestroyFramebuffer(device, Framebuffer, null);
        if (Sampler.Handle != 0)
            vk.DestroySampler(device, Sampler, null);
        if (View.Handle != 0)
            vk.DestroyImageView(device, View, null);
        if (Image.Handle != 0)
            vk.DestroyImage(device, Image, null);
        if (Memory.Handle != 0)
            vk.FreeMemory(device, Memory, null);
        Framebuffer = default;
        Sampler = default;
        View = default;
        Image = default;
        Memory = default;
        IsInitialized = false;
    }

    /// <summary>Creates one persistently mapped uniform buffer and its shadow descriptor set.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Owning device.</param>
    /// <param name="descriptorSetLayout">Shadow descriptor layout.</param>
    /// <param name="descriptorPool">Shadow descriptor pool.</param>
    /// <param name="findMemoryType">Memory-type resolver.</param>
    /// <param name="frameIndex">Frame slot to initialize.</param>
    private void CreateFrameData(
        Vk vk,
        Device device,
        DescriptorSetLayout descriptorSetLayout,
        DescriptorPool descriptorPool,
        Func<uint, MemoryPropertyFlags, uint> findMemoryType,
        int frameIndex)
    {
        var bufferSize = (ulong)sizeof(GpuDirectionalShadowData);
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = bufferSize,
            Usage = BufferUsageFlags.UniformBufferBit,
            SharingMode = SharingMode.Exclusive
        };
        Check(vk.CreateBuffer(device, &bufferInfo, null, out _dataBuffers[frameIndex]),
            "create shadow data buffer");
        vk.GetBufferMemoryRequirements(device, _dataBuffers[frameIndex], out var requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = findMemoryType(requirements.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };
        Check(vk.AllocateMemory(device, &allocation, null, out _dataMemories[frameIndex]),
            "allocate shadow data memory");
        Check(vk.BindBufferMemory(device, _dataBuffers[frameIndex],
            _dataMemories[frameIndex], 0), "bind shadow data memory");
        void* mapped;
        Check(vk.MapMemory(device, _dataMemories[frameIndex], 0, bufferSize, 0, &mapped),
            "map shadow data memory");
        _mappedData[frameIndex] = (nint)mapped;

        var layout = descriptorSetLayout;
        var allocationInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout
        };
        Check(vk.AllocateDescriptorSets(device, &allocationInfo,
            out _descriptorSets[frameIndex]), "allocate shadow descriptor");
        var imageInfo = new DescriptorImageInfo
        {
            ImageView = View,
            ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal
        };
        var samplerInfo = new DescriptorImageInfo { Sampler = Sampler };
        var bufferDescriptor = new DescriptorBufferInfo
        {
            Buffer = _dataBuffers[frameIndex],
            Offset = 0,
            Range = bufferSize
        };
        var writes = stackalloc WriteDescriptorSet[3];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSets[frameIndex],
            DstBinding = 0,
            DescriptorType = DescriptorType.SampledImage,
            DescriptorCount = 1,
            PImageInfo = &imageInfo
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSets[frameIndex],
            DstBinding = 1,
            DescriptorType = DescriptorType.Sampler,
            DescriptorCount = 1,
            PImageInfo = &samplerInfo
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSets[frameIndex],
            DstBinding = 2,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            PBufferInfo = &bufferDescriptor
        };
        vk.UpdateDescriptorSets(device, 3, writes, 0, null);
    }

    /// <summary>Throws when a Vulkan operation fails.</summary>
    /// <param name="result">Vulkan result.</param>
    /// <param name="operation">Operation description.</param>
    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}");
    }
}
