using System.Numerics;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>Owns a row-per-light atlas for point and spot shadows.</summary>
internal unsafe sealed class LocalShadowAtlas
{
    public const uint FaceResolution = 512;
    public const uint FaceColumnCount = 6;
    public const uint AtlasWidth = FaceResolution * FaceColumnCount;
    public const uint AtlasHeight = FaceResolution * SceneLightSet.MaximumShadowedLocalLights;
    private const int FrameCount = 2;
    private const int MatrixCount = SceneLightSet.MaximumShadowedLocalLights * 6;
    private const int BufferSize = MatrixCount * sizeof(float) * 16 + sizeof(float) * 4;
    private readonly Silk.NET.Vulkan.Buffer[] _dataBuffers =
        new Silk.NET.Vulkan.Buffer[FrameCount];
    private readonly DeviceMemory[] _dataMemories = new DeviceMemory[FrameCount];
    private readonly nint[] _mappedData = new nint[FrameCount];

    public Image Image;
    public DeviceMemory Memory;
    public ImageView View;
    public Framebuffer Framebuffer;
    public Sampler Sampler;

    /// <summary>Gets whether the atlas has reached its shader-readable layout.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>Creates the atlas and extends each shadow descriptor with local-shadow bindings.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Owning logical device.</param>
    /// <param name="renderPass">Compatible depth-only render pass.</param>
    /// <param name="depthFormat">Selected depth format.</param>
    /// <param name="deviceLocalMemoryType">Compatible device-local memory type.</param>
    /// <param name="frame0Descriptor">First directional-shadow descriptor to extend.</param>
    /// <param name="frame1Descriptor">Second directional-shadow descriptor to extend.</param>
    /// <param name="findMemoryType">Memory-type resolver.</param>
    public void Create(
        Vk vk,
        Device device,
        RenderPass renderPass,
        Format depthFormat,
        uint deviceLocalMemoryType,
        DescriptorSet frame0Descriptor,
        DescriptorSet frame1Descriptor,
        Func<uint, MemoryPropertyFlags, uint> findMemoryType)
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = depthFormat,
            Extent = new Extent3D { Width = AtlasWidth, Height = AtlasHeight, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        Check(vk.CreateImage(device, &imageInfo, null, out Image),
            "create local-shadow atlas image");
        vk.GetImageMemoryRequirements(device, Image, out var imageRequirements);
        var imageAllocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = imageRequirements.Size,
            MemoryTypeIndex = deviceLocalMemoryType
        };
        Check(vk.AllocateMemory(device, &imageAllocation, null, out Memory),
            "allocate local-shadow atlas memory");
        Check(vk.BindImageMemory(device, Image, Memory, 0),
            "bind local-shadow atlas memory");
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
            "create local-shadow atlas view");
        var attachment = View;
        var framebufferInfo = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = renderPass,
            AttachmentCount = 1,
            PAttachments = &attachment,
            Width = AtlasWidth,
            Height = AtlasHeight,
            Layers = 1
        };
        Check(vk.CreateFramebuffer(device, &framebufferInfo, null, out Framebuffer),
            "create local-shadow framebuffer");
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
            "create local-shadow comparison sampler");
        CreateFrameData(vk, device, frame0Descriptor, findMemoryType, 0);
        CreateFrameData(vk, device, frame1Descriptor, findMemoryType, 1);
    }

    /// <summary>Uploads all active local-light transforms and filter settings.</summary>
    /// <param name="frameIndex">Frame-in-flight index.</param>
    /// <param name="lights">Collected view lights.</param>
    /// <param name="settings">Active local-shadow settings.</param>
    public void Update(uint frameIndex, SceneLightSet lights, LocalShadowSettings settings)
    {
        if (frameIndex >= FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        ArgumentNullException.ThrowIfNull(lights);
        new Span<byte>((void*)_mappedData[frameIndex], BufferSize).Clear();
        var matrices = (Matrix4x4*)_mappedData[frameIndex];
        if (settings.IsEnabled)
        {
            var source = lights.Lights;
            for (var lightIndex = 0; lightIndex < source.Length; lightIndex++)
            {
                var light = source[lightIndex];
                if (light.ShadowIndex < 0)
                    continue;
                var transforms = light.Type == SceneLightType.Point
                    ? LocalShadowMatrixCalculator.CalculatePoint(light)
                    : LocalShadowMatrixCalculator.CalculateSpot(light);
                for (var faceIndex = 0; faceIndex < transforms.Count; faceIndex++)
                    matrices[light.ShadowIndex * 6 + faceIndex] =
                        transforms.GetMatrix(faceIndex);
            }
        }
        var parameters = (Vector4*)(matrices + MatrixCount);
        *parameters = new Vector4(settings.Strength, settings.NormalBias,
            1f / FaceResolution, 0f);
    }

    /// <summary>Records that a completed render pass left the atlas shader-readable.</summary>
    public void MarkInitialized() => IsInitialized = true;

    /// <summary>Releases atlas images, buffers, and mapped memory.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Owning logical device.</param>
    public void Destroy(Vk vk, Device device)
    {
        for (var frameIndex = 0; frameIndex < FrameCount; frameIndex++)
        {
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

    /// <summary>Creates one mapped data buffer and writes its descriptor bindings.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Owning device.</param>
    /// <param name="descriptorSet">Shadow descriptor for this frame.</param>
    /// <param name="findMemoryType">Memory-type resolver.</param>
    /// <param name="frameIndex">Frame slot.</param>
    private void CreateFrameData(
        Vk vk,
        Device device,
        DescriptorSet descriptorSet,
        Func<uint, MemoryPropertyFlags, uint> findMemoryType,
        int frameIndex)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = BufferSize,
            Usage = BufferUsageFlags.UniformBufferBit,
            SharingMode = SharingMode.Exclusive
        };
        Check(vk.CreateBuffer(device, &bufferInfo, null, out _dataBuffers[frameIndex]),
            "create local-shadow data buffer");
        vk.GetBufferMemoryRequirements(device, _dataBuffers[frameIndex], out var requirements);
        var allocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = findMemoryType(requirements.MemoryTypeBits,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };
        Check(vk.AllocateMemory(device, &allocation, null, out _dataMemories[frameIndex]),
            "allocate local-shadow data memory");
        Check(vk.BindBufferMemory(device, _dataBuffers[frameIndex],
            _dataMemories[frameIndex], 0), "bind local-shadow data memory");
        void* mapped;
        Check(vk.MapMemory(device, _dataMemories[frameIndex], 0, BufferSize, 0, &mapped),
            "map local-shadow data memory");
        _mappedData[frameIndex] = (nint)mapped;
        new Span<byte>(mapped, BufferSize).Clear();

        var image = new DescriptorImageInfo
        {
            ImageView = View,
            ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal
        };
        var sampler = new DescriptorImageInfo { Sampler = Sampler };
        var buffer = new DescriptorBufferInfo
        {
            Buffer = _dataBuffers[frameIndex],
            Range = BufferSize
        };
        var writes = stackalloc WriteDescriptorSet[3];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 3,
            DescriptorType = DescriptorType.SampledImage,
            DescriptorCount = 1,
            PImageInfo = &image
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 4,
            DescriptorType = DescriptorType.Sampler,
            DescriptorCount = 1,
            PImageInfo = &sampler
        };
        writes[2] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = descriptorSet,
            DstBinding = 5,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            PBufferInfo = &buffer
        };
        vk.UpdateDescriptorSets(device, 3, writes, 0, null);
    }

    /// <summary>Throws when Vulkan reports a failed local-shadow operation.</summary>
    /// <param name="result">Vulkan result.</param>
    /// <param name="operation">Operation description.</param>
    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"Failed to {operation}: {result}");
    }
}
