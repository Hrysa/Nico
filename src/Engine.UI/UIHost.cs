using System.Numerics;
using System.Globalization;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Controls whether routed UI navigation and gameplay input run concurrently.</summary>
public enum UIInputContextMode
{
    /// <summary>Leaves controller navigation entirely to gameplay.</summary>
    GameplayOnly,

    /// <summary>Routes controller navigation to UI while allowing gameplay input to continue.</summary>
    Shared,

    /// <summary>Routes controller navigation to UI and suppresses gameplay input.</summary>
    UIExclusive
}

/// <summary>Selects how a UI host requests recurring native-window updates.</summary>
public enum UIHostSchedulingMode
{
    /// <summary>Leaves continuous-rendering ownership with the application.</summary>
    ExternallyManaged,

    /// <summary>Disables recurring updates and renders only after explicit wake requests.</summary>
    EventDriven,

    /// <summary>Keeps recurring updates enabled for a full-speed game UI.</summary>
    Continuous,

    /// <summary>Enables recurring updates only while retained UI timers or animations are active.</summary>
    Hybrid
}

/// <summary>Controls standard UI routing after a host pointer-button preview.</summary>
public enum UIHostPointerRouting
{
    /// <summary>Routes the event normally, including click generation on release.</summary>
    Route,

    /// <summary>Routes the event but suppresses compatible click generation on release.</summary>
    RouteWithoutClick,

    /// <summary>Consumes the event before it reaches the retained UI router.</summary>
    Consume
}

/// <summary>Hosts one independent UI tree, renderer, input router, and native-window lifecycle.</summary>
public sealed class UIHost : IDisposable
{
    private readonly IWindow _window;
    private readonly IInputSource _input;
    private readonly IInputSourceV2? _inputV2;
    private readonly IPointerGestureSource? _gestureInput;
    private readonly INavigationInputSource? _navigationInput;
    private readonly ITextInputMethodSource? _textInputMethod;
    private readonly IRenderer _renderer;
    private readonly ITextLayoutService? _previousTextLayoutOverride;
    private readonly bool _ownsTextLayoutOverride;
    private readonly IUIViewportPolicy _viewportPolicy;
    private readonly IUIRasterScaleService? _rasterScaleService;
    private readonly IInteractiveFrameScheduler? _interactiveFrameScheduler;
    private UIViewportLayout _viewportLayout;
    private Vector2 _clientSize;
    private bool _disposed;
    private UIInputContextMode _inputContext;
    private PointerCursorKind _pointerCursor = PointerCursorKind.Default;
    private UINavigationAction? _heldNavigationAction;
    private int _heldNavigationDevice;
    private double _navigationRepeatElapsed;
    private bool _navigationRepeatStarted;
    private bool _deferSnapshotRefresh;
    private readonly UIKeyRepeatController _keyRepeat = new();
    private readonly Action<KeyInputEvent> _routeRepeatedKey;
    private UIHostSchedulingMode _schedulingMode;
    private bool _demandDrivenContinuous;
    private readonly WindowsAccessibilityAdapter? _windowsAccessibility;
    private readonly MacOSAccessibilityAdapter? _macOSAccessibility;

    /// <summary>Gets the root element hosted by this window.</summary>
    public UIElement Root { get; }

    /// <summary>Gets the independent input router for this UI tree.</summary>
    public UIEventRouter InputRouter { get; }

    /// <summary>Gets the dispatcher used to marshal work onto this host's UI thread.</summary>
    public UIDispatcher Dispatcher { get; }

    /// <summary>Gets host-local transient overlay coordination when an overlay was supplied.</summary>
    public UIOverlayManager? OverlayManager { get; }

    /// <summary>Gets or sets an application preview returning true to consume pointer movement.</summary>
    public Func<PointerMoveEvent, bool>? PreviewPointerMove { get; set; }

    /// <summary>Gets or sets an application callback invoked after pointer-move routing.</summary>
    public Action<PointerMoveEvent, bool>? PointerMoveProcessed { get; set; }

    /// <summary>Gets or sets an application preview controlling pointer-button routing.</summary>
    public Func<PointerButtonEvent, UIHostPointerRouting>? PreviewPointerButton { get; set; }

    /// <summary>Gets or sets an application callback invoked after pointer-button routing.</summary>
    public Action<PointerButtonEvent, bool>? PointerButtonProcessed { get; set; }

    /// <summary>Gets or sets an application preview returning true to consume pointer-wheel input.</summary>
    public Func<PointerWheelEvent, bool>? PreviewPointerWheel { get; set; }

    /// <summary>Gets or sets an application preview consuming trackpad magnification.</summary>
    public Func<PointerMagnifyEvent, bool>? PreviewPointerMagnify { get; set; }

    /// <summary>Gets or sets an application preview returning true to consume keyboard input.</summary>
    public Func<KeyInputEvent, bool>? PreviewKey { get; set; }

    /// <summary>Gets or sets an application preview returning true to consume committed text.</summary>
    public Func<string, bool>? PreviewTextInput { get; set; }

    /// <summary>Gets or sets an application preview returning true to consume IME composition.</summary>
    public Func<TextCompositionEvent, bool>? PreviewTextComposition { get; set; }

    /// <summary>Gets the latest logical pointer position reported by this window.</summary>
    public Vector2 PointerPosition { get; private set; }

    /// <summary>Gets whether an externally scheduled host currently needs recurring updates.</summary>
    public bool RequiresContinuousUpdates =>
        HasPendingKeyRepeat || HasActivePointerInteraction || Root.HasActiveTimeUpdates();

    /// <summary>Gets or sets how controller input is shared with gameplay.</summary>
    public UIInputContextMode InputContext
    {
        get => _inputContext;
        set
        {
            if (_inputContext == value)
                return;
            _inputContext = value;
            if (value == UIInputContextMode.GameplayOnly)
                ClearNavigationRepeat();
            InputContextChanged?.Invoke(value);
        }
    }

    /// <summary>Gets whether gameplay should currently receive input.</summary>
    public bool AllowsGameplayInput => InputContext != UIInputContextMode.UIExclusive;

    /// <summary>Occurs when gameplay/UI input arbitration changes.</summary>
    public event Action<UIInputContextMode>? InputContextChanged;

    /// <summary>Occurs after a controller navigation transition is offered to UI.</summary>
    public event Action<NavigationInputEvent, bool>? NavigationProcessed;

    /// <summary>Occurs after a versioned keyboard transition is routed through UI.</summary>
    public event Action<KeyInputEvent>? KeyProcessed;

    /// <summary>Occurs after retained layout is current and before its draw list is submitted.</summary>
    public event Action? LayoutUpdated;

    /// <summary>Gets or sets the initial held-navigation repeat delay in seconds.</summary>
    public double NavigationRepeatDelay { get; set; } = 0.4d;

    /// <summary>Gets or sets the held-navigation repeat interval in seconds.</summary>
    public double NavigationRepeatInterval { get; set; } = 0.1d;

    /// <summary>Gets or sets the delay before a held keyboard key starts repeating.</summary>
    public double KeyRepeatDelay
    {
        get => _keyRepeat.Delay;
        set => _keyRepeat.Delay = value;
    }

    /// <summary>Gets or sets the interval between synthesized held-key repeats.</summary>
    public double KeyRepeatInterval
    {
        get => _keyRepeat.Interval;
        set => _keyRepeat.Interval = value;
    }

    /// <summary>Gets or sets the gameplay simulation scale supplied to scaled UI subtrees.</summary>
    public double SimulationTimeScale { get; set; } = 1d;

    /// <summary>Gets the culture applied to the hosted retained tree.</summary>
    public CultureInfo Culture => Root.Culture;

    /// <summary>Gets the active preference for non-essential UI motion.</summary>
    public UIMotionPreference MotionPreference => Root.MotionPreference;

    /// <summary>Gets or sets how this host owns recurring window updates.</summary>
    public UIHostSchedulingMode SchedulingMode
    {
        get => _schedulingMode;
        set
        {
            Dispatcher.VerifyAccess();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_schedulingMode == value)
                return;
            _schedulingMode = value;
            ApplySchedulingMode();
        }
    }

    /// <summary>Creates and connects one UI host.</summary>
    /// <param name="window">Native window lifecycle.</param>
    /// <param name="input">Window input source.</param>
    /// <param name="renderer">Renderer presenting this UI tree.</param>
    /// <param name="root">Root UI element.</param>
    /// <param name="width">Initial logical width.</param>
    /// <param name="height">Initial logical height.</param>
    /// <param name="overlay">Optional host-local overlay canvas for transient visuals.</param>
    /// <param name="textLayout">Optional host-local text measurement and caret service.</param>
    /// <param name="viewportPolicy">Optional runtime reference-resolution and safe-area policy.</param>
    /// <param name="inputContext">Initial gameplay/UI input arbitration mode.</param>
    /// <param name="schedulingMode">Recurring update ownership policy.</param>
    public UIHost(
        IWindow window,
        IInputSource input,
        IRenderer renderer,
        UIElement root,
        float width,
        float height,
        Canvas? overlay = null,
        ITextLayoutService? textLayout = null,
        IUIViewportPolicy? viewportPolicy = null,
        UIInputContextMode inputContext = UIInputContextMode.Shared,
        UIHostSchedulingMode schedulingMode = UIHostSchedulingMode.ExternallyManaged)
    {
        _routeRepeatedKey = RouteRepeatedKey;
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(root);
        _window = window;
        _input = input;
        _inputV2 = input as IInputSourceV2;
        _gestureInput = input as IPointerGestureSource;
        _navigationInput = input as INavigationInputSource;
        _textInputMethod = input as ITextInputMethodSource;
        _renderer = renderer;
        _viewportPolicy = viewportPolicy ?? StretchUIViewportPolicy.Instance;
        _inputContext = inputContext;
        _schedulingMode = schedulingMode;
        _rasterScaleService = window as IUIRasterScaleService;
        _interactiveFrameScheduler = window as IInteractiveFrameScheduler;
        Root = root;
        Dispatcher = new UIDispatcher(_window.RequestFrame);
        Root.SetHostDispatcher(Dispatcher);
        _previousTextLayoutOverride = Root.TextLayoutOverride;
        _ownsTextLayoutOverride = textLayout is not null;
        if (_ownsTextLayoutOverride)
            Root.TextLayoutOverride = textLayout;
        InputRouter = new UIEventRouter(root, RefreshIfActive, window as IClipboardService);
        OverlayManager = overlay is null ? null : new UIOverlayManager(overlay, InputRouter);
        if (overlay is not null && window is IDisplayService displayService)
            overlay.PopupWorkAreaProvider = new ViewportDisplayPopupWorkAreaProvider(
                displayService, () => _viewportLayout);
        _window.Update += OnUpdate;
        _window.Resized += OnResized;
        if (_inputV2 is not null)
        {
            _inputV2.PointerMoved += OnPointerMoved;
            _inputV2.PointerButtonChanged += OnPointerButtonChanged;
            _inputV2.PointerWheelChanged += OnPointerWheelChanged;
            _inputV2.KeyChanged += OnKeyChanged;
            _inputV2.TextEntered += OnTextEntered;
        }
        else
        {
            _input.MouseMove += OnMouseMove;
            _input.MouseDown += OnMouseDown;
            _input.MouseUp += OnMouseUp;
            _input.MouseDoubleClick += OnMouseDoubleClick;
            _input.MouseScroll += OnMouseScroll;
            _input.KeyDown += OnKeyDown;
            _input.KeyUp += OnKeyUp;
            _input.TextInput += OnTextInput;
        }
        _textInputMethod?.TextCompositionChanged += OnTextCompositionChanged;
        _gestureInput?.PointerMagnified += OnPointerMagnified;
        _navigationInput?.NavigationChanged += OnNavigationChanged;
        _windowsAccessibility = WindowsAccessibilityAdapter.TryCreate(
            window, root, Dispatcher, Refresh);
        _macOSAccessibility = MacOSAccessibilityAdapter.TryCreate(
            window, root, Dispatcher, Refresh);
        Resize(width, height);
        ApplySchedulingMode();
    }

    /// <summary>Rebuilds the retained UI snapshot after a visual or structural change.</summary>
    public void Refresh()
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        SubmitSnapshot();
        _window.RequestFrame();
    }

    /// <summary>Builds and submits the current retained snapshot without scheduling another tick.</summary>
    private void SubmitSnapshot()
    {
        var drawList = Root.BuildDrawList();
        LayoutUpdated?.Invoke();
        _renderer.SubmitUI(drawList);
        _windowsAccessibility?.Update();
        _macOSAccessibility?.Update();
    }

    /// <summary>Refreshes routed visual state unless a synchronous event disposed this host.</summary>
    private void RefreshIfActive()
    {
        if (_disposed)
            return;
        if (_deferSnapshotRefresh)
        {
            Dispatcher.RequestFrame();
            return;
        }
        Refresh();
    }

    /// <summary>Measures, arranges, and submits the root at a new logical size.</summary>
    /// <param name="width">Logical client width.</param>
    /// <param name="height">Logical client height.</param>
    public void Resize(float width, float height)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        width = MathF.Max(1f, width);
        height = MathF.Max(1f, height);
        _clientSize = new Vector2(width, height);
        _viewportLayout = _viewportPolicy.Resolve(
            _clientSize, _rasterScaleService?.RasterScale ?? 1f);
        var content = _viewportLayout.ContentBounds;
        var contentSize = new Vector2(
            MathF.Max(0f, content.Right - content.Left),
            MathF.Max(0f, content.Bottom - content.Top));
        Root.Width = contentSize.X;
        Root.Height = contentSize.Y;
        Root.Measure(contentSize);
        Root.Arrange(new Vector2(content.Left, content.Top), contentSize);
        _renderer.SetPushConstants(CreatePushConstants(
            _viewportLayout.LogicalSize.X, _viewportLayout.LogicalSize.Y));
        Refresh();
    }

    /// <summary>Reapplies a mutable viewport policy after safe-area or user-scale changes.</summary>
    public void RefreshViewportPolicy()
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        Resize(_clientSize.X, _clientSize.Y);
    }

    /// <summary>Applies runtime culture and optionally derives the root text direction.</summary>
    /// <param name="culture">Culture used by UI formatting, lookup, and text behavior.</param>
    /// <param name="updateFlowDirection">Whether to derive direction from the culture.</param>
    public void SetCulture(CultureInfo culture, bool updateFlowDirection = true)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(culture);
        Root.Culture = culture;
        if (updateFlowDirection)
        {
            Root.FlowDirection = culture.TextInfo.IsRightToLeft
                ? UIFlowDirection.RightToLeft
                : UIFlowDirection.LeftToRight;
        }
        Refresh();
    }

    /// <summary>Applies an accessibility preference for non-essential UI motion.</summary>
    /// <param name="motionPreference">New inherited motion preference.</param>
    public void SetMotionPreference(UIMotionPreference motionPreference)
    {
        Dispatcher.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Root.MotionPreference == motionPreference)
            return;
        Root.MotionPreference = motionPreference;
        Refresh();
    }

    /// <summary>Creates orthographic UI push constants for one logical host size.</summary>
    /// <param name="width">Logical host width.</param>
    /// <param name="height">Logical host height.</param>
    /// <returns>Identity model/view and top-left-origin orthographic projection.</returns>
    private static PushConstants CreatePushConstants(float width, float height)
    {
        return new PushConstants
        {
            Model = Matrix4x4.Identity,
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.CreateOrthographicOffCenter(0f, width, 0f, height, -1f, 1f)
        };
    }

    /// <summary>Disconnects window and input events without owning the supplied services.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        Dispatcher.VerifyAccess();
        _disposed = true;
        _window.Update -= OnUpdate;
        _window.Resized -= OnResized;
        if (_inputV2 is not null)
        {
            _inputV2.PointerMoved -= OnPointerMoved;
            _inputV2.PointerButtonChanged -= OnPointerButtonChanged;
            _inputV2.PointerWheelChanged -= OnPointerWheelChanged;
            _inputV2.KeyChanged -= OnKeyChanged;
            _inputV2.TextEntered -= OnTextEntered;
        }
        else
        {
            _input.MouseMove -= OnMouseMove;
            _input.MouseDown -= OnMouseDown;
            _input.MouseUp -= OnMouseUp;
            _input.MouseDoubleClick -= OnMouseDoubleClick;
            _input.MouseScroll -= OnMouseScroll;
            _input.KeyDown -= OnKeyDown;
            _input.KeyUp -= OnKeyUp;
            _input.TextInput -= OnTextInput;
        }
        _textInputMethod?.TextCompositionChanged -= OnTextCompositionChanged;
        _gestureInput?.PointerMagnified -= OnPointerMagnified;
        _navigationInput?.NavigationChanged -= OnNavigationChanged;
        if (_schedulingMode != UIHostSchedulingMode.ExternallyManaged)
            _window.SetContinuousRendering(false);
        if (_pointerCursor != PointerCursorKind.Default)
        {
            _pointerCursor = PointerCursorKind.Default;
            _window.SetPointerCursor(PointerCursorKind.Default);
        }
        OverlayManager?.Dispose();
        _windowsAccessibility?.Dispose();
        _macOSAccessibility?.Dispose();
        Root.CancelAnimationsRecursive();
        Root.DisposeBindingsRecursive();
        Root.SetHostDispatcher(null);
        if (_ownsTextLayoutOverride)
            Root.TextLayoutOverride = _previousTextLayoutOverride;
        Dispatcher.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Drains worker-posted UI work before the host's update callback completes.</summary>
    /// <param name="deltaTime">Elapsed update time, unused by dispatcher work.</param>
    private void OnUpdate(double deltaTime)
    {
        Dispatcher.BeginFrame();
        Dispatcher.Drain();
        AdvanceNavigationRepeat(deltaTime);
        AdvanceKeyRepeat(deltaTime);
        var timeScale = double.IsFinite(SimulationTimeScale)
            ? Math.Max(0d, SimulationTimeScale)
            : 1d;
        var timeChanged = Root.AdvanceTime(deltaTime, deltaTime * timeScale);
        if (timeChanged || Root.RequiresDrawListRebuild)
            SubmitSnapshot();
        if (_schedulingMode is UIHostSchedulingMode.EventDriven or UIHostSchedulingMode.Hybrid)
            UpdateDemandDrivenScheduling();
    }

    /// <summary>Applies the selected recurring-update ownership policy.</summary>
    private void ApplySchedulingMode()
    {
        if (_schedulingMode == UIHostSchedulingMode.ExternallyManaged)
            return;
        if (_schedulingMode is UIHostSchedulingMode.EventDriven or UIHostSchedulingMode.Hybrid)
        {
            UpdateDemandDrivenScheduling(force: true);
            return;
        }
        _demandDrivenContinuous = false;
        _window.SetContinuousRendering(_schedulingMode == UIHostSchedulingMode.Continuous);
    }

    /// <summary>Starts or stops recurring updates for retained timers and synthesized key repeat.</summary>
    /// <param name="force">Whether to apply state even when the cached value matches.</param>
    private void UpdateDemandDrivenScheduling(bool force = false)
    {
        var continuous = HasPendingKeyRepeat || HasActivePointerInteraction ||
            (_schedulingMode == UIHostSchedulingMode.Hybrid && Root.HasActiveTimeUpdates());
        if (!force && _demandDrivenContinuous == continuous)
            return;
        _demandDrivenContinuous = continuous;
        _window.SetContinuousRendering(continuous);
    }

    /// <summary>Relays one versioned pointer-move event.</summary>
    /// <param name="pointerEvent">Device-neutral pointer movement.</param>
    private void OnPointerMoved(PointerMoveEvent pointerEvent)
    {
        var logicalPosition = _viewportLayout.ToLogical(pointerEvent.Position);
        PointerPosition = logicalPosition;
        var logicalEvent = pointerEvent with
        {
            Position = logicalPosition,
            Delta = _viewportLayout.DeltaToLogical(pointerEvent.Delta)
        };
        var routed = PreviewPointerMove?.Invoke(logicalEvent) != true;
        if (routed)
            InputRouter.RoutePointerMove(logicalEvent);
        if (_disposed)
            return;
        UpdatePointerCursor(routed);
        PointerMoveProcessed?.Invoke(logicalEvent, routed);
        UpdatePointerInteractionScheduling();
        PresentCapturedInteraction();
    }

    /// <summary>Updates the host cursor from captured drag or routed hover state.</summary>
    /// <param name="routed">Whether retained UI received the current pointer event.</param>
    private void UpdatePointerCursor(bool routed)
    {
        var thumb = InputRouter.CapturedElement as Thumb;
        if (thumb is null || !thumb.IsDragging)
            thumb = routed ? InputRouter.HoveredElement as Thumb : null;
        var cursor = thumb?.CursorKind ?? PointerCursorKind.Default;
        if (_pointerCursor == cursor)
            return;
        _pointerCursor = cursor;
        _window.SetPointerCursor(cursor);
    }

    /// <summary>Relays one versioned pointer-button event.</summary>
    /// <param name="pointerEvent">Device-neutral button transition.</param>
    private void OnPointerButtonChanged(PointerButtonEvent pointerEvent)
    {
        var logicalEvent = pointerEvent with
        {
            Position = _viewportLayout.ToLogical(pointerEvent.Position)
        };
        PointerPosition = logicalEvent.Position;
        var routing = PreviewPointerButton?.Invoke(logicalEvent) ?? UIHostPointerRouting.Route;
        var routed = routing != UIHostPointerRouting.Consume;
        if (routed)
        {
            InputRouter.RoutePointerMove(new PointerMoveEvent(
                logicalEvent.PointerId, logicalEvent.Position, Vector2.Zero,
                logicalEvent.DeviceKind, logicalEvent.Modifiers, logicalEvent.PressedButtons));
            if (logicalEvent.ClickCount >= 2 && logicalEvent.IsPressed)
                InputRouter.DoubleClick(logicalEvent);
            else if (logicalEvent.IsPressed)
                InputRouter.Press(logicalEvent);
            else
                InputRouter.Release(logicalEvent,
                    invokeClick: routing == UIHostPointerRouting.Route);
        }
        if (_disposed)
            return;
        UpdatePointerCursor(routed);
        PointerButtonProcessed?.Invoke(logicalEvent, routed);
        UpdatePointerInteractionScheduling();
    }

    /// <summary>Relays one versioned pointer-wheel event.</summary>
    /// <param name="pointerEvent">Device-neutral wheel movement.</param>
    private void OnPointerWheelChanged(PointerWheelEvent pointerEvent)
    {
        var logicalEvent = pointerEvent with
        {
            Position = _viewportLayout.ToLogical(pointerEvent.Position)
        };
        PointerPosition = logicalEvent.Position;
        _deferSnapshotRefresh = true;
        try
        {
            if (PreviewPointerWheel?.Invoke(logicalEvent) != true)
                InputRouter.Scroll(logicalEvent);
        }
        finally
        {
            _deferSnapshotRefresh = false;
        }
    }

    /// <summary>Relays one native trackpad magnification gesture.</summary>
    /// <param name="pointerEvent">Device-neutral incremental magnification.</param>
    private void OnPointerMagnified(PointerMagnifyEvent pointerEvent)
    {
        var logicalEvent = pointerEvent with
        {
            Position = _viewportLayout.ToLogical(pointerEvent.Position)
        };
        PointerPosition = logicalEvent.Position;
        PreviewPointerMagnify?.Invoke(logicalEvent);
    }

    /// <summary>Relays one versioned keyboard transition.</summary>
    /// <param name="keyEvent">Device-neutral key transition.</param>
    private void OnKeyChanged(KeyInputEvent keyEvent)
    {
        UpdateKeyRepeatState(keyEvent);
        if (PreviewKey?.Invoke(keyEvent) != true)
            InputRouter.RouteKey(keyEvent);
        KeyProcessed?.Invoke(keyEvent);
    }

    /// <summary>Relays committed versioned text input.</summary>
    /// <param name="text">Committed Unicode text.</param>
    private void OnTextEntered(string text)
    {
        if (PreviewTextInput?.Invoke(text) != true)
            InputRouter.RouteText(text);
    }

    /// <summary>Relays native input-method composition through the focused UI route.</summary>
    /// <param name="composition">Composition transition.</param>
    private void OnTextCompositionChanged(TextCompositionEvent composition)
    {
        if (PreviewTextComposition?.Invoke(composition) != true)
            InputRouter.RouteTextComposition(composition);
    }

    /// <summary>Routes controller navigation according to the active gameplay/UI input context.</summary>
    /// <param name="navigationEvent">Controller navigation transition.</param>
    private void OnNavigationChanged(NavigationInputEvent navigationEvent)
    {
        if (InputContext == UIInputContextMode.GameplayOnly)
        {
            NavigationProcessed?.Invoke(navigationEvent, false);
            return;
        }
        if (IsDirectionalNavigation(navigationEvent.Action))
        {
            if (navigationEvent.IsPressed && !navigationEvent.IsRepeat)
            {
                _heldNavigationAction = navigationEvent.Action;
                _heldNavigationDevice = navigationEvent.DeviceId;
                _navigationRepeatElapsed = 0d;
                _navigationRepeatStarted = false;
            }
            else if (!navigationEvent.IsPressed &&
                _heldNavigationAction == navigationEvent.Action &&
                _heldNavigationDevice == navigationEvent.DeviceId)
            {
                ClearNavigationRepeat();
            }
        }
        var handled = InputRouter.RouteNavigation(navigationEvent);
        NavigationProcessed?.Invoke(navigationEvent, handled);
    }

    /// <summary>Advances held directional controller repeat using host rather than simulation time.</summary>
    /// <param name="deltaTime">Unscaled host elapsed seconds.</param>
    private void AdvanceNavigationRepeat(double deltaTime)
    {
        if (_heldNavigationAction is not { } action || deltaTime <= 0d ||
            !double.IsFinite(deltaTime))
            return;
        _navigationRepeatElapsed += deltaTime;
        var threshold = _navigationRepeatStarted
            ? Math.Max(0.01d, NavigationRepeatInterval)
            : Math.Max(0d, NavigationRepeatDelay);
        if (_navigationRepeatElapsed < threshold)
            return;
        _navigationRepeatElapsed -= threshold;
        _navigationRepeatStarted = true;
        var navigationEvent = new NavigationInputEvent(
            action, true, IsRepeat: true, DeviceId: _heldNavigationDevice);
        var handled = InputRouter.RouteNavigation(navigationEvent);
        NavigationProcessed?.Invoke(navigationEvent, handled);
    }

    /// <summary>Clears held controller navigation repeat state.</summary>
    private void ClearNavigationRepeat()
    {
        _heldNavigationAction = null;
        _navigationRepeatElapsed = 0d;
        _navigationRepeatStarted = false;
    }

    /// <summary>Gets whether this host must synthesize repeat for a held non-modifier key.</summary>
    private bool HasPendingKeyRepeat => _keyRepeat.IsRepeatPending;

    /// <summary>Gets whether routed pointer capture or drag requires high-rate event pumping.</summary>
    private bool HasActivePointerInteraction =>
        InputRouter.CapturedElement is not null || InputRouter.IsDragging;

    /// <summary>Refreshes demand-driven scheduling after pointer ownership may have changed.</summary>
    private void UpdatePointerInteractionScheduling()
    {
        if (_schedulingMode is UIHostSchedulingMode.EventDriven or UIHostSchedulingMode.Hybrid)
            UpdateDemandDrivenScheduling();
    }

    /// <summary>Presents captured movement before the current native event batch can collapse it.</summary>
    private void PresentCapturedInteraction()
    {
        if (HasActivePointerInteraction)
            _interactiveFrameScheduler?.PresentInteractiveFrame();
    }

    /// <summary>Tracks the most recently pressed non-modifier key and native repeat availability.</summary>
    /// <param name="keyEvent">Incoming device-neutral key transition.</param>
    private void UpdateKeyRepeatState(KeyInputEvent keyEvent)
    {
        _keyRepeat.Observe(keyEvent);
        if (_schedulingMode is UIHostSchedulingMode.EventDriven or UIHostSchedulingMode.Hybrid)
            UpdateDemandDrivenScheduling();
    }

    /// <summary>Advances held keyboard repeat using unscaled host time.</summary>
    /// <param name="deltaTime">Elapsed host time in seconds.</param>
    private void AdvanceKeyRepeat(double deltaTime)
    {
        _keyRepeat.Advance(deltaTime, _routeRepeatedKey);
    }

    /// <summary>Routes one synthesized held-key transition through the hosted UI tree.</summary>
    /// <param name="keyEvent">Synthesized repeat transition.</param>
    private void RouteRepeatedKey(KeyInputEvent keyEvent)
    {
        if (PreviewKey?.Invoke(keyEvent) != true)
            InputRouter.RouteKey(keyEvent);
        KeyProcessed?.Invoke(keyEvent);
    }

    /// <summary>Gets whether an action participates in held directional repeat.</summary>
    /// <param name="action">Navigation action.</param>
    /// <returns>True for the four spatial directions.</returns>
    private static bool IsDirectionalNavigation(UINavigationAction action) =>
        action is UINavigationAction.Up or UINavigationAction.Down or
            UINavigationAction.Left or UINavigationAction.Right;

    /// <summary>Relays native resize events into UI layout.</summary>
    /// <param name="width">Logical width.</param>
    /// <param name="height">Logical height.</param>
    private void OnResized(int width, int height) => Resize(width, height);

    /// <summary>Relays pointer movement.</summary>
    /// <param name="position">Logical pointer position.</param>
    private void OnMouseMove(Vector2 position)
    {
        PointerPosition = _viewportLayout.ToLogical(position);
        InputRouter.MovePointer(PointerPosition);
        if (_disposed)
            return;
        UpdatePointerCursor(routed: true);
        UpdatePointerInteractionScheduling();
        PresentCapturedInteraction();
    }

    /// <summary>Relays pointer press.</summary>
    /// <param name="button">Native button identifier.</param>
    private void OnMouseDown(int button)
    {
        InputRouter.Press();
        if (_disposed)
            return;
        UpdatePointerCursor(routed: true);
        UpdatePointerInteractionScheduling();
    }

    /// <summary>Relays pointer release.</summary>
    /// <param name="button">Native button identifier.</param>
    private void OnMouseUp(int button)
    {
        InputRouter.Release(invokeClick: true);
        if (_disposed)
            return;
        UpdatePointerCursor(routed: true);
        UpdatePointerInteractionScheduling();
    }

    /// <summary>Relays pointer double-click.</summary>
    /// <param name="button">Native button identifier.</param>
    private void OnMouseDoubleClick(int button) => InputRouter.DoubleClick();

    /// <summary>Relays pointer scrolling.</summary>
    /// <param name="offset">Wheel offset.</param>
    private void OnMouseScroll(float offset)
    {
        _deferSnapshotRefresh = true;
        try
        {
            InputRouter.Scroll(offset);
        }
        finally
        {
            _deferSnapshotRefresh = false;
        }
    }

    /// <summary>Relays keyboard press.</summary>
    /// <param name="key">Engine key.</param>
    private void OnKeyDown(InputKey key)
    {
        OnKeyChanged(new KeyInputEvent(
            key, true, IsRepeat: _keyRepeat.IsHeld(key), InputModifiers.None));
    }

    /// <summary>Relays keyboard release.</summary>
    /// <param name="key">Engine key.</param>
    private void OnKeyUp(InputKey key)
    {
        OnKeyChanged(new KeyInputEvent(key, false, false, InputModifiers.None));
    }

    /// <summary>Relays text input.</summary>
    /// <param name="character">Produced character.</param>
    private void OnTextInput(char character) => InputRouter.TextInput(character);
}
