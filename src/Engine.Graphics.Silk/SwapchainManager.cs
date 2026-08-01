using Microsoft.Extensions.Logging;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Engine.Graphics;

/// <summary>
/// Owns Vulkan swapchain images, views, extent, and presentation framebuffers.
/// </summary>
internal unsafe sealed class SwapchainManager
{
    private readonly Vk _vk;
    private readonly Device _device;
    private readonly PhysicalDevice _physicalDevice;
    private readonly SurfaceKHR _surface;
    private readonly KhrSurface _surfaceExtension;
    private readonly uint _graphicsFamily;
    private readonly uint _presentFamily;
    private readonly ILogger _logger;
    private readonly KhrSwapchain _extension;
    private SwapchainKHR _swapchain;
    private ImageView[] _imageViews = [];
    private Framebuffer[] _framebuffers = [];

    /// <summary>Gets the swapchain extension API.</summary>
    internal KhrSwapchain Extension => _extension;

    /// <summary>Gets the active swapchain handle.</summary>
    internal SwapchainKHR Handle => _swapchain;

    /// <summary>Gets the active swapchain extent.</summary>
    internal Extent2D Extent { get; private set; }

    /// <summary>Gets the active swapchain image format.</summary>
    internal Format ImageFormat { get; private set; }

    /// <summary>Gets the presentation framebuffers.</summary>
    internal IReadOnlyList<Framebuffer> Framebuffers => _framebuffers;

    /// <summary>
    /// Creates a swapchain resource owner.
    /// </summary>
    /// <param name="vk">Vulkan API.</param>
    /// <param name="device">Logical device.</param>
    /// <param name="physicalDevice">Physical device.</param>
    /// <param name="surface">Presentation surface.</param>
    /// <param name="surfaceExtension">Surface extension API.</param>
    /// <param name="graphicsFamily">Graphics queue family.</param>
    /// <param name="presentFamily">Presentation queue family.</param>
    /// <param name="logger">Backend logger.</param>
    internal SwapchainManager(
        Vk vk,
        Device device,
        PhysicalDevice physicalDevice,
        SurfaceKHR surface,
        KhrSurface surfaceExtension,
        uint graphicsFamily,
        uint presentFamily,
        ILogger logger)
    {
        _vk = vk;
        _device = device;
        _physicalDevice = physicalDevice;
        _surface = surface;
        _surfaceExtension = surfaceExtension;
        _graphicsFamily = graphicsFamily;
        _presentFamily = presentFamily;
        _logger = logger;
        _extension = new KhrSwapchain(vk.Context);
    }

    /// <summary>Creates the swapchain and its image views.</summary>
    /// <param name="requestedWidth">Requested framebuffer width.</param>
    /// <param name="requestedHeight">Requested framebuffer height.</param>
    internal void Create(uint requestedWidth, uint requestedHeight)
    {
        var support = QuerySupport(_surfaceExtension, _physicalDevice, _surface);
        var surfaceFormat = SwapchainPolicy.ChooseSurfaceFormat(support.Formats);
        var presentMode = SwapchainPolicy.ChoosePresentMode(support.PresentModes);
        var extent = SwapchainPolicy.ChooseExtent(support.Capabilities, requestedWidth, requestedHeight);
        var imageCount = support.Capabilities.MinImageCount + 1;
        if (support.Capabilities.MaxImageCount > 0 && imageCount > support.Capabilities.MaxImageCount)
            imageCount = support.Capabilities.MaxImageCount;

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = surfaceFormat.Format,
            ImageColorSpace = surfaceFormat.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            PreTransform = support.Capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = new Bool32(true)
        };
        var queueFamilies = stackalloc[] { _graphicsFamily, _presentFamily };
        if (_graphicsFamily != _presentFamily)
        {
            createInfo.ImageSharingMode = SharingMode.Concurrent;
            createInfo.QueueFamilyIndexCount = 2;
            createInfo.PQueueFamilyIndices = queueFamilies;
        }
        else
        {
            createInfo.ImageSharingMode = SharingMode.Exclusive;
        }

        Check(_extension.CreateSwapchain(_device, &createInfo, null, out _swapchain), "create swapchain");
        _extension.GetSwapchainImages(_device, _swapchain, &imageCount, null);
        var images = new Image[imageCount];
        fixed (Image* imagePointer = images)
            _extension.GetSwapchainImages(_device, _swapchain, &imageCount, imagePointer);

        Extent = extent;
        ImageFormat = surfaceFormat.Format;
        _imageViews = new ImageView[imageCount];
        for (var index = 0u; index < imageCount; index++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = images[index],
                ViewType = ImageViewType.Type2D,
                Format = ImageFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LevelCount = 1,
                    LayerCount = 1
                }
            };
            Check(_vk.CreateImageView(_device, &viewInfo, null, out _imageViews[index]), "create image view");
        }
        _logger.LogInformation("Swapchain created ({Width}x{Height}, {Count} images)",
            extent.Width, extent.Height, imageCount);
    }

    /// <summary>Creates one framebuffer for every swapchain image view.</summary>
    /// <param name="renderPass">Presentation render pass.</param>
    internal void CreateFramebuffers(RenderPass renderPass)
    {
        DestroyFramebuffers();
        _framebuffers = new Framebuffer[_imageViews.Length];
        for (var index = 0; index < _imageViews.Length; index++)
        {
            var imageView = _imageViews[index];
            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = 1,
                PAttachments = &imageView,
                Width = Extent.Width,
                Height = Extent.Height,
                Layers = 1
            };
            Check(_vk.CreateFramebuffer(_device, &framebufferInfo, null, out _framebuffers[index]),
                "create framebuffer");
        }
    }

    /// <summary>Recreates all swapchain-owned resources.</summary>
    /// <param name="requestedWidth">Requested framebuffer width.</param>
    /// <param name="requestedHeight">Requested framebuffer height.</param>
    /// <param name="renderPass">Presentation render pass.</param>
    internal void Recreate(uint requestedWidth, uint requestedHeight, RenderPass renderPass)
    {
        Destroy();
        Create(requestedWidth, requestedHeight);
        CreateFramebuffers(renderPass);
    }

    /// <summary>Destroys framebuffers, image views, and the swapchain.</summary>
    internal void Destroy()
    {
        DestroyFramebuffers();
        foreach (var imageView in _imageViews)
            _vk.DestroyImageView(_device, imageView, null);
        _imageViews = [];
        if (_swapchain.Handle != 0)
            _extension.DestroySwapchain(_device, _swapchain, null);
        _swapchain = default;
    }

    /// <summary>Queries swapchain support for device selection or creation.</summary>
    /// <param name="surfaceExtension">Surface extension API.</param>
    /// <param name="device">Physical device.</param>
    /// <param name="surface">Presentation surface.</param>
    /// <returns>Surface capabilities, formats, and presentation modes.</returns>
    internal static SwapchainSupport QuerySupport(
        KhrSurface surfaceExtension,
        PhysicalDevice device,
        SurfaceKHR surface)
    {
        surfaceExtension.GetPhysicalDeviceSurfaceCapabilities(device, surface, out var capabilities);
        uint formatCount = 0;
        surfaceExtension.GetPhysicalDeviceSurfaceFormats(device, surface, &formatCount, null);
        var formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* formatPointer = formats)
            surfaceExtension.GetPhysicalDeviceSurfaceFormats(device, surface, &formatCount, formatPointer);
        uint modeCount = 0;
        surfaceExtension.GetPhysicalDeviceSurfacePresentModes(device, surface, &modeCount, null);
        var modes = new PresentModeKHR[modeCount];
        fixed (PresentModeKHR* modePointer = modes)
            surfaceExtension.GetPhysicalDeviceSurfacePresentModes(device, surface, &modeCount, modePointer);
        return new SwapchainSupport(capabilities, formats, modes);
    }

    /// <summary>Destroys presentation framebuffers.</summary>
    private void DestroyFramebuffers()
    {
        foreach (var framebuffer in _framebuffers)
            _vk.DestroyFramebuffer(_device, framebuffer, null);
        _framebuffers = [];
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

/// <summary>
/// Contains queried surface support used for swapchain selection.
/// </summary>
/// <param name="Capabilities">Surface capabilities.</param>
/// <param name="Formats">Supported formats.</param>
/// <param name="PresentModes">Supported presentation modes.</param>
internal readonly record struct SwapchainSupport(
    SurfaceCapabilitiesKHR Capabilities,
    SurfaceFormatKHR[] Formats,
    PresentModeKHR[] PresentModes);
