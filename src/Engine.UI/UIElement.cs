using System.Numerics;
using Engine.Core;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Base class for all UI elements. Extends <see cref="Node"/> with layout, size, color, and interaction.
/// </summary>
public class UIElement : Node
{
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
    private HorizontalAlignment _horizontalAlignment = HorizontalAlignment.Stretch;
    private VerticalAlignment _verticalAlignment = VerticalAlignment.Stretch;
    private Thickness _margin = Thickness.Zero;
    private Thickness _padding = Thickness.Zero;
    private float _minWidth;
    private float _minHeight;
    private float _maxWidth = float.PositiveInfinity;
    private float _maxHeight = float.PositiveInfinity;
    private Color _backgroundColor = Color.Black;
    private Color _foregroundColor = Color.White;
    private bool _isVisible = true;
    private bool _isOverlay;
    private bool _isHovered;
    private bool _isPressed;
    private bool _isFocused;

    /// <summary>Gets or sets horizontal placement within the parent allocation.</summary>
    public HorizontalAlignment HorizontalAlignment
    {
        get => _horizontalAlignment;
        set { if (_horizontalAlignment != value) { _horizontalAlignment = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets or sets vertical placement within the parent allocation.</summary>
    public VerticalAlignment VerticalAlignment
    {
        get => _verticalAlignment;
        set { if (_verticalAlignment != value) { _verticalAlignment = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets the size requested by the most recent measure pass, including margin.</summary>
    public Vector2 DesiredSize => _desiredSize;
    /// <summary>Gets or sets spacing outside the element's border box.</summary>
    public Thickness Margin
    {
        get => _margin;
        set { if (_margin != value) { _margin = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets or sets spacing between the element border and its content.</summary>
    public Thickness Padding
    {
        get => _padding;
        set { if (_padding != value) { _padding = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets or sets the minimum permitted width.</summary>
    public float MinWidth
    {
        get => _minWidth;
        set { if (_minWidth != value) { _minWidth = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets or sets the minimum permitted height.</summary>
    public float MinHeight
    {
        get => _minHeight;
        set { if (_minHeight != value) { _minHeight = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets or sets the maximum permitted width.</summary>
    public float MaxWidth
    {
        get => _maxWidth;
        set { if (_maxWidth != value) { _maxWidth = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets or sets the maximum permitted height.</summary>
    public float MaxHeight
    {
        get => _maxHeight;
        set { if (_maxHeight != value) { _maxHeight = value; InvalidateMeasure(); } }
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

    /// <summary>Gets or sets the background color.</summary>
    public Color BackgroundColor
    {
        get => _backgroundColor;
        set { if (!_backgroundColor.Equals(value)) { _backgroundColor = value; InvalidateVisual(); } }
    }

    /// <summary>Gets or sets the foreground (text/icon) color.</summary>
    public Color ForegroundColor
    {
        get => _foregroundColor;
        set { if (!_foregroundColor.Equals(value)) { _foregroundColor = value; InvalidateVisual(); } }
    }

    /// <summary>Gets or sets whether this element is visible.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set { if (_isVisible != value) { _isVisible = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets or sets whether this element can receive pointer hit tests.</summary>
    public bool IsHitTestVisible { get; set; } = true;

    /// <summary>Gets or sets whether this subtree is composited above viewport textures.</summary>
    public bool IsOverlay
    {
        get => _isOverlay;
        set { if (_isOverlay != value) { _isOverlay = value; InvalidatePaintSubtree(); InvalidateTreeSnapshot(); } }
    }

    /// <summary>Gets or sets whether the mouse is hovering over this element.</summary>
    public bool IsHovered
    {
        get => _isHovered;
        set { if (_isHovered != value) { _isHovered = value; InvalidateVisual(); } }
    }

    /// <summary>Gets or sets whether this element is currently pressed.</summary>
    public bool IsPressed
    {
        get => _isPressed;
        set { if (_isPressed != value) { _isPressed = value; InvalidateVisual(); } }
    }

    /// <summary>Gets or sets whether this element has keyboard focus.</summary>
    public bool IsFocused
    {
        get => _isFocused;
        set { if (_isFocused != value) { _isFocused = value; InvalidateVisual(); } }
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
        if (_arrangeValid && _lastArrangePosition == slotPosition && _lastArrangeSize == slotSize)
            return;
        if (!IsVisible)
            return;
        var availableWidth = MathF.Max(0f, slotSize.X - Margin.Horizontal);
        var availableHeight = MathF.Max(0f, slotSize.Y - Margin.Vertical);
        var desiredWidth = MathF.Max(0f, _desiredSize.X - Margin.Horizontal);
        var desiredHeight = MathF.Max(0f, _desiredSize.Y - Margin.Vertical);
        var width = HorizontalAlignment == HorizontalAlignment.Stretch && _requestedWidth is null
            ? availableWidth : MathF.Min(availableWidth, desiredWidth);
        var height = VerticalAlignment == VerticalAlignment.Stretch && _requestedHeight is null
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
        _cachedDrawList = null;
        if (Parent is UIElement parent)
            parent.InvalidateMeasure();
    }

    /// <summary>Invalidates final placement without discarding the desired size.</summary>
    public void InvalidateArrange()
    {
        _arrangeValid = false;
        _visualValid = false;
        _paintValid = false;
        _cachedDrawList = null;
        if (Parent is UIElement parent)
            parent.InvalidateArrange();
    }

    /// <summary>Invalidates cached paint output without discarding layout.</summary>
    public void InvalidateVisual()
    {
        _visualValid = false;
        _paintValid = false;
        _cachedDrawList = null;
        if (Parent is UIElement parent)
            parent.InvalidateTreeSnapshot();
    }

    /// <summary>Invalidates only the composed subtree snapshot while retaining local paint commands.</summary>
    private void InvalidateTreeSnapshot()
    {
        _visualValid = false;
        _cachedDrawList = null;
        if (Parent is UIElement parent)
            parent.InvalidateTreeSnapshot();
    }

    /// <summary>Invalidates cached paint commands for this element and every descendant.</summary>
    private void InvalidatePaintSubtree()
    {
        _visualValid = false;
        _paintValid = false;
        _cachedDrawList = null;
        foreach (var child in Children.OfType<UIElement>())
            child.InvalidatePaintSubtree();
    }

    /// <summary>Adds a child and invalidates layout.</summary>
    /// <param name="child">Node to add.</param>
    public new void AddChild(Node child)
    {
        base.AddChild(child);
        InvalidateMeasure();
    }

    /// <summary>Removes a child and invalidates layout.</summary>
    /// <param name="child">Node to remove.</param>
    /// <returns>True when the child was present.</returns>
    public new bool RemoveChild(Node child)
    {
        var removed = base.RemoveChild(child);
        if (removed)
            InvalidateMeasure();
        return removed;
    }

    /// <summary>Removes all children and invalidates layout.</summary>
    public new void ClearChildren()
    {
        base.ClearChildren();
        InvalidateMeasure();
    }

    /// <summary>Measures content for a derived element.</summary>
    /// <param name="availableSize">Available size after margin removal.</param>
    /// <returns>Desired border-box size.</returns>
    protected virtual Vector2 MeasureOverride(Vector2 availableSize)
    {
        foreach (var child in Children.OfType<UIElement>())
            child.Measure(availableSize);
        return Vector2.Zero;
    }

    /// <summary>Arranges child content after this element receives its final size.</summary>
    /// <param name="contentSize">Size inside this element's padding.</param>
    protected virtual void ArrangeOverride(Vector2 contentSize)
    {
        foreach (var child in Children.OfType<UIElement>())
            child.Arrange(Vector2.Zero, child.DesiredSize);
    }

    /// <summary>Calculates alignment offset on one axis.</summary>
    /// <param name="available">Available axis size.</param>
    /// <param name="actual">Actual element size.</param>
    /// <param name="alignment">Axis alignment value.</param>
    /// <returns>Offset within the available size.</returns>
    private static float AlignOffset(float available, float actual, object alignment)
    {
        return alignment switch
        {
            HorizontalAlignment.Center or VerticalAlignment.Center => (available - actual) / 2f,
            HorizontalAlignment.Right or VerticalAlignment.Bottom => available - actual,
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

    /// <summary>
    /// Appends paint commands for this element.
    /// </summary>
    /// <param name="drawList">Draw list receiving paint commands.</param>
    protected virtual void Paint(UIDrawList drawList)
    {
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
        var drawList = new UIDrawList();
        PaintRecursive(drawList, inheritedOverlay: false);
        _cachedDrawList = drawList;
        _visualValid = true;
        return drawList;
    }

    /// <summary>Recursively appends visible paint commands.</summary>
    /// <param name="drawList">Draw list receiving paint commands.</param>
    /// <param name="inheritedOverlay">Whether an ancestor establishes overlay composition.</param>
    private void PaintRecursive(UIDrawList drawList, bool inheritedOverlay)
    {
        if (!IsVisible)
            return;

        var overlay = inheritedOverlay || IsOverlay;
        var layer = overlay ? UIDrawLayer.Overlay : UIDrawLayer.Content;
        if (!_paintValid || _cachedPaintCommands.Commands.Any(command => command.Layer != layer))
        {
            _cachedPaintCommands.Reset(layer);
            Paint(_cachedPaintCommands);
            _paintValid = true;
        }
        drawList.AddRange(_cachedPaintCommands.Commands);

        foreach (var child in Children)
        {
            if (child is UIElement ui)
                ui.PaintRecursive(drawList, overlay);
        }
    }
}
