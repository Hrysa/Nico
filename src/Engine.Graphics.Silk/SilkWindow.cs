using System.Numerics;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Input;
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
    private IInputContext? _input;
    private IMouse? _mouse;
    private IKeyboard? _keyboard;

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
    private bool _framebufferResized;

    private Vk? _vk;

    // Render graph for per-pass command buffers
    private RenderGraph? _renderGraph;

    // New: Shader modules
    private ShaderModule _vertShaderModule;
    private ShaderModule _fragShaderModule;

    // New: Graphics pipeline
    private PipelineLayout _pipelineLayout;
    private Pipeline _graphicsPipeline;
    private Pipeline _fboGraphicsPipeline;

    // New: Vertex buffer
    private Silk.NET.Vulkan.Buffer _vertexBuffer;
    private DeviceMemory _vertexBufferMemory;
    private Vertex[] _vertices = [];
    private uint _vertexCount;
    private PushConstants _pushConstants;

    // New: Uniform buffer + descriptor set
    private DescriptorSetLayout _descriptorSetLayout;
    private DescriptorPool _descriptorPool;
    private DescriptorSet _descriptorSet;
    private Silk.NET.Vulkan.Buffer _uniformBuffer;
    private DeviceMemory _uniformBufferMemory;
    private void* _uniformBufferMapped;

    // Viewport FBO management
    private readonly Dictionary<uint, ViewportFbo> _viewportFbos = new();
    private readonly Dictionary<uint, Action<ViewportRenderContext>> _viewportRenderCallbacks = new();
    private readonly Dictionary<uint, (Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory, uint vertexCount)> _viewportQuadBuffers = new();
    private readonly Dictionary<uint, List<(Vertex[] vertices, PushConstants pushConstants)>> _pendingViewportDraws = new();
    private uint _nextViewportId = 1;
    private RenderPass _fboRenderPass;

    // Persistent buffer for viewport draws (reused each frame)
    private Silk.NET.Vulkan.Buffer _viewportDrawBuffer;
    private DeviceMemory _viewportDrawBufferMemory;
    private void* _viewportDrawBufferMapped;
    private uint _viewportDrawBufferCapacity;

    // Shared texture pipeline resources
    private DescriptorSetLayout _textureDescriptorSetLayout;
    private DescriptorPool _textureDescriptorPool;

    // Textured quad pipeline
    private ShaderModule _textureVertShaderModule;
    private ShaderModule _textureFragShaderModule;
    private PipelineLayout _texturePipelineLayout;
    private Pipeline _texturePipeline;

    // FBO pipeline
    private PipelineLayout _fboPipelineLayout;

    public bool IsRunning => _window != null && !_window.IsClosing;

    /// <inheritdoc/>
    public event Action<Vector2>? MouseMove;

    /// <inheritdoc/>
    public event Action<int>? MouseDown;

    /// <inheritdoc/>
    public event Action<int>? MouseUp;

    /// <inheritdoc/>
    public event Action<int>? MouseDoubleClick;

    /// <inheritdoc/>
    public event Action<float>? MouseScroll;

    /// <inheritdoc/>
    public event Action<int>? KeyDown;

    /// <inheritdoc/>
    public event Action<int>? KeyUp;

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

    public void SetVertices(Vertex[] vertices)
    {
        _vertices = vertices;
        _vertexCount = (uint)vertices.Length;
    }

    public void SetPushConstants(PushConstants pushConstants)
    {
        _pushConstants = pushConstants;
    }

    private void OnLoad()
    {
        _logger.LogInformation("Window.Load fired — initializing Vulkan");

        _input = _window!.CreateInput();
        _mouse = _input.Mice.Count > 0 ? _input.Mice[0] : null;
        _keyboard = _input.Keyboards.Count > 0 ? _input.Keyboards[0] : null;

        if (_mouse != null)
        {
            _mouse.MouseMove += OnMouseMove;
            _mouse.MouseDown += OnMouseDown;
            _mouse.MouseUp += OnMouseUp;
            _mouse.DoubleClick += OnMouseDoubleClick;
            _mouse.Scroll += OnMouseScroll;
            _logger.LogInformation("Mouse input attached");
        }

        if (_keyboard != null)
        {
            _keyboard.KeyDown += OnKeyDown;
            _keyboard.KeyUp += OnKeyUp;
            _logger.LogInformation("Keyboard input attached");
        }

        _vk = Vk.GetApi();
        CreateInstance();
        CreateSurface();
        PickPhysicalDevice();
        CreateLogicalDevice();
        _renderGraph = new RenderGraph(_vk!, _device, _graphicsQueue, FindQueueFamilies(_physicalDevice).GraphicsFamily!.Value);
        CreateSwapchain();
        CreateFboRenderPass();
        CreateRenderPass();
        CreateGraphicsPipeline();
        CreateFboGraphicsPipeline();
        CreateFramebuffers();
        CreateCommandPool();
        CreateCommandBuffers();
        CreateSyncObjects();
        CreateTexturePipeline();

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

    private void OnMouseMove(IMouse mouse, Vector2 pos)
    {
        MouseMove?.Invoke(new Vector2(pos.X, pos.Y));
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        MouseDown?.Invoke((int)button);
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        MouseUp?.Invoke((int)button);
    }

    private void OnMouseDoubleClick(IMouse mouse, MouseButton button, Vector2 pos)
    {
        MouseDoubleClick?.Invoke((int)button);
    }

    private void OnMouseScroll(IMouse mouse, ScrollWheel scroll)
    {
        MouseScroll?.Invoke(scroll.Y);
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int keyCode)
    {
        KeyDown?.Invoke((int)key);
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int keyCode)
    {
        KeyUp?.Invoke((int)key);
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

    private void CreateGraphicsPipeline()
    {
        _logger.LogDebug("Creating graphics pipeline");

        var vertCode = LoadSpirV("basic.vert.spv");
        var fragCode = LoadSpirV("basic.frag.spv");

        fixed (uint* pVertCode = vertCode)
        {
            var vertModuleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(vertCode.Length * sizeof(uint)),
                PCode = pVertCode
            };
            var result = _vk!.CreateShaderModule(_device, &vertModuleInfo, null, out _vertShaderModule);
            if (result != Result.Success)
                throw new Exception($"Failed to create vertex shader module: {result}");
        }

        fixed (uint* pFragCode = fragCode)
        {
            var fragModuleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(fragCode.Length * sizeof(uint)),
                PCode = pFragCode
            };
            var result = _vk!.CreateShaderModule(_device, &fragModuleInfo, null, out _fragShaderModule);
            if (result != Result.Success)
                throw new Exception($"Failed to create fragment shader module: {result}");
        }

        var entryPointName = SilkMarshal.StringToPtr("main", NativeStringEncoding.UTF8);

        var vertShaderStageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = _vertShaderModule,
            PName = (byte*)entryPointName
        };

        var fragShaderStageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = _fragShaderModule,
            PName = (byte*)entryPointName
        };

        var shaderStages = new[] { vertShaderStageInfo, fragShaderStageInfo };

        // Vertex input: binding 0 (stride = 6 floats = vec3 pos + vec3 color)
        var vertexInputBinding = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = Vertex.Stride,
            InputRate = VertexInputRate.Vertex
        };

        var vertexInputAttributes = new VertexInputAttributeDescription[2];
        vertexInputAttributes[0] = new VertexInputAttributeDescription
        {
            Binding = 0,
            Location = 0,
            Format = Format.R32G32B32Sfloat, // vec3
            Offset = 0
        };
        vertexInputAttributes[1] = new VertexInputAttributeDescription
        {
            Binding = 0,
            Location = 1,
            Format = Format.R32G32B32Sfloat, // vec3
            Offset = (uint)(sizeof(float) * 3)
        };

        fixed (VertexInputAttributeDescription* pAttributes = vertexInputAttributes)
        {
            var vertexInputInfo = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &vertexInputBinding,
                VertexAttributeDescriptionCount = 2,
                PVertexAttributeDescriptions = pAttributes
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = new Bool32(false)
            };

            // Dynamic viewport and scissor
            var dynamicStates = new[] { DynamicState.Viewport, DynamicState.Scissor };
            fixed (DynamicState* pDynamicStates = dynamicStates)
            {
                var dynamicStateInfo = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 2,
                    PDynamicStates = pDynamicStates
                };

                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount = 1
                };

                var rasterizer = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    DepthClampEnable = new Bool32(false),
                    RasterizerDiscardEnable = new Bool32(false),
                    PolygonMode = PolygonMode.Fill,
                    LineWidth = 1.0f,
                    CullMode = CullModeFlags.BackBit,
                    FrontFace = FrontFace.CounterClockwise,
                    DepthBiasEnable = new Bool32(false)
                };

                var multisampling = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    SampleShadingEnable = new Bool32(false),
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };

                var colorBlendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                    BlendEnable = new Bool32(false)
                };

                var colorBlending = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOpEnable = new Bool32(false),
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment
                };

                // Push constant range for MVP matrices
                var pushConstantRange = new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.VertexBit,
                    Offset = 0,
                    Size = (uint)sizeof(PushConstants)
                };

                var pipelineLayoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushConstantRange
                };

                var result = _vk!.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _pipelineLayout);
                if (result != Result.Success)
                    throw new Exception($"Failed to create pipeline layout: {result}");

                // Create graphics pipeline
                fixed (PipelineShaderStageCreateInfo* pStages = shaderStages)
                {
                    var pipelineInfo = new GraphicsPipelineCreateInfo
                    {
                        SType = StructureType.GraphicsPipelineCreateInfo,
                        StageCount = 2,
                        PStages = pStages,
                        PVertexInputState = &vertexInputInfo,
                        PInputAssemblyState = &inputAssembly,
                        PViewportState = &viewportState,
                        PRasterizationState = &rasterizer,
                        PMultisampleState = &multisampling,
                        PColorBlendState = &colorBlending,
                        PDynamicState = &dynamicStateInfo,
                        Layout = _pipelineLayout,
                        RenderPass = _renderPass,
                        Subpass = 0,
                        BasePipelineHandle = default
                    };

                    result = _vk.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, out _graphicsPipeline);
                    if (result != Result.Success)
                        throw new Exception($"Failed to create graphics pipeline: {result}");
                }
            }
        }

        SilkMarshal.Free(entryPointName);

        _logger.LogInformation("Graphics pipeline created");
    }

    private void CreateFboGraphicsPipeline()
    {
        _logger.LogDebug("Creating FBO graphics pipeline");

        // Load same shaders as main pipeline
        var vertCode = LoadSpirV("basic.vert.spv");
        var fragCode = LoadSpirV("basic.frag.spv");

        ShaderModule fboVertModule, fboFragModule;

        fixed (uint* pVertCode = vertCode)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(vertCode.Length * sizeof(uint)),
                PCode = pVertCode
            };
            var r = _vk!.CreateShaderModule(_device, &info, null, out fboVertModule);
            if (r != Result.Success) throw new Exception($"Failed to create FBO vert shader: {r}");
        }

        fixed (uint* pFragCode = fragCode)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(fragCode.Length * sizeof(uint)),
                PCode = pFragCode
            };
            var r = _vk!.CreateShaderModule(_device, &info, null, out fboFragModule);
            if (r != Result.Success) throw new Exception($"Failed to create FBO frag shader: {r}");
        }

        var entryPointName = SilkMarshal.StringToPtr("main", NativeStringEncoding.UTF8);

        var vertStage = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = fboVertModule,
            PName = (byte*)entryPointName
        };

        var fragStage = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fboFragModule,
            PName = (byte*)entryPointName
        };

        var shaderStages = new[] { vertStage, fragStage };

        var vertexInputBinding = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = Vertex.Stride,
            InputRate = VertexInputRate.Vertex
        };

        var vertexInputAttributes = new VertexInputAttributeDescription[2];
        vertexInputAttributes[0] = new VertexInputAttributeDescription
        {
            Binding = 0, Location = 0,
            Format = Format.R32G32B32Sfloat, Offset = 0
        };
        vertexInputAttributes[1] = new VertexInputAttributeDescription
        {
            Binding = 0, Location = 1,
            Format = Format.R32G32B32Sfloat, Offset = (uint)(sizeof(float) * 3)
        };

        fixed (VertexInputAttributeDescription* pAttributes = vertexInputAttributes)
        {
            var vertexInputInfo = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &vertexInputBinding,
                VertexAttributeDescriptionCount = 2,
                PVertexAttributeDescriptions = pAttributes
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = new Bool32(false)
            };

            var dynamicStates = new[] { DynamicState.Viewport, DynamicState.Scissor };
            fixed (DynamicState* pDynamicStates = dynamicStates)
            {
                var dynamicStateInfo = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 2,
                    PDynamicStates = pDynamicStates
                };

                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount = 1
                };

                var rasterizer = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    DepthClampEnable = new Bool32(false),
                    RasterizerDiscardEnable = new Bool32(false),
                    PolygonMode = PolygonMode.Fill,
                    LineWidth = 1.0f,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    DepthBiasEnable = new Bool32(false)
                };

                var multisampling = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    SampleShadingEnable = new Bool32(false),
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };

                var depthStencilState = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = new Bool32(true),
                    DepthWriteEnable = new Bool32(true),
                    DepthCompareOp = CompareOp.LessOrEqual,
                    DepthBoundsTestEnable = new Bool32(false),
                    StencilTestEnable = new Bool32(false)
                };

                var colorBlendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                    BlendEnable = new Bool32(false)
                };

                var colorBlending = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOpEnable = new Bool32(false),
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment
                };

                var pushConstantRange = new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.VertexBit,
                    Offset = 0,
                    Size = (uint)sizeof(PushConstants)
                };

                var pipelineLayoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushConstantRange
                };

                var r = _vk!.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _fboPipelineLayout);
                if (r != Result.Success) throw new Exception($"Failed to create FBO pipeline layout: {r}");

                fixed (PipelineShaderStageCreateInfo* pStages = shaderStages)
                {
                    var pipelineInfo = new GraphicsPipelineCreateInfo
                    {
                        SType = StructureType.GraphicsPipelineCreateInfo,
                        StageCount = 2,
                        PStages = pStages,
                        PVertexInputState = &vertexInputInfo,
                        PInputAssemblyState = &inputAssembly,
                        PViewportState = &viewportState,
                        PRasterizationState = &rasterizer,
                        PMultisampleState = &multisampling,
                        PDepthStencilState = &depthStencilState,
                        PColorBlendState = &colorBlending,
                        PDynamicState = &dynamicStateInfo,
                        Layout = _fboPipelineLayout,
                        RenderPass = _fboRenderPass,
                        Subpass = 0,
                        BasePipelineHandle = default
                    };

                    r = _vk.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, out _fboGraphicsPipeline);
                    if (r != Result.Success) throw new Exception($"Failed to create FBO graphics pipeline: {r}");
                }
            }
        }

        SilkMarshal.Free(entryPointName);
        _logger.LogInformation("FBO graphics pipeline created");
    }

    private uint[] LoadSpirV(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var available = assembly.GetManifestResourceNames();
        var match = available.FirstOrDefault(n => n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase))
            ?? throw new Exception($"Embedded resource '{resourceName}' not found. Available: {string.Join(", ", available)}");
        using var stream = assembly.GetManifestResourceStream(match)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        if (bytes.Length % 4 != 0)
            throw new Exception($"SPIR-V bytecode length {bytes.Length} is not a multiple of 4");
        var result = new uint[bytes.Length / 4];
        System.Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
        return result;
    }

    private void CreateFboRenderPass()
    {
        _logger.LogDebug("Creating FBO render pass (color only)");

        var format = GetSwapchainImageFormat();
        var depthFormat = FindDepthFormat();

        var attachments = stackalloc[] { new AttachmentDescription
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
        }, new AttachmentDescription
        {
            Format = depthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal
        } };

        var colorAttachmentRef = new AttachmentReference
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal
        };

        var depthAttachmentRef = new AttachmentReference
        {
            Attachment = 1,
            Layout = ImageLayout.DepthStencilAttachmentOptimal
        };

        var subpassDescription = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef,
            PDepthStencilAttachment = &depthAttachmentRef
        };

        var subpassDependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit
        };

        var renderPassInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpassDescription,
            DependencyCount = 1,
            PDependencies = &subpassDependency
        };

        var result = _vk!.CreateRenderPass(_device, &renderPassInfo, null, out _fboRenderPass);
        if (result != Result.Success)
            throw new Exception($"Failed to create FBO render pass: {result}");

        _logger.LogInformation("FBO render pass created (color + depth)");
    }

    private void CreateTexturePipeline()
    {
        _logger.LogDebug("Creating texture pipeline");

        // Create descriptor set layout for combined image sampler
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit
        };

        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding
        };
        var result = _vk!.CreateDescriptorSetLayout(_device, &layoutInfo, null, out _textureDescriptorSetLayout);
        if (result != Result.Success)
            throw new Exception($"Failed to create texture descriptor set layout: {result}");

        // Create descriptor pool for max viewports
        const uint maxViewports = 16;
        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = maxViewports
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = maxViewports
        };
        result = _vk.CreateDescriptorPool(_device, &poolInfo, null, out _textureDescriptorPool);
        if (result != Result.Success)
            throw new Exception($"Failed to create texture descriptor pool: {result}");

        var vertCode = LoadSpirV("texture.vert.spv");
        var fragCode = LoadSpirV("texture.frag.spv");

        fixed (uint* pVertCode = vertCode)
        {
            var vertModuleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(vertCode.Length * sizeof(uint)),
                PCode = pVertCode
            };
            var vertResult = _vk!.CreateShaderModule(_device, &vertModuleInfo, null, out _textureVertShaderModule);
            if (vertResult != Result.Success)
                throw new Exception($"Failed to create texture vertex shader module: {vertResult}");
        }

        fixed (uint* pFragCode = fragCode)
        {
            var fragModuleInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(fragCode.Length * sizeof(uint)),
                PCode = pFragCode
            };
            var fragResult = _vk!.CreateShaderModule(_device, &fragModuleInfo, null, out _textureFragShaderModule);
            if (fragResult != Result.Success)
                throw new Exception($"Failed to create texture fragment shader module: {fragResult}");
        }

        var entryPointName = SilkMarshal.StringToPtr("main", NativeStringEncoding.UTF8);

        var vertShaderStageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = _textureVertShaderModule,
            PName = (byte*)entryPointName
        };

        var fragShaderStageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = _textureFragShaderModule,
            PName = (byte*)entryPointName
        };

        var shaderStages = new[] { vertShaderStageInfo, fragShaderStageInfo };

        // Vertex input: binding 0 (stride = 5 floats = vec3 pos + vec2 uv)
        var vertexInputBinding = new VertexInputBindingDescription
        {
            Binding = 0,
            Stride = VertexT.Stride,
            InputRate = VertexInputRate.Vertex
        };

        var vertexInputAttributes = new VertexInputAttributeDescription[2];
        vertexInputAttributes[0] = new VertexInputAttributeDescription
        {
            Binding = 0,
            Location = 0,
            Format = Format.R32G32B32Sfloat,
            Offset = 0
        };
        vertexInputAttributes[1] = new VertexInputAttributeDescription
        {
            Binding = 0,
            Location = 1,
            Format = Format.R32G32Sfloat,
            Offset = (uint)(sizeof(float) * 3)
        };

        fixed (VertexInputAttributeDescription* pAttributes = vertexInputAttributes)
        {
            var vertexInputInfo = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &vertexInputBinding,
                VertexAttributeDescriptionCount = 2,
                PVertexAttributeDescriptions = pAttributes
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = new Bool32(false)
            };

            var dynamicStates = new[] { DynamicState.Viewport, DynamicState.Scissor };
            fixed (DynamicState* pDynamicStates = dynamicStates)
            {
                var dynamicStateInfo = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 2,
                    PDynamicStates = pDynamicStates
                };

                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    ScissorCount = 1
                };

                var rasterizer = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    DepthClampEnable = new Bool32(false),
                    RasterizerDiscardEnable = new Bool32(false),
                    PolygonMode = PolygonMode.Fill,
                    LineWidth = 1.0f,
                    CullMode = CullModeFlags.BackBit,
                    FrontFace = FrontFace.CounterClockwise,
                    DepthBiasEnable = new Bool32(false)
                };

                var multisampling = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    SampleShadingEnable = new Bool32(false),
                    RasterizationSamples = SampleCountFlags.Count1Bit
                };

                var colorBlendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                    BlendEnable = new Bool32(false)
                };

                var colorBlending = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    LogicOpEnable = new Bool32(false),
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment
                };

                var pushConstantRange = new PushConstantRange
                {
                    StageFlags = ShaderStageFlags.VertexBit,
                    Offset = 0,
                    Size = (uint)sizeof(PushConstants)
                };

                var pipelineLayoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    PushConstantRangeCount = 1,
                    PPushConstantRanges = &pushConstantRange,
                    SetLayoutCount = 1
                };

                fixed (DescriptorSetLayout* pSetLayout = &_textureDescriptorSetLayout)
                {
                    pipelineLayoutInfo.PSetLayouts = pSetLayout;

                    var layoutResult = _vk!.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _texturePipelineLayout);
                    if (layoutResult != Result.Success)
                        throw new Exception($"Failed to create texture pipeline layout: {layoutResult}");
                }

                fixed (PipelineShaderStageCreateInfo* pStages = shaderStages)
                {
                    var pipelineInfo = new GraphicsPipelineCreateInfo
                    {
                        SType = StructureType.GraphicsPipelineCreateInfo,
                        StageCount = 2,
                        PStages = pStages,
                        PVertexInputState = &vertexInputInfo,
                        PInputAssemblyState = &inputAssembly,
                        PViewportState = &viewportState,
                        PRasterizationState = &rasterizer,
                        PMultisampleState = &multisampling,
                        PColorBlendState = &colorBlending,
                        PDynamicState = &dynamicStateInfo,
                        Layout = _texturePipelineLayout,
                        RenderPass = _renderPass,
                        Subpass = 0,
                        BasePipelineHandle = default
                    };

                    var pipelineResult = _vk.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, out _texturePipeline);
                    if (pipelineResult != Result.Success)
                        throw new Exception($"Failed to create texture graphics pipeline: {pipelineResult}");
                }
            }
        }

        SilkMarshal.Free(entryPointName);

        _logger.LogInformation("Texture pipeline created");
    }

    // ── Viewport FBO Management ────────────────────────────────

    /// <inheritdoc/>
    public uint RegisterViewport(float width, float height)
    {
        var id = _nextViewportId++;
        var fbo = new ViewportFbo(id, (uint)width, (uint)height);
        var deviceLocalMemoryType = FindMemoryType(0xFFFFFFFF, MemoryPropertyFlags.DeviceLocalBit);
        fbo.Create(_vk!, _device, _fboRenderPass, GetSwapchainImageFormat(), FindDepthFormat(),
            deviceLocalMemoryType,
            _textureDescriptorSetLayout, _textureDescriptorPool);
        _viewportFbos[id] = fbo;
        _logger.LogInformation("Viewport {Id} registered ({Width}x{Height})", id, (uint)width, (uint)height);
        return id;
    }

    /// <inheritdoc/>
    public void UnregisterViewport(uint viewportId)
    {
        _vk!.DeviceWaitIdle(_device);

        if (_viewportFbos.TryGetValue(viewportId, out var fbo))
        {
            fbo.Destroy(_vk, _device);
            _viewportFbos.Remove(viewportId);
        }

        if (_viewportQuadBuffers.TryGetValue(viewportId, out var buf))
        {
            _vk.DestroyBuffer(_device, buf.buffer, null);
            _vk.FreeMemory(_device, buf.memory, null);
            _viewportQuadBuffers.Remove(viewportId);
        }

        _viewportRenderCallbacks.Remove(viewportId);
        _logger.LogInformation("Viewport {Id} unregistered", viewportId);
    }

    /// <inheritdoc/>
    public void ResizeViewport(uint viewportId, float width, float height)
    {
        if (_viewportFbos.TryGetValue(viewportId, out var fbo))
            fbo.Resize((uint)width, (uint)height);
    }

    /// <inheritdoc/>
    public void SetViewportRenderCallback(uint viewportId, Action<ViewportRenderContext> callback)
    {
        _viewportRenderCallbacks[viewportId] = callback;
    }

    /// <inheritdoc/>
    public void SetViewportQuadVertices(uint viewportId, VertexT[] vertices)
    {
        _vk!.DeviceWaitIdle(_device);

        // Destroy old buffer if exists
        if (_viewportQuadBuffers.TryGetValue(viewportId, out var old))
        {
            _vk.DestroyBuffer(_device, old.buffer, null);
            _vk.FreeMemory(_device, old.memory, null);
        }

        var vertexCount = (uint)vertices.Length;
        var bufferSize = (nuint)(vertices.Length * VertexT.Stride);

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = bufferSize,
            Usage = BufferUsageFlags.VertexBufferBit,
            SharingMode = SharingMode.Exclusive
        };

        var result = _vk.CreateBuffer(_device, &bufferInfo, null, out var buffer);
        if (result != Result.Success)
            throw new Exception($"Failed to create viewport quad buffer: {result}");

        _vk.GetBufferMemoryRequirements(_device, buffer, out var memRequirements);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };

        result = _vk.AllocateMemory(_device, &allocInfo, null, out var memory);
        if (result != Result.Success)
            throw new Exception($"Failed to allocate viewport quad memory: {result}");

        _vk.BindBufferMemory(_device, buffer, memory, 0);

        void* data;
        _vk.MapMemory(_device, memory, 0, bufferSize, 0, &data);
        fixed (VertexT* pVertices = vertices)
            System.Buffer.MemoryCopy(pVertices, data, bufferSize, bufferSize);
        _vk.UnmapMemory(_device, memory);

        _viewportQuadBuffers[viewportId] = (buffer, memory, vertexCount);
        _logger.LogDebug("Viewport {Id} quad vertices set ({Count} vertices)", viewportId, vertexCount);
    }

    /// <inheritdoc/>
    public ViewportRenderContext CreateRenderContext(uint viewportId)
    {
        var fbo = _viewportFbos[viewportId];
        return new ViewportRenderContext
        {
            ViewportId = viewportId,
            Width = fbo.Width,
            Height = fbo.Height
        };
    }

    /// <inheritdoc/>
    public void DrawInViewport(uint viewportId, Vertex[] vertices, PushConstants pushConstants)
    {
        if (!_pendingViewportDraws.ContainsKey(viewportId))
            _pendingViewportDraws[viewportId] = new List<(Vertex[], PushConstants)>();
        _pendingViewportDraws[viewportId].Add((vertices, pushConstants));
    }

    /// <inheritdoc/>
    public void SetViewportClearColor(uint viewportId, float r, float g, float b, float a = 1.0f)
    {
        if (_viewportFbos.TryGetValue(viewportId, out var fbo))
            fbo.ClearColor = new Vector4(r, g, b, a);
    }

    private void EnsureViewportDrawBuffer(uint requiredVertices)
    {
        if (_viewportDrawBufferCapacity >= requiredVertices)
            return;

        _vk!.DeviceWaitIdle(_device);

        // Destroy old buffer
        if (_viewportDrawBuffer.Handle != 0)
        {
            _vk.DestroyBuffer(_device, _viewportDrawBuffer, null);
            _vk.FreeMemory(_device, _viewportDrawBufferMemory, null);
        }

        // Create new larger buffer
        _viewportDrawBufferCapacity = Math.Max(requiredVertices, 1024);
        var bufferSize = (nuint)(_viewportDrawBufferCapacity * Vertex.Stride);

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = bufferSize,
            Usage = BufferUsageFlags.VertexBufferBit,
            SharingMode = SharingMode.Exclusive
        };
        _vk.CreateBuffer(_device, &bufferInfo, null, out _viewportDrawBuffer);

        _vk.GetBufferMemoryRequirements(_device, _viewportDrawBuffer, out var memReqs);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReqs.Size,
            MemoryTypeIndex = FindMemoryType(memReqs.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };
        _vk.AllocateMemory(_device, &allocInfo, null, out _viewportDrawBufferMemory);
        _vk.BindBufferMemory(_device, _viewportDrawBuffer, _viewportDrawBufferMemory, 0);

        void* mapped;
        _vk.MapMemory(_device, _viewportDrawBufferMemory, 0, bufferSize, 0, &mapped);
        _viewportDrawBufferMapped = mapped;

        _logger.LogDebug("Viewport draw buffer created/recreated ({Capacity} vertices)", _viewportDrawBufferCapacity);
    }

    private void RecreateDirtyFbos()
    {
        var deviceLocalMemoryType = FindMemoryType(0xFFFFFFFF, MemoryPropertyFlags.DeviceLocalBit);
        foreach (var (id, fbo) in _viewportFbos)
        {
            if (fbo.IsDirty)
            {
                _logger.LogDebug("Recreating viewport {Id} FBO ({Width}x{Height})", id, fbo.Width, fbo.Height);
                fbo.Recreate(_vk!, _device, _fboRenderPass, GetSwapchainImageFormat(), FindDepthFormat(),
                    deviceLocalMemoryType,
                    _textureDescriptorSetLayout, _textureDescriptorPool);
            }
        }
    }

    // ── Old method removed — now per-viewport via SetViewportQuadVertices ──

    public void CreateVertexBuffer()
    {
        _logger.LogDebug("Creating vertex buffer");

        var bufferSize = (nuint)(_vertices.Length * Vertex.Stride);

        // Create buffer
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = bufferSize,
            Usage = BufferUsageFlags.VertexBufferBit,
            SharingMode = SharingMode.Exclusive
        };

        var result = _vk!.CreateBuffer(_device, &bufferInfo, null, out _vertexBuffer);
        if (result != Result.Success)
            throw new Exception($"Failed to create vertex buffer: {result}");

        // Query memory requirements
        _vk.GetBufferMemoryRequirements(_device, _vertexBuffer, out var memRequirements);

        // Find suitable memory type
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };

        result = _vk.AllocateMemory(_device, &allocInfo, null, out _vertexBufferMemory);
        if (result != Result.Success)
            throw new Exception($"Failed to allocate vertex buffer memory: {result}");

        _vk.BindBufferMemory(_device, _vertexBuffer, _vertexBufferMemory, 0);

        // Map and copy vertex data
        void* data;
        _vk.MapMemory(_device, _vertexBufferMemory, 0, bufferSize, 0, &data);
        fixed (Vertex* pVertices = _vertices)
            System.Buffer.MemoryCopy(pVertices, data, bufferSize, bufferSize);
        _vk.UnmapMemory(_device, _vertexBufferMemory);

        _logger.LogInformation("Vertex buffer created ({Size} bytes)", bufferSize);
    }

    public void UpdateVertexBuffer(Vertex[] vertices)
    {
        _vertices = vertices;
        _vertexCount = (uint)vertices.Length;

        var bufferSize = (nuint)(vertices.Length * Vertex.Stride);
        void* data;
        _vk!.MapMemory(_device, _vertexBufferMemory, 0, bufferSize, 0, &data);
        fixed (Vertex* pVertices = _vertices)
            System.Buffer.MemoryCopy(pVertices, data, bufferSize, bufferSize);
        _vk.UnmapMemory(_device, _vertexBufferMemory);
    }

    public void CreateUniformBuffer()
    {
        _logger.LogDebug("Creating uniform buffer");

        var bufferSize = (nuint)sizeof(PushConstants);

        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = bufferSize,
            Usage = BufferUsageFlags.UniformBufferBit,
            SharingMode = SharingMode.Exclusive
        };

        var result = _vk!.CreateBuffer(_device, &bufferInfo, null, out _uniformBuffer);
        if (result != Result.Success)
            throw new Exception($"Failed to create uniform buffer: {result}");

        _vk.GetBufferMemoryRequirements(_device, _uniformBuffer, out var memRequirements);

        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memRequirements.Size,
            MemoryTypeIndex = FindMemoryType(memRequirements.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit)
        };

        result = _vk.AllocateMemory(_device, &allocInfo, null, out _uniformBufferMemory);
        if (result != Result.Success)
            throw new Exception($"Failed to allocate uniform buffer memory: {result}");

        _vk.BindBufferMemory(_device, _uniformBuffer, _uniformBufferMemory, 0);
        void* mapped;
        _vk.MapMemory(_device, _uniformBufferMemory, 0, bufferSize, 0, &mapped);
        _uniformBufferMapped = mapped;

        _logger.LogInformation("Uniform buffer created ({Size} bytes)", bufferSize);
    }

    public void CreateDescriptorResources()
    {
        _logger.LogDebug("Creating descriptor resources");

        // Descriptor pool
        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.UniformBuffer,
            DescriptorCount = 1
        };

        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = 1
        };

        var result = _vk!.CreateDescriptorPool(_device, &poolInfo, null, out _descriptorPool);
        if (result != Result.Success)
            throw new Exception($"Failed to create descriptor pool: {result}");

        // Descriptor set
        var setLayout = _descriptorSetLayout;
        var setInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _descriptorPool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout
        };

        result = _vk.AllocateDescriptorSets(_device, &setInfo, out _descriptorSet);
        if (result != Result.Success)
            throw new Exception($"Failed to allocate descriptor set: {result}");

        // Update descriptor set
        var bufferInfo = new DescriptorBufferInfo
        {
            Buffer = _uniformBuffer,
            Offset = 0,
            Range = (nuint)sizeof(PushConstants)
        };

        var writeDescriptor = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _descriptorSet,
            DstBinding = 0,
            DstArrayElement = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            PBufferInfo = &bufferInfo
        };

        _vk.UpdateDescriptorSets(_device, 1, &writeDescriptor, 0, null);

        _logger.LogInformation("Descriptor resources created");
    }

    public void UpdateUniformBuffer()
    {
        fixed (PushConstants* pPush = &_pushConstants)
            System.Buffer.MemoryCopy(pPush, _uniformBufferMapped, sizeof(PushConstants), sizeof(PushConstants));
    }

    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        _vk!.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var memProperties);

        for (var i = 0; i < (int)memProperties.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1 << i)) != 0 &&
                ((uint)memProperties.MemoryTypes[i].PropertyFlags & (uint)properties) == (uint)properties)
                return (uint)i;
        }

        throw new Exception("Failed to find suitable memory type");
    }

    private Format FindDepthFormat()
    {
        var candidates = new[] { Format.D32Sfloat, Format.D32SfloatS8Uint, Format.D24UnormS8Uint };
        foreach (var format in candidates)
        {
            _vk!.GetPhysicalDeviceFormatProperties(_physicalDevice, format, out var props);
            if ((props.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0)
                return format;
        }
        throw new Exception("Failed to find suitable depth format");
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

        // Only imageAvailable semaphores are needed here (for swapchain image acquisition).
        // RenderGraph manages its own fences and inter-pass semaphores.
        _imageAvailableSemaphores = new Silk.NET.Vulkan.Semaphore[MaxFramesInFlight];

        var semaphoreInfo = new SemaphoreCreateInfo
        {
            SType = StructureType.SemaphoreCreateInfo
        };

        for (var i = 0; i < MaxFramesInFlight; i++)
        {
            if (_vk!.CreateSemaphore(_device, &semaphoreInfo, null, out _imageAvailableSemaphores[i]) != Result.Success)
                throw new Exception("Failed to create image available semaphore");
        }

        _logger.LogInformation("Sync objects created");
    }

    private void DrawFrame()
    {
        // Recreate any dirty viewport FBOs
        RecreateDirtyFbos();

        // ── Begin frame (waits for previous frame's fence) ──
        var frameIndex = _renderGraph!.BeginFrame();

        // ── Acquire swapchain image ──
        uint imageIndex = 0;
        var imageAvailableSemaphore = _imageAvailableSemaphores![frameIndex];
        var result = _khrSwapchain!.AcquireNextImage(_device, _swapchain, ulong.MaxValue, imageAvailableSemaphore, default, &imageIndex);

        if (result == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            _renderGraph.EndFrame();
            return;
        }

        // ── Pass 1: Render viewport content into FBOs ──
        Silk.NET.Vulkan.Semaphore pass1Semaphore;
        {
            var (cmdBuffer, sem) = _renderGraph.BeginPass();
            pass1Semaphore = sem;

            RecordFboPass(cmdBuffer);

            _renderGraph.EndPass(cmdBuffer);
            _renderGraph.SubmitPass(cmdBuffer, imageAvailableSemaphore, sem, default);
        }

        // ── Pass 2: Render editor UI + viewport quads to swapchain ──
        Silk.NET.Vulkan.Semaphore pass2Semaphore;
        {
            var (cmdBuffer, sem) = _renderGraph.BeginPass();
            pass2Semaphore = sem;

            RecordSwapchainPass(cmdBuffer, imageIndex);

            _renderGraph.EndPass(cmdBuffer);
            _renderGraph.SubmitPass(cmdBuffer, pass1Semaphore, sem, _renderGraph.GetCurrentFence());
        }

        // ── Present ──
        var swapchain = _swapchain;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &pass2Semaphore,
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

        _renderGraph.EndFrame();
    }

    private void RecordFboPass(CommandBuffer commandBuffer)
    {
        // ═══════════════════════════════════════════════════════════════
        // Render each viewport's content into its own FBO
        // ═══════════════════════════════════════════════════════════════

        // First pass: count total vertices across all viewports and invoke callbacks
        uint totalVertices = 0;
        foreach (var (viewportId, fbo) in _viewportFbos)
        {
            if (fbo.IsDirty)
                continue;

            if (_viewportRenderCallbacks.TryGetValue(viewportId, out var callback))
            {
                var context = CreateRenderContext(viewportId);
                callback(context);
            }

            if (_pendingViewportDraws.TryGetValue(viewportId, out var draws))
            {
                foreach (var (verts, _) in draws)
                    totalVertices += (uint)verts.Length;
            }
        }

        if (totalVertices > 0)
            EnsureViewportDrawBuffer(totalVertices);

        // Second pass: record draw commands into the shared buffer
        uint vertexOffset = 0;
        foreach (var (viewportId, fbo) in _viewportFbos)
        {
            if (fbo.IsDirty)
                continue;

            var clearValues = stackalloc ClearValue[2];
            clearValues[0] = new ClearValue
            {
                Color = new ClearColorValue
                {
                    Float32_0 = fbo.ClearColor.X,
                    Float32_1 = fbo.ClearColor.Y,
                    Float32_2 = fbo.ClearColor.Z,
                    Float32_3 = fbo.ClearColor.W
                }
            };
            clearValues[1] = new ClearValue
            {
                DepthStencil = new ClearDepthStencilValue { Depth = 1.0f, Stencil = 0 }
            };

            var fboRenderPassInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _fboRenderPass,
                Framebuffer = fbo.Framebuffer,
                RenderArea = new Rect2D
                {
                    Offset = new Offset2D { X = 0, Y = 0 },
                    Extent = new Extent2D { Width = fbo.Width, Height = fbo.Height }
                },
                ClearValueCount = 2,
                PClearValues = clearValues
            };

            _vk.CmdBeginRenderPass(commandBuffer, &fboRenderPassInfo, SubpassContents.Inline);

            var vp = new Viewport
            {
                X = 0, Y = 0,
                Width = fbo.Width, Height = fbo.Height,
                MinDepth = 0.0f, MaxDepth = 1.0f
            };
            _vk.CmdSetViewport(commandBuffer, 0, 1, &vp);

            var scissor = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = new Extent2D { Width = fbo.Width, Height = fbo.Height }
            };
            _vk.CmdSetScissor(commandBuffer, 0, 1, &scissor);

            // Replay pending draws
            if (_pendingViewportDraws.TryGetValue(viewportId, out var draws) && draws.Count > 0)
            {
                _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _fboGraphicsPipeline);

                foreach (var (verts, push) in draws)
                {
                    var vertsSize = (nuint)(verts.Length * Vertex.Stride);
                    fixed (Vertex* pVerts = verts)
                    {
                        var dst = (byte*)_viewportDrawBufferMapped + (vertexOffset * Vertex.Stride);
                        System.Buffer.MemoryCopy(pVerts, dst, vertsSize, vertsSize);
                    }

                    var vb = _viewportDrawBuffer;
                    ulong bufOffset = vertexOffset * Vertex.Stride;
                    _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vb, &bufOffset);

                    var pc = push;
                    _vk.CmdPushConstants(commandBuffer, _fboPipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(PushConstants), &pc);

                    _vk.CmdDraw(commandBuffer, (uint)verts.Length, 1, 0, 0);

                    vertexOffset += (uint)verts.Length;
                }

                draws.Clear();
            }

            _vk.CmdEndRenderPass(commandBuffer);
        }
    }

    private void RecordSwapchainPass(CommandBuffer commandBuffer, uint imageIndex)
    {
        // ═══════════════════════════════════════════════════════════════
        // Render editor UI + viewport quads to swapchain
        // ═══════════════════════════════════════════════════════════════
        var clearColor = new ClearValue
        {
            Color = new ClearColorValue { Float32_0 = 0.0f, Float32_1 = 0.0f, Float32_2 = 0.0f, Float32_3 = 1.0f }
        };

        var renderPassInfo = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = _framebuffers![imageIndex],
            RenderArea = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = _swapchainExtent
            },
            ClearValueCount = 1,
            PClearValues = &clearColor
        };

        _vk.CmdBeginRenderPass(commandBuffer, &renderPassInfo, SubpassContents.Inline);

        var windowViewport = new Viewport
        {
            X = 0, Y = 0,
            Width = _swapchainExtent.Width, Height = _swapchainExtent.Height,
            MinDepth = 0.0f, MaxDepth = 1.0f
        };
        _vk.CmdSetViewport(commandBuffer, 0, 1, &windowViewport);

        var windowScissor = new Rect2D
        {
            Offset = new Offset2D { X = 0, Y = 0 },
            Extent = _swapchainExtent
        };
        _vk.CmdSetScissor(commandBuffer, 0, 1, &windowScissor);

        // Draw editor UI
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _graphicsPipeline);

        var vertexBuffer = _vertexBuffer;
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vertexBuffer, &offset);

        var pushConstants = _pushConstants;
        _vk.CmdPushConstants(commandBuffer, _pipelineLayout, ShaderStageFlags.VertexBit,
            0, (uint)sizeof(PushConstants), &pushConstants);

        _vk.CmdDraw(commandBuffer, _vertexCount, 1, 0, 0);

        // Draw FBO textures for each viewport
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _texturePipeline);

        foreach (var (viewportId, fbo) in _viewportFbos)
        {
            if (fbo.IsDirty)
                continue;

            if (_viewportQuadBuffers.TryGetValue(viewportId, out var quadBuf))
            {
                fixed (DescriptorSet* pDescSet = &fbo.DescriptorSet)
                {
                    _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics,
                        _texturePipelineLayout, 0, 1, pDescSet, 0, null);
                }

                var texVb = quadBuf.buffer;
                ulong texOffset = 0;
                _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &texVb, &texOffset);

                _vk.CmdPushConstants(commandBuffer, _texturePipelineLayout,
                    ShaderStageFlags.VertexBit, 0, (uint)sizeof(PushConstants), &pushConstants);

                _vk.CmdDraw(commandBuffer, quadBuf.vertexCount, 1, 0, 0);
            }
        }

        _vk.CmdEndRenderPass(commandBuffer);
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

        // Destroy RenderGraph
        _renderGraph?.Destroy();

        // Cleanup viewport FBOs and their vertex buffers
        foreach (var (id, fbo) in _viewportFbos)
            fbo.Destroy(_vk!, _device);
        _viewportFbos.Clear();

        foreach (var (id, buf) in _viewportQuadBuffers)
        {
            _vk?.DestroyBuffer(_device, buf.buffer, null);
            _vk?.FreeMemory(_device, buf.memory, null);
        }
        _viewportQuadBuffers.Clear();

        // Cleanup persistent viewport draw buffer
        if (_viewportDrawBuffer.Handle != 0)
        {
            _vk?.DestroyBuffer(_device, _viewportDrawBuffer, null);
            _vk?.FreeMemory(_device, _viewportDrawBufferMemory, null);
        }

        // Cleanup shared resources
        _vk?.DestroyBuffer(_device, _vertexBuffer, null);
        _vk?.FreeMemory(_device, _vertexBufferMemory, null);
        _vk?.DestroyBuffer(_device, _uniformBuffer, null);
        _vk?.FreeMemory(_device, _uniformBufferMemory, null);
        _vk?.DestroyDescriptorPool(_device, _descriptorPool, null);
        _vk?.DestroyDescriptorSetLayout(_device, _descriptorSetLayout, null);
        _vk?.DestroyPipeline(_device, _graphicsPipeline, null);
        _vk?.DestroyPipeline(_device, _fboGraphicsPipeline, null);
        _vk?.DestroyPipelineLayout(_device, _fboPipelineLayout, null);
        _vk?.DestroyPipelineLayout(_device, _pipelineLayout, null);
        _vk?.DestroyShaderModule(_device, _vertShaderModule, null);
        _vk?.DestroyShaderModule(_device, _fragShaderModule, null);

        // Cleanup texture pipeline
        _vk?.DestroyDescriptorPool(_device, _textureDescriptorPool, null);
        _vk?.DestroyDescriptorSetLayout(_device, _textureDescriptorSetLayout, null);
        _vk?.DestroyPipeline(_device, _texturePipeline, null);
        _vk?.DestroyPipelineLayout(_device, _texturePipelineLayout, null);
        _vk?.DestroyShaderModule(_device, _textureVertShaderModule, null);
        _vk?.DestroyShaderModule(_device, _textureFragShaderModule, null);
        _vk?.DestroyRenderPass(_device, _fboRenderPass, null);

        _vk?.DestroyCommandPool(_device, _commandPool, null);
        _vk?.DestroyRenderPass(_device, _renderPass, null);
        _khrSwapchain?.DestroySwapchain(_device, _swapchain, null);
        _khrSurface?.DestroySurface(_instance, _surface, null);
        _vk?.DestroyDevice(_device, null);
        _vk?.DestroyInstance(_instance, null);

        _input?.Dispose();

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
