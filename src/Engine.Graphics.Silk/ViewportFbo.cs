using System.Numerics;
using Silk.NET.Core;
using Silk.NET.Vulkan;

namespace Engine.Graphics;

/// <summary>
/// Encapsulates all Vulkan GPU resources for a single offscreen viewport
/// render target: color image, depth image, framebuffer, sampler, and
/// descriptor set for textured-quad rendering.
/// </summary>
internal unsafe class ViewportFbo
{
    /// <summary>Unique viewport identifier.</summary>
    public uint Id { get; }

    /// <summary>Current width in pixels.</summary>
    public uint Width { get; private set; }

    /// <summary>Current height in pixels.</summary>
    public uint Height { get; private set; }

    /// <summary>Clear color for this viewport's FBO.</summary>
    public Vector4 ClearColor { get; set; } = new(0.1f, 0.1f, 0.15f, 1.0f);

    /// <summary>True when the FBO needs to be recreated at a new size.</summary>
    public bool IsDirty { get; set; }

    // Vulkan handles
    public Image ColorImage;
    public DeviceMemory ColorMemory;
    public ImageView ColorView;
    public Image DepthImage;
    public DeviceMemory DepthMemory;
    public ImageView DepthView;
    public Framebuffer Framebuffer;
    public Sampler Sampler;
    public DescriptorSet DescriptorSet;

    public ViewportFbo(uint id, uint width, uint height)
    {
        Id = id;
        Width = width;
        Height = height;
    }

    public void Resize(uint width, uint height)
    {
        if (Width == width && Height == height)
            return;
        Width = width;
        Height = height;
        IsDirty = true;
    }

    public void Create(
        Vk vk, Device device, RenderPass fboRenderPass,
        Format colorFormat, Format depthFormat, uint deviceLocalMemoryType,
        DescriptorSetLayout descriptorSetLayout, DescriptorPool descriptorPool)
    {
        // ── Color image ────────────────────────────────────────
        var colorInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = colorFormat,
            Extent = new Extent3D { Width = Width, Height = Height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };

        var result = vk.CreateImage(device, &colorInfo, null, out ColorImage);
        if (result != Result.Success)
            throw new Exception($"Failed to create viewport FBO color image: {result}");

        vk.GetImageMemoryRequirements(device, ColorImage, out var colorMemReqs);
        var colorAllocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = colorMemReqs.Size,
            MemoryTypeIndex = deviceLocalMemoryType
        };
        result = vk.AllocateMemory(device, &colorAllocInfo, null, out ColorMemory);
        if (result != Result.Success)
            throw new Exception($"Failed to allocate viewport FBO color memory: {result}");
        vk.BindImageMemory(device, ColorImage, ColorMemory, 0);

        var colorViewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = ColorImage,
            ViewType = ImageViewType.Type2D,
            Format = colorFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = 0, LevelCount = 1,
                BaseArrayLayer = 0, LayerCount = 1
            }
        };
        result = vk.CreateImageView(device, &colorViewInfo, null, out ColorView);
        if (result != Result.Success)
            throw new Exception($"Failed to create viewport FBO color view: {result}");

        // ── Depth image ────────────────────────────────────────
        var depthInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = depthFormat,
            Extent = new Extent3D { Width = Width, Height = Height, Depth = 1 },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };

        result = vk.CreateImage(device, &depthInfo, null, out DepthImage);
        if (result != Result.Success)
            throw new Exception($"Failed to create viewport FBO depth image: {result}");

        vk.GetImageMemoryRequirements(device, DepthImage, out var depthMemReqs);
        var depthAllocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = depthMemReqs.Size,
            MemoryTypeIndex = deviceLocalMemoryType
        };
        result = vk.AllocateMemory(device, &depthAllocInfo, null, out DepthMemory);
        if (result != Result.Success)
            throw new Exception($"Failed to allocate viewport FBO depth memory: {result}");
        vk.BindImageMemory(device, DepthImage, DepthMemory, 0);

        var depthViewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = DepthImage,
            ViewType = ImageViewType.Type2D,
            Format = depthFormat,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.DepthBit,
                BaseMipLevel = 0, LevelCount = 1,
                BaseArrayLayer = 0, LayerCount = 1
            }
        };
        result = vk.CreateImageView(device, &depthViewInfo, null, out DepthView);
        if (result != Result.Success)
            throw new Exception($"Failed to create viewport FBO depth view: {result}");

        // ── Framebuffer (color + depth) ────────────────────────
        var fbInfo = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = fboRenderPass,
            AttachmentCount = 2,
            Width = Width,
            Height = Height,
            Layers = 1
        };

        var attachments = stackalloc[] { ColorView, DepthView };
        fbInfo.PAttachments = attachments;
        result = vk.CreateFramebuffer(device, &fbInfo, null, out Framebuffer);
        if (result != Result.Success)
            throw new Exception($"Failed to create viewport FBO framebuffer: {result}");

        // ── Sampler ─────────────────────────────────────────────
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            AnisotropyEnable = new Bool32(false),
            MaxAnisotropy = 1,
            BorderColor = BorderColor.IntOpaqueBlack,
            UnnormalizedCoordinates = new Bool32(false),
            CompareEnable = new Bool32(false),
            CompareOp = CompareOp.Always,
            MipmapMode = SamplerMipmapMode.Nearest
        };
        result = vk.CreateSampler(device, &samplerInfo, null, out Sampler);
        if (result != Result.Success)
            throw new Exception($"Failed to create viewport FBO sampler: {result}");

        // ── Descriptor set ──────────────────────────────────────
        var setLayout = descriptorSetLayout;
        var setInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout
        };
        result = vk.AllocateDescriptorSets(device, &setInfo, out DescriptorSet);
        if (result != Result.Success)
            throw new Exception($"Failed to allocate viewport FBO descriptor set: {result}");

        var imageInfoDesc = new DescriptorImageInfo
        {
            Sampler = Sampler,
            ImageView = ColorView,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal
        };
        var writeDescriptor = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = DescriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &imageInfoDesc
        };
        vk.UpdateDescriptorSets(device, 1, &writeDescriptor, 0, null);
    }

    public void Destroy(Vk vk, Device device)
    {
        vk.DestroySampler(device, Sampler, null);
        vk.DestroyImageView(device, DepthView, null);
        vk.DestroyImage(device, DepthImage, null);
        vk.FreeMemory(device, DepthMemory, null);
        vk.DestroyImageView(device, ColorView, null);
        vk.DestroyImage(device, ColorImage, null);
        vk.FreeMemory(device, ColorMemory, null);
        vk.DestroyFramebuffer(device, Framebuffer, null);
    }

    public void Recreate(
        Vk vk, Device device, RenderPass fboRenderPass,
        Format colorFormat, Format depthFormat, uint deviceLocalMemoryType,
        DescriptorSetLayout descriptorSetLayout, DescriptorPool descriptorPool)
    {
        Destroy(vk, device);
        Create(vk, device, fboRenderPass, colorFormat, depthFormat, deviceLocalMemoryType, descriptorSetLayout, descriptorPool);
        IsDirty = false;
    }

    public void Dispose() { /* destroys image, view, framebuffer, sampler */ }
}
