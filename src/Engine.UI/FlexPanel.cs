using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Measures and arranges children using a CSS-like one-dimensional flex algorithm.</summary>
public sealed class FlexPanel : Panel
{
    private FlexDirection _direction;
    private FlexJustify _justifyContent;
    private FlexAlignment _alignItems = FlexAlignment.Stretch;
    private FlexJustify _alignContent = FlexJustify.Start;
    private FlexWrap _wrap;
    private float _gap;
    private float[] _mainSizes = [];
    private float[] _crossSizes = [];
    private int[] _lineCounts = [];
    private float[] _lineMainSizes = [];
    private float[] _lineCrossSizes = [];
    private UIElement?[] _visibleChildren = [];
    private bool[] _frozenItems = [];

    /// <summary>Gets or sets the main axis and item order.</summary>
    public FlexDirection Direction
    {
        get => _direction;
        set { if (_direction != value) { _direction = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets or sets how unused main-axis space is distributed.</summary>
    public FlexJustify JustifyContent
    {
        get => _justifyContent;
        set { if (_justifyContent != value) { _justifyContent = value; InvalidateArrange(); } }
    }

    /// <summary>Gets or sets the default cross-axis alignment of items.</summary>
    public FlexAlignment AlignItems
    {
        get => _alignItems;
        set
        {
            if (value == FlexAlignment.Auto)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_alignItems != value) { _alignItems = value; InvalidateArrange(); }
        }
    }

    /// <summary>Gets or sets how unused cross-axis space is distributed between wrapped lines.</summary>
    public FlexJustify AlignContent
    {
        get => _alignContent;
        set { if (_alignContent != value) { _alignContent = value; InvalidateArrange(); } }
    }

    /// <summary>Gets or sets whether overflowing items form additional lines.</summary>
    public FlexWrap Wrap
    {
        get => _wrap;
        set { if (_wrap != value) { _wrap = value; InvalidateMeasure(); } }
    }

    /// <summary>Gets or sets uniform spacing between adjacent items and wrapped lines.</summary>
    public float Gap
    {
        get => _gap;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_gap != value) { _gap = value; InvalidateMeasure(); }
        }
    }

    /// <summary>Creates an empty flex panel.</summary>
    /// <param name="backgroundColor">Optional painted background; null creates a layout-only panel.</param>
    public FlexPanel(Color? backgroundColor = null)
        : base(backgroundColor ?? Color.Black)
    {
        PaintBackground = backgroundColor.HasValue;
    }

    /// <summary>Measures intrinsic item sizes and flex line breaks.</summary>
    /// <param name="availableSize">Space offered by the parent.</param>
    /// <returns>The content-derived desired border-box size.</returns>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var inner = new Vector2(
            MathF.Max(0f, availableSize.X - Padding.Horizontal),
            MathF.Max(0f, availableSize.Y - Padding.Vertical));
        var horizontal = IsHorizontal();
        var availableMain = horizontal ? inner.X : inner.Y;
        var availableCross = horizontal ? inner.Y : inner.X;
        var children = Children;
        EnsureCapacity(children.Count);
        var visibleCount = 0;
        for (var ordinal = 0; ordinal < children.Count; ordinal++)
        {
            if (GetChild(children, ordinal) is not { IsVisible: true } child)
                continue;
            var childAvailable = horizontal
                ? new Vector2(availableMain, availableCross)
                : new Vector2(availableCross, availableMain);
            child.Measure(childAvailable);
            _visibleChildren[visibleCount] = child;
            _mainSizes[visibleCount] = GetMainSize(child, horizontal);
            _crossSizes[visibleCount] = horizontal ? child.DesiredSize.Y : child.DesiredSize.X;
            visibleCount++;
        }

        var lineCount = BuildLines(visibleCount, availableMain);
        var desiredMain = 0f;
        var desiredCross = 0f;
        for (var line = 0; line < lineCount; line++)
        {
            desiredMain = MathF.Max(desiredMain, _lineMainSizes[line]);
            desiredCross += _lineCrossSizes[line];
        }
        if (lineCount > 1)
            desiredCross += Gap * (lineCount - 1);
        return horizontal
            ? new Vector2(desiredMain + Padding.Horizontal, desiredCross + Padding.Vertical)
            : new Vector2(desiredCross + Padding.Horizontal, desiredMain + Padding.Vertical);
    }

    /// <summary>Distributes free space and arranges every flex line.</summary>
    /// <param name="contentSize">Size inside this panel's padding.</param>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        var horizontal = IsHorizontal();
        var availableMain = horizontal ? contentSize.X : contentSize.Y;
        var availableCross = horizontal ? contentSize.Y : contentSize.X;
        var children = Children;
        var visibleCount = CaptureCurrentSizes(children, horizontal);
        var lineCount = BuildLines(visibleCount, availableMain);
        if (lineCount == 1)
            _lineCrossSizes[0] = MathF.Max(_lineCrossSizes[0], availableCross);
        var usedCross = lineCount > 1 ? Gap * (lineCount - 1) : 0f;
        for (var line = 0; line < lineCount; line++)
            usedCross += _lineCrossSizes[line];
        ResolveSpacing(AlignContent, MathF.Max(0f, availableCross - usedCross), lineCount,
            out var crossOffset, out var extraLineGap);

        var itemOrdinal = 0;
        var crossPosition = crossOffset;
        for (var line = 0; line < lineCount; line++)
        {
            var count = _lineCounts[line];
            DistributeFlexibleSpace(itemOrdinal, count, availableMain, horizontal);
            var occupiedMain = count > 1 ? Gap * (count - 1) : 0f;
            for (var item = 0; item < count; item++)
                occupiedMain += _mainSizes[itemOrdinal + item];
            ResolveSpacing(GetDirectedJustification(),
                MathF.Max(0f, availableMain - occupiedMain), count,
                out var mainPosition, out var extraItemGap);
            var lineCross = _lineCrossSizes[line];
            for (var item = 0; item < count; item++, itemOrdinal++)
            {
                var child = GetVisibleChild(itemOrdinal);
                var itemMain = _mainSizes[itemOrdinal];
                var itemCross = _crossSizes[itemOrdinal];
                var alignment = child.AlignSelf == FlexAlignment.Auto ? AlignItems : child.AlignSelf;
                var crossSlot = alignment == FlexAlignment.Stretch
                    ? lineCross
                    : MathF.Min(lineCross, itemCross);
                var itemCrossOffset = alignment switch
                {
                    FlexAlignment.Center => (lineCross - crossSlot) / 2f,
                    FlexAlignment.End => lineCross - crossSlot,
                    _ => 0f
                };
                var position = horizontal
                    ? new Vector2(Padding.Left + mainPosition, Padding.Top + crossPosition + itemCrossOffset)
                    : new Vector2(Padding.Left + crossPosition + itemCrossOffset, Padding.Top + mainPosition);
                var size = horizontal
                    ? new Vector2(itemMain, crossSlot)
                    : new Vector2(crossSlot, itemMain);
                child.ArrangeFlex(position, size, horizontal);
                mainPosition += itemMain + Gap + extraItemGap;
            }
            crossPosition += lineCross + Gap + extraLineGap;
        }
    }

    /// <summary>Copies current intrinsic sizes into reusable layout buffers.</summary>
    /// <param name="children">Visual child list.</param>
    /// <param name="horizontal">Whether the main axis is horizontal.</param>
    /// <returns>The number of visible children.</returns>
    private int CaptureCurrentSizes(IReadOnlyList<Engine.Core.Node> children, bool horizontal)
    {
        EnsureCapacity(children.Count);
        var visibleCount = 0;
        for (var ordinal = 0; ordinal < children.Count; ordinal++)
        {
            if (GetChild(children, ordinal) is not { IsVisible: true } child)
                continue;
            _visibleChildren[visibleCount] = child;
            _mainSizes[visibleCount] = GetMainSize(child, horizontal);
            _crossSizes[visibleCount] = horizontal ? child.DesiredSize.Y : child.DesiredSize.X;
            visibleCount++;
        }
        return visibleCount;
    }

    /// <summary>Partitions visible items into flex lines.</summary>
    /// <param name="itemCount">Number of visible items.</param>
    /// <param name="availableMain">Available main-axis size.</param>
    /// <returns>The resulting number of lines.</returns>
    private int BuildLines(int itemCount, float availableMain)
    {
        if (itemCount == 0)
            return 0;
        var canWrap = Wrap == FlexWrap.Wrap && float.IsFinite(availableMain);
        var lineCount = 0;
        var lineItems = 0;
        var lineMain = 0f;
        var lineCross = 0f;
        for (var item = 0; item < itemCount; item++)
        {
            var nextMain = lineItems == 0 ? _mainSizes[item] : lineMain + Gap + _mainSizes[item];
            if (canWrap && lineItems > 0 && nextMain > availableMain)
            {
                StoreLine(lineCount++, lineItems, lineMain, lineCross);
                lineItems = 0;
                lineMain = 0f;
                lineCross = 0f;
                nextMain = _mainSizes[item];
            }
            lineMain = nextMain;
            lineCross = MathF.Max(lineCross, _crossSizes[item]);
            lineItems++;
        }
        StoreLine(lineCount++, lineItems, lineMain, lineCross);
        return lineCount;
    }

    /// <summary>Stores one computed line in reusable buffers.</summary>
    /// <param name="line">Line index.</param>
    /// <param name="count">Item count.</param>
    /// <param name="main">Occupied main-axis size.</param>
    /// <param name="cross">Required cross-axis size.</param>
    private void StoreLine(int line, int count, float main, float cross)
    {
        _lineCounts[line] = count;
        _lineMainSizes[line] = main;
        _lineCrossSizes[line] = cross;
    }

    /// <summary>Applies grow or shrink factors to one line.</summary>
    /// <param name="start">First visible item ordinal.</param>
    /// <param name="count">Number of items in the line.</param>
    /// <param name="availableMain">Available main-axis size.</param>
    /// <param name="horizontal">Whether the main axis is horizontal.</param>
    private void DistributeFlexibleSpace(
        int start,
        int count,
        float availableMain,
        bool horizontal)
    {
        var occupied = count > 1 ? Gap * (count - 1) : 0f;
        var grow = 0f;
        var scaledShrink = 0f;
        for (var item = 0; item < count; item++)
        {
            var ordinal = start + item;
            var child = GetVisibleChild(ordinal);
            occupied += _mainSizes[ordinal];
            grow += child.FlexGrow;
            scaledShrink += child.FlexShrink * _mainSizes[ordinal];
        }
        var free = availableMain - occupied;
        if (free > 0f && grow > 0f)
            GrowItems(start, count, free, horizontal);
        else if (free < 0f && scaledShrink > 0f)
            ShrinkItems(start, count, -free, horizontal);
    }

    /// <summary>Distributes positive free space while freezing items at maximum size.</summary>
    /// <param name="start">First item ordinal.</param>
    /// <param name="count">Number of items.</param>
    /// <param name="free">Positive free space.</param>
    /// <param name="horizontal">Whether the main axis is horizontal.</param>
    private void GrowItems(int start, int count, float free, bool horizontal)
    {
        Array.Clear(_frozenItems, start, count);
        var remaining = free;
        for (var iteration = 0; iteration < count && remaining > 0f; iteration++)
        {
            var factor = 0f;
            for (var item = 0; item < count; item++)
            {
                var ordinal = start + item;
                if (!_frozenItems[ordinal])
                    factor += GetVisibleChild(ordinal).FlexGrow;
            }
            if (factor <= 0f)
                return;
            var clamped = false;
            for (var item = 0; item < count; item++)
            {
                var ordinal = start + item;
                if (_frozenItems[ordinal])
                    continue;
                var child = GetVisibleChild(ordinal);
                var maximum = horizontal
                    ? child.MaxWidth + child.Margin.Horizontal
                    : child.MaxHeight + child.Margin.Vertical;
                var share = remaining * child.FlexGrow / factor;
                if (_mainSizes[ordinal] + share <= maximum)
                    continue;
                var consumed = MathF.Max(0f, maximum - _mainSizes[ordinal]);
                _mainSizes[ordinal] = maximum;
                remaining -= consumed;
                _frozenItems[ordinal] = true;
                clamped = true;
            }
            if (clamped)
                continue;
            for (var item = 0; item < count; item++)
            {
                var ordinal = start + item;
                if (!_frozenItems[ordinal])
                    _mainSizes[ordinal] += remaining * GetVisibleChild(ordinal).FlexGrow / factor;
            }
            return;
        }
    }

    /// <summary>Removes overflow while freezing items at minimum size.</summary>
    /// <param name="start">First item ordinal.</param>
    /// <param name="count">Number of items.</param>
    /// <param name="overflow">Positive overflow to remove.</param>
    /// <param name="horizontal">Whether the main axis is horizontal.</param>
    private void ShrinkItems(int start, int count, float overflow, bool horizontal)
    {
        Array.Clear(_frozenItems, start, count);
        var remaining = overflow;
        for (var iteration = 0; iteration < count && remaining > 0f; iteration++)
        {
            var factor = 0f;
            for (var item = 0; item < count; item++)
            {
                var ordinal = start + item;
                if (!_frozenItems[ordinal])
                {
                    var child = GetVisibleChild(ordinal);
                    factor += child.FlexShrink * _mainSizes[ordinal];
                }
            }
            if (factor <= 0f)
                return;
            var clamped = false;
            for (var item = 0; item < count; item++)
            {
                var ordinal = start + item;
                if (_frozenItems[ordinal])
                    continue;
                var child = GetVisibleChild(ordinal);
                var minimum = horizontal
                    ? child.MinWidth + child.Margin.Horizontal
                    : child.MinHeight + child.Margin.Vertical;
                var share = remaining * child.FlexShrink * _mainSizes[ordinal] / factor;
                if (_mainSizes[ordinal] - share >= minimum)
                    continue;
                var removed = MathF.Max(0f, _mainSizes[ordinal] - minimum);
                _mainSizes[ordinal] = minimum;
                remaining -= removed;
                _frozenItems[ordinal] = true;
                clamped = true;
            }
            if (clamped)
                continue;
            for (var item = 0; item < count; item++)
            {
                var ordinal = start + item;
                if (_frozenItems[ordinal])
                    continue;
                var child = GetVisibleChild(ordinal);
                _mainSizes[ordinal] -=
                    remaining * child.FlexShrink * _mainSizes[ordinal] / factor;
            }
            return;
        }
    }

    /// <summary>Calculates leading and inter-item free-space distribution.</summary>
    /// <param name="mode">Distribution policy.</param>
    /// <param name="free">Unused non-negative space.</param>
    /// <param name="count">Number of items or lines.</param>
    /// <param name="offset">Leading offset.</param>
    /// <param name="additionalGap">Additional space between adjacent entries.</param>
    private static void ResolveSpacing(
        FlexJustify mode,
        float free,
        int count,
        out float offset,
        out float additionalGap)
    {
        offset = 0f;
        additionalGap = 0f;
        if (count == 0)
            return;
        switch (mode)
        {
            case FlexJustify.Center:
                offset = free / 2f;
                break;
            case FlexJustify.End:
                offset = free;
                break;
            case FlexJustify.SpaceBetween when count > 1:
                additionalGap = free / (count - 1);
                break;
            case FlexJustify.SpaceAround:
                additionalGap = free / count;
                offset = additionalGap / 2f;
                break;
            case FlexJustify.SpaceEvenly:
                additionalGap = free / (count + 1);
                offset = additionalGap;
                break;
        }
    }

    /// <summary>Gets a constrained intrinsic or explicit flex basis including margin.</summary>
    /// <param name="child">Flex item.</param>
    /// <param name="horizontal">Whether the main axis is horizontal.</param>
    /// <returns>Constrained outer main-axis size.</returns>
    private static float GetMainSize(UIElement child, bool horizontal)
    {
        var intrinsic = horizontal ? child.DesiredSize.X : child.DesiredSize.Y;
        if (child.FlexBasis is not { } basis)
            return intrinsic;
        var margin = horizontal ? child.Margin.Horizontal : child.Margin.Vertical;
        var minimum = (horizontal ? child.MinWidth : child.MinHeight) + margin;
        var maximum = (horizontal ? child.MaxWidth : child.MaxHeight) + margin;
        return Math.Clamp(basis, minimum, maximum);
    }

    /// <summary>Maps logical start and end onto a reversed main direction.</summary>
    /// <returns>The physical free-space distribution policy.</returns>
    private FlexJustify GetDirectedJustification()
    {
        if (Direction is not (FlexDirection.RowReverse or FlexDirection.ColumnReverse))
            return JustifyContent;
        return JustifyContent switch
        {
            FlexJustify.Start => FlexJustify.End,
            FlexJustify.End => FlexJustify.Start,
            _ => JustifyContent
        };
    }

    /// <summary>Gets a UI child in logical flex order.</summary>
    /// <param name="children">Visual child list.</param>
    /// <param name="ordinal">Logical ordinal.</param>
    /// <returns>The UI child, or null for a non-UI node.</returns>
    private UIElement? GetChild(IReadOnlyList<Engine.Core.Node> children, int ordinal)
    {
        var reverse = Direction is FlexDirection.RowReverse or FlexDirection.ColumnReverse;
        var index = reverse ? children.Count - ordinal - 1 : ordinal;
        return children[index] as UIElement;
    }

    /// <summary>Gets a visible UI child by its compacted visible ordinal.</summary>
    /// <param name="visibleOrdinal">Visible child ordinal.</param>
    /// <returns>The matching child.</returns>
    private UIElement GetVisibleChild(int visibleOrdinal)
    {
        return _visibleChildren[visibleOrdinal] ??
            throw new InvalidOperationException("Visible flex child ordinal is out of range.");
    }

    /// <summary>Ensures all reusable item and line buffers can hold the current child count.</summary>
    /// <param name="count">Required capacity.</param>
    private void EnsureCapacity(int count)
    {
        if (_mainSizes.Length >= count)
            return;
        _mainSizes = new float[count];
        _crossSizes = new float[count];
        _lineCounts = new int[count];
        _lineMainSizes = new float[count];
        _lineCrossSizes = new float[count];
        _visibleChildren = new UIElement?[count];
        _frozenItems = new bool[count];
    }

    /// <summary>Gets whether the configured main axis is horizontal.</summary>
    /// <returns>True for row directions.</returns>
    private bool IsHorizontal() => Direction is FlexDirection.Row or FlexDirection.RowReverse;
}
