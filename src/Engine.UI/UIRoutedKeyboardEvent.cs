using Engine.Graphics;

namespace Engine.UI;

/// <summary>Identifies a routed keyboard transition.</summary>
public enum UIKeyEventKind
{
    /// <summary>A key was pressed.</summary>
    KeyDown,
    /// <summary>A key was released.</summary>
    KeyUp
}

/// <summary>Handles one routed keyboard event.</summary>
/// <param name="sender">Element currently receiving the route.</param>
/// <param name="keyEvent">Reusable event data valid only during synchronous dispatch.</param>
public delegate void UIKeyEventHandler(UIElement sender, UIKeyEventArgs keyEvent);

/// <summary>Handles one routed committed-text event.</summary>
/// <param name="sender">Element currently receiving the route.</param>
/// <param name="textEvent">Reusable event data valid only during synchronous dispatch.</param>
public delegate void UITextInputEventHandler(UIElement sender, UITextInputEventArgs textEvent);

/// <summary>Handles one routed input-method composition transition.</summary>
/// <param name="sender">Element currently receiving the route.</param>
/// <param name="compositionEvent">Reusable composition data valid during synchronous dispatch.</param>
public delegate void UITextCompositionEventHandler(UIElement sender, UITextCompositionEventArgs compositionEvent);

/// <summary>Provides reusable device-neutral data for one routed key transition.</summary>
public sealed class UIKeyEventArgs
{
    private Action<UIElement>? _focus;

    /// <summary>Gets the transition kind.</summary>
    public UIKeyEventKind Kind { get; private set; }

    /// <summary>Gets the current routing phase.</summary>
    public UIRoutePhase RoutePhase { get; private set; }

    /// <summary>Gets the element focused when dispatch began.</summary>
    public UIElement Source { get; private set; } = null!;

    /// <summary>Gets the element currently receiving the event.</summary>
    public UIElement CurrentTarget { get; private set; } = null!;

    /// <summary>Gets the logical engine key.</summary>
    public InputKey Key { get; private set; }

    /// <summary>Gets whether this transition is automatic repeat.</summary>
    public bool IsRepeat { get; private set; }

    /// <summary>Gets active keyboard modifiers.</summary>
    public InputModifiers Modifiers { get; private set; }

    /// <summary>Gets or sets whether later route and compatibility behavior are suppressed.</summary>
    public bool Handled { get; set; }

    /// <summary>Moves keyboard focus to an attached element in the active input scope.</summary>
    /// <param name="element">Element that should receive focus.</param>
    public void Focus(UIElement element) => _focus?.Invoke(element);

    /// <summary>Initializes this reusable instance for one key route.</summary>
    /// <param name="kind">Transition kind.</param>
    /// <param name="source">Focused route target.</param>
    /// <param name="key">Logical key.</param>
    /// <param name="isRepeat">Whether this is automatic repeat.</param>
    /// <param name="modifiers">Active modifiers.</param>
    /// <param name="focus">Router focus callback.</param>
    internal void Reset(UIKeyEventKind kind, UIElement source, InputKey key, bool isRepeat,
        InputModifiers modifiers, Action<UIElement> focus)
    {
        Kind = kind;
        Source = source;
        CurrentTarget = source;
        RoutePhase = UIRoutePhase.Target;
        Key = key;
        IsRepeat = isRepeat;
        Modifiers = modifiers;
        _focus = focus;
        Handled = false;
    }

    /// <summary>Changes the current receiver while traversing the route.</summary>
    /// <param name="target">Next receiver.</param>
    /// <param name="phase">Current route phase.</param>
    internal void SetCurrentTarget(UIElement target, UIRoutePhase phase)
    {
        CurrentTarget = target;
        RoutePhase = phase;
    }
}

/// <summary>Provides reusable data for one routed committed-text event.</summary>
public sealed class UITextInputEventArgs
{
    private Action<UIElement>? _focus;

    /// <summary>Gets the current routing phase.</summary>
    public UIRoutePhase RoutePhase { get; private set; }

    /// <summary>Gets the element focused when dispatch began.</summary>
    public UIElement Source { get; private set; } = null!;

    /// <summary>Gets the element currently receiving the event.</summary>
    public UIElement CurrentTarget { get; private set; } = null!;

    /// <summary>Gets committed Unicode text.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Gets or sets whether later route and compatibility behavior are suppressed.</summary>
    public bool Handled { get; set; }

    /// <summary>Moves keyboard focus to an attached element in the active input scope.</summary>
    /// <param name="element">Element that should receive focus.</param>
    public void Focus(UIElement element) => _focus?.Invoke(element);

    /// <summary>Initializes this reusable instance for one text route.</summary>
    /// <param name="source">Focused route target.</param>
    /// <param name="text">Committed text.</param>
    /// <param name="focus">Router focus callback.</param>
    internal void Reset(UIElement source, string text, Action<UIElement> focus)
    {
        Source = source;
        CurrentTarget = source;
        RoutePhase = UIRoutePhase.Target;
        Text = text;
        _focus = focus;
        Handled = false;
    }

    /// <summary>Changes the current receiver while traversing the route.</summary>
    /// <param name="target">Next receiver.</param>
    /// <param name="phase">Current route phase.</param>
    internal void SetCurrentTarget(UIElement target, UIRoutePhase phase)
    {
        CurrentTarget = target;
        RoutePhase = phase;
    }
}

/// <summary>Provides reusable data for one routed input-method composition transition.</summary>
public sealed class UITextCompositionEventArgs
{
    /// <summary>Gets the current routing phase.</summary>
    public UIRoutePhase RoutePhase { get; private set; }
    /// <summary>Gets the element focused when dispatch began.</summary>
    public UIElement Source { get; private set; } = null!;
    /// <summary>Gets the element currently receiving the event.</summary>
    public UIElement CurrentTarget { get; private set; } = null!;
    /// <summary>Gets the composition transition.</summary>
    public TextCompositionKind Kind { get; private set; }
    /// <summary>Gets transient or committed composition text.</summary>
    public string Text { get; private set; } = string.Empty;
    /// <summary>Gets the UTF-16 caret within composition text.</summary>
    public int CaretIndex { get; private set; }
    /// <summary>Gets the UTF-16 start of the active candidate/conversion range.</summary>
    public int SelectionStart { get; private set; }
    /// <summary>Gets the UTF-16 length of the active candidate/conversion range.</summary>
    public int SelectionLength { get; private set; }
    /// <summary>Gets or sets whether later routing is suppressed.</summary>
    public bool Handled { get; set; }

    /// <summary>Initializes this reusable composition event.</summary>
    /// <param name="source">Focused route target.</param>
    /// <param name="composition">Device-neutral composition transition.</param>
    internal void Reset(UIElement source, TextCompositionEvent composition)
    {
        Source = source;
        CurrentTarget = source;
        RoutePhase = UIRoutePhase.Target;
        Kind = composition.Kind;
        Text = composition.Text ?? string.Empty;
        CaretIndex = Math.Clamp(composition.CaretIndex, 0, Text.Length);
        SelectionStart = Math.Clamp(composition.SelectionStart, 0, Text.Length);
        SelectionLength = Math.Clamp(
            composition.SelectionLength, 0, Text.Length - SelectionStart);
        Handled = false;
    }

    /// <summary>Changes the current receiver while traversing the route.</summary>
    /// <param name="target">Next receiver.</param>
    /// <param name="phase">Current route phase.</param>
    internal void SetCurrentTarget(UIElement target, UIRoutePhase phase)
    {
        CurrentTarget = target;
        RoutePhase = phase;
    }
}
