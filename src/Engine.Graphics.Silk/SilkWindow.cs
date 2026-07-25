using Microsoft.Extensions.Logging;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;

namespace Engine.Graphics;

public unsafe class SilkWindow : IWindow
{
    private const uint MaxFramesInFlight = 2;

    private readonly ILogger _logger;
    private Silk.NET.Windowing.IWindow? _window;

    private Instance _instance;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _graphicsQueue;
    private Queue _presentQueue;
    private KhrSurface? _khrSurface;
    private KhrSwapchain? _khrSwapchain;
    private SwapchainKHR _swapchain;
    private Extent2D _swapchainExtent;
    private Image[]? _swapchainImages;
    private ImageView[]? _swapchainImageViews;
    private RenderPass _renderPass;
    private Framebuffer[]? _framebuffers;
    private CommandPool _commandPool;
    private CommandBuffer[]? _commandBuffers;
    private Silk.NET.Vulkan.Semaphore[]? _imageAvailableSemaphores;
    private Silk.NET.Vulkan.Semaphore[]? _renderFinishedSemaphores;
    private Fence[]? _inFlightFences;
    private uint _currentFrame;
    private bool _framebufferResized;

    private Vk? _vk;

    public bool IsRunning => _window != null && !_window.IsClosing;

    public SilkWindow(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<SilkWindow>();
    }

    public void Initialize(WindowOptions options)
    {
        _logger.LogInformation("Creating window '{Title}' ({Width}x{Height}) [Vulkan]", options.Title, options.Width, options.Height);

        var settings = new Silk.NET.Windowing.WindowOptions
        {
            Size = new Vector2D<int>(options.Width, options.Height),
            Title = options.Title,
            API = new GraphicsAPI(ContextAPI.Vulkan, new APIVersion(1, 1)),
            ShouldSwapAutomatically = false,
            IsVisible = true
        };

        _window = Window.Create(settings);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;
        _window.Resize += OnResize;

        _window.Initialize();
        _logger.LogInformation("Window initialized");
    }

    private void OnLoad()
    {
        _logger.LogInformation("Window.Load fired — initializing Vulkan");

        _vk = Vk.GetApi();
        CreateInstance();
        CreateSurface();
        PickPhysicalDevice();
        CreateLogicalDevice();
        CreateSwapchain();
        CreateRenderPass();
        CreateFramebuffers();
        CreateCommandPool();
        CreateCommandBuffers();
        CreateSyncObjects();

        _logger.LogInformation("Vulkan initialization complete");
    }

    private void OnUpdate(double delta)
    {
    }

    private void OnRender(double delta)
    {
        DrawFrame();
    }

    private void OnClosing()
    {
        _vk!.DeviceWaitIdle(_device);
    }

    private void OnResize(Vector2D<int> size)
    {
        _framebufferResized = true;
    }

    private void CreateInstance()
    {
        _logger.LogDebug("Creating VkInstance");

        var appName = SilkMarshal.StringToPtr("GameEngine", NativeStringEncoding.UTF8);
        var engineName = SilkMarshal.StringToPtr("GameEngine", NativeStringEncoding.UTF8);

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = (byte*)appName,
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = (byte*)engineName,
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version11
        };

        var requiredExtensions = GetRequiredInstanceExtensions();
        requiredExtensions = [.. requiredExtensions, "VK_KHR_portability_enumeration"];
        var extensionsMem = SilkMarshal.StringArrayToPtr(requiredExtensions, NativeStringEncoding.UTF8);

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)requiredExtensions.Length,
            PpEnabledExtensionNames = (byte**)extensionsMem,
            Flags = InstanceCreateFlags.EnumeratePortabilityBitKhr
        };

        var result = _vk!.CreateInstance(&createInfo, null, out _instance);
        if (result != Result.Success)
            throw new Exception($"Failed to create VkInstance: {result}");

        _vk.TryGetInstanceExtension(_instance, out _khrSurface);

        SilkMarshal.Free(extensionsMem);
        SilkMarshal.Free(appName);
        SilkMarshal.Free(engineName);

        _logger.LogInformation("VkInstance created");
    }

    private string[] GetRequiredInstanceExtensions()
    {
        var windowExts = _window!.VkSurface.GetRequiredExtensions(out var count);
        var extensions = new string[count];
        for (var i = 0; i < count; i++)
            extensions[i] = SilkMarshal.PtrToString((nint)windowExts[i], NativeStringEncoding.UTF8)!;
        return extensions;
    }

    private void CreateSurface()
    {
        _logger.LogDebug("Creating VkSurfaceKHR");

        var surfaceHandle = _window!.VkSurface.Create<AllocationCallbacks>(new VkHandle(_instance.Handle), null);
        _surface = new SurfaceKHR(surfaceHandle.Handle);

        _logger.LogInformation("VkSurfaceKHR created");
    }

    private void PickPhysicalDevice()
    {
        _logger.LogDebug("Picking physical device");

        uint deviceCount = 0;
        _vk!.EnumeratePhysicalDevices(_instance, &deviceCount, null);
        if (deviceCount == 0)
            throw new Exception("No Vulkan physical devices found");

        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* pDevices = devices)
            _vk.EnumeratePhysicalDevices(_instance, &deviceCount, pDevices);

        foreach (var device in devices)
        {
            if (IsDeviceSuitable(device))
            {
                _physicalDevice = device;
                var props = _vk.GetPhysicalDeviceProperties(device);
                _logger.LogInformation("Using GPU: {Name}", SilkMarshal.PtrToString((nint)props.DeviceName, NativeStringEncoding.UTF8));
                return;
            }
        }

        throw new Exception("No suitable Vulkan physical device found");
    }

    private bool IsDeviceSuitable(PhysicalDevice device)
    {
        var indices = FindQueueFamilies(device);
        return indices.GraphicsFamily.HasValue && indices.PresentFamily.HasValue;
    }

    private QueueFamilyIndices FindQueueFamilies(PhysicalDevice device)
    {
        var indices = new QueueFamilyIndices();

        uint queueFamilyCount = 0;
        _vk!.GetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, null);

        var queueFamilies = new QueueFamilyProperties[queueFamilyCount];
        fixed (QueueFamilyProperties* pQueueFamilies = queueFamilies)
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, pQueueFamilies);

        for (var i = 0u; i < queueFamilyCount; i++)
        {
            if (queueFamilies[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
                indices.GraphicsFamily = i;

            _khrSurface!.GetPhysicalDeviceSurfaceSupport(device, i, _surface, out var presentSupport);
            if (presentSupport == new Bool32(true))
                indices.PresentFamily = i;

            if (indices.GraphicsFamily.HasValue && indices.PresentFamily.HasValue)
                break;
        }

        return indices;
    }

    private void CreateLogicalDevice()
    {
        _logger.LogDebug("Creating logical device");

        var indices = FindQueueFamilies(_physicalDevice);
        _logger.LogDebug("Graphics queue family: {Family}, Present queue family: {Family}", indices.GraphicsFamily, indices.PresentFamily);

        var queuePriorityArr = new[] { 1.0f };
        fixed (float* pQueuePriority = queuePriorityArr)
        {
            var queueCreateInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = indices.GraphicsFamily!.Value,
                QueueCount = 1,
                PQueuePriorities = pQueuePriority
            };

        var extensions = new[] { "VK_KHR_swapchain", "VK_KHR_portability_subset" };
        var extensionNamesMem = SilkMarshal.StringArrayToPtr(extensions, NativeStringEncoding.UTF8);

        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = 1,
            PQueueCreateInfos = &queueCreateInfo,
            EnabledExtensionCount = (uint)extensions.Length,
            PpEnabledExtensionNames = (byte**)extensionNamesMem
        };

        _logger.LogDebug("Calling vkCreateDevice...");
        var result = _vk!.CreateDevice(_physicalDevice, &createInfo, null, out _device);
        SilkMarshal.Free(extensionNamesMem);

            if (result != Result.Success)
                throw new Exception($"Failed to create logical device: {result}");
        }

        _vk.GetDeviceQueue(_device, indices.GraphicsFamily.Value, 0, out _graphicsQueue);
        _vk.GetDeviceQueue(_device, indices.PresentFamily.Value, 0, out _presentQueue);

        _logger.LogInformation("Logical device created");
    }

    private void CreateSwapchain()
    {
        _logger.LogDebug("Creating swapchain");

        var support = QuerySwapchainSupport(_physicalDevice);
        var surfaceFormat = ChooseSwapSurfaceFormat(support.Formats);
        var presentMode = ChooseSwapPresentMode(support.PresentModes);
        var extent = ChooseSwapExtent(support.Capabilities);

        uint imageCount = support.Capabilities.MinImageCount + 1;
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
            ImageUsage = ImageUsageFlags.ColorAttachmentBit
        };

        var indices = FindQueueFamilies(_physicalDevice);

        if (indices.GraphicsFamily != indices.PresentFamily)
        {
            var queueFamilyIndices = new[] { indices.GraphicsFamily!.Value, indices.PresentFamily!.Value };
            fixed (uint* p = queueFamilyIndices)
            {
                createInfo.ImageSharingMode = SharingMode.Concurrent;
                createInfo.QueueFamilyIndexCount = 2;
                createInfo.PQueueFamilyIndices = p;
            }
        }
        else
        {
            createInfo.ImageSharingMode = SharingMode.Exclusive;
        }

        createInfo.PreTransform = support.Capabilities.CurrentTransform;
        createInfo.CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr;
        createInfo.PresentMode = presentMode;
        createInfo.Clipped = new Bool32(true);
        createInfo.OldSwapchain = default;

        _khrSwapchain ??= new KhrSwapchain(_vk!.Context);

        var result = _khrSwapchain.CreateSwapchain(_device, &createInfo, null, out _swapchain);
        if (result != Result.Success)
            throw new Exception($"Failed to create swapchain: {result}");

        _khrSwapchain.GetSwapchainImages(_device, _swapchain, &imageCount, null);
        _swapchainImages = new Image[imageCount];
        fixed (Image* pImages = _swapchainImages)
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, &imageCount, pImages);

        _swapchainExtent = extent;
        _swapchainImageViews = new ImageView[imageCount];

        for (var i = 0u; i < imageCount; i++)
        {
            var imageViewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = surfaceFormat.Format,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1
                }
            };

            result = _vk!.CreateImageView(_device, &imageViewInfo, null, out _swapchainImageViews[i]);
            if (result != Result.Success)
                throw new Exception($"Failed to create swapchain image view: {result}");
        }

        _logger.LogInformation("Swapchain created ({Width}x{Height}, {Count} images)", extent.Width, extent.Height, imageCount);
    }

    private void RecreateSwapchain()
    {
        var size = _window!.Size;
        while (size.X == 0 || size.Y == 0)
        {
            size = _window.Size;
            _window.DoEvents();
        }

        _vk!.DeviceWaitIdle(_device);
        CleanupSwapchain();
        CreateSwapchain();
        CreateFramebuffers();
    }

    private void CleanupSwapchain()
    {
        if (_framebuffers != null)
            foreach (var fb in _framebuffers)
                _vk!.DestroyFramebuffer(_device, fb, null);

        if (_swapchainImageViews != null)
            foreach (var iv in _swapchainImageViews)
                _vk!.DestroyImageView(_device, iv, null);

        _khrSwapchain?.DestroySwapchain(_device, _swapchain, null);
    }

    private void CreateRenderPass()
    {
        _logger.LogDebug("Creating render pass");

        var colorAttachment = new AttachmentDescription
        {
            Format = GetSwapchainImageFormat(),
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr
        };

        var colorAttachmentRef = new AttachmentReference
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal
        };

        var subpassDescription = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef
        };

        var subpassDependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit
        };

        var renderPassInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &colorAttachment,
            SubpassCount = 1,
            PSubpasses = &subpassDescription,
            DependencyCount = 1,
            PDependencies = &subpassDependency
        };

        var result = _vk!.CreateRenderPass(_device, &renderPassInfo, null, out _renderPass);
        if (result != Result.Success)
            throw new Exception($"Failed to create render pass: {result}");

        _logger.LogInformation("Render pass created");
    }

    private void CreateFramebuffers()
    {
        _logger.LogDebug("Creating framebuffers");

        _framebuffers = new Framebuffer[_swapchainImageViews!.Length];

        for (var i = 0; i < _swapchainImageViews.Length; i++)
        {
            var imageView = _swapchainImageViews[i];
            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 1,
                PAttachments = &imageView,
                Width = _swapchainExtent.Width,
                Height = _swapchainExtent.Height,
                Layers = 1
            };

            var result = _vk!.CreateFramebuffer(_device, &framebufferInfo, null, out _framebuffers[i]);
            if (result != Result.Success)
                throw new Exception($"Failed to create framebuffer: {result}");
        }

        _logger.LogInformation("Framebuffers created");
    }

    private void CreateCommandPool()
    {
        _logger.LogDebug("Creating command pool");

        var indices = FindQueueFamilies(_physicalDevice);
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = indices.GraphicsFamily!.Value
        };

        var result = _vk!.CreateCommandPool(_device, &poolInfo, null, out _commandPool);
        if (result != Result.Success)
            throw new Exception($"Failed to create command pool: {result}");

        _logger.LogInformation("Command pool created");
    }

    private void CreateCommandBuffers()
    {
        _logger.LogDebug("Creating command buffers");

        _commandBuffers = new CommandBuffer[_framebuffers!.Length];
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)_commandBuffers.Length
        };

        fixed (CommandBuffer* pBuffers = _commandBuffers)
        {
            var result = _vk!.AllocateCommandBuffers(_device, &allocInfo, pBuffers);
            if (result != Result.Success)
                throw new Exception($"Failed to allocate command buffers: {result}");
        }

        _logger.LogInformation("Command buffers allocated");
    }

    private void CreateSyncObjects()
    {
        _logger.LogDebug("Creating sync objects");

        _imageAvailableSemaphores = new Silk.NET.Vulkan.Semaphore[MaxFramesInFlight];
        _renderFinishedSemaphores = new Silk.NET.Vulkan.Semaphore[MaxFramesInFlight];
        _inFlightFences = new Fence[MaxFramesInFlight];

        var semaphoreInfo = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo
        };

        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit
        };

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            if (_vk!.CreateSemaphore(_device, &semaphoreInfo, null, out _imageAvailableSemaphores[i]) != Result.Success)
                throw new Exception("Failed to create image available semaphore");
            if (_vk.CreateSemaphore(_device, &semaphoreInfo, null, out _renderFinishedSemaphores[i]) != Result.Success)
                throw new Exception("Failed to create render finished semaphore");
            if (_vk.CreateFence(_device, &fenceInfo, null, out _inFlightFences[i]) != Result.Success)
                throw new Exception("Failed to create in-flight fence");
        }

        _logger.LogInformation("Sync objects created");
    }

    private void DrawFrame()
    {
        var inFlightFence = _inFlightFences![_currentFrame];
        _vk!.WaitForFences(_device, 1, &inFlightFence, new Bool32(true), ulong.MaxValue);

        uint imageIndex = 0;
        var imageAvailableSemaphore = _imageAvailableSemaphores![_currentFrame];
        var result = _khrSwapchain!.AcquireNextImage(_device, _swapchain, ulong.MaxValue, imageAvailableSemaphore, default, &imageIndex);

        if (result == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            return;
        }

        _vk.ResetFences(_device, 1, &inFlightFence);

        RecordCommandBuffer(_commandBuffers![imageIndex], imageIndex);

        var waitSemaphores = stackalloc[] { imageAvailableSemaphore };
        var waitStages = stackalloc[] { PipelineStageFlags.ColorAttachmentOutputBit };
        var signalSemaphores = stackalloc[] { _renderFinishedSemaphores![_currentFrame] };

        var cmdBuffer = _commandBuffers[imageIndex];
        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = waitSemaphores,
            PWaitDstStageMask = waitStages,
            CommandBufferCount = 1,
            PCommandBuffers = &cmdBuffer,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = signalSemaphores
        };

        _vk.QueueSubmit(_graphicsQueue, 1, &submitInfo, inFlightFence);

        var swapchain = _swapchain;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = signalSemaphores,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex
        };

        result = _khrSwapchain.QueuePresent(_presentQueue, &presentInfo);

        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr || _framebufferResized)
        {
            _framebufferResized = false;
            RecreateSwapchain();
        }

        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
    }

    private void RecordCommandBuffer(CommandBuffer commandBuffer, uint imageIndex)
    {
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo
        };

        _vk!.BeginCommandBuffer(commandBuffer, &beginInfo);

        var clearColor = new ClearValue
        {
            Color = new ClearColorValue { Float32_0 = 0.0f, Float32_1 = 0.0f, Float32_2 = 0.0f, Float32_3 = 1.0f }
        };

        var renderPassInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffers![imageIndex],
            RenderArea = new Rect2D { Offset = new Offset2D { X = 0, Y = 0 }, Extent = _swapchainExtent },
            ClearValueCount = 1,
            PClearValues = &clearColor
        };

        _vk.CmdBeginRenderPass(commandBuffer, &renderPassInfo, SubpassContents.Inline);
        _vk.CmdEndRenderPass(commandBuffer);

        var result = _vk.EndCommandBuffer(commandBuffer);
        if (result != Result.Success)
            throw new Exception($"Failed to record command buffer: {result}");
    }

    private Format GetSwapchainImageFormat()
    {
        var support = QuerySwapchainSupport(_physicalDevice);
        var surfaceFormat = ChooseSwapSurfaceFormat(support.Formats);
        return surfaceFormat.Format;
    }

    private SurfaceFormatKHR ChooseSwapSurfaceFormat(SurfaceFormatKHR[] available)
    {
        foreach (var format in available)
            if (format.Format == Format.B8G8R8A8Srgb && format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                return format;
        return available[0];
    }

    private PresentModeKHR ChooseSwapPresentMode(PresentModeKHR[] available)
    {
        foreach (var mode in available)
            if (mode == PresentModeKHR.MailboxKhr)
                return mode;
        return PresentModeKHR.FifoKhr;
    }

    private Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
            return capabilities.CurrentExtent;

        var size = _window!.Size;
        var extent = new Extent2D
        {
            Width = (uint)Math.Clamp(size.X, (int)capabilities.MinImageExtent.Width, (int)capabilities.MaxImageExtent.Width),
            Height = (uint)Math.Clamp(size.Y, (int)capabilities.MinImageExtent.Height, (int)capabilities.MaxImageExtent.Height)
        };
        return extent;
    }

    private SwapchainSupportDetails QuerySwapchainSupport(PhysicalDevice device)
    {
        var details = new SwapchainSupportDetails();
        _khrSurface!.GetPhysicalDeviceSurfaceCapabilities(device, _surface, out details.Capabilities);

        uint formatCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(device, _surface, &formatCount, null);
        details.Formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* pFormats = details.Formats)
            _khrSurface.GetPhysicalDeviceSurfaceFormats(device, _surface, &formatCount, pFormats);

        uint presentModeCount = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(device, _surface, &presentModeCount, null);
        details.PresentModes = new PresentModeKHR[presentModeCount];
        fixed (PresentModeKHR* pModes = details.PresentModes)
            _khrSurface.GetPhysicalDeviceSurfacePresentModes(device, _surface, &presentModeCount, pModes);

        return details;
    }

    public void Run()
    {
        _logger.LogInformation("Entering main loop...");
        _window?.Run();
    }

    public void Shutdown()
    {
        _logger.LogInformation("Shutting down...");

        _vk?.DeviceWaitIdle(_device);

        CleanupSwapchain();

        if (_inFlightFences != null)
            foreach (var f in _inFlightFences)
                _vk?.DestroyFence(_device, f, null);

        if (_imageAvailableSemaphores != null)
            foreach (var s in _imageAvailableSemaphores)
                _vk?.DestroySemaphore(_device, s, null);

        if (_renderFinishedSemaphores != null)
            foreach (var s in _renderFinishedSemaphores)
                _vk?.DestroySemaphore(_device, s, null);

        _vk?.DestroyCommandPool(_device, _commandPool, null);
        _vk?.DestroyRenderPass(_device, _renderPass, null);
        _khrSwapchain?.DestroySwapchain(_device, _swapchain, null);
        _khrSurface?.DestroySurface(_instance, _surface, null);
        _vk?.DestroyDevice(_device, null);
        _vk?.DestroyInstance(_instance, null);

        _logger.LogInformation("Shutdown complete");
    }

    public void ProcessEvents()
    {
        _window?.DoEvents();
    }

    public void Dispose()
    {
        Shutdown();
        _window?.Dispose();
    }

    private struct QueueFamilyIndices
    {
        public uint? GraphicsFamily;
        public uint? PresentFamily;
    }

    private struct SwapchainSupportDetails
    {
        public SurfaceCapabilitiesKHR Capabilities;
        public SurfaceFormatKHR[] Formats;
        public PresentModeKHR[] PresentModes;
    }
}
