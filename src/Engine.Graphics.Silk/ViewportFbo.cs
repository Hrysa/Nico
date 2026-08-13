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
    public const uint ShadowResolution = 2048;
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
    public Image ShadowImage;
    public DeviceMemory ShadowMemory;
    public ImageView ShadowView;
    public Framebuffer ShadowFramebuffer;
    public Sampler ShadowSampler;
    public DescriptorSet ShadowDescriptorSet;

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
        Vk vk, Device device, RenderPass fboRenderPass, RenderPass shadowRenderPass,
        Format colorFormat, Format depthFormat, SampleCountFlags samples, uint deviceLocalMemoryType,
        DescriptorSetLayout descriptorSetLayout, DescriptorPool descriptorPool,
        DescriptorSetLayout shadowDescriptorSetLayout, DescriptorPool shadowDescriptorPool,
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
            MagFilter = Filter.Linear, MinFilter = Filter.Linear,
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

        var shadowInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = depthFormat,
            Extent = new Extent3D
            {
                Width = ShadowResolution, Height = ShadowResolution, Depth = 1
            },
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        vk.CreateImage(device, &shadowInfo, null, out ShadowImage);
        vk.GetImageMemoryRequirements(device, ShadowImage, out var shadowMemoryRequirements);
        var shadowAllocation = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = shadowMemoryRequirements.Size,
            MemoryTypeIndex = deviceLocalMemoryType
        };
        vk.AllocateMemory(device, in shadowAllocation, null, out ShadowMemory);
        vk.BindImageMemory(device, ShadowImage, ShadowMemory, 0);
        var shadowViewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = ShadowImage,
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
        vk.CreateImageView(device, in shadowViewInfo, null, out ShadowView);
        var shadowAttachment = ShadowView;
        var shadowFramebufferInfo = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = shadowRenderPass,
            AttachmentCount = 1,
            PAttachments = &shadowAttachment,
            Width = ShadowResolution,
            Height = ShadowResolution,
            Layers = 1
        };
        vk.CreateFramebuffer(device, &shadowFramebufferInfo, null, out ShadowFramebuffer);
        var shadowSamplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            AddressModeU = SamplerAddressMode.ClampToBorder,
            AddressModeV = SamplerAddressMode.ClampToBorder,
            AddressModeW = SamplerAddressMode.ClampToBorder,
            BorderColor = BorderColor.FloatOpaqueWhite,
            MipmapMode = SamplerMipmapMode.Nearest,
            MaxLod = 1f
        };
        vk.CreateSampler(device, in shadowSamplerInfo, null, out ShadowSampler);
        if (allocateDescriptorSet)
        {
            var shadowLayout = shadowDescriptorSetLayout;
            var shadowSetInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = shadowDescriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &shadowLayout
            };
            var shadowAllocationResult = vk.AllocateDescriptorSets(
                device, in shadowSetInfo, out ShadowDescriptorSet);
            if (shadowAllocationResult != Result.Success)
                throw new InvalidOperationException(
                    $"Failed to allocate viewport {Id} shadow descriptor: {shadowAllocationResult}");
        }
        var shadowDescriptorInfo = new DescriptorImageInfo
        {
            Sampler = ShadowSampler,
            ImageView = ShadowView,
            ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal
        };
        var shadowWrite = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = ShadowDescriptorSet,
            DstBinding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            PImageInfo = &shadowDescriptorInfo
        };
        vk.UpdateDescriptorSets(device, 1, &shadowWrite, 0, null);
    }

    /// <summary>Destroys framebuffer resources and optionally returns its sampled-image descriptor.</summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Owning logical device.</param>
    /// <param name="descriptorPool">Pool that allocated the viewport descriptor.</param>
    /// <param name="releaseDescriptorSet">Whether this is final destruction rather than resize.</param>
    public void Destroy(
        Vk vk,
        Device device,
        DescriptorPool descriptorPool,
        DescriptorPool shadowDescriptorPool,
        bool releaseDescriptorSet = true)
    {
        if (releaseDescriptorSet && DescriptorSet.Handle != 0)
        {
            var descriptorSet = DescriptorSet;
            vk.FreeDescriptorSets(device, descriptorPool, 1, &descriptorSet);
            DescriptorSet = default;
        }
        if (releaseDescriptorSet && ShadowDescriptorSet.Handle != 0)
        {
            var shadowDescriptor = ShadowDescriptorSet;
            vk.FreeDescriptorSets(device, shadowDescriptorPool, 1, &shadowDescriptor);
            ShadowDescriptorSet = default;
        }
        if (ShadowFramebuffer.Handle != 0)
            vk.DestroyFramebuffer(device, ShadowFramebuffer, null);
        if (ShadowSampler.Handle != 0)
            vk.DestroySampler(device, ShadowSampler, null);
        if (ShadowView.Handle != 0)
            vk.DestroyImageView(device, ShadowView, null);
        if (ShadowImage.Handle != 0)
            vk.DestroyImage(device, ShadowImage, null);
        if (ShadowMemory.Handle != 0)
            vk.FreeMemory(device, ShadowMemory, null);
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
        Vk vk, Device device, RenderPass fboRenderPass, RenderPass shadowRenderPass,
        Format colorFormat, Format depthFormat, SampleCountFlags samples, uint deviceLocalMemoryType,
        DescriptorSetLayout descriptorSetLayout, DescriptorPool descriptorPool,
        DescriptorSetLayout shadowDescriptorSetLayout, DescriptorPool shadowDescriptorPool)
    {
        Destroy(vk, device, descriptorPool, shadowDescriptorPool, releaseDescriptorSet: false);
        Create(vk, device, fboRenderPass, shadowRenderPass, colorFormat, depthFormat, samples,
            deviceLocalMemoryType, descriptorSetLayout, descriptorPool,
            shadowDescriptorSetLayout, shadowDescriptorPool, allocateDescriptorSet: false);
        IsDirty = false;
    }
}
