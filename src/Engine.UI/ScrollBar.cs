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
public sealed class ScrollBar : RangeBase
{
    private readonly UITheme _theme;

    /// <summary>Gets the bar orientation.</summary>
    public UIOrientation ScrollOrientation { get; }

    /// <summary>Gets the reusable draggable thumb.</summary>
    public Thumb Thumb { get; }

    /// <summary>Gets or sets the visible size used to calculate thumb length.</summary>
    public float ViewportSize
    {
        get;
        set
        {
            var resolved = MathF.Max(0f, value);
            if (field == resolved)
                return;
            field = resolved;
            InvalidateArrange();
            InvalidateVisual();
        }
    }

    /// <summary>Creates a scroll bar.</summary>
    /// <param name="orientation">Scrolling axis.</param>
    /// <param name="theme">Theme supplying track and thumb colors.</param>
    public ScrollBar(UIOrientation orientation, UITheme? theme = null)
        : base(0f, 0f, 0f, 0f)
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
    public void SynchronizeValue(float value) => SetValueCore(value, notify: false);

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
        var start = travel * NormalizedValue;
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
        Value = ValueFromRatio(coordinate / length);
        pointerEvent.Handled = true;
    }

    /// <summary>Converts thumb drag distance into scroll-value movement.</summary>
    /// <param name="delta">Logical pointer movement.</param>
    private void OnThumbDragDelta(System.Numerics.Vector2 delta)
    {
        var trackLength = ScrollOrientation == UIOrientation.Horizontal ? Width : Height;
        var travel = MathF.Max(0f, trackLength - ResolveThumbLength(trackLength));
        if (travel <= 0f || RangeLength <= 0f)
            return;
        var axisDelta = ScrollOrientation == UIOrientation.Horizontal ? delta.X : delta.Y;
        Value += axisDelta * RangeLength / travel;
    }

    /// <summary>Calculates thumb length from extent and viewport state.</summary>
    /// <param name="trackLength">Available length along the scrolling axis.</param>
    /// <returns>Clamped thumb length.</returns>
    private float ResolveThumbLength(float trackLength)
    {
        var total = RangeLength + ViewportSize;
        var thumbLength = total <= 0f ? trackLength : trackLength * ViewportSize / total;
        return Math.Clamp(thumbLength, MathF.Min(trackLength, 12f), trackLength);
    }

    /// <inheritdoc/>
    protected override void OnRangeChanged()
    {
        InvalidateArrange();
        base.OnRangeChanged();
    }

    /// <inheritdoc/>
    protected override void OnValueChanged(float previousValue, float value)
    {
        InvalidateArrange();
        base.OnValueChanged(previousValue, value);
    }
}
