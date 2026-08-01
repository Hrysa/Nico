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
    public uint Id { get; }
    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public Vector4 ClearColor { get; set; } = new(0.1f, 0.1f, 0.15f, 1.0f);
    public bool IsDirty { get; set; }

    public Image ColorImage;
    public DeviceMemory ColorMemory;
    public ImageView ColorView;
    public Image MsaaColorImage;
    public DeviceMemory MsaaColorMemory;
    public ImageView MsaaColorView;
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
        if (Width == width && Height == height) return;
        Width = width;
        Height = height;
        IsDirty = true;
    }

    public void Create(
        Vk vk, Device device, RenderPass fboRenderPass,
        Format colorFormat, Format depthFormat, SampleCountFlags samples, uint deviceLocalMemoryType,
        DescriptorSetLayout descriptorSetLayout, DescriptorPool descriptorPool,
        bool allocateDescriptorSet = true)
    {
        // ── Color image ────────────────────────────────────────
        var colorInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = colorFormat,
            Extent = new Extent3D { Width = Width, Height = Height, Depth = 1 },
            MipLevels = 1, ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        vk.CreateImage(device, &colorInfo, null, out ColorImage);
        vk.GetImageMemoryRequirements(device, ColorImage, out var colorMemReqs);
        var colorAllocation = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = colorMemReqs.Size, MemoryTypeIndex = deviceLocalMemoryType };
        vk.AllocateMemory(device, in colorAllocation, null, out ColorMemory);
        vk.BindImageMemory(device, ColorImage, ColorMemory, 0);
        var colorViewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo, Image = ColorImage, ViewType = ImageViewType.Type2D, Format = colorFormat,
            SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 }
        };
        vk.CreateImageView(device, in colorViewInfo, null, out ColorView);

        // ── Multisampled color image ───────────────────────────
        colorInfo.Samples = samples;
        colorInfo.Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransientAttachmentBit;
        vk.CreateImage(device, &colorInfo, null, out MsaaColorImage);
        vk.GetImageMemoryRequirements(device, MsaaColorImage, out var msaaColorMemReqs);
        var msaaAllocation = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = msaaColorMemReqs.Size, MemoryTypeIndex = deviceLocalMemoryType };
        vk.AllocateMemory(device, in msaaAllocation, null, out MsaaColorMemory);
        vk.BindImageMemory(device, MsaaColorImage, MsaaColorMemory, 0);
        var msaaViewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo, Image = MsaaColorImage, ViewType = ImageViewType.Type2D, Format = colorFormat,
            SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.ColorBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 }
        };
        vk.CreateImageView(device, in msaaViewInfo, null, out MsaaColorView);

        // ── Depth image ────────────────────────────────────────
        var depthInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = depthFormat,
            Extent = new Extent3D { Width = Width, Height = Height, Depth = 1 },
            MipLevels = 1, ArrayLayers = 1,
            Samples = samples,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        vk.CreateImage(device, &depthInfo, null, out DepthImage);
        vk.GetImageMemoryRequirements(device, DepthImage, out var depthMemReqs);
        var depthAllocation = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = depthMemReqs.Size, MemoryTypeIndex = deviceLocalMemoryType };
        vk.AllocateMemory(device, in depthAllocation, null, out DepthMemory);
        vk.BindImageMemory(device, DepthImage, DepthMemory, 0);
        var depthViewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo, Image = DepthImage, ViewType = ImageViewType.Type2D, Format = depthFormat,
            SubresourceRange = new ImageSubresourceRange { AspectMask = ImageAspectFlags.DepthBit, BaseMipLevel = 0, LevelCount = 1, BaseArrayLayer = 0, LayerCount = 1 }
        };
        vk.CreateImageView(device, in depthViewInfo, null, out DepthView);

        // ── Framebuffer (MSAA color + depth + resolved color) ──
        var fbAttachments = stackalloc[] { MsaaColorView, DepthView, ColorView };
        var fbInfo = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = fboRenderPass,
            AttachmentCount = 3,
            PAttachments = fbAttachments,
            Width = Width, Height = Height, Layers = 1
        };
        vk.CreateFramebuffer(device, &fbInfo, null, out Framebuffer);

        // ── Sampler ─────────────────────────────────────────────
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest, MinFilter = Filter.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge, AddressModeW = SamplerAddressMode.ClampToEdge,
            AnisotropyEnable = new Bool32(false), MaxAnisotropy = 1,
            BorderColor = BorderColor.IntOpaqueBlack, UnnormalizedCoordinates = new Bool32(false),
            CompareEnable = new Bool32(false), CompareOp = CompareOp.Always, MipmapMode = SamplerMipmapMode.Nearest
        };
        vk.CreateSampler(device, in samplerInfo, null, out Sampler);

        // ── Descriptor set ──────────────────────────────────────
        if (allocateDescriptorSet)
        {
            var setLayout = descriptorSetLayout;
            var descriptorSetInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = descriptorPool,
                DescriptorSetCount = 1, PSetLayouts = &setLayout
            };
            var allocationResult = vk.AllocateDescriptorSets(device, in descriptorSetInfo, out DescriptorSet);
            if (allocationResult != Result.Success)
                throw new InvalidOperationException(
                    $"Failed to allocate viewport {Id} texture descriptor: {allocationResult}");
        }
        var imageInfoDesc = new DescriptorImageInfo { Sampler = Sampler, ImageView = ColorView, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        var writeDescriptor = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstSet = DescriptorSet, DstBinding = 0, DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &imageInfoDesc
        };
        vk.UpdateDescriptorSets(device, 1, &writeDescriptor, 0, null);
    }

    public void Destroy(Vk vk, Device device)
    {
        vk.DestroyFramebuffer(device, Framebuffer, null);
        vk.DestroySampler(device, Sampler, null);
        vk.DestroyImageView(device, MsaaColorView, null);
        vk.DestroyImage(device, MsaaColorImage, null);
        vk.FreeMemory(device, MsaaColorMemory, null);
        vk.DestroyImageView(device, DepthView, null);
        vk.DestroyImage(device, DepthImage, null);
        vk.FreeMemory(device, DepthMemory, null);
        vk.DestroyImageView(device, ColorView, null);
        vk.DestroyImage(device, ColorImage, null);
        vk.FreeMemory(device, ColorMemory, null);
    }

    public void Recreate(
        Vk vk, Device device, RenderPass fboRenderPass,
        Format colorFormat, Format depthFormat, SampleCountFlags samples, uint deviceLocalMemoryType,
        DescriptorSetLayout descriptorSetLayout, DescriptorPool descriptorPool)
    {
        Destroy(vk, device);
        Create(vk, device, fboRenderPass, colorFormat, depthFormat, samples, deviceLocalMemoryType,
            descriptorSetLayout, descriptorPool, allocateDescriptorSet: false);
        IsDirty = false;
    }
}
