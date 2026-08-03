using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;

namespace Engine.Graphics;

public unsafe class SilkWindow : IWindow, IInputSource, IRenderer
{
    private const uint MaxFramesInFlight = 2;

    private readonly ILogger _logger;
    private bool _shutdown;
    private Silk.NET.Windowing.IWindow? _window;
    private IInputContext? _input;
    private IMouse? _mouse;
    private IKeyboard? _keyboard;

    private Instance _instance;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    private SampleCountFlags _msaaSamples = SampleCountFlags.Count1Bit;
    private Device _device;
    private Queue _graphicsQueue;
    private Queue _presentQueue;
    private KhrSurface? _khrSurface;
    private SwapchainManager? _swapchainManager;
    private RenderPass _renderPass;
    private Silk.NET.Vulkan.Semaphore[]? _imageAvailableSemaphores;
    private bool _framebufferResized;
    private bool _windowDragging;
    private Vector2 _windowDragOffset;
    private bool _macFullScreen;
    private float _uiFramebufferScale = 1f;
    private float _uiInputScale = 1f;
    private WindowsWindowChrome? _windowsWindowChrome;
    private bool _customTitleBar;
    private int _requestedWidth;
    private int _requestedHeight;
    private long _lastLiveResizeFrameTimestamp;
    private bool _renderingFrame;

    private Vk? _vk;

    // Render graph for per-pass command buffers
    private FrameScheduler? _frameScheduler;

    private PipelineResources _pipelines = null!;
    private readonly TrueTypeFontRasterizer _fontRasterizer = new();

    // New: Vertex buffer
    private Vertex[] _vertices = [];
    private uint _vertexCount;
    private uint _contentUiVertexCount;
    private uint _overlayUiFirstVertex;
    private uint _overlayUiVertexCount;
    private PushConstants _pushConstants;
    private FrameVertexBuffers? _uiBuffers;

    // Viewport FBO management
    private readonly Dictionary<uint, ViewportFbo> _viewportFbos = new();
    private readonly Dictionary<uint, FrameVertexBuffers> _viewportQuadBuffers = new();
    private readonly Dictionary<uint, VertexT[]> _viewportQuadVertices = new();
    private readonly Dictionary<uint, List<(Vertex[] vertices, PushConstants pushConstants)>> _pendingViewportDraws = new();
    private readonly Dictionary<uint, GridPushConstants> _pendingGridDraws = new();
    private uint _nextViewportId = 1;
    private RenderPass _fboRenderPass;

    // Persistent buffer for viewport draws (reused each frame)
    private FrameVertexBuffers? _viewportDrawBuffers;

    // 2D overlay vertices (drawn on top of everything in swapchain pass)
    private Vertex[] _overlayVertices = [];
    private FrameVertexBuffers? _overlayBuffers;
    private uint _activeFrameIndex;

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
    public event Action<InputKey>? KeyDown;

    /// <inheritdoc/>
    public event Action<InputKey>? KeyUp;

    /// <inheritdoc/>
    public event Action<char>? TextInput;

    /// <inheritdoc/>
    public event Action<double>? Update;

    /// <inheritdoc/>
    public event Action<int, int>? Resized;

    /// <summary>
    /// Creates a window using a disabled logger.
    /// </summary>
    public SilkWindow()
        : this(NullLoggerFactory.Instance)
    {
    }

    /// <summary>
    /// Creates a window using the supplied logger factory.
    /// </summary>
    /// <param name="loggerFactory">Factory used to create backend loggers.</param>
    public SilkWindow(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<SilkWindow>();
    }

    public void Initialize(WindowOptions options)
    {
        if (_window is not null)
            throw new InvalidOperationException("The window has already been initialized.");

        _shutdown = false;
        _customTitleBar = options.CustomTitleBar;
        _requestedWidth = options.Width;
        _requestedHeight = options.Height;
        _logger.LogInformation("Creating window '{Title}' ({Width}x{Height}) [Vulkan]", options.Title, options.Width, options.Height);

        var settings = new Silk.NET.Windowing.WindowOptions
        {
            Size = new Vector2D<int>(options.Width, options.Height),
            Title = options.Title,
            API = new GraphicsAPI(ContextAPI.Vulkan, new APIVersion(1, 1)),
            ShouldSwapAutomatically = false,
            IsVisible = true,
            WindowBorder = WindowBorder.Resizable,
            TransparentFramebuffer = options.CustomTitleBar && OperatingSystem.IsMacOS()
        };

        _window = Window.Create(settings);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += OnClosing;
        _window.Resize += OnResize;
        _window.FramebufferResize += OnFramebufferResize;

        _window.Initialize();
        if (options.CustomTitleBar && OperatingSystem.IsMacOS())
            MacOSWindowChrome.Apply(_window);
        if (!OperatingSystem.IsWindows())
        {
            _uiFramebufferScale = CalculateFramebufferScale();
            _uiInputScale = 1f;
        }
        _logger.LogInformation("Window initialized");
    }

    /// <inheritdoc/>
    public void SetUI(UIDrawList drawList)
    {
        _vertices = BuildUIVertices(drawList);
        _vertexCount = (uint)_vertices.Length;
    }

    public void SetPushConstants(PushConstants pushConstants)
    {
        _pushConstants = pushConstants;
    }

    private void OnLoad()
    {
        _logger.LogInformation("Window.Load fired — initializing Vulkan");

        InitializeWindowsClientGeometry();

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
            _keyboard.KeyChar += OnKeyChar;
            _logger.LogInformation("Keyboard input attached");
        }

        _vk = Vk.GetApi();
        CreateInstance();
        CreateSurface();
        PickPhysicalDevice();
        CreateLogicalDevice();
        _pipelines = new PipelineResources(_vk!, _device);
        _frameScheduler = new FrameScheduler(_vk!, _device, _graphicsQueue, FindQueueFamilies(_physicalDevice).GraphicsFamily!.Value);
        _viewportDrawBuffers = new FrameVertexBuffers(_vk, _device, MaxFramesInFlight, 1024,
            "viewport draw", FindMemoryType, _logger);
        _overlayBuffers = new FrameVertexBuffers(_vk, _device, MaxFramesInFlight, 256,
            "overlay", FindMemoryType, _logger);
        CreateSwapchain();
        CreateFboRenderPass();
        CreateRenderPass();
        CreateGraphicsPipeline();
        CreateFboGraphicsPipeline();
        CreateGridPipeline();
        CreateFramebuffers();
        CreateSyncObjects();
        CreateTexturePipeline();

        _logger.LogInformation("Vulkan initialization complete");
    }

    /// <summary>Finalizes Windows chrome, DPI, size, and placement before Vulkan reads the surface extent.</summary>
    private void InitializeWindowsClientGeometry()
    {
        if (!OperatingSystem.IsWindows() || _window is null)
            return;

        if (_customTitleBar)
            _windowsWindowChrome = WindowsWindowChrome.Apply(_window);

        _uiFramebufferScale = CalculateFramebufferScale();
        _uiInputScale = _uiFramebufferScale;
        _window.Size = new Vector2D<int>(
            Math.Max(1, (int)MathF.Round(_requestedWidth * _uiFramebufferScale)),
            Math.Max(1, (int)MathF.Round(_requestedHeight * _uiFramebufferScale)));
        _windowsWindowChrome?.EnsureVisible();
    }

    private void OnUpdate(double delta)
    {
        Update?.Invoke(delta);
    }

    private void OnRender(double delta)
    {
        if (_renderingFrame)
            return;

        _renderingFrame = true;
        try
        {
            DrawFrame();
        }
        finally
        {
            _renderingFrame = false;
        }
    }

    private void OnClosing()
    {
        _vk!.DeviceWaitIdle(_device);
    }

    private void OnResize(Vector2D<int> size)
    {
        // Logical layout changes are dispatched from OnFramebufferResize so layout and
        // swapchain dimensions always originate from the same native resize event.
    }

    /// <summary>Marks swapchain resources stale after the physical framebuffer changes size.</summary>
    /// <param name="size">New physical framebuffer size.</param>
    private void OnFramebufferResize(Vector2D<int> size)
    {
        if (size.X <= 0 || size.Y <= 0)
            return;

        _uiFramebufferScale = CalculateFramebufferScale();
        _uiInputScale = OperatingSystem.IsWindows() ? _uiFramebufferScale : 1f;
        var logicalWidth = Math.Max(1, (int)MathF.Round(size.X / _uiFramebufferScale));
        var logicalHeight = Math.Max(1, (int)MathF.Round(size.Y / _uiFramebufferScale));
        Resized?.Invoke(logicalWidth, logicalHeight);
        if (_vk is null)
            return;

        _framebufferResized = true;
        var liveFrameDue = _lastLiveResizeFrameTimestamp == 0
            || System.Diagnostics.Stopwatch.GetElapsedTime(_lastLiveResizeFrameTimestamp)
                >= TimeSpan.FromMilliseconds(16);
        if (!liveFrameDue || _window is null || _renderingFrame)
            return;

        _lastLiveResizeFrameTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        _window.DoUpdate();
        _window.DoRender();
    }

    private void OnMouseMove(IMouse mouse, Vector2 pos)
    {
        MouseMove?.Invoke(new Vector2(pos.X / _uiInputScale, pos.Y / _uiInputScale));
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
        KeyDown?.Invoke(ToInputKey(key));
    }

    private void OnKeyUp(IKeyboard keyboard, Key key, int keyCode)
    {
        KeyUp?.Invoke(ToInputKey(key));
    }

    /// <summary>Forwards one device text-input character.</summary>
    /// <param name="keyboard">Keyboard producing the character.</param>
    /// <param name="character">Produced Unicode character.</param>
    private void OnKeyChar(IKeyboard keyboard, char character)
    {
        TextInput?.Invoke(character);
    }

    /// <inheritdoc/>
    public void SetMouseCaptured(bool captured)
    {
        if (_mouse is null)
            return;

        var cursor = _mouse.Cursor;
        if (!captured)
        {
            cursor.CursorMode = CursorMode.Normal;
            return;
        }

        cursor.CursorMode = cursor.IsSupported(CursorMode.Raw)
            ? CursorMode.Raw
            : CursorMode.Disabled;
    }

    /// <inheritdoc/>
    public void BeginWindowDrag(Vector2 pointerPosition)
    {
        if (_window is null || _window.WindowState != WindowState.Normal)
            return;
        if (MacOSWindowChrome.TryBeginWindowDrag(_window))
        {
            _windowDragging = false;
            return;
        }
        if (_windowsWindowChrome?.TryBeginWindowDrag() == true)
        {
            _windowDragging = false;
            return;
        }

        _windowDragging = true;
        _windowDragOffset = pointerPosition;
    }

    /// <inheritdoc/>
    public void UpdateWindowDrag(Vector2 pointerPosition)
    {
        if (!_windowDragging || _window is null)
            return;
        if (!float.IsFinite(pointerPosition.X) || !float.IsFinite(pointerPosition.Y))
            return;

        var pointerScreenX = _window.Position.X + pointerPosition.X;
        var pointerScreenY = _window.Position.Y + pointerPosition.Y;
        var targetX = pointerScreenX - _windowDragOffset.X;
        var targetY = pointerScreenY - _windowDragOffset.Y;
        _window.Position = new Vector2D<int>(
            ClampWindowCoordinate(targetX),
            ClampWindowCoordinate(targetY));
    }

    /// <summary>Rounds a finite window coordinate while protecting the native backend from overflow.</summary>
    /// <param name="coordinate">Floating-point screen coordinate.</param>
    /// <returns>A safely rounded native window coordinate.</returns>
    private static int ClampWindowCoordinate(float coordinate)
    {
        var rounded = Math.Round((double)coordinate);
        return (int)Math.Clamp(rounded, int.MinValue, int.MaxValue);
    }

    /// <inheritdoc/>
    public void EndWindowDrag()
    {
        _windowDragging = false;
    }

    /// <inheritdoc/>
    public void Minimize()
    {
        if (_window is not null)
            _window.WindowState = WindowState.Minimized;
    }

    /// <inheritdoc/>
    public void ToggleMaximize()
    {
        if (_window is null)
            return;
        _windowDragging = false;
        _window.WindowState = _window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        MacOSWindowChrome.SetRounded(_window, _window.WindowState == WindowState.Normal);
    }

    /// <inheritdoc/>
    public void ToggleFullScreen()
    {
        if (_window is null)
            return;
        _windowDragging = false;
        if (MacOSWindowChrome.TryToggleFullScreen(_window))
        {
            _macFullScreen = !_macFullScreen;
            MacOSWindowChrome.SetRounded(_window, !_macFullScreen);
            return;
        }

        _window.WindowState = _window.WindowState == WindowState.Fullscreen
            ? WindowState.Normal
            : WindowState.Fullscreen;
    }

    /// <inheritdoc/>
    public void Close()
    {
        if (_window is not null)
            _window.IsClosing = true;
    }

    /// <summary>
    /// Maps a Silk.NET key to the engine input abstraction.
    /// </summary>
    /// <param name="key">Silk.NET keyboard key.</param>
    /// <returns>The corresponding engine key.</returns>
    private static InputKey ToInputKey(Key key)
    {
        return key switch
        {
            Key.A => InputKey.A,
            Key.D => InputKey.D,
            Key.F => InputKey.F,
            Key.S => InputKey.S,
            Key.W => InputKey.W,
            Key.Space => InputKey.Space,
            Key.ControlLeft => InputKey.LeftControl,
            Key.ControlRight => InputKey.RightControl,
            Key.SuperLeft => InputKey.LeftSuper,
            Key.SuperRight => InputKey.RightSuper,
            Key.ShiftLeft => InputKey.LeftShift,
            Key.ShiftRight => InputKey.RightShift,
            Key.Backspace => InputKey.Backspace,
            Key.Delete => InputKey.Delete,
            Key.Left => InputKey.Left,
            Key.Right => InputKey.Right,
            Key.Home => InputKey.Home,
            Key.End => InputKey.End,
            Key.Escape => InputKey.Escape,
            _ => InputKey.Unknown
        };
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
        var surface = _window!.VkSurface ?? throw new InvalidOperationException("Window does not expose a Vulkan surface.");
        var windowExts = surface.GetRequiredExtensions(out var count);
        var extensions = new string[count];
        for (var i = 0; i < count; i++)
            extensions[i] = SilkMarshal.PtrToString((nint)windowExts[i], NativeStringEncoding.UTF8)!;
        return extensions;
    }

    private void CreateSurface()
    {
        _logger.LogDebug("Creating VkSurfaceKHR");

        var surface = _window!.VkSurface ?? throw new InvalidOperationException("Window does not expose a Vulkan surface.");
        var surfaceHandle = surface.Create<AllocationCallbacks>(new VkHandle(_instance.Handle), null);
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
                _msaaSamples = SelectMsaaSamples(props);
                _logger.LogInformation("Using GPU: {Name}", SilkMarshal.PtrToString((nint)props.DeviceName, NativeStringEncoding.UTF8));
                _logger.LogInformation("Viewport MSAA: {Samples}", _msaaSamples);
                return;
            }
        }

        throw new Exception("No suitable Vulkan physical device found");
    }

    /// <summary>
    /// Selects the highest color/depth sample count up to four samples.
    /// </summary>
    /// <param name="properties">Physical-device properties.</param>
    /// <returns>The selected sample count.</returns>
    private static SampleCountFlags SelectMsaaSamples(PhysicalDeviceProperties properties)
    {
        var supported = properties.Limits.FramebufferColorSampleCounts
            & properties.Limits.FramebufferDepthSampleCounts;
        if ((supported & SampleCountFlags.Count4Bit) != 0)
            return SampleCountFlags.Count4Bit;
        if ((supported & SampleCountFlags.Count2Bit) != 0)
            return SampleCountFlags.Count2Bit;
        return SampleCountFlags.Count1Bit;
    }

    private bool IsDeviceSuitable(PhysicalDevice device)
    {
        var indices = FindQueueFamilies(device);
        if (!indices.GraphicsFamily.HasValue || !indices.PresentFamily.HasValue)
            return false;

        if (!SupportsDeviceExtension(device, KhrSwapchain.ExtensionName))
            return false;

        var support = SwapchainManager.QuerySupport(_khrSurface!, device, _surface);
        return support.Formats.Length > 0 && support.PresentModes.Length > 0;
    }

    /// <summary>
    /// Determines whether a physical device advertises an extension.
    /// </summary>
    /// <param name="device">Physical device to inspect.</param>
    /// <param name="extensionName">Extension name to locate.</param>
    /// <returns><see langword="true"/> when the extension is supported.</returns>
    private bool SupportsDeviceExtension(PhysicalDevice device, string extensionName)
    {
        uint extensionCount = 0;
        _vk!.EnumerateDeviceExtensionProperties(device, (byte*)null, &extensionCount, null);

        var properties = new ExtensionProperties[extensionCount];
        fixed (ExtensionProperties* pProperties = properties)
            _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &extensionCount, pProperties);

        foreach (var property in properties)
        {
            var name = SilkMarshal.PtrToString(
                (nint)property.ExtensionName,
                NativeStringEncoding.UTF8);
            if (string.Equals(name, extensionName, StringComparison.Ordinal))
                return true;
        }

        return false;
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

            var extensions = new List<string> { KhrSwapchain.ExtensionName };
            if (SupportsDeviceExtension(_physicalDevice, "VK_KHR_portability_subset"))
                extensions.Add("VK_KHR_portability_subset");

            _logger.LogDebug("Enabling device extensions: {Extensions}", string.Join(", ", extensions));
            var extensionNamesMem = SilkMarshal.StringArrayToPtr(extensions.ToArray(), NativeStringEncoding.UTF8);

            var createInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueCreateInfo,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = (byte**)extensionNamesMem
            };

            _logger.LogDebug("Calling vkCreateDevice...");
            var result = _vk!.CreateDevice(_physicalDevice, &createInfo, null, out _device);
            SilkMarshal.Free(extensionNamesMem);

            if (result != Result.Success)
                throw new Exception($"Failed to create logical device: {result}");
        }

        _vk.GetDeviceQueue(_device, indices.GraphicsFamily!.Value, 0, out _graphicsQueue);
        _vk.GetDeviceQueue(_device, indices.PresentFamily!.Value, 0, out _presentQueue);

        _logger.LogInformation("Logical device created");
    }

    private void CreateSwapchain()
    {
        var framebufferSize = _window!.FramebufferSize;
        var indices = FindQueueFamilies(_physicalDevice);
        _swapchainManager = new SwapchainManager(_vk!, _device, _physicalDevice, _surface,
            _khrSurface!, indices.GraphicsFamily!.Value, indices.PresentFamily!.Value, _logger);
        _swapchainManager.Create(
            (uint)Math.Max(framebufferSize.X, 0),
            (uint)Math.Max(framebufferSize.Y, 0));
    }

    private void RecreateSwapchain()
    {
        var size = _window!.FramebufferSize;
        while (size.X == 0 || size.Y == 0)
        {
            size = _window.FramebufferSize;
            _window.DoEvents();
        }

        _vk!.DeviceWaitIdle(_device);
        _swapchainManager!.Recreate((uint)size.X, (uint)size.Y, _renderPass);
    }

    private void CleanupSwapchain()
    {
        _swapchainManager?.Destroy();
    }

    private void CreateRenderPass()
    {
        _logger.LogDebug("Creating render pass");

        var colorAttachment = new AttachmentDescription
        {
            Format = _swapchainManager!.ImageFormat,
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
            var result = _vk!.CreateShaderModule(_device, &vertModuleInfo, null, out _pipelines.UiVertexShader);
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
            var result = _vk!.CreateShaderModule(_device, &fragModuleInfo, null, out _pipelines.UiFragmentShader);
            if (result != Result.Success)
                throw new Exception($"Failed to create fragment shader module: {result}");
        }

        var entryPointName = SilkMarshal.StringToPtr("main", NativeStringEncoding.UTF8);

        var vertShaderStageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = _pipelines.UiVertexShader,
            PName = (byte*)entryPointName
        };

        var fragShaderStageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = _pipelines.UiFragmentShader,
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

                var result = _vk!.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _pipelines.UiLayout);
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
                        Layout = _pipelines.UiLayout,
                        RenderPass = _renderPass,
                        Subpass = 0,
                        BasePipelineHandle = default
                    };

                    result = _vk.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, out _pipelines.UiPipeline);
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
                    RasterizationSamples = _msaaSamples
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

                var r = _vk!.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _pipelines.ViewportLayout);
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
                        Layout = _pipelines.ViewportLayout,
                        RenderPass = _fboRenderPass,
                        Subpass = 0,
                        BasePipelineHandle = default
                    };

                    r = _vk.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, out _pipelines.ViewportPipeline);
                    if (r != Result.Success) throw new Exception($"Failed to create FBO graphics pipeline: {r}");
                }
            }
        }

        SilkMarshal.Free(entryPointName);
        _logger.LogInformation("FBO graphics pipeline created");
    }

    /// <summary>
    /// Creates the fullscreen procedural ground-grid pipeline for viewport FBOs.
    /// </summary>
    private void CreateGridPipeline()
    {
        _logger.LogDebug("Creating procedural grid pipeline");

        _pipelines.GridVertexShader = CreateShaderModule("grid.vert.spv");
        _pipelines.GridFragmentShader = CreateShaderModule("grid.frag.spv");
        var entryPointName = SilkMarshal.StringToPtr("main", NativeStringEncoding.UTF8);

        var shaderStages = new[]
        {
            new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = _pipelines.GridVertexShader,
                PName = (byte*)entryPointName
            },
            new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = _pipelines.GridFragmentShader,
                PName = (byte*)entryPointName
            }
        };

        var vertexInputInfo = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo
        };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList
        };
        var dynamicStates = new[] { DynamicState.Viewport, DynamicState.Scissor };

        fixed (DynamicState* pDynamicStates = dynamicStates)
        fixed (PipelineShaderStageCreateInfo* pStages = shaderStages)
        {
            var dynamicStateInfo = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = (uint)dynamicStates.Length,
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
                PolygonMode = PolygonMode.Fill,
                CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
                LineWidth = 1f
            };
            var multisampling = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = _msaaSamples
            };
            var depthStencilState = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = new Bool32(true),
                DepthWriteEnable = new Bool32(true),
                DepthCompareOp = CompareOp.LessOrEqual
            };
            var blendAttachment = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = new Bool32(true),
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add
            };
            var colorBlending = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1,
                PAttachments = &blendAttachment
            };
            var pushConstantRange = new PushConstantRange
            {
                StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                Size = (uint)sizeof(GridPushConstants)
            };
            var pipelineLayoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                PushConstantRangeCount = 1,
                PPushConstantRanges = &pushConstantRange
            };

            var result = _vk!.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _pipelines.GridLayout);
            if (result != Result.Success)
                throw new Exception($"Failed to create grid pipeline layout: {result}");

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = (uint)shaderStages.Length,
                PStages = pStages,
                PVertexInputState = &vertexInputInfo,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisampling,
                PDepthStencilState = &depthStencilState,
                PColorBlendState = &colorBlending,
                PDynamicState = &dynamicStateInfo,
                Layout = _pipelines.GridLayout,
                RenderPass = _fboRenderPass
            };

            result = _vk.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, out _pipelines.GridPipeline);
            if (result != Result.Success)
                throw new Exception($"Failed to create grid pipeline: {result}");
        }

        SilkMarshal.Free(entryPointName);
        _logger.LogInformation("Procedural grid pipeline created");
    }

    /// <summary>
    /// Creates one Vulkan shader module from an embedded SPIR-V resource.
    /// </summary>
    /// <param name="resourceName">Embedded SPIR-V resource name.</param>
    /// <returns>The created shader module.</returns>
    private ShaderModule CreateShaderModule(string resourceName)
    {
        var code = LoadSpirV(resourceName);
        fixed (uint* pCode = code)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(code.Length * sizeof(uint)),
                PCode = pCode
            };
            var result = _vk!.CreateShaderModule(_device, &createInfo, null, out var shaderModule);
            if (result != Result.Success)
                throw new Exception($"Failed to create shader module '{resourceName}': {result}");
            return shaderModule;
        }
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
        _logger.LogDebug("Creating multisampled FBO render pass");

        var format = _swapchainManager!.ImageFormat;
        var depthFormat = FindDepthFormat();

        var attachments = stackalloc[] { new AttachmentDescription
        {
            Format = format,
            Samples = _msaaSamples,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ColorAttachmentOptimal
        }, new AttachmentDescription
        {
            Format = depthFormat,
            Samples = _msaaSamples,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal
        }, new AttachmentDescription
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal
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

        var resolveAttachmentRef = new AttachmentReference
        {
            Attachment = 2,
            Layout = ImageLayout.ColorAttachmentOptimal
        };

        var subpassDescription = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef,
            PResolveAttachments = &resolveAttachmentRef,
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
            AttachmentCount = 3,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpassDescription,
            DependencyCount = 1,
            PDependencies = &subpassDependency
        };

        var result = _vk!.CreateRenderPass(_device, &renderPassInfo, null, out _fboRenderPass);
        if (result != Result.Success)
            throw new Exception($"Failed to create FBO render pass: {result}");

        _logger.LogInformation("FBO render pass created ({Samples} MSAA + resolve + depth)", _msaaSamples);
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
        var result = _vk!.CreateDescriptorSetLayout(_device, &layoutInfo, null, out _pipelines.TextureDescriptorSetLayout);
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
        result = _vk.CreateDescriptorPool(_device, &poolInfo, null, out _pipelines.TextureDescriptorPool);
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
            var vertResult = _vk!.CreateShaderModule(_device, &vertModuleInfo, null, out _pipelines.TextureVertexShader);
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
            var fragResult = _vk!.CreateShaderModule(_device, &fragModuleInfo, null, out _pipelines.TextureFragmentShader);
            if (fragResult != Result.Success)
                throw new Exception($"Failed to create texture fragment shader module: {fragResult}");
        }

        var entryPointName = SilkMarshal.StringToPtr("main", NativeStringEncoding.UTF8);

        var vertShaderStageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = _pipelines.TextureVertexShader,
            PName = (byte*)entryPointName
        };

        var fragShaderStageInfo = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = _pipelines.TextureFragmentShader,
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

                fixed (DescriptorSetLayout* pSetLayout = &_pipelines.TextureDescriptorSetLayout)
                {
                    pipelineLayoutInfo.PSetLayouts = pSetLayout;

                    var layoutResult = _vk!.CreatePipelineLayout(_device, &pipelineLayoutInfo, null, out _pipelines.TextureLayout);
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
                        Layout = _pipelines.TextureLayout,
                        RenderPass = _renderPass,
                        Subpass = 0,
                        BasePipelineHandle = default
                    };

                    var pipelineResult = _vk.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo, null, out _pipelines.TexturePipeline);
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
        fbo.Create(_vk!, _device, _fboRenderPass, _swapchainManager!.ImageFormat, FindDepthFormat(), _msaaSamples,
            deviceLocalMemoryType,
            _pipelines.TextureDescriptorSetLayout, _pipelines.TextureDescriptorPool);
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

        if (_viewportQuadBuffers.TryGetValue(viewportId, out var buffers))
        {
            buffers.Destroy();
            _viewportQuadBuffers.Remove(viewportId);
        }
        _viewportQuadVertices.Remove(viewportId);

        _pendingGridDraws.Remove(viewportId);

        _logger.LogInformation("Viewport {Id} unregistered", viewportId);
    }

    /// <inheritdoc/>
    public void ResizeViewport(uint viewportId, float width, float height)
    {
        if (_viewportFbos.TryGetValue(viewportId, out var fbo))
            fbo.Resize((uint)width, (uint)height);
    }

    /// <inheritdoc/>
    public void SetViewportQuadVertices(uint viewportId, VertexT[] vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        if (!_viewportQuadBuffers.ContainsKey(viewportId))
        {
            _viewportQuadBuffers[viewportId] = new FrameVertexBuffers(
                _vk!, _device, MaxFramesInFlight, 6, $"viewport {viewportId} quad",
                FindMemoryType, _logger);
        }

        _viewportQuadVertices[viewportId] = vertices;
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
    public void Submit(uint viewportId, RenderQueue renderQueue)
    {
        ArgumentNullException.ThrowIfNull(renderQueue);
        if (!_viewportFbos.ContainsKey(viewportId))
            throw new ArgumentOutOfRangeException(nameof(viewportId), viewportId, "Viewport is not registered.");

        if (!_pendingViewportDraws.ContainsKey(viewportId))
            _pendingViewportDraws[viewportId] = new List<(Vertex[], PushConstants)>();

        foreach (var command in renderQueue.Commands)
            _pendingViewportDraws[viewportId].Add((command.Vertices, command.PushConstants));
    }

    /// <inheritdoc/>
    public void DrawGroundGrid(uint viewportId, Matrix4x4 view, Matrix4x4 projection)
    {
        var viewProjection = view * projection;
        if (!_viewportFbos.ContainsKey(viewportId)
            || !Matrix4x4.Invert(viewProjection, out var inverseViewProjection))
            return;

        _pendingGridDraws[viewportId] = new GridPushConstants
        {
            ViewProjection = viewProjection,
            InverseViewProjection = inverseViewProjection
        };
    }

    /// <inheritdoc/>
    public void DrawOverlay(Vertex[] vertices)
    {
        _overlayVertices = vertices;
    }

    /// <inheritdoc/>
    public void SetViewportClearColor(uint viewportId, float r, float g, float b, float a = 1.0f)
    {
        if (_viewportFbos.TryGetValue(viewportId, out var fbo))
            fbo.ClearColor = new Vector4(r, g, b, a);
    }

    private void RecreateDirtyFbos()
    {
        if (!_viewportFbos.Values.Any(fbo => fbo.IsDirty))
            return;

        _vk!.DeviceWaitIdle(_device);
        var deviceLocalMemoryType = FindMemoryType(0xFFFFFFFF, MemoryPropertyFlags.DeviceLocalBit);
        foreach (var (id, fbo) in _viewportFbos)
        {
            if (fbo.IsDirty)
            {
                _logger.LogDebug("Recreating viewport {Id} FBO ({Width}x{Height})", id, fbo.Width, fbo.Height);
                fbo.Recreate(_vk!, _device, _fboRenderPass, _swapchainManager!.ImageFormat, FindDepthFormat(), _msaaSamples,
                    deviceLocalMemoryType,
                    _pipelines.TextureDescriptorSetLayout, _pipelines.TextureDescriptorPool);
            }
        }
    }

    // ── Old method removed — now per-viewport via SetViewportQuadVertices ──

    public void CreateVertexBuffer()
    {
        _uiBuffers ??= new FrameVertexBuffers(
            _vk!, _device, MaxFramesInFlight, Math.Max(1024u, (uint)_vertices.Length),
            "UI", FindMemoryType, _logger);
    }

    /// <inheritdoc/>
    public void UpdateUI(UIDrawList drawList)
    {
        _vertices = BuildUIVertices(drawList);
        _vertexCount = (uint)_vertices.Length;
    }

    /// <summary>Translates semantic UI rectangles into backend triangle vertices.</summary>
    /// <param name="drawList">UI draw list.</param>
    /// <returns>Colored triangle vertices.</returns>
    private Vertex[] BuildUIVertices(UIDrawList drawList)
    {
        ArgumentNullException.ThrowIfNull(drawList);
        var framebufferScale = GetFramebufferScale();
        var contentVertices = new List<Vertex>(drawList.Commands.Count * 6);
        var overlayVertices = new List<Vertex>();
        foreach (var command in drawList.Commands)
        {
            var target = command.Layer == UIDrawLayer.Overlay ? overlayVertices : contentVertices;
            AppendUICommandVertices(target, command, framebufferScale);
        }
        _contentUiVertexCount = (uint)contentVertices.Count;
        _overlayUiFirstVertex = _contentUiVertexCount;
        _overlayUiVertexCount = (uint)overlayVertices.Count;
        contentVertices.AddRange(overlayVertices);
        return contentVertices.ToArray();
    }

    /// <summary>Translates one semantic UI command into triangle vertices.</summary>
    /// <param name="vertices">Destination vertex collection.</param>
    /// <param name="command">Semantic UI command.</param>
    /// <param name="framebufferScale">Physical framebuffer scale used for font rasterization.</param>
    private void AppendUICommandVertices(
        List<Vertex> vertices,
        UIDrawCommand command,
        float framebufferScale)
    {
        if (command.Type == UIDrawCommandType.Text)
        {
            _fontRasterizer.AppendVertices(vertices, command, framebufferScale);
            return;
        }

        if (command.Type == UIDrawCommandType.Ellipse)
        {
            AppendEllipseVertices(vertices, command);
            return;
        }

        vertices.Add(new Vertex(new Vector3(command.Left, command.Top, 0f), command.Color));
        vertices.Add(new Vertex(new Vector3(command.Left, command.Bottom, 0f), command.Color));
        vertices.Add(new Vertex(new Vector3(command.Right, command.Bottom, 0f), command.Color));
        vertices.Add(new Vertex(new Vector3(command.Right, command.Bottom, 0f), command.Color));
        vertices.Add(new Vertex(new Vector3(command.Right, command.Top, 0f), command.Color));
        vertices.Add(new Vertex(new Vector3(command.Left, command.Top, 0f), command.Color));
    }

    /// <summary>Tessellates one filled UI ellipse into triangles.</summary>
    /// <param name="vertices">Destination vertex collection.</param>
    /// <param name="command">Ellipse command and bounds.</param>
    private static void AppendEllipseVertices(List<Vertex> vertices, UIDrawCommand command)
    {
        const int SegmentCount = 24;
        var centerX = (command.Left + command.Right) / 2f;
        var centerY = (command.Top + command.Bottom) / 2f;
        var radiusX = MathF.Max(0f, (command.Right - command.Left) / 2f);
        var radiusY = MathF.Max(0f, (command.Bottom - command.Top) / 2f);
        var center = new Vector3(centerX, centerY, 0f);
        for (var index = 0; index < SegmentCount; index++)
        {
            var angleA = index * MathF.Tau / SegmentCount;
            var angleB = (index + 1) * MathF.Tau / SegmentCount;
            vertices.Add(new Vertex(center, command.Color));
            vertices.Add(new Vertex(new Vector3(
                centerX + MathF.Cos(angleB) * radiusX,
                centerY + MathF.Sin(angleB) * radiusY,
                0f), command.Color));
            vertices.Add(new Vertex(new Vector3(
                centerX + MathF.Cos(angleA) * radiusX,
                centerY + MathF.Sin(angleA) * radiusY,
                0f), command.Color));
        }
    }

    /// <summary>Gets physical framebuffer pixels per logical UI pixel.</summary>
    /// <returns>The larger positive framebuffer scale axis, or one before window creation.</returns>
    private float GetFramebufferScale()
    {
        return _uiFramebufferScale;
    }

    /// <summary>Calculates the stable physical-pixel density of the initialized window.</summary>
    /// <returns>The larger positive framebuffer-to-window scale axis.</returns>
    private float CalculateFramebufferScale()
    {
        if (_window is null || _window.Size.X <= 0 || _window.Size.Y <= 0)
            return 1f;

        var framebufferSize = _window.FramebufferSize;
        var scaleX = framebufferSize.X / (float)_window.Size.X;
        var scaleY = framebufferSize.Y / (float)_window.Size.Y;
        var scale = MathF.Max(1f, MathF.Max(scaleX, scaleY));
        if (!OperatingSystem.IsWindows())
            return scale;

        var win32 = _window.Native?.Win32;
        if (win32 is null || win32.Value.Item1 == IntPtr.Zero)
            return scale;

        var dpi = GetDpiForWindow(win32.Value.Item1);
        return MathF.Max(scale, dpi / 96f);
    }

    /// <summary>Gets the effective DPI for a native Windows window.</summary>
    /// <param name="windowHandle">Native Win32 window handle.</param>
    /// <returns>The DPI value for the window.</returns>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

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
        _swapchainManager!.CreateFramebuffers(_renderPass);
    }

    private void CreateSyncObjects()
    {
        _logger.LogDebug("Creating sync objects");

        // Only imageAvailable semaphores are needed here (for swapchain image acquisition).
        // FrameScheduler manages its own fences and inter-pass semaphores.
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
        if (_framebufferResized)
        {
            _framebufferResized = false;
            RecreateSwapchain();
        }

        // Recreate any dirty viewport FBOs
        RecreateDirtyFbos();

        // ── Begin frame (waits for previous frame's fence) ──
        var frameIndex = _frameScheduler!.BeginFrame();
        _activeFrameIndex = frameIndex;

        // ── Acquire swapchain image ──
        uint imageIndex = 0;
        var imageAvailableSemaphore = _imageAvailableSemaphores![frameIndex];
        var result = _swapchainManager!.Extension.AcquireNextImage(
            _device, _swapchainManager.Handle, ulong.MaxValue, imageAvailableSemaphore, default, &imageIndex);

        if (result == Result.ErrorOutOfDateKhr)
        {
            RecreateSwapchain();
            _frameScheduler.EndFrame();
            return;
        }

        // ── Pass 1: Render viewport content into FBOs ──
        Silk.NET.Vulkan.Semaphore pass1Semaphore;
        {
            var (cmdBuffer, sem) = _frameScheduler.BeginPass();
            pass1Semaphore = sem;

            RecordFboPass(cmdBuffer);

            _frameScheduler.EndPass(cmdBuffer);
            _frameScheduler.SubmitPass(cmdBuffer, imageAvailableSemaphore, sem, default);
        }

        // ── Pass 2: Render editor UI + viewport quads to swapchain ──
        Silk.NET.Vulkan.Semaphore pass2Semaphore;
        {
            var (cmdBuffer, sem) = _frameScheduler.BeginPass();
            pass2Semaphore = sem;

            RecordSwapchainPass(cmdBuffer, imageIndex);

            _frameScheduler.EndPass(cmdBuffer);
            _frameScheduler.PrepareCurrentFenceForSubmission();
            _frameScheduler.SubmitPass(cmdBuffer, pass1Semaphore, sem, _frameScheduler.GetCurrentFence());
        }

        // ── Present ──
        var swapchain = _swapchainManager.Handle;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &pass2Semaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex
        };

        result = _swapchainManager.Extension.QueuePresent(_presentQueue, &presentInfo);

        if (result == Result.ErrorOutOfDateKhr || result == Result.SuboptimalKhr)
        {
            RecreateSwapchain();
        }

        _frameScheduler.EndFrame();
    }

    private void RecordFboPass(CommandBuffer commandBuffer)
    {
        // ═══════════════════════════════════════════════════════════════
        // Render each viewport's content into its own FBO
        // ═══════════════════════════════════════════════════════════════

        // First pass: count total vertices across all viewports
        uint totalVertices = 0;
        foreach (var (viewportId, fbo) in _viewportFbos)
        {
            if (fbo.IsDirty)
                continue;

            if (_pendingViewportDraws.TryGetValue(viewportId, out var draws))
            {
                foreach (var (verts, _) in draws)
                    totalVertices += (uint)verts.Length;
            }
        }

        if (totalVertices > 0)
            _viewportDrawBuffers!.Ensure(_activeFrameIndex, totalVertices, Vertex.Stride);

        // Second pass: record draw commands into the shared buffer
        uint vertexOffset = 0;
        var clearValues = stackalloc ClearValue[2];
        foreach (var (viewportId, fbo) in _viewportFbos)
        {
            if (fbo.IsDirty)
                continue;

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

            _vk!.CmdBeginRenderPass(commandBuffer, &fboRenderPassInfo, SubpassContents.Inline);

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

            // Draw the infinite ground grid before opaque scene geometry so
            // subsequent meshes naturally occlude it through the depth buffer.
            if (_pendingGridDraws.Remove(viewportId, out var gridPushConstants))
            {
                _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipelines.GridPipeline);
                _vk.CmdPushConstants(commandBuffer, _pipelines.GridLayout,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
                    0, (uint)sizeof(GridPushConstants), &gridPushConstants);
                _vk.CmdDraw(commandBuffer, 3, 1, 0, 0);
            }

            // Replay pending draws
            if (_pendingViewportDraws.TryGetValue(viewportId, out var draws) && draws.Count > 0)
            {
                _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipelines.ViewportPipeline);

                foreach (var (verts, push) in draws)
                {
                    var vertsSize = (nuint)(verts.Length * Vertex.Stride);
                    fixed (Vertex* pVerts = verts)
                    {
                        var dst = (byte*)_viewportDrawBuffers!.GetMappedPointer(_activeFrameIndex)
                            + (vertexOffset * Vertex.Stride);
                        System.Buffer.MemoryCopy(pVerts, dst, vertsSize, vertsSize);
                    }

                    var vb = _viewportDrawBuffers!.GetBuffer(_activeFrameIndex);
                    ulong bufOffset = vertexOffset * Vertex.Stride;
                    _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vb, &bufOffset);

                    var pc = push;
                    _vk.CmdPushConstants(commandBuffer, _pipelines.ViewportLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(PushConstants), &pc);

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
            Framebuffer = _swapchainManager!.Framebuffers[(int)imageIndex],
            RenderArea = new Rect2D
            {
                Offset = new Offset2D { X = 0, Y = 0 },
                Extent = _swapchainManager.Extent
            },
            ClearValueCount = 1,
            PClearValues = &clearColor
        };

        _vk!.CmdBeginRenderPass(commandBuffer, &renderPassInfo, SubpassContents.Inline);

        var windowViewport = new Viewport
        {
            X = 0, Y = 0,
            Width = _swapchainManager.Extent.Width, Height = _swapchainManager.Extent.Height,
            MinDepth = 0.0f, MaxDepth = 1.0f
        };
        _vk.CmdSetViewport(commandBuffer, 0, 1, &windowViewport);

        var windowScissor = new Rect2D
        {
            Offset = new Offset2D { X = 0, Y = 0 },
            Extent = _swapchainManager.Extent
        };
        _vk.CmdSetScissor(commandBuffer, 0, 1, &windowScissor);

        Silk.NET.Vulkan.Buffer uiFrameBuffer = default;
        if (_vertexCount > 0)
        {
            _uiBuffers!.Ensure(_activeFrameIndex, _vertexCount, Vertex.Stride);
            var uiSize = (nuint)(_vertices.Length * Vertex.Stride);
            fixed (Vertex* source = _vertices)
            {
                System.Buffer.MemoryCopy(source,
                    _uiBuffers.GetMappedPointer(_activeFrameIndex), uiSize, uiSize);
            }
            uiFrameBuffer = _uiBuffers.GetBuffer(_activeFrameIndex);
        }

        // Draw persistent editor chrome below viewport textures.
        var pushConstants = _pushConstants;
        if (_contentUiVertexCount > 0)
        {
            _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipelines.UiPipeline);

            ulong offset = 0;
            _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &uiFrameBuffer, &offset);
            _vk.CmdPushConstants(commandBuffer, _pipelines.UiLayout, ShaderStageFlags.VertexBit,
                0, (uint)sizeof(PushConstants), &pushConstants);
            _vk.CmdDraw(commandBuffer, _contentUiVertexCount, 1, 0, 0);
        }

        // Draw FBO textures for each viewport
        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipelines.TexturePipeline);

        foreach (var (viewportId, fbo) in _viewportFbos)
        {
            if (fbo.IsDirty)
                continue;

            if (_viewportQuadBuffers.TryGetValue(viewportId, out var quadBuffers)
                && _viewportQuadVertices.TryGetValue(viewportId, out var quadVertices))
            {
                var vertexCount = (uint)quadVertices.Length;
                quadBuffers.Ensure(_activeFrameIndex, vertexCount, VertexT.Stride);
                var quadSize = (nuint)(quadVertices.Length * VertexT.Stride);
                fixed (VertexT* source = quadVertices)
                {
                    System.Buffer.MemoryCopy(source,
                        quadBuffers.GetMappedPointer(_activeFrameIndex), quadSize, quadSize);
                }
                fixed (DescriptorSet* pDescSet = &fbo.DescriptorSet)
                {
                    _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics,
                        _pipelines.TextureLayout, 0, 1, pDescSet, 0, null);
                }

                var texVb = quadBuffers.GetBuffer(_activeFrameIndex);
                ulong texOffset = 0;
                _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &texVb, &texOffset);

                _vk.CmdPushConstants(commandBuffer, _pipelines.TextureLayout,
                    ShaderStageFlags.VertexBit, 0, (uint)sizeof(PushConstants), &pushConstants);

                _vk.CmdDraw(commandBuffer, vertexCount, 1, 0, 0);
            }
        }

        // Draw 2D overlay (gizmo lines, etc.)
        if (_overlayVertices.Length > 0)
        {
            _overlayBuffers!.Ensure(_activeFrameIndex, (uint)_overlayVertices.Length, Vertex.Stride);

            _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipelines.UiPipeline);

            var ovSize = (nuint)(_overlayVertices.Length * Vertex.Stride);
            fixed (Vertex* pVerts = _overlayVertices)
            {
                System.Buffer.MemoryCopy(pVerts, _overlayBuffers.GetMappedPointer(_activeFrameIndex), ovSize, ovSize);
            }

            var ovB = _overlayBuffers.GetBuffer(_activeFrameIndex);
            ulong ovOffset = 0;
            _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &ovB, &ovOffset);

            _vk.CmdPushConstants(commandBuffer, _pipelines.UiLayout, ShaderStageFlags.VertexBit,
                0, (uint)sizeof(PushConstants), &pushConstants);

            _vk.CmdDraw(commandBuffer, (uint)_overlayVertices.Length, 1, 0, 0);
        }

        // Draw floating UI last so menus and dialogs cover viewport textures and gizmos.
        if (_overlayUiVertexCount > 0)
        {
            _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipelines.UiPipeline);

            ulong uiOffset = 0;
            _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &uiFrameBuffer, &uiOffset);
            _vk.CmdPushConstants(commandBuffer, _pipelines.UiLayout, ShaderStageFlags.VertexBit,
                0, (uint)sizeof(PushConstants), &pushConstants);
            _vk.CmdDraw(commandBuffer, _overlayUiVertexCount, 1, _overlayUiFirstVertex, 0);
        }

        _vk.CmdEndRenderPass(commandBuffer);
    }

    public void Run()
    {
        _logger.LogInformation("Entering main loop...");
        _window?.Run();
    }

    public void Shutdown()
    {
        if (_shutdown)
            return;

        _shutdown = true;
        _logger.LogInformation("Shutting down...");
        _windowsWindowChrome?.Dispose();
        _windowsWindowChrome = null;

        if (_vk is null)
        {
            _input?.Dispose();
            return;
        }

        if (_device.Handle != 0)
            _vk.DeviceWaitIdle(_device);

        CleanupSwapchain();

        // Destroy frame scheduler
        _frameScheduler?.Destroy();

        // Cleanup viewport FBOs and their vertex buffers
        foreach (var (id, fbo) in _viewportFbos)
            fbo.Destroy(_vk!, _device);
        _viewportFbos.Clear();

        foreach (var buffers in _viewportQuadBuffers.Values)
            buffers.Destroy();
        _viewportQuadBuffers.Clear();
        _viewportQuadVertices.Clear();

        // Cleanup persistent viewport draw buffer
        _viewportDrawBuffers?.Destroy();
        _overlayBuffers?.Destroy();

        // Cleanup shared resources
        _uiBuffers?.Destroy();
        _pipelines?.Dispose();
        if (_device.Handle != 0 && _fboRenderPass.Handle != 0)
            _vk.DestroyRenderPass(_device, _fboRenderPass, null);

        if (_device.Handle != 0 && _renderPass.Handle != 0)
            _vk.DestroyRenderPass(_device, _renderPass, null);
        if (_instance.Handle != 0 && _surface.Handle != 0)
            _khrSurface?.DestroySurface(_instance, _surface, null);
        if (_device.Handle != 0)
            _vk.DestroyDevice(_device, null);
        if (_instance.Handle != 0)
            _vk.DestroyInstance(_instance, null);

        _input?.Dispose();
        _fontRasterizer.Dispose();

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
        _window = null;
        GC.SuppressFinalize(this);
    }

    private struct QueueFamilyIndices
    {
        public uint? GraphicsFamily;
        public uint? PresentFamily;
    }

}
