using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Identifies a requested or accepted drag-and-drop operation.</summary>
[Flags]
public enum UIDragEffect
{
    /// <summary>No drag operation.</summary>
    None = 0,
    /// <summary>Copy the payload.</summary>
    Copy = 1,
    /// <summary>Move the payload.</summary>
    Move = 2,
    /// <summary>Link to the payload.</summary>
    Link = 4
}

/// <summary>Identifies the semantic kind of routed drag input.</summary>
public enum UIDragEventKind
{
    /// <summary>The drag entered a target.</summary>
    Enter,
    /// <summary>The drag moved over a target.</summary>
    Over,
    /// <summary>The drag left a target.</summary>
    Leave,
    /// <summary>The payload was dropped.</summary>
    Drop,
    /// <summary>The drag was canceled.</summary>
    Cancel
}

/// <summary>Wraps one strongly typed drag payload.</summary>
public sealed class UIDragData
{
    private readonly object _value;

    /// <summary>Gets the runtime payload type.</summary>
    public Type DataType => _value.GetType();

    /// <summary>Gets the short label used by drag preview visuals.</summary>
    public string DisplayText { get; }

    /// <summary>Creates drag data around one non-null value.</summary>
    /// <param name="value">Value carried by the drag operation.</param>
    /// <param name="displayText">Optional short preview label.</param>
    public UIDragData(object value, string? displayText = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
        DisplayText = displayText ?? value.ToString() ?? value.GetType().Name;
    }

    /// <summary>Attempts to read the payload as the requested type.</summary>
    /// <typeparam name="T">Requested payload type.</typeparam>
    /// <param name="value">Typed value when compatible.</param>
    /// <returns>True when the payload is assignable to the requested type.</returns>
    public bool TryGet<T>(out T? value)
    {
        if (_value is T typed)
        {
            value = typed;
            return true;
        }
        value = default;
        return false;
    }
}

/// <summary>Handles one synchronously routed drag event.</summary>
/// <param name="sender">Element currently receiving the route.</param>
/// <param name="dragEvent">Reusable event data valid only during dispatch.</param>
public delegate void UIDragEventHandler(UIElement sender, UIDragEventArgs dragEvent);

/// <summary>Provides data and negotiated effects for routed drag-and-drop input.</summary>
public sealed class UIDragEventArgs
{
    /// <summary>Gets the semantic drag event kind.</summary>
    public UIDragEventKind Kind { get; private set; }

    /// <summary>Gets the current route phase.</summary>
    public UIRoutePhase RoutePhase { get; private set; }

    /// <summary>Gets the drag source.</summary>
    public UIElement Source { get; private set; } = null!;

    /// <summary>Gets the original drop target.</summary>
    public UIElement Target { get; private set; } = null!;

    /// <summary>Gets the element currently receiving the event.</summary>
    public UIElement CurrentTarget { get; private set; } = null!;

    /// <summary>Gets the typed drag payload.</summary>
    public UIDragData Data { get; private set; } = null!;

    /// <summary>Gets the effects permitted by the source.</summary>
    public UIDragEffect AllowedEffects { get; private set; }

    /// <summary>Gets or sets the effect accepted by a drop target.</summary>
    public UIDragEffect Effect { get; set; }

    /// <summary>Gets the pointer position in host coordinates.</summary>
    public Vector2 Position { get; private set; }

    /// <summary>Gets the pointer position relative to the current target.</summary>
    public Vector2 LocalPosition { get; private set; }

    /// <summary>Gets or sets the host-coordinate bounds used by the shared drop indicator.</summary>
    public UIClipRect? DropIndicatorBounds { get; set; }

    /// <summary>Gets or sets whether later route handlers are suppressed.</summary>
    public bool Handled { get; set; }

    /// <summary>Initializes this reusable instance for one drag route.</summary>
    /// <param name="kind">Semantic event kind.</param>
    /// <param name="source">Drag source.</param>
    /// <param name="target">Original drop target.</param>
    /// <param name="data">Typed payload.</param>
    /// <param name="allowedEffects">Source-permitted effects.</param>
    /// <param name="effect">Currently negotiated effect.</param>
    /// <param name="position">Host pointer position.</param>
    internal void Reset(UIDragEventKind kind, UIElement source, UIElement target, UIDragData data,
        UIDragEffect allowedEffects, UIDragEffect effect, Vector2 position)
    {
        Kind = kind;
        Source = source;
        Target = target;
        CurrentTarget = target;
        Data = data;
        AllowedEffects = allowedEffects;
        Effect = effect;
        Position = position;
        LocalPosition = position - new Vector2(target.Left, target.Top);
        DropIndicatorBounds = null;
        RoutePhase = UIRoutePhase.Target;
        Handled = false;
    }

    /// <summary>Changes the current receiver while traversing the route.</summary>
    /// <param name="target">Current route element.</param>
    /// <param name="phase">Current route phase.</param>
    internal void SetCurrentTarget(UIElement target, UIRoutePhase phase)
    {
        CurrentTarget = target;
        RoutePhase = phase;
        LocalPosition = Position - new Vector2(target.Left, target.Top);
    }
}
