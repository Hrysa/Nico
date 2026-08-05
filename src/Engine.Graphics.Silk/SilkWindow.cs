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
    private static int _nextRendererId;

    private readonly ILogger _logger;
    private readonly uint _rendererId;
    private readonly SilkWindow? _sharedDeviceOwner;
    private bool _shutdown;
    private Silk.NET.Windowing.IWindow? _window;
    private IInputContext? _input;
    private IMouse? _mouse;
    private IKeyboard? _keyboard;

    private Instance _instance;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    private SampleCountFlags _msaaSamples = SampleCountFlags.Count1Bit;
    private int _requestedMsaaSamples = 4;
    private Device _device;
    private Queue _graphicsQueue;
    private Queue _presentQueue;
    private uint _graphicsQueueFamily;
    private uint _presentQueueFamily;
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
    private float _viewportRenderScale = 1f;
    private WindowsWindowChrome? _windowsWindowChrome;
    private WindowsWindowCloak? _windowsWindowCloak;
    private bool _customTitleBar;
    private int _requestedWidth;
    private int _requestedHeight;
    private int _pendingInitialLogicalWidth;
    private int _pendingInitialLogicalHeight;
    private long _lastLiveResizeFrameTimestamp;
    private bool _renderingFrame;
    private bool _continuousRendering;
    private bool _eventDrivenIdle;
    private double _targetFrameRate;
    private Timer? _continuousWakeTimer;
    private bool _firstFramePresented;
#if DEBUG_GC_ALLOC
    private long _frameAllocationStart;
    private ulong _allocationFrameNumber;
#endif

    private Vk? _vk;

    // Render graph for per-pass command buffers
    private FrameScheduler? _frameScheduler;

    private PipelineResources _pipelines = null!;
    private readonly TrueTypeFontRasterizer _fontRasterizer = new();

    // New: Vertex buffer
    private readonly NativeBuffer<Vertex> _vertices = new();
    private readonly NativeBuffer<VertexT> _textVertices = new();
    private readonly List<UiBatch> _uiBatches = [];
    private uint _vertexCount;
    private ulong _uiGeneration = 1;
    private ulong _submittedUiGeneration;
    private readonly ulong[] _uploadedUiGenerations = new ulong[MaxFramesInFlight];
    private PushConstants _pushConstants;
    private FrameVertexBuffers? _uiBuffers;
    private FrameVertexBuffers? _textBuffers;
    private FontAtlasTexture? _fontAtlas;
    private readonly ulong[] _uploadedTextGenerations = new ulong[MaxFramesInFlight];
    private readonly NativeBuffer<Vertex>[] _uploadedUiVertices = [new(), new()];
    private readonly NativeBuffer<VertexT>[] _uploadedTextVertices = [new(), new()];

    // Viewport FBO management
    private readonly Dictionary<uint, ViewportFbo> _viewportFbos = new();
    private readonly Dictionary<uint, FrameVertexBuffers> _viewportQuadBuffers = new();
    private PersistentVertexArena? _persistentVertices;
    private PersistentIndexedMeshStore? _persistentIndexedMeshes;
    private PersistentTextureStore? _persistentTextures;
    private FrameTransientArena? _transientArena;
    private readonly List<MeshHandle>[] _retiredMeshes = [[], []];
    private readonly List<TextureHandle>[] _retiredTextures = [[], []];
    private readonly HashSet<MeshHandle> _pendingMeshRetirements = [];
    private readonly HashSet<TextureHandle> _pendingTextureRetirements = [];
    private uint _nextMeshHandle = 1;
    private uint _nextTextureHandle = 1;
    private TextureHandle _defaultModelTexture;
    private readonly Dictionary<uint, VertexT[]> _viewportQuadVertices = new();
    private readonly Dictionary<uint, ulong> _viewportQuadGenerations = new();
    private readonly Dictionary<uint, ulong[]> _uploadedViewportQuadGenerations = new();
    private readonly Dictionary<uint, VertexT[][]> _uploadedViewportQuadVertices = new();
    private readonly Dictionary<uint, List<RenderCommand>> _pendingViewportDraws = new();
    private readonly Dictionary<uint, GridPushConstants> _pendingGridDraws = new();
    private readonly HashSet<uint> _pendingViewportRenders = [];
    private uint _nextViewportId = 1;
    private RenderPass _fboRenderPass;

    // 2D overlay vertices (drawn on top of everything in swapchain pass)
    private Vertex[] _overlayVertices = [];
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
        _rendererId = checked((uint)Interlocked.Increment(ref _nextRendererId));
    }

    /// <summary>Creates a window that shares the initialized Vulkan device of another window.</summary>
    /// <param name="sharedDeviceOwner">Primary window that owns the Vulkan instance and device.</param>
    /// <param name="loggerFactory">Factory used to create backend loggers.</param>
    public SilkWindow(SilkWindow sharedDeviceOwner, ILoggerFactory loggerFactory)
        : this(loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(sharedDeviceOwner);
        _sharedDeviceOwner = sharedDeviceOwner;
    }

    public void Initialize(WindowOptions options)
    {
        if (_window is not null)
            throw new InvalidOperationException("The window has already been initialized.");

        _shutdown = false;
        _customTitleBar = options.CustomTitleBar;
        _eventDrivenIdle = options.IsEventDriven;
        _targetFrameRate = options.TargetFrameRate;
        if (options.ViewportRenderScale < 0f)
            throw new ArgumentOutOfRangeException(nameof(options),
                "Viewport render scale cannot be negative.");
        _viewportRenderScale = options.ViewportRenderScale == 0f
            ? 1f : options.ViewportRenderScale;
        if (options.MsaaSamples is not (0 or 1 or 2 or 4 or 8))
            throw new ArgumentOutOfRangeException(nameof(options),
                "MSAA samples must be zero, one, two, four, or eight.");
        _requestedMsaaSamples = options.MsaaSamples == 0 ? 4 : options.MsaaSamples;
        _requestedWidth = options.Width;
        _requestedHeight = options.Height;
        _logger.LogInformation("Creating window '{Title}' ({Width}x{Height}) [Vulkan]", options.Title, options.Width, options.Height);

        var settings = new Silk.NET.Windowing.WindowOptions
        {
            Size = new Vector2D<int>(options.Width, options.Height),
            Title = options.Title,
            API = new GraphicsAPI(ContextAPI.Vulkan, new APIVersion(1, 1)),
            ShouldSwapAutomatically = false,
            IsEventDriven = options.IsEventDriven,
            FramesPerSecond = options.TargetFrameRate,
            UpdatesPerSecond = options.TargetFrameRate,
            IsVisible = !OperatingSystem.IsWindows(),
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
            _window.Center(null);
        }
        _logger.LogInformation("Window initialized");
    }

    /// <inheritdoc/>
    public void SubmitUI(UIDrawList drawList)
    {
        ArgumentNullException.ThrowIfNull(drawList);
        if (_submittedUiGeneration == drawList.Generation)
            return;
        BuildUIVertices(drawList);
        _vertexCount = (uint)_vertices.Count;
        _uiGeneration = drawList.Generation;
        _submittedUiGeneration = drawList.Generation;
        _uiBuffers ??= new FrameVertexBuffers(
            _vk!, _device, MaxFramesInFlight, Math.Max(1024u, (uint)_vertices.Count),
            "UI", FindMemoryType, _logger);
        _textBuffers ??= new FrameVertexBuffers(
            _vk!, _device, MaxFramesInFlight, Math.Max(1024u, (uint)_textVertices.Count),
            "UI text", FindMemoryType, _logger);
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

        if (_sharedDeviceOwner is null)
        {
            _vk = Vk.GetApi();
            CreateInstance();
            CreateSurface();
            PickPhysicalDevice();
            CreateLogicalDevice();
        }
        else
        {
            AdoptSharedDevice(_sharedDeviceOwner);
            CreateSurface();
            EnsureSharedDeviceCanPresent();
        }
        _pipelines = new PipelineResources(_vk!, _device);
        _frameScheduler = new FrameScheduler(
            _vk!, _device, _graphicsQueue, _graphicsQueueFamily);
        _persistentVertices = new PersistentVertexArena(_vk!, _device, FindMemoryType, _logger);
        _persistentIndexedMeshes = new PersistentIndexedMeshStore(
            _vk!, _device, FindMemoryType, _logger);
        _transientArena = new FrameTransientArena(
            _vk!, _device, MaxFramesInFlight, FindMemoryType, _logger);
        CreateSwapchain();
        CreateFboRenderPass();
        CreateRenderPass();
        CreateGraphicsPipeline();
        CreateFboGraphicsPipeline();
        CreateModelPipeline();
        _persistentTextures = new PersistentTextureStore(_vk!, _device, FindMemoryType,
            _pipelines.ModelTextureDescriptorSetLayout,
            _pipelines.ModelTextureDescriptorPool);
        _defaultModelTexture = CreateTexture(new TextureResource(1, 1,
            [255, 255, 255, 255], TextureColorSpace.Srgb));
        CreateGridPipeline();
        CreateFramebuffers();
        CreateSyncObjects();
        CreateTexturePipeline();
        _fontAtlas = new FontAtlasTexture(_vk!, _device, _fontRasterizer, FindMemoryType,
            _pipelines.TextureDescriptorSetLayout, _pipelines.TextureDescriptorPool);

        _logger.LogInformation("Vulkan initialization complete");
    }

    /// <summary>Copies immutable device handles from the primary window.</summary>
    /// <param name="owner">Initialized primary window.</param>
    private void AdoptSharedDevice(SilkWindow owner)
    {
        if (owner._vk is null || owner._device.Handle == 0 || owner._instance.Handle == 0)
            throw new InvalidOperationException(
                "The shared-device owner must be initialized before secondary windows.");
        _vk = owner._vk;
        _instance = owner._instance;
        _physicalDevice = owner._physicalDevice;
        _device = owner._device;
        _graphicsQueue = owner._graphicsQueue;
        _presentQueue = owner._presentQueue;
        _graphicsQueueFamily = owner._graphicsQueueFamily;
        _presentQueueFamily = owner._presentQueueFamily;
        _msaaSamples = owner._msaaSamples;
    }

    /// <summary>Verifies the shared physical device supports presenting to this window surface.</summary>
    private void EnsureSharedDeviceCanPresent()
    {
        _khrSurface!.GetPhysicalDeviceSurfaceSupport(
            _physicalDevice, _presentQueueFamily, _surface, out var supported);
        if (supported != new Bool32(true))
            throw new InvalidOperationException(
                "The shared Vulkan present queue cannot present to the secondary window surface.");
    }

    /// <summary>Finalizes Windows chrome, DPI, size, and placement before Vulkan reads the surface extent.</summary>
    private void InitializeWindowsClientGeometry()
    {
        if (!OperatingSystem.IsWindows() || _window is null)
            return;

        if (_customTitleBar)
            _windowsWindowChrome = WindowsWindowChrome.Apply(_window);
        _windowsWindowCloak = WindowsWindowCloak.Apply(_window);

        _uiFramebufferScale = CalculateFramebufferScale();
        _uiInputScale = _uiFramebufferScale;
        _window.Size = new Vector2D<int>(
            Math.Max(1, (int)MathF.Round(_requestedWidth * _uiFramebufferScale)),
            Math.Max(1, (int)MathF.Round(_requestedHeight * _uiFramebufferScale)));
        _window.Center(null);
        var framebufferSize = _window.FramebufferSize;
        _pendingInitialLogicalWidth = Math.Max(1,
            (int)MathF.Round(framebufferSize.X / _uiFramebufferScale));
        _pendingInitialLogicalHeight = Math.Max(1,
            (int)MathF.Round(framebufferSize.Y / _uiFramebufferScale));
    }

    private void OnUpdate(double delta)
    {
#if DEBUG_GC_ALLOC
        _frameAllocationStart = GC.GetAllocatedBytesForCurrentThread();
#endif
        if (_pendingInitialLogicalWidth > 0 && _pendingInitialLogicalHeight > 0)
        {
            Resized?.Invoke(_pendingInitialLogicalWidth, _pendingInitialLogicalHeight);
            _pendingInitialLogicalWidth = 0;
            _pendingInitialLogicalHeight = 0;
        }

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
#if DEBUG_GC_ALLOC
            var allocationEnd = GC.GetAllocatedBytesForCurrentThread();
            var allocatedBytes = Math.Max(0L, allocationEnd - _frameAllocationStart);
            _allocationFrameNumber++;
            _logger.LogInformation(
                "Frame {FrameNumber} GC allocation: {AllocatedBytes} bytes",
                _allocationFrameNumber, allocatedBytes);
#endif
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
                _msaaSamples = SelectMsaaSamples(props, _requestedMsaaSamples);
                _logger.LogInformation("Using GPU: {Name}", SilkMarshal.PtrToString((nint)props.DeviceName, NativeStringEncoding.UTF8));
                _logger.LogInformation("Viewport MSAA: {Samples}", _msaaSamples);
                return;
            }
        }

        throw new Exception("No suitable Vulkan physical device found");
    }

    /// <summary>
    /// Selects the highest supported color/depth sample count up to the requested value.
    /// </summary>
    /// <param name="properties">Physical-device properties.</param>
    /// <param name="requestedSamples">Requested maximum sample count.</param>
    /// <returns>The selected sample count.</returns>
    private static SampleCountFlags SelectMsaaSamples(
        PhysicalDeviceProperties properties,
        int requestedSamples)
    {
        var supported = properties.Limits.FramebufferColorSampleCounts
            & properties.Limits.FramebufferDepthSampleCounts;
        if (requestedSamples >= 8 && (supported & SampleCountFlags.Count8Bit) != 0)
            return SampleCountFlags.Count8Bit;
        if (requestedSamples >= 4 && (supported & SampleCountFlags.Count4Bit) != 0)
            return SampleCountFlags.Count4Bit;
        if (requestedSamples >= 2 && (supported & SampleCountFlags.Count2Bit) != 0)
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
        _graphicsQueueFamily = indices.GraphicsFamily!.Value;
        _presentQueueFamily = indices.PresentFamily!.Value;
        _logger.LogDebug("Graphics queue family: {Family}, Present queue family: {Family}", indices.GraphicsFamily, indices.PresentFamily);

        var queuePriorityArr = new[] { 1.0f };
        fixed (float* pQueuePriority = queuePriorityArr)
        {
            var queueFamilies = _graphicsQueueFamily == _presentQueueFamily
                ? new[] { _graphicsQueueFamily }
                : new[] { _graphicsQueueFamily, _presentQueueFamily };
            var queueCreateInfos = stackalloc DeviceQueueCreateInfo[queueFamilies.Length];
            for (var index = 0; index < queueFamilies.Length; index++)
            {
                queueCreateInfos[index] = new DeviceQueueCreateInfo
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = queueFamilies[index],
                    QueueCount = 1,
                    PQueuePriorities = pQueuePriority
                };
            }

            var extensions = new List<string> { KhrSwapchain.ExtensionName };
            if (SupportsDeviceExtension(_physicalDevice, "VK_KHR_portability_subset"))
                extensions.Add("VK_KHR_portability_subset");

            _logger.LogDebug("Enabling device extensions: {Extensions}", string.Join(", ", extensions));
            var extensionNamesMem = SilkMarshal.StringArrayToPtr(extensions.ToArray(), NativeStringEncoding.UTF8);

            var createInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = (uint)queueFamilies.Length,
                PQueueCreateInfos = queueCreateInfos,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = (byte**)extensionNamesMem
            };

            _logger.LogDebug("Calling vkCreateDevice...");
            var result = _vk!.CreateDevice(_physicalDevice, &createInfo, null, out _device);
            SilkMarshal.Free(extensionNamesMem);

            if (result != Result.Success)
                throw new Exception($"Failed to create logical device: {result}");
        }

        _vk.GetDeviceQueue(_device, _graphicsQueueFamily, 0, out _graphicsQueue);
        _vk.GetDeviceQueue(_device, _presentQueueFamily, 0, out _presentQueue);

        _logger.LogInformation("Logical device created");
    }

    private void CreateSwapchain()
    {
        var framebufferSize = _window!.FramebufferSize;
        _swapchainManager = new SwapchainManager(_vk!, _device, _physicalDevice, _surface,
            _khrSurface!, _graphicsQueueFamily, _presentQueueFamily, _logger);
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

    /// <summary>Creates the built-in forward pipeline for indexed static models.</summary>
    private void CreateModelPipeline()
    {
        _logger.LogDebug("Creating indexed model pipeline");
        var textureBinding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit
        };
        var descriptorLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &textureBinding
        };
        var descriptorResult = _vk!.CreateDescriptorSetLayout(_device, &descriptorLayoutInfo,
            null, out _pipelines.ModelTextureDescriptorSetLayout);
        if (descriptorResult != Result.Success)
            throw new InvalidOperationException(
                $"Failed to create model texture descriptor layout: {descriptorResult}");
        const uint maximumModelTextures = 1024;
        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = maximumModelTextures
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = maximumModelTextures
        };
        descriptorResult = _vk.CreateDescriptorPool(_device, &poolInfo, null,
            out _pipelines.ModelTextureDescriptorPool);
        if (descriptorResult != Result.Success)
            throw new InvalidOperationException(
                $"Failed to create model texture descriptor pool: {descriptorResult}");
        var pushConstantRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Size = (uint)sizeof(PushConstants)
        };
        var descriptorLayout = _pipelines.ModelTextureDescriptorSetLayout;
        var modelLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &descriptorLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushConstantRange
        };
        descriptorResult = _vk.CreatePipelineLayout(_device, &modelLayoutInfo, null,
            out _pipelines.ModelLayout);
        if (descriptorResult != Result.Success)
            throw new InvalidOperationException(
                $"Failed to create model pipeline layout: {descriptorResult}");
        _pipelines.ModelVertexShader = CreateShaderModule("model.vert.spv");
        _pipelines.ModelFragmentShader = CreateShaderModule("model.frag.spv");
        var entryPointName = SilkMarshal.StringToPtr("main", NativeStringEncoding.UTF8);
        try
        {
            var stages = new[]
            {
                new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = _pipelines.ModelVertexShader,
                    PName = (byte*)entryPointName
                },
                new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = _pipelines.ModelFragmentShader,
                    PName = (byte*)entryPointName
                }
            };
            var binding = new VertexInputBindingDescription
            {
                Binding = 0,
                Stride = ForwardModelVertex.Stride,
                InputRate = VertexInputRate.Vertex
            };
            var attributes = new[]
            {
                new VertexInputAttributeDescription
                {
                    Binding = 0, Location = 0, Format = Format.R32G32B32Sfloat, Offset = 0
                },
                new VertexInputAttributeDescription
                {
                    Binding = 0, Location = 1, Format = Format.R32G32B32Sfloat,
                    Offset = sizeof(float) * 3u
                },
                new VertexInputAttributeDescription
                {
                    Binding = 0, Location = 2, Format = Format.R32G32Sfloat,
                    Offset = sizeof(float) * 6u
                },
                new VertexInputAttributeDescription
                {
                    Binding = 0, Location = 3, Format = Format.R32G32B32A32Sfloat,
                    Offset = sizeof(float) * 8u
                }
            };
            fixed (PipelineShaderStageCreateInfo* stagePointer = stages)
            fixed (VertexInputAttributeDescription* attributePointer = attributes)
            {
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = 1,
                    PVertexBindingDescriptions = &binding,
                    VertexAttributeDescriptionCount = checked((uint)attributes.Length),
                    PVertexAttributeDescriptions = attributePointer
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList
                };
                var dynamicStates = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor };
                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 2,
                    PDynamicStates = dynamicStates
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
                    CullMode = CullModeFlags.BackBit,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1f
                };
                var multisampling = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = _msaaSamples
                };
                var depthStencil = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = true,
                    DepthWriteEnable = true,
                    DepthCompareOp = CompareOp.LessOrEqual
                };
                var blendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                        ColorComponentFlags.BBit | ColorComponentFlags.ABit
                };
                var blending = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &blendAttachment
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = stagePointer,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterizer,
                    PMultisampleState = &multisampling,
                    PDepthStencilState = &depthStencil,
                    PColorBlendState = &blending,
                    PDynamicState = &dynamicState,
                    Layout = _pipelines.ModelLayout,
                    RenderPass = _fboRenderPass,
                    Subpass = 0
                };
                var result = _vk!.CreateGraphicsPipelines(_device, default, 1, &pipelineInfo,
                    null, out _pipelines.ModelPipeline);
                if (result != Result.Success)
                    throw new InvalidOperationException(
                        $"Failed to create indexed model pipeline: {result}");
            }
        }
        finally
        {
            SilkMarshal.Free(entryPointName);
        }
        _logger.LogInformation("Indexed model pipeline created");
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
        const uint maxTextureSets = 32;
        var poolSize = new DescriptorPoolSize
        {
            Type = DescriptorType.CombinedImageSampler,
            DescriptorCount = maxTextureSets
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
            MaxSets = maxTextureSets
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
    public RenderViewHandle CreateRenderView(float width, float height)
    {
        var id = _nextViewportId++;
        var pixelSize = CalculateViewportPixelSize(width, height);
        var fbo = new ViewportFbo(id, pixelSize.Width, pixelSize.Height);
        var deviceLocalMemoryType = FindMemoryType(0xFFFFFFFF, MemoryPropertyFlags.DeviceLocalBit);
        fbo.Create(_vk!, _device, _fboRenderPass, _swapchainManager!.ImageFormat, FindDepthFormat(), _msaaSamples,
            deviceLocalMemoryType,
            _pipelines.TextureDescriptorSetLayout, _pipelines.TextureDescriptorPool);
        _viewportFbos[id] = fbo;
        _logger.LogInformation(
            "Viewport {Id} registered ({LogicalWidth}x{LogicalHeight} logical, {PixelWidth}x{PixelHeight} pixels, {Scale:F2}x scale)",
            id, width, height, pixelSize.Width, pixelSize.Height,
            _uiFramebufferScale * _viewportRenderScale);
        return new RenderViewHandle(CreateOwnedHandle(id));
    }

    /// <inheritdoc/>
    public void DestroyRenderView(RenderViewHandle view)
    {
        var viewportId = GetLocalId(view);
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
        _viewportQuadGenerations.Remove(viewportId);
        _uploadedViewportQuadGenerations.Remove(viewportId);
        _uploadedViewportQuadVertices.Remove(viewportId);

        _pendingViewportDraws.Remove(viewportId);
        _pendingGridDraws.Remove(viewportId);
        _pendingViewportRenders.Remove(viewportId);

        _logger.LogInformation("Viewport {Id} unregistered", viewportId);
    }

    /// <inheritdoc/>
    public void ResizeRenderView(RenderViewHandle view, float width, float height)
    {
        var viewportId = GetLocalId(view);
        if (_viewportFbos.TryGetValue(viewportId, out var fbo))
        {
            var pixelSize = CalculateViewportPixelSize(width, height);
            fbo.Resize(pixelSize.Width, pixelSize.Height);
        }
    }

    /// <summary>Converts a logical viewport extent into physical render-target pixels.</summary>
    /// <param name="width">Logical viewport width.</param>
    /// <param name="height">Logical viewport height.</param>
    /// <returns>The clamped physical render-target extent.</returns>
    private (uint Width, uint Height) CalculateViewportPixelSize(float width, float height)
    {
        var scale = _uiFramebufferScale * _viewportRenderScale;
        return (
            checked((uint)Math.Max(1, MathF.Round(width * scale))),
            checked((uint)Math.Max(1, MathF.Round(height * scale))));
    }

    /// <inheritdoc/>
    public void SetViewportQuadVertices(RenderViewHandle view, VertexT[] vertices)
    {
        var viewportId = GetLocalId(view);
        ArgumentNullException.ThrowIfNull(vertices);
        if (!_viewportQuadBuffers.ContainsKey(viewportId))
        {
            _viewportQuadBuffers[viewportId] = new FrameVertexBuffers(
                _vk!, _device, MaxFramesInFlight, 6, $"viewport {viewportId} quad",
                FindMemoryType, _logger);
        }

        _viewportQuadVertices[viewportId] = vertices;
        _viewportQuadGenerations[viewportId] = _viewportQuadGenerations.GetValueOrDefault(viewportId) + 1;
        _uploadedViewportQuadGenerations.TryAdd(viewportId, new ulong[MaxFramesInFlight]);
        _uploadedViewportQuadVertices.TryAdd(viewportId, [[], []]);
    }

    /// <inheritdoc/>
    public ViewportRenderContext CreateRenderContext(RenderViewHandle view)
    {
        var viewportId = GetLocalId(view);
        var fbo = _viewportFbos[viewportId];
        return new ViewportRenderContext
        {
            View = view,
            Width = fbo.Width,
            Height = fbo.Height
        };
    }

    /// <inheritdoc/>
    public void Submit(RenderViewHandle view, RenderQueue renderQueue)
    {
        var viewportId = GetLocalId(view);
        ArgumentNullException.ThrowIfNull(renderQueue);
        if (!_viewportFbos.ContainsKey(viewportId))
            throw new ArgumentOutOfRangeException(nameof(viewportId), viewportId, "Viewport is not registered.");

        if (!_pendingViewportDraws.ContainsKey(viewportId))
            _pendingViewportDraws[viewportId] = [];

        foreach (var command in renderQueue.Commands)
        {
            ValidateOwner(command.Mesh);
            _pendingViewportDraws[viewportId].Add(command);
        }
        _pendingViewportRenders.Add(viewportId);
    }

    /// <inheritdoc/>
    public MeshHandle CreateMesh(MeshDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(description.Vertices);
        var handle = new MeshHandle(CreateOwnedHandle(_nextMeshHandle++));
        _persistentVertices!.Add(handle, description.Vertices);
        return handle;
    }

    /// <inheritdoc/>
    public MeshHandle CreateStaticMesh(StaticMeshResource mesh, StandardMaterialResource material)
    {
        var handle = new MeshHandle(CreateOwnedHandle(_nextMeshHandle++));
        var vertices = BuiltInForwardMeshBuilder.BuildIndexedVertices(mesh, material);
        var texture = material.BaseColorTexture.IsValid
            ? material.BaseColorTexture : _defaultModelTexture;
        ValidateOwner(texture);
        _persistentTextures!.GetDescriptor(texture);
        _persistentIndexedMeshes!.Add(handle, vertices, mesh.Indices, texture);
        return handle;
    }

    /// <inheritdoc/>
    public TextureHandle CreateTexture(TextureResource texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        var handle = new TextureHandle(CreateOwnedHandle(_nextTextureHandle++));
        _persistentTextures!.Add(handle, texture);
        return handle;
    }

    /// <inheritdoc/>
    public void DestroyTexture(TextureHandle texture)
    {
        ValidateOwner(texture);
        if (texture == _defaultModelTexture)
            throw new InvalidOperationException("The renderer default texture cannot be destroyed.");
        if (!_pendingTextureRetirements.Add(texture))
            throw new InvalidOperationException("The texture is already pending destruction.");
        _retiredTextures[_activeFrameIndex].Add(texture);
    }

    /// <inheritdoc/>
    public void UpdateMesh(MeshHandle mesh, MeshUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(update.Vertices);
        ValidateOwner(mesh);
        if (_persistentIndexedMeshes!.Contains(mesh))
            throw new InvalidOperationException("Immutable static model meshes cannot be updated.");
        _persistentVertices!.Update(mesh, update);
    }

    /// <inheritdoc/>
    public void DestroyMesh(MeshHandle mesh)
    {
        ValidateOwner(mesh);
        if (!_persistentIndexedMeshes!.Contains(mesh))
            _persistentVertices!.GetBinding(mesh);
        if (!_pendingMeshRetirements.Add(mesh))
            throw new InvalidOperationException("The mesh is already pending destruction.");
        _retiredMeshes[_activeFrameIndex].Add(mesh);
    }

    /// <inheritdoc/>
    public void DrawGroundGrid(RenderViewHandle renderView, Matrix4x4 view, Matrix4x4 projection)
    {
        var viewportId = GetLocalId(renderView);
        var viewProjection = view * projection;
        if (!_viewportFbos.ContainsKey(viewportId)
            || !Matrix4x4.Invert(viewProjection, out var inverseViewProjection))
            return;

        _pendingGridDraws[viewportId] = new GridPushConstants
        {
            ViewProjection = viewProjection,
            InverseViewProjection = inverseViewProjection
        };
        _pendingViewportRenders.Add(viewportId);
    }

    /// <inheritdoc/>
    public void SubmitTransient(TransientGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry.Vertices);
        _overlayVertices = geometry.Vertices;
    }

    /// <inheritdoc/>
    public void SetViewportClearColor(RenderViewHandle view, float r, float g, float b, float a = 1.0f)
    {
        var viewportId = GetLocalId(view);
        if (_viewportFbos.TryGetValue(viewportId, out var fbo))
            fbo.ClearColor = new Vector4(r, g, b, a);
    }

    /// <summary>Combines this renderer's identity with a local resource identifier.</summary>
    /// <param name="localId">Non-zero renderer-local identifier.</param>
    /// <returns>Opaque renderer-owned handle value.</returns>
    private ulong CreateOwnedHandle(uint localId)
    {
        return ((ulong)_rendererId << 32) | localId;
    }

    /// <summary>Validates a render-view owner and extracts its local identifier.</summary>
    /// <param name="view">Render view handle to validate.</param>
    /// <returns>Renderer-local view identifier.</returns>
    private uint GetLocalId(RenderViewHandle view)
    {
        if (!view.IsValid || (uint)(view.Value >> 32) != _rendererId)
            throw new ArgumentException("Render view belongs to a different renderer.", nameof(view));
        return (uint)view.Value;
    }

    /// <summary>Ensures a mesh handle belongs to this renderer.</summary>
    /// <param name="mesh">Mesh handle to validate.</param>
    private void ValidateOwner(MeshHandle mesh)
    {
        if (!mesh.IsValid || (uint)(mesh.Value >> 32) != _rendererId)
            throw new ArgumentException("Mesh belongs to a different renderer.", nameof(mesh));
    }

    /// <summary>Ensures a texture handle belongs to this renderer.</summary>
    /// <param name="texture">Texture handle to validate.</param>
    private void ValidateOwner(TextureHandle texture)
    {
        if (!texture.IsValid || (uint)(texture.Value >> 32) != _rendererId)
            throw new ArgumentException("Texture handle belongs to another renderer.",
                nameof(texture));
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

    /// <summary>Translates semantic UI rectangles into backend triangle vertices.</summary>
    /// <param name="drawList">UI draw list.</param>
    private void BuildUIVertices(UIDrawList drawList)
    {
        ArgumentNullException.ThrowIfNull(drawList);
        var framebufferScale = GetFramebufferScale();
        _vertices.Clear();
        _textVertices.Clear();
        _vertices.EnsureCapacity(drawList.Commands.Count * 6);
        _uiBatches.Clear();
        foreach (var command in drawList.Commands)
        {
            if (command.Type == UIDrawCommandType.Text)
            {
                var firstVertex = (uint)_textVertices.Count;
                var caretLeft = _fontRasterizer.AppendVertices(
                    _textVertices, command, framebufferScale);
                AddUiBatch(UiGeometryKind.Text, command.Layer, firstVertex,
                    (uint)_textVertices.Count - firstVertex);
                if (command.CaretIndex >= 0)
                {
                    firstVertex = (uint)_vertices.Count;
                    AppendUICommandVertices(_vertices, new UIDrawCommand(
                        caretLeft, command.Top, caretLeft + 1f,
                        command.Top + command.FontPixelHeight, command.Color,
                        Layer: command.Layer));
                    AddUiBatch(UiGeometryKind.Color, command.Layer, firstVertex,
                        (uint)_vertices.Count - firstVertex);
                }
            }
            else
            {
                var firstVertex = (uint)_vertices.Count;
                AppendUICommandVertices(_vertices, command);
                AddUiBatch(UiGeometryKind.Color, command.Layer, firstVertex,
                    (uint)_vertices.Count - firstVertex);
            }
        }
    }

    /// <summary>Translates one semantic UI command into triangle vertices.</summary>
    /// <param name="vertices">Destination vertex collection.</param>
    /// <param name="command">Semantic UI command.</param>
    private static void AppendUICommandVertices(NativeBuffer<Vertex> vertices, UIDrawCommand command)
    {
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

    /// <summary>Adds or extends one ordered UI geometry batch.</summary>
    /// <param name="kind">Vertex/pipeline kind.</param>
    /// <param name="layer">UI composition layer.</param>
    /// <param name="firstVertex">First vertex in the kind-specific buffer.</param>
    /// <param name="vertexCount">Number of vertices added.</param>
    private void AddUiBatch(
        UiGeometryKind kind,
        UIDrawLayer layer,
        uint firstVertex,
        uint vertexCount)
    {
        if (vertexCount == 0)
            return;
        if (_uiBatches.Count > 0)
        {
            var previous = _uiBatches[^1];
            if (previous.Kind == kind && previous.Layer == layer
                && previous.FirstVertex + previous.VertexCount == firstVertex)
            {
                _uiBatches[^1] = previous with { VertexCount = previous.VertexCount + vertexCount };
                return;
            }
        }
        _uiBatches.Add(new UiBatch(kind, layer, firstVertex, vertexCount));
    }

    /// <summary>Tessellates one filled UI ellipse into triangles.</summary>
    /// <param name="vertices">Destination vertex collection.</param>
    /// <param name="command">Ellipse command and bounds.</param>
    private static void AppendEllipseVertices(NativeBuffer<Vertex> vertices, UIDrawCommand command)
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
        _transientArena!.Reset(frameIndex);
        foreach (var mesh in _retiredMeshes[frameIndex])
        {
            if (_persistentIndexedMeshes!.Contains(mesh))
                _persistentIndexedMeshes.Release(_persistentIndexedMeshes.Remove(mesh));
            else
                _persistentVertices!.Release(_persistentVertices.Remove(mesh));
            _pendingMeshRetirements.Remove(mesh);
        }
        _retiredMeshes[frameIndex].Clear();
        foreach (var texture in _retiredTextures[frameIndex])
        {
            _persistentTextures!.Release(_persistentTextures.Remove(texture));
            _pendingTextureRetirements.Remove(texture);
        }
        _retiredTextures[frameIndex].Clear();

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

        // Record viewport and swapchain passes in submission order on one graphics command buffer.
        // A single queue submission avoids an unnecessary CPU/driver transition and the same-queue
        // semaphore that previously connected these passes.
        Silk.NET.Vulkan.Semaphore renderFinishedSemaphore;
        {
            var (cmdBuffer, sem) = _frameScheduler.BeginPass();
            renderFinishedSemaphore = sem;

            RecordFboPass(cmdBuffer);
            RecordSwapchainPass(cmdBuffer, imageIndex);

            _frameScheduler.EndPass(cmdBuffer);
            _frameScheduler.PrepareCurrentFenceForSubmission();
            _frameScheduler.SubmitPass(cmdBuffer, imageAvailableSemaphore, sem,
                _frameScheduler.GetCurrentFence());
        }

        // ── Present ──
        var swapchain = _swapchainManager.Handle;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &renderFinishedSemaphore,
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
        _persistentVertices!.RecordPendingUploads(commandBuffer, _transientArena!, _activeFrameIndex);
        _persistentIndexedMeshes!.RecordPendingUploads(
            commandBuffer, _transientArena!, _activeFrameIndex);
        _persistentTextures!.RecordPendingUploads(
            commandBuffer, _transientArena!, _activeFrameIndex);
        // ═══════════════════════════════════════════════════════════════
        // Render each viewport's content into its own FBO
        // ═══════════════════════════════════════════════════════════════

        var clearValues = stackalloc ClearValue[2];
        foreach (var (viewportId, fbo) in _viewportFbos)
        {
            if (fbo.IsDirty || !_pendingViewportRenders.Remove(viewportId))
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

                foreach (var draw in draws)
                {
                    if (_persistentIndexedMeshes!.Contains(draw.Mesh))
                    {
                        _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics,
                            _pipelines.ModelPipeline);
                        var indexed = _persistentIndexedMeshes.GetBinding(draw.Mesh);
                        if (indexed.IndexCount == 0)
                            continue;
                        var indexedVertexBuffer = indexed.VertexBuffer;
                        var indexedOffset = 0UL;
                        _vk.CmdBindVertexBuffers(commandBuffer, 0, 1,
                            &indexedVertexBuffer, &indexedOffset);
                        _vk.CmdBindIndexBuffer(commandBuffer, indexed.IndexBuffer, 0,
                            IndexType.Uint32);
                        var modelDescriptor = _persistentTextures.GetDescriptor(indexed.Texture);
                        _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics,
                            _pipelines.ModelLayout, 0, 1, &modelDescriptor, 0, null);
                        var indexedConstants = draw.PushConstants;
                        _vk.CmdPushConstants(commandBuffer, _pipelines.ModelLayout,
                            ShaderStageFlags.VertexBit, 0, (uint)sizeof(PushConstants),
                            &indexedConstants);
                        _vk.CmdDrawIndexed(commandBuffer, indexed.IndexCount, 1, 0, 0, 0);
                        continue;
                    }
                    _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics,
                        _pipelines.ViewportPipeline);
                    var binding = _persistentVertices!.GetBinding(draw.Mesh);
                    if (binding.VertexCount == 0)
                        continue;
                    var vb = binding.Buffer;
                    var bufOffset = binding.ByteOffset;
                    _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &vb, &bufOffset);

                    var pc = draw.PushConstants;
                    _vk.CmdPushConstants(commandBuffer, _pipelines.ViewportLayout, ShaderStageFlags.VertexBit, 0, (uint)sizeof(PushConstants), &pc);

                    _vk.CmdDraw(commandBuffer, binding.VertexCount, 1, 0, 0);
                }

                draws.Clear();
            }

            _vk.CmdEndRenderPass(commandBuffer);
        }
    }

    private void RecordSwapchainPass(CommandBuffer commandBuffer, uint imageIndex)
    {
        _fontAtlas!.RecordPendingUpload(commandBuffer, _transientArena!, _activeFrameIndex);
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
            var reallocated = _uiBuffers!.Ensure(_activeFrameIndex, _vertexCount, Vertex.Stride);
            if (_uploadedUiGenerations[_activeFrameIndex] != _uiGeneration)
            {
                UploadChangedRanges(_vertices.WrittenSpan, _uploadedUiVertices[_activeFrameIndex],
                    _uiBuffers.GetMappedPointer(_activeFrameIndex), Vertex.Stride, reallocated);
                _uploadedUiGenerations[_activeFrameIndex] = _uiGeneration;
            }
            uiFrameBuffer = _uiBuffers.GetBuffer(_activeFrameIndex);
        }
        Silk.NET.Vulkan.Buffer textFrameBuffer = default;
        if (_textVertices.Count > 0)
        {
            var reallocated = _textBuffers!.Ensure(
                _activeFrameIndex, (uint)_textVertices.Count, VertexT.Stride);
            if (_uploadedTextGenerations[_activeFrameIndex] != _uiGeneration)
            {
                UploadChangedRanges(_textVertices.WrittenSpan, _uploadedTextVertices[_activeFrameIndex],
                    _textBuffers.GetMappedPointer(_activeFrameIndex), VertexT.Stride, reallocated);
                _uploadedTextGenerations[_activeFrameIndex] = _uiGeneration;
            }
            textFrameBuffer = _textBuffers.GetBuffer(_activeFrameIndex);
        }

        // Draw persistent editor chrome below viewport textures.
        var pushConstants = _pushConstants;
        DrawUiBatches(commandBuffer, UIDrawLayer.Content, uiFrameBuffer, textFrameBuffer, pushConstants);

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
                var reallocated = quadBuffers.Ensure(_activeFrameIndex, vertexCount, VertexT.Stride);
                var generation = _viewportQuadGenerations[viewportId];
                var uploadedGenerations = _uploadedViewportQuadGenerations[viewportId];
                if (uploadedGenerations[_activeFrameIndex] != generation)
                {
                    var snapshots = _uploadedViewportQuadVertices[viewportId];
                    UploadChangedRanges(quadVertices, ref snapshots[_activeFrameIndex],
                        quadBuffers.GetMappedPointer(_activeFrameIndex), VertexT.Stride, reallocated);
                    uploadedGenerations[_activeFrameIndex] = generation;
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
            _vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipelines.UiPipeline);

            var ovSize = checked((uint)(_overlayVertices.Length * Vertex.Stride));
            var overlayAllocation = _transientArena!.Allocate(_activeFrameIndex, ovSize);
            fixed (Vertex* pVerts = _overlayVertices)
            {
                System.Buffer.MemoryCopy(pVerts, overlayAllocation.MappedPointer, ovSize, ovSize);
            }

            var ovB = overlayAllocation.Buffer;
            var ovOffset = overlayAllocation.ByteOffset;
            _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &ovB, &ovOffset);

            _vk.CmdPushConstants(commandBuffer, _pipelines.UiLayout, ShaderStageFlags.VertexBit,
                0, (uint)sizeof(PushConstants), &pushConstants);

            _vk.CmdDraw(commandBuffer, (uint)_overlayVertices.Length, 1, 0, 0);
        }

        // Draw floating UI last so menus and dialogs cover viewport textures and gizmos.
        DrawUiBatches(commandBuffer, UIDrawLayer.Overlay, uiFrameBuffer, textFrameBuffer, pushConstants);

        _vk.CmdEndRenderPass(commandBuffer);
    }

    /// <summary>Draws ordered colored and glyph-atlas batches for one UI layer.</summary>
    /// <param name="commandBuffer">Recording command buffer.</param>
    /// <param name="layer">Layer to draw.</param>
    /// <param name="colorBuffer">Colored UI vertex buffer.</param>
    /// <param name="textBuffer">Textured glyph vertex buffer.</param>
    /// <param name="pushConstants">Screen-space transforms.</param>
    private void DrawUiBatches(
        CommandBuffer commandBuffer,
        UIDrawLayer layer,
        Silk.NET.Vulkan.Buffer colorBuffer,
        Silk.NET.Vulkan.Buffer textBuffer,
        PushConstants pushConstants)
    {
        foreach (var batch in _uiBatches.Where(batch => batch.Layer == layer))
        {
            ulong offset = 0;
            if (batch.Kind == UiGeometryKind.Color)
            {
                _vk!.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipelines.UiPipeline);
                _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &colorBuffer, &offset);
                _vk.CmdPushConstants(commandBuffer, _pipelines.UiLayout, ShaderStageFlags.VertexBit,
                    0, (uint)sizeof(PushConstants), &pushConstants);
            }
            else
            {
                _vk!.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, _pipelines.TexturePipeline);
                var descriptorSet = _fontAtlas!.DescriptorSet;
                _vk.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics,
                    _pipelines.TextureLayout, 0, 1, &descriptorSet, 0, null);
                _vk.CmdBindVertexBuffers(commandBuffer, 0, 1, &textBuffer, &offset);
                _vk.CmdPushConstants(commandBuffer, _pipelines.TextureLayout, ShaderStageFlags.VertexBit,
                    0, (uint)sizeof(PushConstants), &pushConstants);
            }
            _vk.CmdDraw(commandBuffer, batch.VertexCount, 1, batch.FirstVertex, 0);
        }
    }

    /// <summary>Copies only changed contiguous vertex ranges into a retained mapped buffer.</summary>
    /// <typeparam name="T">Unmanaged vertex type.</typeparam>
    /// <param name="current">Current CPU vertices.</param>
    /// <param name="uploaded">Snapshot represented by the destination buffer.</param>
    /// <param name="destination">Mapped destination buffer.</param>
    /// <param name="stride">Vertex stride in bytes.</param>
    /// <param name="forceFullUpload">Whether the destination backing allocation changed.</param>
    private static void UploadChangedRanges<T>(
        ReadOnlySpan<T> current,
        NativeBuffer<T> uploaded,
        void* destination,
        uint stride,
        bool forceFullUpload)
        where T : unmanaged
    {
        var uploadedSpan = uploaded.WrittenSpan;
        fixed (T* source = current)
        {
            if (forceFullUpload || uploadedSpan.Length != current.Length)
            {
                var byteCount = (nuint)(current.Length * stride);
                System.Buffer.MemoryCopy(source, destination, byteCount, byteCount);
            }
            else
            {
                var comparer = EqualityComparer<T>.Default;
                var index = 0;
                while (index < current.Length)
                {
                    if (comparer.Equals(current[index], uploadedSpan[index]))
                    {
                        index++;
                        continue;
                    }
                    var first = index++;
                    while (index < current.Length && !comparer.Equals(current[index], uploadedSpan[index]))
                        index++;
                    var byteCount = (nuint)((index - first) * stride);
                    var sourceRange = (byte*)source + first * stride;
                    var destinationRange = (byte*)destination + first * stride;
                    System.Buffer.MemoryCopy(sourceRange, destinationRange, byteCount, byteCount);
                }
            }
        }
        uploaded.ReplaceWith(current);
    }

    /// <summary>Copies only changed contiguous array-backed vertex ranges into a mapped buffer.</summary>
    /// <typeparam name="T">Unmanaged vertex type.</typeparam>
    /// <param name="current">Current CPU vertices.</param>
    /// <param name="uploaded">Snapshot represented by the destination buffer.</param>
    /// <param name="destination">Mapped destination buffer.</param>
    /// <param name="stride">Vertex stride in bytes.</param>
    /// <param name="forceFullUpload">Whether the destination backing allocation changed.</param>
    private static void UploadChangedRanges<T>(
        T[] current,
        ref T[] uploaded,
        void* destination,
        uint stride,
        bool forceFullUpload)
        where T : unmanaged
    {
        fixed (T* source = current)
        {
            if (forceFullUpload || uploaded.Length != current.Length)
            {
                var byteCount = (nuint)(current.Length * stride);
                System.Buffer.MemoryCopy(source, destination, byteCount, byteCount);
            }
            else
            {
                var comparer = EqualityComparer<T>.Default;
                var index = 0;
                while (index < current.Length)
                {
                    if (comparer.Equals(current[index], uploaded[index]))
                    {
                        index++;
                        continue;
                    }
                    var first = index++;
                    while (index < current.Length && !comparer.Equals(current[index], uploaded[index]))
                        index++;
                    var byteCount = (nuint)((index - first) * stride);
                    var sourceRange = (byte*)source + first * stride;
                    var destinationRange = (byte*)destination + first * stride;
                    System.Buffer.MemoryCopy(sourceRange, destinationRange, byteCount, byteCount);
                }
            }
        }
        uploaded = current.ToArray();
    }

    /// <summary>Identifies the pipeline and vertex format for a UI batch.</summary>
    private enum UiGeometryKind
    {
        /// <summary>Solid colored geometry.</summary>
        Color,

        /// <summary>Glyph-atlas textured geometry.</summary>
        Text
    }

    /// <summary>Describes one ordered UI draw range.</summary>
    /// <param name="Kind">Pipeline and vertex format.</param>
    /// <param name="Layer">Composition layer.</param>
    /// <param name="FirstVertex">First vertex in the kind-specific buffer.</param>
    /// <param name="VertexCount">Number of vertices.</param>
    private readonly record struct UiBatch(
        UiGeometryKind Kind,
        UIDrawLayer Layer,
        uint FirstVertex,
        uint VertexCount);

    public void Run()
    {
        _logger.LogInformation("Entering main loop...");
        if (_window is null)
            return;
        if (_window.IsVisible)
            _firstFramePresented = true;
        else
            PresentFirstFrameAndReveal();
        _window.Run();
    }

    public void Shutdown()
    {
        if (_shutdown)
            return;

        _shutdown = true;
        _vertices.Dispose();
        _textVertices.Dispose();
        foreach (var uploaded in _uploadedUiVertices)
            uploaded.Dispose();
        foreach (var uploaded in _uploadedTextVertices)
            uploaded.Dispose();
        _continuousWakeTimer?.Dispose();
        _continuousWakeTimer = null;
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

        // Drain deferred retirements while their lookup stores are still alive.
        if (_persistentIndexedMeshes is not null && _persistentVertices is not null)
        {
            foreach (var retired in _retiredMeshes)
            foreach (var mesh in retired)
            {
                if (_persistentIndexedMeshes.Contains(mesh))
                    _persistentIndexedMeshes.Release(_persistentIndexedMeshes.Remove(mesh));
                else
                    _persistentVertices.Release(_persistentVertices.Remove(mesh));
            }
        }
        foreach (var retired in _retiredMeshes)
            retired.Clear();
        _pendingMeshRetirements.Clear();
        _persistentIndexedMeshes?.Destroy();
        _persistentVertices?.Destroy();
        if (_persistentTextures is not null)
        {
            foreach (var retired in _retiredTextures)
            foreach (var texture in retired)
                _persistentTextures.Release(_persistentTextures.Remove(texture));
        }
        foreach (var retired in _retiredTextures)
            retired.Clear();
        _pendingTextureRetirements.Clear();
        _persistentTextures?.Destroy();
        _transientArena?.Destroy();

        // Cleanup shared resources
        _uiBuffers?.Destroy();
        _textBuffers?.Destroy();
        _fontAtlas?.Destroy();
        _pipelines?.Dispose();
        if (_device.Handle != 0 && _fboRenderPass.Handle != 0)
            _vk.DestroyRenderPass(_device, _fboRenderPass, null);

        if (_device.Handle != 0 && _renderPass.Handle != 0)
            _vk.DestroyRenderPass(_device, _renderPass, null);
        if (_instance.Handle != 0 && _surface.Handle != 0)
            _khrSurface?.DestroySurface(_instance, _surface, null);
        if (_sharedDeviceOwner is null && _device.Handle != 0)
            _vk.DestroyDevice(_device, null);
        if (_sharedDeviceOwner is null && _instance.Handle != 0)
            _vk.DestroyInstance(_instance, null);

        _input?.Dispose();
        _fontRasterizer.Dispose();

        _logger.LogInformation("Shutdown complete");
    }

    public void ProcessEvents()
    {
        _window?.DoEvents();
    }

    /// <inheritdoc/>
    public void PumpFrame()
    {
        if (_window is null || _window.IsClosing)
            return;
        if (!_firstFramePresented)
        {
            if (!_window.IsVisible)
            {
                PresentFirstFrameAndReveal();
                return;
            }
            _firstFramePresented = true;
        }
        _window.DoEvents();
        _window.DoUpdate();
        _window.DoRender();
    }

    /// <inheritdoc/>
    public void RequestFrame()
    {
        _window?.ContinueEvents();
    }

    /// <inheritdoc/>
    public void SetContinuousRendering(bool enabled)
    {
        if (_continuousRendering == enabled)
            return;
        _continuousRendering = enabled;
        if (_eventDrivenIdle)
        {
            var interval = TimeSpan.FromSeconds(1d /
                (_targetFrameRate > 0d ? _targetFrameRate : 60d));
            _continuousWakeTimer ??= new Timer(
                static state => ((SilkWindow)state!).RequestFrame(), this,
                Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _continuousWakeTimer.Change(enabled ? TimeSpan.Zero : Timeout.InfiniteTimeSpan,
                enabled ? interval : Timeout.InfiniteTimeSpan);
        }
        RequestFrame();
    }

    /// <summary>Renders complete initialized content before revealing a deferred Windows window.</summary>
    private void PresentFirstFrameAndReveal()
    {
        if (_window is null || _firstFramePresented)
            return;
        if (_windowsWindowCloak is not null && !_window.IsVisible)
            _window.IsVisible = true;
        _window.DoEvents();
        _window.DoUpdate();
        _window.DoRender();
        if (OperatingSystem.IsWindows() && _vk is not null && _device.Handle != 0)
            _vk.DeviceWaitIdle(_device);
        _firstFramePresented = true;
        if (_windowsWindowCloak is not null)
            _windowsWindowCloak.Reveal();
        else if (!_window.IsVisible)
            _window.IsVisible = true;
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
