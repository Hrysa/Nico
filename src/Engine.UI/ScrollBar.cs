using Engine.Graphics;

namespace Engine.UI;

/// <summary>Identifies a scroll bar axis.</summary>
public enum UIOrientation
{
    /// <summary>Horizontal orientation.</summary>
    Horizontal,
    /// <summary>Vertical orientation.</summary>
    Vertical
}

/// <summary>Displays and edits one normalized view over a scrollable extent.</summary>
public sealed class ScrollBar : UIElement
{
    private readonly UITheme _theme;
    private float _value;
    private float _maximum;
    private float _viewportSize;

    /// <summary>Gets the bar orientation.</summary>
    public UIOrientation ScrollOrientation { get; }

    /// <summary>Gets the reusable draggable thumb.</summary>
    public Thumb Thumb { get; }

    /// <summary>Gets or sets the current offset.</summary>
    public float Value
    {
        get => _value;
        set => SetValue(value, notify: true);
    }

    /// <summary>Gets or sets the greatest permitted offset.</summary>
    public float Maximum
    {
        get => _maximum;
        set
        {
            var resolved = MathF.Max(0f, value);
            if (_maximum == resolved)
                return;
            _maximum = resolved;
            SetValue(_value, notify: false);
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets the visible size used to calculate thumb length.</summary>
    public float ViewportSize
    {
        get => _viewportSize;
        set
        {
            var resolved = MathF.Max(0f, value);
            if (_viewportSize == resolved)
                return;
            _viewportSize = resolved;
            InvalidateVisual();
        }
    }

    /// <summary>Occurs when user or application input changes the value.</summary>
    public event Action<float>? ValueChanged;

    /// <summary>Creates a scroll bar.</summary>
    /// <param name="orientation">Scrolling axis.</param>
    /// <param name="theme">Theme supplying track and thumb colors.</param>
    public ScrollBar(UIOrientation orientation, UITheme? theme = null)
    {
        ScrollOrientation = orientation;
        _theme = theme ?? UITheme.Dark;
        Thumb = new Thumb(_theme);
        Thumb.DragDelta += OnThumbDragDelta;
        AddChild(Thumb);
        Pointer += OnPointer;
    }

    /// <summary>Updates the bar without reporting a user-facing value change.</summary>
    /// <param name="value">New offset.</param>
    public void SynchronizeValue(float value) => SetValue(value, notify: false);

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        drawList.AddRectangle(Left, Top, Right, Bottom, _theme.SurfaceRaised);
    }

    /// <inheritdoc/>
    protected override System.Numerics.Vector2 MeasureOverride(System.Numerics.Vector2 availableSize)
    {
        Thumb.Measure(availableSize);
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(System.Numerics.Vector2 contentSize)
    {
        var trackLength = ScrollOrientation == UIOrientation.Horizontal ? contentSize.X : contentSize.Y;
        var thumbLength = ResolveThumbLength(trackLength);
        var travel = MathF.Max(0f, trackLength - thumbLength);
        var start = Maximum <= 0f ? 0f : travel * Value / Maximum;
        if (ScrollOrientation == UIOrientation.Horizontal)
            Thumb.Arrange(new System.Numerics.Vector2(start, 0f),
                new System.Numerics.Vector2(thumbLength, contentSize.Y));
        else
            Thumb.Arrange(new System.Numerics.Vector2(0f, start),
                new System.Numerics.Vector2(contentSize.X, thumbLength));
    }

    /// <summary>Maps pointer presses and drags to the scroll range.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="pointerEvent">Routed pointer data.</param>
    private void OnPointer(UIElement sender, UIPointerEventArgs pointerEvent)
    {
        if (pointerEvent.RoutePhase != UIRoutePhase.Target ||
            pointerEvent.Kind != UIPointerEventKind.Press)
            return;
        var length = ScrollOrientation == UIOrientation.Horizontal ? Width : Height;
        if (length <= 0f)
            return;
        var coordinate = ScrollOrientation == UIOrientation.Horizontal
            ? pointerEvent.LocalPosition.X : pointerEvent.LocalPosition.Y;
        Value = Maximum * Math.Clamp(coordinate / length, 0f, 1f);
        pointerEvent.Handled = true;
    }

    /// <summary>Converts thumb drag distance into scroll-value movement.</summary>
    /// <param name="delta">Logical pointer movement.</param>
    private void OnThumbDragDelta(System.Numerics.Vector2 delta)
    {
        var trackLength = ScrollOrientation == UIOrientation.Horizontal ? Width : Height;
        var travel = MathF.Max(0f, trackLength - ResolveThumbLength(trackLength));
        if (travel <= 0f || Maximum <= 0f)
            return;
        var axisDelta = ScrollOrientation == UIOrientation.Horizontal ? delta.X : delta.Y;
        Value += axisDelta * Maximum / travel;
    }

    /// <summary>Calculates thumb length from extent and viewport state.</summary>
    /// <param name="trackLength">Available length along the scrolling axis.</param>
    /// <returns>Clamped thumb length.</returns>
    private float ResolveThumbLength(float trackLength)
    {
        var total = Maximum + ViewportSize;
        var thumbLength = total <= 0f ? trackLength : trackLength * ViewportSize / total;
        return Math.Clamp(thumbLength, MathF.Min(trackLength, 12f), trackLength);
    }

    /// <summary>Clamps and optionally reports a value change.</summary>
    /// <param name="value">Requested value.</param>
    /// <param name="notify">Whether to raise <see cref="ValueChanged"/>.</param>
    private void SetValue(float value, bool notify)
    {
        var resolved = Math.Clamp(value, 0f, Maximum);
        if (_value == resolved)
            return;
        _value = resolved;
        InvalidateVisual();
        InvalidateArrange();
        if (notify)
            ValueChanged?.Invoke(_value);
    }
}
