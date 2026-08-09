using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Supplies a logical extent while rendering only the viewport owned by a scroll container.</summary>
internal interface IScrollViewportContent
{
    /// <summary>Gets the complete logical extent for a proposed viewport.</summary>
    /// <param name="viewportSize">Visible content size.</param>
    /// <returns>Complete scrollable extent.</returns>
    Vector2 GetScrollExtent(Vector2 viewportSize);

    /// <summary>Applies the parent-owned offset and visible size.</summary>
    /// <param name="offset">Parent scroll offset.</param>
    /// <param name="viewportSize">Visible content size.</param>
    void SetScrollViewport(Vector2 offset, Vector2 viewportSize);
}

/// <summary>Clips one content element and provides two-axis scrolling with synchronized bars.</summary>
public sealed class ScrollViewer : Panel
{
    private const float DefaultBarThickness = 10f;
    private UIElement? _content;
    private float _horizontalOffset;
    private float _verticalOffset;
    private float _extentWidth;
    private float _extentHeight;
    private float _viewportWidth;
    private float _viewportHeight;

    /// <summary>Gets or sets the single scrollable content element.</summary>
    public UIElement? Content
    {
        get => _content;
        set
        {
            if (ReferenceEquals(_content, value))
                return;
            if (_content is not null)
                RemoveChild(_content);
            RemoveChild(HorizontalScrollBar);
            RemoveChild(VerticalScrollBar);
            _content = value;
            if (_content is not null)
                AddChild(_content);
            AddChild(HorizontalScrollBar);
            AddChild(VerticalScrollBar);
            InvalidateMeasure();
        }
    }

    /// <summary>Gets or sets whether horizontal scrolling is permitted.</summary>
    public bool CanScrollHorizontally { get; set; }

    /// <summary>Gets or sets whether vertical scrolling is permitted.</summary>
    public bool CanScrollVertically { get; set; } = true;

    /// <summary>Gets or sets logical pixels applied per wheel unit.</summary>
    public float WheelStep { get; set; } = 32f;

    /// <summary>Gets the current horizontal offset.</summary>
    public float HorizontalOffset => _horizontalOffset;

    /// <summary>Gets the current vertical offset.</summary>
    public float VerticalOffset => _verticalOffset;

    /// <summary>Gets the measured content width.</summary>
    public float ExtentWidth => _extentWidth;

    /// <summary>Gets the measured content height.</summary>
    public float ExtentHeight => _extentHeight;

    /// <summary>Gets the horizontal scroll bar.</summary>
    public ScrollBar HorizontalScrollBar { get; }

    /// <summary>Gets the vertical scroll bar.</summary>
    public ScrollBar VerticalScrollBar { get; }

    /// <summary>Creates an empty clipped scroll viewer.</summary>
    /// <param name="width">Viewer width.</param>
    /// <param name="height">Viewer height.</param>
    /// <param name="theme">Theme used by its scroll bars.</param>
    public ScrollViewer(float width = 0f, float height = 0f, UITheme? theme = null)
        : base(null, width, height)
    {
        ClipToBounds = true;
        HorizontalScrollBar = new ScrollBar(UIOrientation.Horizontal, theme);
        VerticalScrollBar = new ScrollBar(UIOrientation.Vertical, theme);
        HorizontalScrollBar.ValueChanged += value => SetOffsets(value, _verticalOffset);
        VerticalScrollBar.ValueChanged += value => SetOffsets(_horizontalOffset, value);
        AddChild(HorizontalScrollBar);
        AddChild(VerticalScrollBar);
        Pointer += OnPointer;
    }

    /// <summary>Scrolls to clamped logical offsets.</summary>
    /// <param name="horizontalOffset">Requested horizontal offset.</param>
    /// <param name="verticalOffset">Requested vertical offset.</param>
    public void ScrollTo(float horizontalOffset, float verticalOffset) =>
        SetOffsets(horizontalOffset, verticalOffset);

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        if (_content is null)
            return availableSize;
        var inner = new Vector2(
            MathF.Max(0f, availableSize.X - Padding.Horizontal),
            MathF.Max(0f, availableSize.Y - Padding.Vertical));
        _content.Measure(_content is IScrollViewportContent
            ? inner
            : new Vector2(
                CanScrollHorizontally ? float.PositiveInfinity : inner.X,
                CanScrollVertically ? float.PositiveInfinity : inner.Y));
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        if (_content is not null)
        {
            var extent = _content is IScrollViewportContent virtualized
                ? virtualized.GetScrollExtent(contentSize)
                : _content.DesiredSize;
            _extentWidth = extent.X;
            _extentHeight = extent.Y;
        }
        else
        {
            _extentWidth = 0f;
            _extentHeight = 0f;
        }

        var showVertical = CanScrollVertically && _extentHeight > contentSize.Y;
        var showHorizontal = CanScrollHorizontally &&
            _extentWidth > contentSize.X - (showVertical ? DefaultBarThickness : 0f);
        if (showHorizontal && !showVertical)
            showVertical = CanScrollVertically &&
                _extentHeight > contentSize.Y - DefaultBarThickness;
        _viewportWidth = MathF.Max(0f, contentSize.X - (showVertical ? DefaultBarThickness : 0f));
        _viewportHeight = MathF.Max(0f, contentSize.Y - (showHorizontal ? DefaultBarThickness : 0f));
        ClampOffsets();

        if (_content is not null)
        {
            if (_content is IScrollViewportContent virtualized)
            {
                var viewportSize = new Vector2(_viewportWidth, _viewportHeight);
                virtualized.SetScrollViewport(
                    new Vector2(_horizontalOffset, _verticalOffset), viewportSize);
                _content.Arrange(new Vector2(Padding.Left, Padding.Top), viewportSize);
            }
            else
            {
                _content.Arrange(
                    new Vector2(Padding.Left - _horizontalOffset, Padding.Top - _verticalOffset),
                    new Vector2(MathF.Max(_extentWidth, _viewportWidth),
                        MathF.Max(_extentHeight, _viewportHeight)));
            }
        }
        HorizontalScrollBar.IsVisible = showHorizontal;
        VerticalScrollBar.IsVisible = showVertical;
        SynchronizeBars();
        if (showHorizontal)
            HorizontalScrollBar.Arrange(new Vector2(Padding.Left, Padding.Top + _viewportHeight),
                new Vector2(_viewportWidth, DefaultBarThickness));
        if (showVertical)
            VerticalScrollBar.Arrange(new Vector2(Padding.Left + _viewportWidth, Padding.Top),
                new Vector2(DefaultBarThickness, _viewportHeight));
    }

    /// <summary>Consumes routed wheel deltas only when this viewer can move.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="pointerEvent">Routed pointer data.</param>
    private void OnPointer(UIElement sender, UIPointerEventArgs pointerEvent)
    {
        if (pointerEvent.Kind != UIPointerEventKind.Wheel)
            return;
        var horizontal = _horizontalOffset - pointerEvent.WheelDelta.X * WheelStep;
        var vertical = _verticalOffset - pointerEvent.WheelDelta.Y * WheelStep;
        var oldHorizontal = _horizontalOffset;
        var oldVertical = _verticalOffset;
        SetOffsets(horizontal, vertical);
        if (_horizontalOffset != oldHorizontal || _verticalOffset != oldVertical)
            pointerEvent.Handled = true;
    }

    /// <summary>Clamps offsets, invalidates arrangement, and synchronizes bars.</summary>
    /// <param name="horizontalOffset">Requested horizontal offset.</param>
    /// <param name="verticalOffset">Requested vertical offset.</param>
    private void SetOffsets(float horizontalOffset, float verticalOffset)
    {
        var resolvedHorizontal = Math.Clamp(horizontalOffset, 0f,
            MathF.Max(0f, _extentWidth - _viewportWidth));
        var resolvedVertical = Math.Clamp(verticalOffset, 0f,
            MathF.Max(0f, _extentHeight - _viewportHeight));
        if (_horizontalOffset == resolvedHorizontal && _verticalOffset == resolvedVertical)
            return;
        _horizontalOffset = resolvedHorizontal;
        _verticalOffset = resolvedVertical;
        SynchronizeBars();
        InvalidateArrange();
    }

    /// <summary>Clamps current offsets after extent or viewport changes.</summary>
    private void ClampOffsets()
    {
        _horizontalOffset = Math.Clamp(_horizontalOffset, 0f,
            MathF.Max(0f, _extentWidth - _viewportWidth));
        _verticalOffset = Math.Clamp(_verticalOffset, 0f,
            MathF.Max(0f, _extentHeight - _viewportHeight));
    }

    /// <summary>Copies extent, viewport, and offset state into both bars.</summary>
    private void SynchronizeBars()
    {
        HorizontalScrollBar.Maximum = MathF.Max(0f, _extentWidth - _viewportWidth);
        HorizontalScrollBar.ViewportSize = _viewportWidth;
        HorizontalScrollBar.SynchronizeValue(_horizontalOffset);
        VerticalScrollBar.Maximum = MathF.Max(0f, _extentHeight - _viewportHeight);
        VerticalScrollBar.ViewportSize = _viewportHeight;
        VerticalScrollBar.SynchronizeValue(_verticalOffset);
    }
}
