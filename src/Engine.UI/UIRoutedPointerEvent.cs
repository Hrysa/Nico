using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Identifies the current phase of routed UI input.</summary>
public enum UIRoutePhase
{
    /// <summary>Routing from the root toward the target.</summary>
    Preview,
    /// <summary>Dispatch at the target.</summary>
    Target,
    /// <summary>Routing from the target toward the root.</summary>
    Bubble
}

/// <summary>Identifies the semantic kind of routed pointer input.</summary>
public enum UIPointerEventKind
{
    /// <summary>Pointer movement.</summary>
    Move,
    /// <summary>Pointer-button press.</summary>
    Press,
    /// <summary>Pointer-button release.</summary>
    Release,
    /// <summary>Pointer double click.</summary>
    DoubleClick,
    /// <summary>Pointer-wheel movement.</summary>
    Wheel
}

/// <summary>Handles one routed pointer event.</summary>
/// <param name="sender">Element currently receiving the route.</param>
/// <param name="pointerEvent">Reusable event data valid only during synchronous dispatch.</param>
public delegate void UIPointerEventHandler(UIElement sender, UIPointerEventArgs pointerEvent);

/// <summary>Provides reusable device-neutral data for one synchronously routed pointer event.</summary>
public sealed class UIPointerEventArgs
{
    private Action<UIElement>? _capturePointer;
    private Action? _releasePointerCapture;
    /// <summary>Gets the semantic input kind.</summary>
    public UIPointerEventKind Kind { get; private set; }

    /// <summary>Gets the current routing phase.</summary>
    public UIRoutePhase RoutePhase { get; private set; }

    /// <summary>Gets the original hit-tested or captured target.</summary>
    public UIElement Source { get; private set; } = null!;

    /// <summary>Gets the element currently receiving the event.</summary>
    public UIElement CurrentTarget { get; private set; } = null!;

    /// <summary>Gets the pointer identity supplied by the input source.</summary>
    public int PointerId { get; private set; }

    /// <summary>Gets the pointer position in logical host coordinates.</summary>
    public Vector2 Position { get; private set; }

    /// <summary>Gets the position relative to the current target.</summary>
    public Vector2 LocalPosition { get; private set; }

    /// <summary>Gets pointer movement since the preceding event.</summary>
    public Vector2 Delta { get; private set; }

    /// <summary>Gets horizontal and vertical wheel movement.</summary>
    public Vector2 WheelDelta { get; private set; }

    /// <summary>Gets the button associated with a press, release, or double click.</summary>
    public InputPointerButton Button { get; private set; }

    /// <summary>Gets the click count associated with a button event.</summary>
    public int ClickCount { get; private set; }

    /// <summary>Gets the pointing-device kind.</summary>
    public PointerDeviceKind DeviceKind { get; private set; }

    /// <summary>Gets keyboard modifiers active for this event.</summary>
    public InputModifiers Modifiers { get; private set; }

    /// <summary>Gets pointer buttons held during this event.</summary>
    public PointerButtons PressedButtons { get; private set; }

    /// <summary>Gets or sets whether later route handlers and compatibility behavior are suppressed.</summary>
    public bool Handled { get; set; }

    /// <summary>Captures subsequent pointer input to the current routed receiver.</summary>
    public void CapturePointer() => _capturePointer?.Invoke(CurrentTarget);

    /// <summary>Releases pointer capture owned by this event's router.</summary>
    public void ReleasePointerCapture() => _releasePointerCapture?.Invoke();

    /// <summary>Initializes this reusable instance for one pointer route.</summary>
    /// <param name="kind">Semantic pointer event kind.</param>
    /// <param name="source">Original route target.</param>
    /// <param name="pointerId">Pointer identity.</param>
    /// <param name="position">Logical host position.</param>
    /// <param name="delta">Pointer movement.</param>
    /// <param name="wheelDelta">Wheel movement.</param>
    /// <param name="button">Associated pointer button.</param>
    /// <param name="clickCount">Associated click count.</param>
    /// <param name="deviceKind">Pointing-device kind.</param>
    /// <param name="modifiers">Active modifiers.</param>
    /// <param name="pressedButtons">Held pointer buttons.</param>
    /// <param name="capturePointer">Router capture callback.</param>
    /// <param name="releasePointerCapture">Router capture-release callback.</param>
    internal void Reset(
        UIPointerEventKind kind,
        UIElement source,
        int pointerId,
        Vector2 position,
        Vector2 delta,
        Vector2 wheelDelta,
        InputPointerButton button,
        int clickCount,
        PointerDeviceKind deviceKind,
        InputModifiers modifiers,
        PointerButtons pressedButtons,
        Action<UIElement> capturePointer,
        Action releasePointerCapture)
    {
        Kind = kind;
        Source = source;
        CurrentTarget = source;
        RoutePhase = UIRoutePhase.Target;
        PointerId = pointerId;
        Position = position;
        LocalPosition = position - new Vector2(source.Left, source.Top);
        Delta = delta;
        WheelDelta = wheelDelta;
        Button = button;
        ClickCount = clickCount;
        DeviceKind = deviceKind;
        Modifiers = modifiers;
        PressedButtons = pressedButtons;
        _capturePointer = capturePointer;
        _releasePointerCapture = releasePointerCapture;
        Handled = false;
    }

    /// <summary>Changes the current target and phase while traversing a stable route.</summary>
    /// <param name="target">Element receiving the next callback.</param>
    /// <param name="phase">Current route phase.</param>
    internal void SetCurrentTarget(UIElement target, UIRoutePhase phase)
    {
        CurrentTarget = target;
        RoutePhase = phase;
        LocalPosition = Position - new Vector2(target.Left, target.Top);
    }
}
