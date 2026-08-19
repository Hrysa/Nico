using System.Numerics;
using System.Globalization;
using Engine.Core;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Controls inherited logical layout and text direction.</summary>
public enum UIFlowDirection
{
    /// <summary>Places logical leading content at the left edge.</summary>
    LeftToRight,

    /// <summary>Places logical leading content at the right edge.</summary>
    RightToLeft
}

/// <summary>Maps inherited UI flow to the graphics text-layout contract.</summary>
internal static class UIFlowDirectionExtensions
{
    /// <summary>Converts UI flow direction to a text paragraph direction.</summary>
    /// <param name="direction">UI flow direction.</param>
    /// <returns>Equivalent graphics text direction.</returns>
    internal static TextFlowDirection ToTextFlowDirection(this UIFlowDirection direction) =>
        direction == UIFlowDirection.RightToLeft
            ? TextFlowDirection.RightToLeft
            : TextFlowDirection.LeftToRight;
}

/// <summary>Selects the host clock used by time-dependent UI behavior.</summary>
public enum UIClockKind
{
    /// <summary>Continues advancing while gameplay simulation is paused.</summary>
    Unscaled,

    /// <summary>Advances with gameplay simulation time.</summary>
    Scaled
}

/// <summary>Selects whether non-essential UI motion should animate.</summary>
public enum UIMotionPreference
{
    /// <summary>Runs UI motion at its authored timing.</summary>
    Full,

    /// <summary>Replaces non-essential motion with stable visual state.</summary>
    Reduced
}

/// <summary>
/// Base class for all UI elements. Extends <see cref="Node"/> with layout, size, color, and interaction.
/// </summary>
public class UIElement : Node
{
    private static long _nextAccessibilityId;
    private Vector2 _desiredSize;
    private float _actualWidth;
    private float _actualHeight;
    private float? _requestedWidth;
    private float? _requestedHeight;
    private bool _measureValid;
    private bool _arrangeValid;
    private Vector2 _lastMeasureSize;
    private Vector2 _lastArrangePosition;
    private Vector2 _lastArrangeSize;
    private UIDrawList? _cachedDrawList;
    private bool _visualValid;
    private readonly UIDrawList _cachedPaintCommands = new();
    private bool _paintValid;
    private bool _hasBackgroundColor;
    private bool _paintBackground;
    private UIDispatcher? _hostDispatcher;
    private ITextLayoutService? _textLayoutOverride;
    private UIFlowDirection? _flowDirectionOverride;
    private UIClockKind? _clockOverride;
    private CultureInfo? _cultureOverride;
    private UIMotionPreference? _motionPreferenceOverride;
    private object? _dataContextOverride;
    private bool _hasLocalDataContext;
    private UIResourceDictionary? _resources;
    private List<IDisposable>? _ownedBindings;
    private UIElement? _logicalParent;
    private List<UIElement>? _logicalChildren;
    private List<OwnedAnimation>? _ownedAnimations;
    private long _nextAnimationSequence;
    private long _inputTreeVersion;
    private readonly long _accessibilityId = Interlocked.Increment(ref _nextAccessibilityId);
    private bool _timeUpdateCacheValid;
    private bool _hasCachedTimeUpdates;

    /// <summary>Pairs a stable animation key with one retained animation instance.</summary>
    /// <param name="Key">Replacement key scoped to the owning element.</param>
    /// <param name="Animation">Owned animation instance.</param>
    /// <param name="Sequence">Monotonic start order used to defer callback-started runs until the next tick.</param>
    private readonly record struct OwnedAnimation(string Key, UIAnimation Animation, long Sequence);

    /// <summary>Gets the parent used by rendering, layout, and routed input.</summary>
    public UIElement? VisualParent => Parent as UIElement;

    /// <summary>Gets visual children used by rendering, layout, and routed input.</summary>
    public IReadOnlyList<Node> VisualChildren => Children;

    /// <summary>Gets the owner used for logical content and inherited state.</summary>
    public UIElement? LogicalParent => _logicalParent;

    /// <summary>Gets logical content owned by this element.</summary>
    public IReadOnlyList<UIElement> LogicalChildren => _logicalChildren ?? [];

    /// <summary>Gets the nearest parent that supplies inherited UI state.</summary>
    private UIElement? InheritanceParent => VisualParent ?? _logicalParent;

    /// <summary>Gets the visual input-tree version used by host-local routing caches.</summary>
    internal long InputTreeVersion => _inputTreeVersion;

    /// <summary>Gets the process-stable identity used by native accessibility adapters.</summary>
    internal long AccessibilityId => _accessibilityId;

    /// <summary>Gets an allocation-free accessibility snapshot for this element.</summary>
    /// <returns>Current semantic role and state.</returns>
    public virtual UISemanticInfo GetSemanticInfo() =>
        new(UISemanticRole.Generic, Name, null, IsEnabled, true, false, null);

    /// <summary>Requests one accessibility action from this element.</summary>
    /// <param name="action">Action requested by an accessibility adapter.</param>
    /// <param name="value">Optional numeric value used by set-value actions.</param>
    /// <returns>True when the action was supported and performed.</returns>
    public virtual bool PerformSemanticAction(UISemanticAction action, double? value = null) => false;

    /// <summary>Gets or sets the stable identifier exposed to accessibility and automation tools.</summary>
    public string? AutomationId { get; set; }

    /// <summary>Gets or sets additional descriptive text exposed to assistive technology.</summary>
    public string? AccessibilityDescription { get; set; }

    /// <summary>Gets or sets the element that supplies this element's accessible label.</summary>
    public UIElement? LabeledBy { get; set; }

    /// <summary>Gets or sets the application data inherited by this retained subtree.</summary>
    public object? DataContext
    {
        get => _hasLocalDataContext
            ? _dataContextOverride
            : InheritanceParent?.DataContext;
        set
        {
            var previous = DataContext;
            _dataContextOverride = value;
            _hasLocalDataContext = true;
            if (!ReferenceEquals(previous, value))
                NotifyDataContextChanged(value);
        }
    }

    /// <summary>Gets resources locally owned by this element.</summary>
    public UIResourceDictionary Resources => _resources ??= new UIResourceDictionary();

    /// <summary>Occurs when this element's effective inherited data context changes.</summary>
    public event Action<object?>? DataContextChanged;

    /// <summary>Gets the number of animations currently advanced by this element.</summary>
    public int ActiveAnimationCount => _ownedAnimations?.Count ?? 0;

    /// <summary>Starts or replaces one keyed animation owned by this element.</summary>
    /// <param name="key">Element-local replacement key.</param>
    /// <param name="animation">Reusable animation instance to start.</param>
    public void StartAnimation(string key, UIAnimation animation)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(animation);
        var dispatcher = Dispatcher;
        dispatcher?.VerifyAccess();
        CancelAnimation(key);
        var active = animation.Start(this, MotionPreference == UIMotionPreference.Reduced);
        InvalidateVisual();
        if (active)
            (_ownedAnimations ??= []).Add(new OwnedAnimation(key, animation, _nextAnimationSequence++));
        else
            animation.PublishCompleted();
        dispatcher?.RequestFrame();
    }

    /// <summary>Cancels and removes one keyed animation.</summary>
    /// <param name="key">Element-local animation key.</param>
    /// <returns>True when an active animation was cancelled.</returns>
    public bool CancelAnimation(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Dispatcher?.VerifyAccess();
        if (_ownedAnimations is null)
            return false;
        for (var index = 0; index < _ownedAnimations.Count; index++)
        {
            if (!string.Equals(_ownedAnimations[index].Key, key, StringComparison.Ordinal))
                continue;
            var animation = _ownedAnimations[index].Animation;
            _ownedAnimations.RemoveAt(index);
            if (_ownedAnimations.Count == 0)
                _ownedAnimations = null;
            animation.Cancel();
            return true;
        }
        return false;
    }

    /// <summary>Cancels every animation directly owned by this element.</summary>
    public void CancelAnimations()
    {
        Dispatcher?.VerifyAccess();
        if (_ownedAnimations is null)
            return;
        while (_ownedAnimations.Count > 0)
        {
            var animation = _ownedAnimations[^1].Animation;
            _ownedAnimations.RemoveAt(_ownedAnimations.Count - 1);
            animation.Cancel();
        }
        _ownedAnimations = null;
    }

    /// <summary>Clears the local data context so this element inherits from its parent.</summary>
    public void ClearDataContext()
    {
        if (!_hasLocalDataContext)
            return;
        var previous = _dataContextOverride;
        _dataContextOverride = null;
        _hasLocalDataContext = false;
        var current = DataContext;
        if (!ReferenceEquals(previous, current))
            NotifyDataContextChanged(current);
    }

    /// <summary>Searches local and ancestor resource dictionaries.</summary>
    /// <param name="key">Resource lookup key.</param>
    /// <param name="value">Resolved resource when present.</param>
    /// <returns>True when a resource was found.</returns>
    public bool TryFindResource(object key, out object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        for (UIElement? element = this; element is not null; element = element.InheritanceParent)
        {
            if (element._resources is not null && element._resources.TryGet(key, out value))
                return true;
        }
        value = null;
        return false;
    }

    /// <summary>Searches local and ancestor resources for a compatible value.</summary>
    /// <typeparam name="T">Required resource type.</typeparam>
    /// <param name="key">Resource lookup key.</param>
    /// <param name="value">Typed resource when present.</param>
    /// <returns>True when a compatible resource was found.</returns>
    public bool TryFindResource<T>(object key, out T? value) where T : class
    {
        if (TryFindResource(key, out var resource) && resource is T typed)
        {
            value = typed;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>Gets or sets an optional named style variant resolved through inherited resources.</summary>
    public string? StyleKey { get; set; }

    /// <summary>Gets or sets an explicit typed style.</summary>
    public IUIStyle? Style
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;
            field = value;
            value?.Apply(this);
        }
    }

    /// <summary>Resolves and applies an explicit or inherited typed style.</summary>
    /// <returns>True when a compatible style was applied.</returns>
    public bool ApplyStyle()
    {
        if (Style is { } explicitStyle)
        {
            explicitStyle.Apply(this);
            return true;
        }
        for (Type? type = GetType(); type is not null && typeof(UIElement).IsAssignableFrom(type);
             type = type.BaseType)
        {
            if (!TryFindResource(new UIStyleResourceKey(type, StyleKey), out IUIStyle? style)
                || style is null)
                continue;
            style.Apply(this);
            return true;
        }
        return false;
    }

    /// <summary>Gets the dispatcher inherited from this element's UI host.</summary>
    public UIDispatcher? Dispatcher => InheritanceParent is { } parent
        ? parent.Dispatcher
        : _hostDispatcher;

    /// <summary>Gets the effective text layout service for this element.</summary>
    public ITextLayoutService TextLayout => _textLayoutOverride ??
        (InheritanceParent is { } parent
            ? parent.TextLayout
            : FallbackTextLayoutService.Instance);

    /// <summary>Gets or sets a text layout service for this subtree, or null to inherit it.</summary>
    public ITextLayoutService? TextLayoutOverride
    {
        get => _textLayoutOverride;
        set
        {
            if (ReferenceEquals(_textLayoutOverride, value))
                return;
            _textLayoutOverride = value;
            InvalidateMeasureSubtree();
        }
    }

    /// <summary>Gets or sets the flow direction inherited by this subtree.</summary>
    public UIFlowDirection FlowDirection
    {
        get => _flowDirectionOverride ??
            (InheritanceParent is { } parent ? parent.FlowDirection : UIFlowDirection.LeftToRight);
        set
        {
            if (_flowDirectionOverride == value)
                return;
            _flowDirectionOverride = value;
            InvalidatePaintSubtree();
            InvalidateTreeSnapshot();
        }
    }

    /// <summary>Gets or sets the clock inherited by time-dependent behavior in this subtree.</summary>
    public UIClockKind Clock
    {
        get => _clockOverride ??
            (InheritanceParent is { } parent ? parent.Clock : UIClockKind.Unscaled);
        set => _clockOverride = value;
    }

    /// <summary>Gets the culture inherited by formatting, lookup, and text behavior.</summary>
    public CultureInfo Culture
    {
        get => _cultureOverride ??
            (InheritanceParent is { } parent ? parent.Culture : CultureInfo.CurrentUICulture);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_cultureOverride, value))
                return;
            _cultureOverride = value;
            InvalidateCultureSubtree();
        }
    }

    /// <summary>Gets or sets the inherited preference for non-essential UI motion.</summary>
    public UIMotionPreference MotionPreference
    {
        get => _motionPreferenceOverride ??
            (InheritanceParent is { } parent ? parent.MotionPreference : UIMotionPreference.Full);
        set
        {
            if (_motionPreferenceOverride == value)
                return;
            _motionPreferenceOverride = value;
            InvalidateMotionSubtree();
        }
    }

    /// <summary>Clears the local clock selection so this element inherits from its parent.</summary>
    public void ClearClock() => _clockOverride = null;

    /// <summary>Clears the local culture so this element inherits from its parent.</summary>
    public void ClearCulture()
    {
        if (_cultureOverride is null)
            return;
        _cultureOverride = null;
        InvalidateCultureSubtree();
    }

    /// <summary>Clears the local motion preference so this element inherits from its parent.</summary>
    public void ClearMotionPreference()
    {
        if (_motionPreferenceOverride is null)
            return;
        _motionPreferenceOverride = null;
        InvalidateMotionSubtree();
    }

    /// <summary>Clears the local flow direction so this element inherits from its parent.</summary>
    public void ClearFlowDirection()
    {
        if (_flowDirectionOverride is null)
            return;
        _flowDirectionOverride = null;
        InvalidatePaintSubtree();
        InvalidateTreeSnapshot();
    }

    /// <summary>Gets or sets multiplicative subtree opacity from zero through one.</summary>
    public float Opacity
    {
        get;
        set
        {
            if (value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
            InvalidateVisual();
        }
    } = 1f;

    /// <summary>Assigns or clears the dispatcher owned by this root's host.</summary>
    /// <param name="dispatcher">Host dispatcher, or null during detachment.</param>
    internal void SetHostDispatcher(UIDispatcher? dispatcher) => _hostDispatcher = dispatcher;

    /// <summary>Registers a binding whose lifetime is bounded by this hosted element.</summary>
    /// <param name="binding">Binding to dispose with the hosted tree.</param>
    internal void RegisterBinding(IDisposable binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        (_ownedBindings ??= []).Add(binding);
    }

    /// <summary>Removes a binding that was disposed explicitly by its owner.</summary>
    /// <param name="binding">Binding whose registration should be removed.</param>
    internal void UnregisterBinding(IDisposable binding) => _ownedBindings?.Remove(binding);

    /// <summary>Disposes bindings in this subtree before a host releases dispatcher ownership.</summary>
    internal void DisposeBindingsRecursive()
    {
        DisposeBindingsRecursive([]);
    }

    /// <summary>Disposes binding ownership once across diverging visual and logical ancestry.</summary>
    /// <param name="visited">Elements already released during this ownership walk.</param>
    private void DisposeBindingsRecursive(HashSet<UIElement> visited)
    {
        if (!visited.Add(this))
            return;
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child)
                child.DisposeBindingsRecursive(visited);
        }
        if (_logicalChildren is not null)
        {
            for (var index = 0; index < _logicalChildren.Count; index++)
                _logicalChildren[index].DisposeBindingsRecursive(visited);
        }
        if (_ownedBindings is null)
            return;
        while (_ownedBindings.Count > 0)
            _ownedBindings[^1].Dispose();
        _ownedBindings = null;
    }

    /// <summary>Cancels animations in this retained subtree before ownership is released.</summary>
    internal void CancelAnimationsRecursive()
    {
        CancelAnimationsRecursive([]);
    }

    /// <summary>Cancels animation ownership once across diverging visual and logical ancestry.</summary>
    /// <param name="visited">Elements already released during this ownership walk.</param>
    private void CancelAnimationsRecursive(HashSet<UIElement> visited)
    {
        if (!visited.Add(this))
            return;
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child)
                child.CancelAnimationsRecursive(visited);
        }
        if (_logicalChildren is not null)
        {
            for (var index = 0; index < _logicalChildren.Count; index++)
                _logicalChildren[index].CancelAnimationsRecursive(visited);
        }
        CancelAnimations();
    }

    /// <summary>Gets or sets horizontal placement within the parent allocation.</summary>
    public HorizontalAlignment HorizontalAlignment
    {
        get;
        set { if (field != value) { field = value; InvalidateMeasure(); } }
    } = HorizontalAlignment.Stretch;

    /// <summary>Gets or sets vertical placement within the parent allocation.</summary>
    public VerticalAlignment VerticalAlignment
    {
        get;
        set { if (field != value) { field = value; InvalidateMeasure(); } }
    } = VerticalAlignment.Stretch;

    /// <summary>Gets the size requested by the most recent measure pass, including margin.</summary>
    public Vector2 DesiredSize => _desiredSize;
    /// <summary>Gets or sets spacing outside the element's border box.</summary>
    public Thickness Margin
    {
        get;
        set { if (field != value) { field = value; InvalidateMeasure(); } }
    } = Thickness.Zero;

    /// <summary>Gets or sets spacing between the element border and its content.</summary>
    public Thickness Padding
    {
        get;
        set { if (field != value) { field = value; InvalidateMeasure(); } }
    } = Thickness.Zero;

    /// <summary>Gets or sets the minimum permitted width.</summary>
    public float MinWidth
    {
        get;
        set { if (field != value) { field = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets or sets the minimum permitted height.</summary>
    public float MinHeight
    {
        get;
        set { if (field != value) { field = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets or sets the maximum permitted width.</summary>
    public float MaxWidth
    {
        get;
        set { if (field != value) { field = value; InvalidateMeasure(); } }
    } = float.PositiveInfinity;

    /// <summary>Gets or sets the maximum permitted height.</summary>
    public float MaxHeight
    {
        get;
        set { if (field != value) { field = value; InvalidateMeasure(); } }
    } = float.PositiveInfinity;

    /// <summary>Gets or sets the share of positive main-axis free space assigned by a flex parent.</summary>
    public float FlexGrow
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field != value) { field = value; InvalidateMeasure(); }
        }
    }

    /// <summary>Gets or sets the share of main-axis overflow removed by a flex parent.</summary>
    public float FlexShrink
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field != value) { field = value; InvalidateMeasure(); }
        }
    } = 1f;

    /// <summary>Gets or sets the preferred main-axis outer size used by a flex parent, or null for intrinsic size.</summary>
    public float? FlexBasis
    {
        get;
        set
        {
            if (value is < 0f || value is { } finite && !float.IsFinite(finite))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field != value) { field = value; InvalidateMeasure(); }
        }
    }

    /// <summary>Gets or sets an optional cross-axis alignment override used by a flex parent.</summary>
    public FlexAlignment AlignSelf
    {
        get;
        set { if (field != value) { field = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets or sets the element width in pixels.</summary>
    public float Width
    {
        get => _actualWidth;
        set
        {
            _requestedWidth = value > 0f ? value : null;
            _actualWidth = MathF.Max(0f, value);
            InvalidateMeasure();
        }
    }

    /// <summary>Gets or sets the element height in pixels.</summary>
    public float Height
    {
        get => _actualHeight;
        set
        {
            _requestedHeight = value > 0f ? value : null;
            _actualHeight = MathF.Max(0f, value);
            InvalidateMeasure();
        }
    }

    /// <summary>Gets or sets the background color and marks this element as having an explicit fill.</summary>
    public Color BackgroundColor
    {
        get;
        set
        {
            if (_hasBackgroundColor && _paintBackground && field.Equals(value))
                return;
            field = value;
            _hasBackgroundColor = true;
            _paintBackground = true;
            InvalidateVisual();
        }
    } = Color.Black;

    /// <summary>Gets or sets whether an explicitly configured background is painted.</summary>
    public bool PaintBackground
    {
        get => _paintBackground;
        set
        {
            if (_paintBackground == value)
                return;
            _paintBackground = value;
            InvalidateVisual();
        }
    }

    /// <summary>Gets whether a background color was explicitly configured.</summary>
    protected bool HasBackgroundColor => _hasBackgroundColor;

    /// <summary>Gets or sets the foreground (text/icon) color.</summary>
    public Color ForegroundColor
    {
        get;
        set { if (!field.Equals(value)) { field = value; InvalidateVisual(); } }
    } = Color.White;

    /// <summary>Gets or sets whether this element is visible.</summary>
    public bool IsVisible
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            InvalidateInputTree();
            InvalidateMeasure();
        }
    } = true;

    /// <summary>Gets whether this element and every retained ancestor are visible.</summary>
    public bool IsEffectivelyVisible
    {
        get
        {
            Node? current = this;
            while (current is UIElement element)
            {
                if (!element.IsVisible)
                    return false;
                current = element.Parent;
            }
            return true;
        }
    }

    /// <summary>Gets or sets whether this element and its descendants accept interaction.</summary>
    public bool IsEnabled
    {
        get;
        set { if (field != value) { field = value; InvalidateVisual(); } }
    } = true;

    /// <summary>Gets or sets whether this element can receive pointer hit tests.</summary>
    public bool IsHitTestVisible { get; set; } = true;

    /// <summary>Gets or sets whether sequential keyboard navigation may focus this element.</summary>
    public bool IsTabStop { get; set; }

    /// <summary>Gets or sets the ordering key used by sequential keyboard navigation.</summary>
    public int TabIndex { get; set; }

    /// <summary>Gets routed command bindings registered on this element.</summary>
    public List<UICommandBinding> CommandBindings { get; } = [];

    /// <summary>Gets key gestures registered in this element's input scope.</summary>
    public List<UIKeyBinding> KeyBindings { get; } = [];

    /// <summary>Gets or sets drag data that starts automatically after a primary-button movement threshold.</summary>
    public UIDragData? DragData { get; set; }

    /// <summary>Gets or sets the operations this element permits when acting as a drag source.</summary>
    public UIDragEffect AllowedDragEffects { get; set; } = UIDragEffect.Copy;

    /// <summary>Gets or sets whether this element can receive routed drag-and-drop input.</summary>
    public bool AllowDrop { get; set; }

    /// <summary>Gets or sets whether this subtree is composited above viewport textures.</summary>
    public bool IsOverlay
    {
        get;
        set { if (field != value) { field = value; InvalidatePaintSubtree(); InvalidateTreeSnapshot(); } }
    }

    /// <summary>Gets or sets whether descendants are clipped to this element's arranged bounds.</summary>
    public bool ClipToBounds
    {
        get;
        set { if (field != value) { field = value; InvalidateTreeSnapshot(); } }
    }

    /// <summary>Gets or sets whether the mouse is hovering over this element.</summary>
    public bool IsHovered
    {
        get;
        set { if (field != value) { field = value; InvalidateVisual(); } }
    }

    /// <summary>Gets or sets whether this element is currently pressed.</summary>
    public bool IsPressed
    {
        get;
        set { if (field != value) { field = value; InvalidateVisual(); } }
    }

    /// <summary>Gets or sets whether this element has keyboard focus.</summary>
    public bool IsFocused
    {
        get;
        set { if (field != value) { field = value; InvalidateVisual(); } }
    }

    /// <summary>Occurs when the mouse enters this element.</summary>
    public event Action? MouseEnter;

    /// <summary>Occurs when the mouse leaves this element.</summary>
    public event Action? MouseLeave;

    /// <summary>Occurs when a mouse button is pressed on this element.</summary>
    public event Action? MouseDown;

    /// <summary>Occurs when a mouse button is released on this element.</summary>
    public event Action? MouseUp;

    /// <summary>Occurs when this element is clicked (released after press).</summary>
    public event Action? Click;

    /// <summary>Occurs when this element is double-clicked.</summary>
    public event Action? DoubleClick;

    /// <summary>Occurs when the mouse wheel scrolls over this element. Provides scroll offset.</summary>
    public event Action<float>? Scroll;

    /// <summary>Occurs when this element loses explicit pointer capture.</summary>
    public event Action? PointerCaptureLost;

    /// <summary>Occurs while pointer input tunnels from the root toward its target.</summary>
    public event UIPointerEventHandler? PreviewPointer;

    /// <summary>Occurs at the pointer target and while input bubbles toward the root.</summary>
    public event UIPointerEventHandler? Pointer;

    /// <summary>Occurs while drag input tunnels from the active scope toward a drop target.</summary>
    public event UIDragEventHandler? PreviewDrag;

    /// <summary>Occurs at a drop target and while drag input bubbles toward the active scope.</summary>
    public event UIDragEventHandler? Drag;

    /// <summary>Occurs while keyboard input tunnels from the root toward the focused element.</summary>
    public event UIKeyEventHandler? PreviewKey;

    /// <summary>Occurs at the focused element and while keyboard input bubbles toward the root.</summary>
    public event UIKeyEventHandler? Key;

    /// <summary>Occurs while committed text tunnels toward the focused element.</summary>
    public event UITextInputEventHandler? PreviewTextInput;

    /// <summary>Occurs at the focused element and while committed text bubbles toward the root.</summary>
    public event UITextInputEventHandler? RoutedTextInput;

    /// <summary>Occurs while IME composition tunnels toward the focused element.</summary>
    public event UITextCompositionEventHandler? PreviewTextComposition;

    /// <summary>Occurs at the focused element and bubbles through its active input scope.</summary>
    public event UITextCompositionEventHandler? TextComposition;

    /// <summary>Occurs when this element gains keyboard focus.</summary>
    public event Action? Focus;

    /// <summary>Occurs when this element loses keyboard focus.</summary>
    public event Action? Blur;

    /// <summary>Occurs when a key is pressed while this element is focused. Provides key code.</summary>
    public event Action<int>? KeyDown;

    /// <summary>Occurs when a key is released while this element is focused. Provides key code.</summary>
    public event Action<int>? KeyUp;

    /// <summary>Occurs when text input produces a character while this element is focused.</summary>
    public event Action<char>? TextInput;

    /// <summary>Gets the absolute left edge after applying parent layout positions.</summary>
    public float Left => GetParentLeft() + Position.X;

    /// <summary>Gets the absolute top edge after applying parent layout positions.</summary>
    public float Top => GetParentTop() + Position.Y;

    /// <summary>Gets the absolute right edge position.</summary>
    public float Right => Left + Width;

    /// <summary>Gets the absolute bottom edge position.</summary>
    public float Bottom => Top + Height;

    /// <summary>Gets the width available to content after padding is removed.</summary>
    public float ContentWidth => MathF.Max(0f, Width - Padding.Horizontal);

    /// <summary>Gets the height available to content after padding is removed.</summary>
    public float ContentHeight => MathF.Max(0f, Height - Padding.Vertical);

    /// <summary>Gets the absolute left edge of the content box.</summary>
    public float ContentLeft => Left + Padding.Left;

    /// <summary>Gets the absolute top edge of the content box.</summary>
    public float ContentTop => Top + Padding.Top;

    /// <summary>Builds the common interaction state used by control palettes.</summary>
    /// <param name="selected">Whether the control has persistent selected state.</param>
    /// <returns>Combined enabled, pointer, press, and selection state.</returns>
    protected UIInteractionState GetInteractionState(bool selected = false)
    {
        var state = UIInteractionState.Normal;
        if (!IsEnabled)
            state |= UIInteractionState.Disabled;
        if (IsHovered)
            state |= UIInteractionState.Hovered;
        if (IsPressed)
            state |= UIInteractionState.Pressed;
        if (selected)
            state |= UIInteractionState.Selected;
        return state;
    }

    /// <summary>
    /// Creates a new UI element with an optional explicit size.
    /// </summary>
    /// <param name="width">The element width.</param>
    /// <param name="height">The element height.</param>
    public UIElement(float width = 0f, float height = 0f)
    {
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Tests whether a point (in screen coordinates) is inside this element.
    /// </summary>
    /// <param name="point">The point to test.</param>
    /// <returns>True if the point is within this element's bounds.</returns>
    public bool ContainsPoint(Vector2 point)
    {
        return point.X >= Left && point.X <= Right
            && point.Y >= Top && point.Y <= Bottom;
    }

    /// <summary>Measures this element and its descendants against available parent space.</summary>
    /// <param name="availableSize">Space offered by the parent.</param>
    public void Measure(Vector2 availableSize)
    {
        if (_measureValid && _lastMeasureSize == availableSize)
            return;
        if (!IsVisible)
        {
            _desiredSize = Vector2.Zero;
            _measureValid = true;
            _lastMeasureSize = availableSize;
            return;
        }
        var availableWithoutMargin = new Vector2(
            MathF.Max(0f, availableSize.X - Margin.Horizontal),
            MathF.Max(0f, availableSize.Y - Margin.Vertical));
        var requested = MeasureOverride(availableWithoutMargin);
        var width = _requestedWidth ?? requested.X;
        var height = _requestedHeight ?? requested.Y;
        width = Math.Clamp(width, MinWidth, MaxWidth);
        height = Math.Clamp(height, MinHeight, MaxHeight);
        _desiredSize = new Vector2(width + Margin.Horizontal, height + Margin.Vertical);
        _lastMeasureSize = availableSize;
        _measureValid = true;
    }

    /// <summary>Arranges this element in a parent-relative slot.</summary>
    /// <param name="slotPosition">Top-left position of the allocated slot.</param>
    /// <param name="slotSize">Size of the allocated slot.</param>
    public void Arrange(Vector2 slotPosition, Vector2 slotSize)
    {
        ArrangeCore(slotPosition, slotSize, false, false);
    }

    /// <summary>Arranges an element while allowing a flex parent to own its main-axis border-box size.</summary>
    /// <param name="slotPosition">Top-left position of the allocated flex item slot.</param>
    /// <param name="slotSize">Size of the allocated flex item slot.</param>
    /// <param name="horizontalMainAxis">Whether the flex main axis is horizontal.</param>
    internal void ArrangeFlex(Vector2 slotPosition, Vector2 slotSize, bool horizontalMainAxis)
    {
        ArrangeCore(slotPosition, slotSize, horizontalMainAxis, !horizontalMainAxis);
    }

    /// <summary>Performs normal or flex-owned arrangement without changing requested dimensions.</summary>
    /// <param name="slotPosition">Top-left position of the allocated slot.</param>
    /// <param name="slotSize">Size of the allocated slot.</param>
    /// <param name="forceWidth">Whether the parent owns the resulting border-box width.</param>
    /// <param name="forceHeight">Whether the parent owns the resulting border-box height.</param>
    private void ArrangeCore(
        Vector2 slotPosition,
        Vector2 slotSize,
        bool forceWidth,
        bool forceHeight)
    {
        if (_arrangeValid && _lastArrangePosition == slotPosition && _lastArrangeSize == slotSize)
            return;
        if (!IsVisible)
            return;
        var availableWidth = MathF.Max(0f, slotSize.X - Margin.Horizontal);
        var availableHeight = MathF.Max(0f, slotSize.Y - Margin.Vertical);
        var desiredWidth = MathF.Max(0f, _desiredSize.X - Margin.Horizontal);
        var desiredHeight = MathF.Max(0f, _desiredSize.Y - Margin.Vertical);
        var width = forceWidth || HorizontalAlignment == HorizontalAlignment.Stretch && _requestedWidth is null
            ? availableWidth : MathF.Min(availableWidth, desiredWidth);
        var height = forceHeight || VerticalAlignment == VerticalAlignment.Stretch && _requestedHeight is null
            ? availableHeight : MathF.Min(availableHeight, desiredHeight);
        width = Math.Clamp(width, MinWidth, MaxWidth);
        height = Math.Clamp(height, MinHeight, MaxHeight);
        var x = slotPosition.X + Margin.Left + AlignOffset(availableWidth, width, HorizontalAlignment);
        var y = slotPosition.Y + Margin.Top + AlignOffset(availableHeight, height, VerticalAlignment);
        if (Position.X != x || Position.Y != y || _actualWidth != width || _actualHeight != height)
            InvalidatePaintSubtree();
        Position = new Vector3(x, y, Position.Z);
        _actualWidth = width;
        _actualHeight = height;
        ArrangeOverride(new Vector2(ContentWidth, ContentHeight));
        _lastArrangePosition = slotPosition;
        _lastArrangeSize = slotSize;
        _arrangeValid = true;
    }

    /// <summary>Invalidates desired size and propagates the change toward the layout root.</summary>
    public void InvalidateMeasure()
    {
        _measureValid = false;
        _arrangeValid = false;
        _visualValid = false;
        _paintValid = false;
        InvalidateTimeUpdateActivity();
        if (Parent is UIElement parent)
            parent.InvalidateMeasure();
        else
            Dispatcher?.RequestFrame();
    }

    /// <summary>Invalidates final placement without discarding the desired size.</summary>
    public void InvalidateArrange()
    {
        _arrangeValid = false;
        _visualValid = false;
        _paintValid = false;
        if (Parent is UIElement parent)
            parent.InvalidateArrange();
        else
            Dispatcher?.RequestFrame();
    }

    /// <summary>Invalidates cached paint output without discarding layout.</summary>
    public void InvalidateVisual()
    {
        _visualValid = false;
        _paintValid = false;
        InvalidateTimeUpdateActivity();
        if (Parent is UIElement parent)
            parent.InvalidateTreeSnapshot();
        else
            Dispatcher?.RequestFrame();
    }

    /// <summary>Invalidates only the composed subtree snapshot while retaining local paint commands.</summary>
    private void InvalidateTreeSnapshot()
    {
        _visualValid = false;
        if (Parent is UIElement parent)
            parent.InvalidateTreeSnapshot();
        else
            Dispatcher?.RequestFrame();
    }

    /// <summary>Invalidates cached active-time state from this element through its visual root.</summary>
    internal void InvalidateTimeUpdateActivity()
    {
        _timeUpdateCacheValid = false;
        if (Parent is UIElement parent)
            parent.InvalidateTimeUpdateActivity();
    }

    /// <summary>Advances the structural/visibility version from this element through its visual root.</summary>
    private void InvalidateInputTree()
    {
        _inputTreeVersion++;
        if (Parent is UIElement parent)
            parent.InvalidateInputTree();
    }

    /// <summary>Invalidates cached paint commands for this element and every descendant.</summary>
    private void InvalidatePaintSubtree()
    {
        _visualValid = false;
        _paintValid = false;
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child)
                child.InvalidatePaintSubtree();
        }
        if (Parent is null)
            Dispatcher?.RequestFrame();
    }

    /// <summary>Invalidates cached measurement throughout this subtree after an inherited service change.</summary>
    private void InvalidateMeasureSubtree()
    {
        _measureValid = false;
        _arrangeValid = false;
        _visualValid = false;
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child && child._textLayoutOverride is null)
                child.InvalidateMeasureSubtree();
        }
        if (_logicalChildren is not null)
        {
            for (var index = 0; index < _logicalChildren.Count; index++)
            {
                var child = _logicalChildren[index];
                if (child.VisualParent is null && child._textLayoutOverride is null)
                    child.InvalidateMeasureSubtree();
            }
        }
        if (Parent is UIElement parent)
            parent.InvalidateMeasure();
        else
            Dispatcher?.RequestFrame();
    }

    /// <summary>Invalidates descendants affected by an inherited culture change.</summary>
    private void InvalidateCultureSubtree()
    {
        _measureValid = false;
        _arrangeValid = false;
        _visualValid = false;
        _paintValid = false;
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child && child._cultureOverride is null)
                child.InvalidateCultureSubtree();
        }
        if (_logicalChildren is not null)
        {
            for (var index = 0; index < _logicalChildren.Count; index++)
            {
                var child = _logicalChildren[index];
                if (child.VisualParent is null && child._cultureOverride is null)
                    child.InvalidateCultureSubtree();
            }
        }
        if (Parent is UIElement parent)
            parent.InvalidateMeasure();
        else
            Dispatcher?.RequestFrame();
    }

    /// <summary>Invalidates descendants affected by an inherited motion preference change.</summary>
    private void InvalidateMotionSubtree()
    {
        _visualValid = false;
        _paintValid = false;
        InvalidateTimeUpdateActivity();
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child && child._motionPreferenceOverride is null)
                child.InvalidateMotionSubtree();
        }
        if (_logicalChildren is not null)
        {
            for (var index = 0; index < _logicalChildren.Count; index++)
            {
                var child = _logicalChildren[index];
                if (child.VisualParent is null && child._motionPreferenceOverride is null)
                    child.InvalidateMotionSubtree();
            }
        }
        if (Parent is UIElement parent)
            parent.InvalidateTreeSnapshot();
        else
            Dispatcher?.RequestFrame();
    }

    /// <summary>Adds a child to both the visual and logical trees and invalidates layout.</summary>
    /// <param name="child">Node to add to this element.</param>
    public override void AddChild(Node child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child is not UIElement element)
        {
            base.AddChild(child);
            InvalidateMeasure();
            return;
        }

        ValidateVisualChild(element);
        var previousDataContext = element.DataContext;
        if (element.VisualParent is { } oldVisualParent && !ReferenceEquals(oldVisualParent, this))
        {
            oldVisualParent.RemoveVisualChildCore(element);
            oldVisualParent.InvalidateMeasure();
        }
        if (element._logicalParent is { } oldLogicalParent && !ReferenceEquals(oldLogicalParent, this))
            oldLogicalParent.RemoveLogicalChildCore(element);

        AddLogicalChildCore(element);
        base.AddChild(child);
        InvalidateInputTree();
        element.OnInheritanceParentChanged(previousDataContext);
        InvalidateMeasure();
    }

    /// <summary>Removes a child from both trees and invalidates layout.</summary>
    /// <param name="child">Node to remove from this element.</param>
    /// <returns>True when either ownership tree contained the child.</returns>
    public override bool RemoveChild(Node child)
    {
        if (child is not UIElement element)
        {
            var removedNode = base.RemoveChild(child);
            if (removedNode)
                InvalidateMeasure();
            return removedNode;
        }

        var previousDataContext = element.DataContext;
        var removed = RemoveVisualChildCore(element);
        removed |= RemoveLogicalChildCore(element);
        if (removed)
        {
            InvalidateInputTree();
            element.OnInheritanceParentChanged(previousDataContext);
            InvalidateMeasure();
        }
        return removed;
    }

    /// <summary>Removes all children and invalidates layout.</summary>
    public override void ClearChildren()
    {
        while (Children.Count > 0)
            RemoveChild(Children[^1]);
        while (_logicalChildren is { Count: > 0 })
            RemoveLogicalChild(_logicalChildren[^1]);
    }

    /// <summary>Adds a child only to the visual layout and routed-input tree.</summary>
    /// <param name="child">Visual child to attach.</param>
    protected internal void AddVisualChild(UIElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        ValidateVisualChild(child);
        var previousDataContext = child.DataContext;
        if (child.VisualParent is { } oldVisualParent && !ReferenceEquals(oldVisualParent, this))
        {
            oldVisualParent.RemoveVisualChildCore(child);
            oldVisualParent.InvalidateMeasure();
        }
        base.AddChild(child);
        InvalidateInputTree();
        child.OnInheritanceParentChanged(previousDataContext);
        InvalidateMeasure();
    }

    /// <summary>Removes a child only from the visual layout and routed-input tree.</summary>
    /// <param name="child">Visual child to detach.</param>
    /// <returns>True when this element visually parented the child.</returns>
    protected internal bool RemoveVisualChild(UIElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        var previousDataContext = child.DataContext;
        if (!RemoveVisualChildCore(child))
            return false;
        InvalidateInputTree();
        child.OnInheritanceParentChanged(previousDataContext);
        InvalidateMeasure();
        return true;
    }

    /// <summary>Adds a child only to the logical ownership tree.</summary>
    /// <param name="child">Logical child to attach.</param>
    protected internal void AddLogicalChild(UIElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        var previousDataContext = child.DataContext;
        if (child._logicalParent is { } oldLogicalParent && !ReferenceEquals(oldLogicalParent, this))
            oldLogicalParent.RemoveLogicalChildCore(child);
        AddLogicalChildCore(child);
        child.OnInheritanceParentChanged(previousDataContext);
    }

    /// <summary>Removes a child only from the logical ownership tree.</summary>
    /// <param name="child">Logical child to detach.</param>
    /// <returns>True when this element logically owned the child.</returns>
    protected internal bool RemoveLogicalChild(UIElement child)
    {
        ArgumentNullException.ThrowIfNull(child);
        var previousDataContext = child.DataContext;
        if (!RemoveLogicalChildCore(child))
            return false;
        child.OnInheritanceParentChanged(previousDataContext);
        return true;
    }

    /// <summary>Adds logical ownership without publishing an intermediate inherited-state change.</summary>
    /// <param name="child">Logical child to attach.</param>
    private void AddLogicalChildCore(UIElement child)
    {
        if (ReferenceEquals(child, this))
            throw new InvalidOperationException("An element cannot logically own itself.");
        for (var ancestor = this; ancestor is not null; ancestor = ancestor.InheritanceParent)
        {
            if (ReferenceEquals(ancestor, child))
                throw new InvalidOperationException("Adding this child would create an inherited-state cycle.");
        }
        if (ReferenceEquals(child._logicalParent, this))
            return;
        child._logicalParent = this;
        (_logicalChildren ??= []).Add(child);
    }

    /// <summary>Validates visual attachment before changing either ownership relation.</summary>
    /// <param name="child">Prospective visual child.</param>
    private void ValidateVisualChild(UIElement child)
    {
        if (ReferenceEquals(child, this))
            throw new InvalidOperationException("An element cannot visually parent itself.");
        for (Node? ancestor = this; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, child))
                throw new InvalidOperationException("Adding this child would create a visual-tree cycle.");
        }
        for (var ancestor = this; ancestor is not null; ancestor = ancestor.InheritanceParent)
        {
            if (ReferenceEquals(ancestor, child))
                throw new InvalidOperationException("Adding this child would create an inherited-state cycle.");
        }
    }

    /// <summary>Removes visual ownership without publishing an intermediate inherited-state change.</summary>
    /// <param name="child">Visual child to detach.</param>
    /// <returns>True when the child was detached.</returns>
    private bool RemoveVisualChildCore(UIElement child)
    {
        if (!ReferenceEquals(child.VisualParent, this))
            return false;
        return base.RemoveChild(child);
    }

    /// <summary>Removes logical ownership without publishing an intermediate inherited-state change.</summary>
    /// <param name="child">Logical child to detach.</param>
    /// <returns>True when the child was detached.</returns>
    private bool RemoveLogicalChildCore(UIElement child)
    {
        if (!ReferenceEquals(child._logicalParent, this))
            return false;
        child._logicalParent = null;
        var removed = _logicalChildren!.Remove(child);
        if (_logicalChildren.Count == 0)
            _logicalChildren = null;
        return removed;
    }

    /// <summary>Invalidates inherited state after either parent relation changes.</summary>
    /// <param name="previousDataContext">Effective data context before the relation changed.</param>
    private void OnInheritanceParentChanged(object? previousDataContext)
    {
        if (!_hasLocalDataContext && !ReferenceEquals(previousDataContext, DataContext))
            NotifyDataContextChanged(DataContext);
        InvalidateMeasureSubtree();
        InvalidatePaintSubtree();
    }

    /// <summary>Publishes an effective data-context change through inheriting descendants.</summary>
    /// <param name="value">New effective data context.</param>
    private void NotifyDataContextChanged(object? value)
    {
        DataContextChanged?.Invoke(value);
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child && !child._hasLocalDataContext)
                child.NotifyDataContextChanged(value);
        }
        if (_logicalChildren is null)
            return;
        for (var index = 0; index < _logicalChildren.Count; index++)
        {
            var child = _logicalChildren[index];
            if (child.VisualParent is null && !child._hasLocalDataContext)
                child.NotifyDataContextChanged(value);
        }
    }

    /// <summary>Measures content for a derived element.</summary>
    /// <param name="availableSize">Available size after margin removal.</param>
    /// <returns>Desired border-box size.</returns>
    protected virtual Vector2 MeasureOverride(Vector2 availableSize)
    {
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child)
                child.Measure(availableSize);
        }
        return Vector2.Zero;
    }

    /// <summary>Arranges child content after this element receives its final size.</summary>
    /// <param name="contentSize">Size inside this element's padding.</param>
    protected virtual void ArrangeOverride(Vector2 contentSize)
    {
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child)
                child.Arrange(Vector2.Zero, child.DesiredSize);
        }
    }

    /// <summary>Calculates alignment offset on one axis.</summary>
    /// <param name="available">Available axis size.</param>
    /// <param name="actual">Actual element size.</param>
    /// <param name="alignment">Horizontal alignment value.</param>
    /// <returns>Offset within the available size.</returns>
    private static float AlignOffset(
        float available,
        float actual,
        HorizontalAlignment alignment)
    {
        return alignment switch
        {
            HorizontalAlignment.Center => (available - actual) / 2f,
            HorizontalAlignment.Right => available - actual,
            _ => 0f
        };
    }

    /// <summary>Calculates vertical alignment offset without boxing the enum.</summary>
    /// <param name="available">Available axis size.</param>
    /// <param name="actual">Actual element size.</param>
    /// <param name="alignment">Vertical alignment value.</param>
    /// <returns>Offset within the available size.</returns>
    private static float AlignOffset(
        float available,
        float actual,
        VerticalAlignment alignment)
    {
        return alignment switch
        {
            VerticalAlignment.Center => (available - actual) / 2f,
            VerticalAlignment.Bottom => available - actual,
            _ => 0f
        };
    }

    /// <summary>Gets the absolute left edge contributed by the UI parent.</summary>
    /// <returns>The parent left edge, or zero for a root element.</returns>
    private float GetParentLeft()
    {
        return Parent is UIElement parent ? parent.Left : 0f;
    }

    /// <summary>Gets the absolute top edge contributed by the UI parent.</summary>
    /// <returns>The parent top edge, or zero for a root element.</returns>
    private float GetParentTop()
    {
        return Parent is UIElement parent ? parent.Top : 0f;
    }

    /// <summary>
    /// Sets the hover state and raises <see cref="MouseEnter"/> / <see cref="MouseLeave"/> as appropriate.
    /// </summary>
    /// <param name="hovered">True if the mouse is hovering over this element.</param>
    public void SetHover(bool hovered)
    {
        if (IsHovered == hovered)
            return;

        IsHovered = hovered;

        if (hovered)
            OnMouseEnter();
        else
            OnMouseLeave();
    }

    /// <summary>
    /// Sets the pressed state and raises <see cref="MouseDown"/> / <see cref="MouseUp"/> as appropriate.
    /// </summary>
    /// <param name="pressed">True if the button is being pressed.</param>
    public void SetPressed(bool pressed)
    {
        if (IsPressed == pressed)
            return;

        IsPressed = pressed;

        if (pressed)
            OnMouseDown();
        else
            OnMouseUp();
    }

    /// <summary>
    /// Raises the <see cref="Click"/> event. Call after a press-release cycle on this element.
    /// </summary>
    public void InvokeClick()
    {
        OnClick();
    }

    /// <summary>
    /// Raises the <see cref="DoubleClick"/> event.
    /// </summary>
    public void InvokeDoubleClick()
    {
        OnDoubleClick();
    }

    /// <summary>
    /// Raises the <see cref="Scroll"/> event.
    /// </summary>
    /// <param name="offset">The scroll offset.</param>
    public void InvokeScroll(float offset)
    {
        OnScroll(offset);
    }

    /// <summary>Raises the pointer-capture-lost event.</summary>
    internal void InvokePointerCaptureLost()
    {
        PointerCaptureLost?.Invoke();
    }

    /// <summary>Raises one preview-phase routed pointer event.</summary>
    /// <param name="pointerEvent">Reusable routed event data.</param>
    internal void InvokePreviewPointer(UIPointerEventArgs pointerEvent)
    {
        PreviewPointer?.Invoke(this, pointerEvent);
    }

    /// <summary>Raises one target- or bubble-phase routed pointer event.</summary>
    /// <param name="pointerEvent">Reusable routed event data.</param>
    internal void InvokePointer(UIPointerEventArgs pointerEvent)
    {
        Pointer?.Invoke(this, pointerEvent);
    }

    /// <summary>Invokes preview drag handlers for routed input.</summary>
    /// <param name="dragEvent">Current routed drag event.</param>
    internal void InvokePreviewDrag(UIDragEventArgs dragEvent) => PreviewDrag?.Invoke(this, dragEvent);

    /// <summary>Invokes target or bubbling drag handlers for routed input.</summary>
    /// <param name="dragEvent">Current routed drag event.</param>
    internal void InvokeDrag(UIDragEventArgs dragEvent) => Drag?.Invoke(this, dragEvent);

    /// <summary>Raises one preview-phase routed key event.</summary>
    /// <param name="keyEvent">Reusable routed event data.</param>
    internal void InvokePreviewKey(UIKeyEventArgs keyEvent) => PreviewKey?.Invoke(this, keyEvent);

    /// <summary>Raises one target- or bubble-phase routed key event.</summary>
    /// <param name="keyEvent">Reusable routed event data.</param>
    internal void InvokeKey(UIKeyEventArgs keyEvent) => Key?.Invoke(this, keyEvent);

    /// <summary>Raises one preview-phase routed text event.</summary>
    /// <param name="textEvent">Reusable routed event data.</param>
    internal void InvokePreviewText(UITextInputEventArgs textEvent) => PreviewTextInput?.Invoke(this, textEvent);

    /// <summary>Raises one target- or bubble-phase routed text event.</summary>
    /// <param name="textEvent">Reusable routed event data.</param>
    internal void InvokeText(UITextInputEventArgs textEvent) => RoutedTextInput?.Invoke(this, textEvent);

    /// <summary>Invokes preview composition handlers.</summary>
    /// <param name="compositionEvent">Routed composition data.</param>
    internal void InvokePreviewTextComposition(UITextCompositionEventArgs compositionEvent) =>
        PreviewTextComposition?.Invoke(this, compositionEvent);

    /// <summary>Invokes target or bubble composition handlers.</summary>
    /// <param name="compositionEvent">Routed composition data.</param>
    internal void InvokeTextComposition(UITextCompositionEventArgs compositionEvent) =>
        TextComposition?.Invoke(this, compositionEvent);

    /// <summary>
    /// Sets the focus state and raises <see cref="Focus"/> / <see cref="Blur"/> as appropriate.
    /// </summary>
    /// <param name="focused">True to give this element focus.</param>
    public void SetFocus(bool focused)
    {
        if (IsFocused == focused)
            return;

        IsFocused = focused;

        if (focused)
            OnFocus();
        else
            OnBlur();
    }

    /// <summary>
    /// Raises <see cref="KeyDown"/> event for this element.
    /// </summary>
    /// <param name="keyCode">The key code.</param>
    public void InvokeKeyDown(int keyCode)
    {
        OnKeyDown(keyCode);
    }

    /// <summary>
    /// Raises <see cref="KeyUp"/> event for this element.
    /// </summary>
    /// <param name="keyCode">The key code.</param>
    public void InvokeKeyUp(int keyCode)
    {
        OnKeyUp(keyCode);
    }

    /// <summary>Raises the <see cref="TextInput"/> event for this element.</summary>
    /// <param name="character">Produced text character.</param>
    public void InvokeTextInput(char character)
    {
        OnTextInput(character);
    }

    /// <summary>Called when the mouse enters this element. Override for custom hover-on behavior.</summary>
    protected virtual void OnMouseEnter()
    {
        MouseEnter?.Invoke();
    }

    /// <summary>Called when the mouse leaves this element. Override for custom hover-off behavior.</summary>
    protected virtual void OnMouseLeave()
    {
        MouseLeave?.Invoke();
    }

    /// <summary>Called when a mouse button is pressed on this element. Override for custom press behavior.</summary>
    protected virtual void OnMouseDown()
    {
        MouseDown?.Invoke();
    }

    /// <summary>Called when a mouse button is released on this element. Override for custom release behavior.</summary>
    protected virtual void OnMouseUp()
    {
        MouseUp?.Invoke();
    }

    /// <summary>Called when this element is clicked. Override for custom click behavior.</summary>
    protected virtual void OnClick()
    {
        Click?.Invoke();
    }

    /// <summary>Called when this element is double-clicked. Override for custom double-click behavior.</summary>
    protected virtual void OnDoubleClick()
    {
        DoubleClick?.Invoke();
    }

    /// <summary>Called when the mouse wheel scrolls over this element. Override for custom scroll behavior.</summary>
    /// <param name="offset">The scroll offset.</param>
    protected virtual void OnScroll(float offset)
    {
        Scroll?.Invoke(offset);
    }

    /// <summary>Called when this element gains keyboard focus. Override for custom focus behavior.</summary>
    protected virtual void OnFocus()
    {
        Focus?.Invoke();
    }

    /// <summary>Called when this element loses keyboard focus. Override for custom blur behavior.</summary>
    protected virtual void OnBlur()
    {
        Blur?.Invoke();
    }

    /// <summary>Called when a key is pressed while focused. Override for custom key-down behavior.</summary>
    /// <param name="keyCode">The key code.</param>
    protected virtual void OnKeyDown(int keyCode)
    {
        KeyDown?.Invoke(keyCode);
    }

    /// <summary>Called when a key is released while focused. Override for custom key-up behavior.</summary>
    /// <param name="keyCode">The key code.</param>
    protected virtual void OnKeyUp(int keyCode)
    {
        KeyUp?.Invoke(keyCode);
    }

    /// <summary>Called when text input produces a character while focused.</summary>
    /// <param name="character">Produced text character.</param>
    protected virtual void OnTextInput(char character)
    {
        TextInput?.Invoke(character);
    }

    /// <summary>Advances time-dependent behavior for this subtree without iterator allocation.</summary>
    /// <param name="deltaTime">Elapsed time in seconds.</param>
    /// <returns>True when the host should rebuild and submit the visual snapshot.</returns>
    public bool AdvanceTime(double deltaTime)
    {
        return AdvanceTime(deltaTime, deltaTime);
    }

    /// <summary>Advances this subtree using separate unscaled host and scaled simulation clocks.</summary>
    /// <param name="unscaledDeltaTime">Host elapsed seconds independent of simulation pause.</param>
    /// <param name="scaledDeltaTime">Simulation-scaled elapsed seconds.</param>
    /// <returns>True when the host should rebuild and submit the visual snapshot.</returns>
    public bool AdvanceTime(double unscaledDeltaTime, double scaledDeltaTime)
    {
        if (!HasActiveTimeUpdates())
            return false;
        return AdvanceTimeCore(unscaledDeltaTime, scaledDeltaTime, UIClockKind.Unscaled);
    }

    /// <summary>Advances this element and descendants with an inherited clock selection.</summary>
    /// <param name="unscaledDeltaTime">Unscaled host elapsed seconds.</param>
    /// <param name="scaledDeltaTime">Scaled simulation elapsed seconds.</param>
    /// <param name="inheritedClock">Clock inherited from the parent.</param>
    /// <returns>True when any visual behavior changed.</returns>
    private bool AdvanceTimeCore(
        double unscaledDeltaTime,
        double scaledDeltaTime,
        UIClockKind inheritedClock)
    {
        var clock = _clockOverride ?? inheritedClock;
        var deltaTime = clock == UIClockKind.Scaled ? scaledDeltaTime : unscaledDeltaTime;
        var changed = AdvanceAnimations(unscaledDeltaTime, scaledDeltaTime);
        var wasElementActive = IsTimeUpdateActive;
        if (wasElementActive)
            changed |= UpdateElement(deltaTime);
        if (wasElementActive != IsTimeUpdateActive)
            InvalidateTimeUpdateActivity();
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child && child.HasActiveTimeUpdates())
                changed |= child.AdvanceTimeCore(unscaledDeltaTime, scaledDeltaTime, clock);
        }
        return changed;
    }

    /// <summary>Advances directly owned animations and removes completed entries without allocation.</summary>
    /// <param name="unscaledDeltaTime">Unscaled host elapsed seconds.</param>
    /// <param name="scaledDeltaTime">Simulation-scaled elapsed seconds.</param>
    /// <returns>True when at least one animation applied a target value.</returns>
    private bool AdvanceAnimations(double unscaledDeltaTime, double scaledDeltaTime)
    {
        if (_ownedAnimations is null)
            return false;
        var changed = false;
        var index = 0;
        var sequenceLimit = _nextAnimationSequence;
        while (_ownedAnimations is not null && index < _ownedAnimations.Count)
        {
            var owned = _ownedAnimations[index];
            if (owned.Sequence >= sequenceLimit)
            {
                index++;
                continue;
            }
            var animation = owned.Animation;
            var deltaTime = animation.Clock == UIClockKind.Scaled
                ? scaledDeltaTime
                : unscaledDeltaTime;
            changed |= animation.Advance(
                deltaTime,
                MotionPreference == UIMotionPreference.Reduced);
            if (animation.IsRunning)
            {
                index++;
                continue;
            }
            _ownedAnimations.RemoveAt(index);
            if (_ownedAnimations.Count == 0)
                _ownedAnimations = null;
            animation.PublishCompleted();
        }
        if (changed)
            InvalidateVisual();
        return changed;
    }

    /// <summary>Advances time-dependent behavior owned by this element.</summary>
    /// <param name="deltaTime">Elapsed time in seconds.</param>
    /// <returns>True when the visual snapshot should be resubmitted.</returns>
    protected virtual bool UpdateElement(double deltaTime) => false;

    /// <summary>
    /// Gets whether this element currently requires repeated host-time updates. A derived element that
    /// overrides <see cref="UpdateElement(double)"/> must return true while that override needs ticks.
    /// </summary>
    protected virtual bool IsTimeUpdateActive => false;

    /// <summary>Checks this retained subtree for active timers or animation.</summary>
    /// <returns>True when hybrid scheduling should continue producing update ticks.</returns>
    internal bool HasActiveTimeUpdates()
    {
        if (_timeUpdateCacheValid)
            return _hasCachedTimeUpdates;
        _hasCachedTimeUpdates = IsTimeUpdateActive || _ownedAnimations is { Count: > 0 };
        var children = Children;
        for (var index = 0; !_hasCachedTimeUpdates && index < children.Count; index++)
        {
            if (children[index] is UIElement child && child.HasActiveTimeUpdates())
                _hasCachedTimeUpdates = true;
        }
        _timeUpdateCacheValid = true;
        return _hasCachedTimeUpdates;
    }

    /// <summary>
    /// Appends paint commands for this element.
    /// </summary>
    /// <param name="drawList">Draw list receiving paint commands.</param>
    protected virtual void Paint(UIDrawList drawList)
    {
        if (!PaintBackground || !HasBackgroundColor)
            return;
        drawList.AddRectangle(Left, Top, Right, Bottom, BackgroundColor);
    }

    /// <summary>
    /// Builds paint commands for this element and all visible descendants.
    /// </summary>
    /// <returns>The ordered UI draw list.</returns>
    public UIDrawList BuildDrawList()
    {
        if (_visualValid && _cachedDrawList is not null)
            return _cachedDrawList;
        if (Parent is null && Width > 0f && Height > 0f && (!_measureValid || !_arrangeValid))
        {
            var size = new Vector2(Width, Height);
            Measure(size);
            Arrange(new Vector2(Position.X, Position.Y), size);
        }
        var drawList = _cachedDrawList ??= new UIDrawList();
        drawList.Reset();
        PaintRecursive(drawList, inheritedOverlay: false, inheritedClip: null, inheritedOpacity: 1f);
        _visualValid = true;
        return drawList;
    }

    /// <summary>Paints content inside the element's padding-defined content box.</summary>
    /// <param name="drawList">Draw list receiving content commands.</param>
    protected virtual void PaintContent(UIDrawList drawList)
    {
    }

    /// <summary>Gets whether retained layout or paint state requires another draw-list build.</summary>
    internal bool RequiresDrawListRebuild => !_measureValid || !_arrangeValid || !_visualValid;

    /// <summary>Recursively appends visible paint commands.</summary>
    /// <param name="drawList">Draw list receiving paint commands.</param>
    /// <param name="inheritedOverlay">Whether an ancestor establishes overlay composition.</param>
    /// <param name="inheritedClip">Clip inherited from clipping ancestors.</param>
    /// <param name="inheritedOpacity">Opacity inherited from ancestors.</param>
    private void PaintRecursive(
        UIDrawList drawList,
        bool inheritedOverlay,
        UIClipRect? inheritedClip,
        float inheritedOpacity)
    {
        if (!IsVisible)
            return;

        var overlay = inheritedOverlay || IsOverlay;
        var layer = overlay ? UIDrawLayer.Overlay : UIDrawLayer.Content;
        var ownBounds = new UIClipRect(Left, Top, Right, Bottom);
        var effectiveClip = ClipToBounds
            ? inheritedClip is { } parentClip
                ? UIClipRect.Intersect(parentClip, ownBounds)
                : ownBounds
            : inheritedClip;
        if (effectiveClip is { IsEmpty: true })
            return;
        var paintCommands = _cachedPaintCommands.Commands;
        var layerChanged = false;
        for (var index = 0; index < paintCommands.Count; index++)
        {
            if (paintCommands[index].Layer == layer)
                continue;
            layerChanged = true;
            break;
        }
        if (!_paintValid || layerChanged)
        {
            _cachedPaintCommands.Reset(layer);
            Paint(_cachedPaintCommands);
            var previousClip = _cachedPaintCommands.CurrentClip;
            var contentClip = new UIClipRect(
                ContentLeft,
                ContentTop,
                ContentLeft + ContentWidth,
                ContentTop + ContentHeight);
            _cachedPaintCommands.CurrentClip = previousClip is { } localClip
                ? UIClipRect.Intersect(localClip, contentClip)
                : contentClip;
            try
            {
                PaintContent(_cachedPaintCommands);
            }
            finally
            {
                _cachedPaintCommands.CurrentClip = previousClip;
            }
            _paintValid = true;
        }
        var effectiveOpacity = inheritedOpacity * Opacity;
        drawList.AddRange(_cachedPaintCommands.Commands, effectiveClip, effectiveOpacity);

        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement ui)
                ui.PaintRecursive(drawList, overlay, effectiveClip, effectiveOpacity);
        }
    }
}
