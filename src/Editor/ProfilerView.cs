using System.Globalization;
using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>Displays a retained history of frame elapsed duration and managed allocation.</summary>
public sealed class ProfilerView : Panel
{
    /// <summary>Gets the number of frame samples retained by the charts.</summary>
    public const int HistoryCapacity = 180;

    private const int RefreshIntervalSamples = 12;
    private const float GraphInset = 12f;
    private const float SummaryHeight = 26f;
    private const float CaptionHeight = 20f;
    private const float GraphHeight = 45f;
    private const float GraphGap = 8f;
    private const float CallTreeHeaderHeight = 20f;
    private const float PreferredMethodColumnWidth = 120f;
    private const float PreferredMetricColumnWidth = 75f;
    private static readonly Color CpuColor = Color.FromSrgb(0x68, 0x9C, 0xF8);
    private static readonly Color GcColor = Color.FromSrgb(0xF2, 0xA6, 0x5A);
    private static readonly Color SampledFrameColor = Color.FromSrgb(0x86, 0xD9, 0x8B);
    private static readonly Color GuideColor = Color.FromSrgb(0x4A, 0x4A, 0x4A);
    private static readonly Color HoverColor = Color.FromSrgb(0xD8, 0xE5, 0xFF);
    private readonly UITheme _theme;
    private readonly float[] _cpuHistory = new float[HistoryCapacity];
    private readonly long[] _gcHistory = new long[HistoryCapacity];
    private readonly FrameProfileSample[] _frames = new FrameProfileSample[HistoryCapacity];
    private readonly TreeView _callTree;
    private readonly ScrollViewer _callTreeScroller;
    private int _nextSample;
    private int _sampleCount;
    private int _samplesSinceRefresh;
    private float _callTreeMetricColumnWidth = PreferredMetricColumnWidth;
    private string _summary = "Waiting for frame samples...";
    private string _cpuCaption = "Frame Time";
    private string _gcCaption = "GC Alloc";
    private FrameProfileSample? _selectedFrame;
    private int _hoveredHistoryIndex = -1;
    private Vector2 _hoverPosition;

    /// <summary>Gets whether incoming frames are currently paused.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Gets the selected captured frame number, or zero when no frame is available.</summary>
    public ulong SelectedFrameNumber => _selectedFrame?.FrameNumber ?? 0;

    /// <summary>Gets the total number of samples currently retained.</summary>
    public int SampleCount => _sampleCount;

    /// <summary>Gets the frame currently under the pointer, or zero when no bar is hovered.</summary>
    public ulong HoveredFrameNumber => _hoveredHistoryIndex >= 0
        ? _frames[_hoveredHistoryIndex].FrameNumber : 0;

    /// <summary>Gets the expandable tree displaying the selected frame's call paths.</summary>
    public TreeView CallTree => _callTree;

    /// <summary>Gets the scroll container owning the call-tree viewport and scroll bar.</summary>
    public ScrollViewer CallTreeScroller => _callTreeScroller;

    /// <summary>Creates an empty profiler history view.</summary>
    /// <param name="theme">Theme supplying profiler colors and typography.</param>
    public ProfilerView(UITheme? theme = null)
        : base((theme ?? UITheme.Dark).Surface)
    {
        _theme = theme ?? UITheme.Dark;
        ForegroundColor = _theme.TextPrimary;
        ClipToBounds = true;
        _callTree = new TreeView(0f, 0f, _theme)
        {
            RowHeight = 19f,
            ShowColumnHeaders = true,
            ColumnHeaderHeight = 20f
        };
        _callTree.SetColumns(
        [
            new TreeViewColumn("Method", 0f, FormatMethodName),
            CreateMetricColumn("Elapsed", FormatTotalTime),
            CreateMetricColumn("Self", FormatSelfTime),
            CreateMetricColumn("Wait", FormatWaitTime),
            CreateMetricColumn("Calls", FormatCallCount),
            CreateMetricColumn("GC", FormatGcAllocation),
            CreateMetricColumn("Self GC", FormatSelfGcAllocation)
        ]);
        _callTreeScroller = new ScrollViewer(theme: _theme)
        {
            Content = _callTree,
            WheelStep = _callTree.RowHeight
        };
        AddChild(_callTreeScroller);
    }

    /// <summary>Adds one frame and reports whether the visible charts should be submitted.</summary>
    /// <param name="sample">Frame measurement to append.</param>
    /// <returns>True when the throttled display snapshot changed.</returns>
    public bool AddSample(FrameProfileSample sample)
    {
        if (IsPaused)
            return false;

        _cpuHistory[_nextSample] = float.IsFinite((float)sample.CpuMilliseconds)
            ? MathF.Max(0f, (float)sample.CpuMilliseconds) : 0f;
        _gcHistory[_nextSample] = Math.Max(0L, sample.GcAllocatedBytes);
        _frames[_nextSample] = sample;
        _nextSample = (_nextSample + 1) % HistoryCapacity;
        _sampleCount = Math.Min(HistoryCapacity, _sampleCount + 1);
        _samplesSinceRefresh++;
        if (_samplesSinceRefresh < RefreshIntervalSamples && _sampleCount > 1)
            return false;

        _samplesSinceRefresh = 0;
        UpdateDisplay(sample, updateCallTree: false);
        return true;
    }

    /// <summary>Changes whether incoming frame capture is paused.</summary>
    /// <param name="paused">True to retain the current history without appending frames.</param>
    public void SetPaused(bool paused)
    {
        if (IsPaused == paused)
            return;
        IsPaused = paused;
        if (_sampleCount == 0)
        {
            InvalidateVisual();
            return;
        }

        var newest = _frames[(_nextSample + HistoryCapacity - 1) % HistoryCapacity];
        _selectedFrame = paused ? newest : null;
        if (!paused)
            _callTree.SetRoots([]);
        UpdateDisplay(newest, updateCallTree: paused);
    }

    /// <summary>Selects the captured frame under a graph position and pauses capture.</summary>
    /// <param name="position">Screen-space click position.</param>
    /// <returns>True when a retained frame was selected.</returns>
    public bool SelectFrame(Vector2 position)
    {
        if (!TryGetHistoryIndex(position, out var selectedIndex))
            return false;
        var selected = _frames[selectedIndex];
        _selectedFrame = selected;
        IsPaused = true;
        UpdateDisplay(selected, updateCallTree: true);
        return true;
    }

    /// <summary>Updates the history-bar hover from a screen-space pointer position.</summary>
    /// <param name="position">Pointer position in screen coordinates.</param>
    /// <returns>True when the hovered frame changed.</returns>
    public bool MovePointer(Vector2 position)
    {
        var hoveredIndex = TryGetHistoryIndex(position, out var index) ? index : -1;
        _hoverPosition = position;
        if (_hoveredHistoryIndex == hoveredIndex)
            return false;
        _hoveredHistoryIndex = hoveredIndex;
        InvalidateVisual();
        return true;
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(int keyCode)
    {
        switch ((InputKey)keyCode)
        {
            case InputKey.Left:
                MoveSelectedFrame(-1);
                break;
            case InputKey.Right:
                MoveSelectedFrame(1);
                break;
        }
        base.OnKeyDown(keyCode);
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        base.Paint(drawList);
        var graphWidth = MathF.Max(0f, Width - GraphInset * 2f);
        var cpuTop = Top + GraphInset + SummaryHeight + CaptionHeight;
        var gcCaptionTop = cpuTop + GraphHeight + GraphGap;
        var gcTop = gcCaptionTop + CaptionHeight;

        drawList.AddText(_summary, Left + GraphInset, Top + GraphInset,
            _theme.CaptionFontSize, _theme.TextPrimary, BackgroundColor);
        drawList.AddText(_cpuCaption, Left + GraphInset, cpuTop - CaptionHeight,
            _theme.CaptionFontSize, _theme.TextSecondary, BackgroundColor);
        drawList.AddText(_gcCaption, Left + GraphInset, gcCaptionTop,
            _theme.CaptionFontSize, _theme.TextSecondary, BackgroundColor);
        drawList.AddRectangle(Left + GraphInset, cpuTop, Left + GraphInset + graphWidth,
            cpuTop + GraphHeight, _theme.Field);
        drawList.AddRectangle(Left + GraphInset, gcTop, Left + GraphInset + graphWidth,
            gcTop + GraphHeight, _theme.Field);

        var cpuScale = MathF.Max(33.33f, GetMaximumCpu());
        var targetY = cpuTop + GraphHeight * (1f - 16.67f / cpuScale);
        drawList.AddRectangle(Left + GraphInset, targetY, Left + GraphInset + graphWidth,
            targetY + 1f, GuideColor);
        PaintHistory(drawList, Left + GraphInset, cpuTop, graphWidth, GraphHeight,
            cpuScale, Math.Max(1024L, GetMaximumGc()), gcTop);
        PaintHoveredFrame(drawList, cpuTop, gcTop);
        PaintCallTreeHeader(drawList, gcTop + GraphHeight + GraphInset);
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        var treeTop = GetCallTreeTop() + CallTreeHeaderHeight;
        var treeSize = new Vector2(
            MathF.Max(0f, contentSize.X - GraphInset * 2f),
            MathF.Max(0f, contentSize.Y - treeTop));
        ResizeCallTreeColumns(treeSize.X);
        _callTreeScroller.Measure(treeSize);
        _callTreeScroller.Arrange(new Vector2(GraphInset, treeTop), treeSize);
    }

    /// <summary>Creates one right-aligned profiler metric column that may shrink with its panel.</summary>
    /// <param name="header">Column header.</param>
    /// <param name="value">Cell formatter.</param>
    /// <returns>Configured metric column.</returns>
    private static TreeViewColumn CreateMetricColumn(string header, Func<Node, string> value)
    {
        return new TreeViewColumn(
            header, PreferredMetricColumnWidth, value, TreeViewColumnAlignment.Right)
        {
            MinWidth = 0f
        };
    }

    /// <summary>Keeps fixed profiler metrics inside the available call-tree width.</summary>
    /// <param name="availableWidth">Current call-tree viewport width.</param>
    private void ResizeCallTreeColumns(float availableWidth)
    {
        if (availableWidth <= 0f)
            return;
        var methodWidth = MathF.Min(PreferredMethodColumnWidth, availableWidth * 0.4f);
        var metricWidth = MathF.Min(
            PreferredMetricColumnWidth,
            MathF.Max(0f, availableWidth - methodWidth) / 6f);
        if (MathF.Abs(metricWidth - _callTreeMetricColumnWidth) < 0.01f)
            return;
        _callTreeMetricColumnWidth = metricWidth;
        for (var index = 1; index < _callTree.Columns.Count; index++)
            _callTree.ResizeColumn(index, metricWidth);
    }

    /// <summary>Paints the label above the expandable call tree.</summary>
    /// <param name="drawList">Draw list receiving the header.</param>
    /// <param name="top">Header top edge in screen coordinates.</param>
    private void PaintCallTreeHeader(UIDrawList drawList, float top)
    {
        var markers = _selectedFrame?.CpuMarkers;
        drawList.AddText("Instrumented Call Tree", Left + GraphInset, top,
            _theme.CaptionFontSize, _theme.TextSecondary, BackgroundColor);
        if (markers is { Length: > 0 })
            return;
        var emptyMessage = _selectedFrame is null
            ? "Pause or click a frame to inspect its managed call tree."
            : "No instrumented managed method ran in this frame.";
        drawList.AddText(emptyMessage, Left + GraphInset,
            top + CallTreeHeaderHeight + _callTree.ColumnHeaderHeight,
            _theme.CaptionFontSize, _theme.TextMuted, BackgroundColor);
    }

    /// <summary>Returns the call-tree header offset within this view.</summary>
    /// <returns>Local Y coordinate of the call-tree header.</returns>
    private static float GetCallTreeTop()
    {
        return GraphInset + SummaryHeight + CaptionHeight + GraphHeight + GraphGap +
            CaptionHeight + GraphHeight + GraphInset;
    }

    /// <summary>Builds expandable nodes from the selected frame's flat pre-order markers.</summary>
    /// <param name="markers">Captured call-path rows.</param>
    private void UpdateCallTree(CpuProfileMarker[]? markers)
    {
        if (markers is null || markers.Length == 0)
        {
            _callTree.SetRoots([]);
            return;
        }

        var nodes = markers.Select(marker => new ProfilerCallTreeNode(marker)).ToArray();
        var roots = new List<Node>();
        for (var index = 0; index < nodes.Length; index++)
        {
            var parentIndex = markers[index].ParentIndex;
            if ((uint)parentIndex < (uint)nodes.Length && parentIndex != index)
                nodes[parentIndex].AddChild(nodes[index]);
            else
                roots.Add(nodes[index]);
        }
        _callTree.SetRoots(roots);
        _callTree.SetExpanded(nodes);
    }

    /// <summary>Formats the hierarchy method-name column.</summary>
    /// <param name="node">Call-tree node to format.</param>
    /// <returns>Method display name.</returns>
    private static string FormatMethodName(Node node)
    {
        return node.Name;
    }

    /// <summary>Formats the inclusive elapsed-time column.</summary>
    /// <param name="node">Call-tree node to format.</param>
    /// <returns>Inclusive milliseconds.</returns>
    private static string FormatTotalTime(Node node)
    {
        return node is ProfilerCallTreeNode call
            ? string.Create(CultureInfo.InvariantCulture, $"{call.Marker.TotalMilliseconds:F2} ms")
            : string.Empty;
    }

    /// <summary>Formats the self elapsed-time column.</summary>
    /// <param name="node">Call-tree node to format.</param>
    /// <returns>Self milliseconds.</returns>
    private static string FormatSelfTime(Node node)
    {
        return node is ProfilerCallTreeNode call
            ? string.Create(CultureInfo.InvariantCulture, $"{call.Marker.SelfMilliseconds:F2} ms")
            : string.Empty;
    }

    /// <summary>Formats the inclusive explicit-wait column.</summary>
    /// <param name="node">Call-tree node to format.</param>
    /// <returns>Inclusive wait milliseconds.</returns>
    private static string FormatWaitTime(Node node)
    {
        return node is ProfilerCallTreeNode call
            ? string.Create(CultureInfo.InvariantCulture, $"{call.Marker.WaitMilliseconds:F2} ms")
            : string.Empty;
    }

    /// <summary>Formats the invocation-count column.</summary>
    /// <param name="node">Call-tree node to format.</param>
    /// <returns>Invocation count.</returns>
    private static string FormatCallCount(Node node)
    {
        return node is ProfilerCallTreeNode call
            ? call.Marker.SampleCount.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    /// <summary>Formats the inclusive allocation column.</summary>
    /// <param name="node">Call-tree node to format.</param>
    /// <returns>Inclusive allocation.</returns>
    private static string FormatGcAllocation(Node node)
    {
        return node is ProfilerCallTreeNode call
            ? FormatBytes(call.Marker.GcAllocatedBytes)
            : string.Empty;
    }

    /// <summary>Formats the self-allocation column.</summary>
    /// <param name="node">Call-tree node to format.</param>
    /// <returns>Self allocation.</returns>
    private static string FormatSelfGcAllocation(Node node)
    {
        return node is ProfilerCallTreeNode call
            ? FormatBytes(call.Marker.SelfGcAllocatedBytes)
            : string.Empty;
    }

    /// <summary>Updates captions for one selected or live frame.</summary>
    /// <param name="sample">Frame displayed by the detail area.</param>
    /// <param name="updateCallTree">Whether to rebuild detailed method rows.</param>
    private void UpdateDisplay(FrameProfileSample sample, bool updateCallTree)
    {
        var state = IsPaused ? "PAUSED    " : string.Empty;
        var gpu = sample.Gpu is { } gpuSample
            ? string.Create(CultureInfo.InvariantCulture,
                $"    GPU {gpuSample.FrameMilliseconds:F2} ms (View {gpuSample.ViewportMilliseconds:F2}, UI {gpuSample.CompositionMilliseconds:F2}, frame {gpuSample.FrameNumber})")
            : string.Empty;
        _summary = string.Create(CultureInfo.InvariantCulture,
            $"{state}Frame {sample.FrameNumber}    Time {sample.CpuMilliseconds:F2} ms    Update {sample.UpdateMilliseconds:F2} ms    Render {sample.RenderMilliseconds:F2} ms{gpu}    GC Alloc {FormatBytes(sample.GcAllocatedBytes)}");
        _cpuCaption = string.Create(CultureInfo.InvariantCulture,
            $"Frame Time    scale {MathF.Max(33.33f, GetMaximumCpu()):F1} ms    green cap = call tree");
        _gcCaption = $"GC Alloc    scale {FormatBytes(Math.Max(1024L, GetMaximumGc()))}";
        if (updateCallTree)
            UpdateCallTree(sample.CpuMarkers);
        InvalidateVisual();
    }

    /// <summary>Paints chronological CPU and allocation bars into their chart regions.</summary>
    /// <param name="drawList">Draw list receiving chart bars.</param>
    /// <param name="left">Chart left edge.</param>
    /// <param name="cpuTop">CPU chart top edge.</param>
    /// <param name="width">Chart width.</param>
    /// <param name="height">Height of each chart.</param>
    /// <param name="cpuScale">CPU chart maximum in milliseconds.</param>
    /// <param name="gcScale">Allocation chart maximum in bytes.</param>
    /// <param name="gcTop">Allocation chart top edge.</param>
    private void PaintHistory(
        UIDrawList drawList,
        float left,
        float cpuTop,
        float width,
        float height,
        float cpuScale,
        long gcScale,
        float gcTop)
    {
        if (_sampleCount == 0 || width <= 0f || height <= 0f)
            return;

        var slotWidth = width / HistoryCapacity;
        var barGap = slotWidth >= 2f ? 1f : slotWidth * 0.2f;
        var barWidth = MathF.Max(0.5f, slotWidth - barGap);
        var firstX = left + width - _sampleCount * slotWidth;
        var oldest = _sampleCount == HistoryCapacity ? _nextSample : 0;
        for (var offset = 0; offset < _sampleCount; offset++)
        {
            var index = (oldest + offset) % HistoryCapacity;
            var x = firstX + offset * slotWidth + barGap * 0.5f;
            var cpuHeight = height * Math.Clamp(_cpuHistory[index] / cpuScale, 0f, 1f);
            var gcHeight = height * Math.Clamp(_gcHistory[index] / (float)gcScale, 0f, 1f);
            drawList.AddRectangle(x, cpuTop + height - cpuHeight,
                x + barWidth, cpuTop + height,
                index == _hoveredHistoryIndex ? HoverColor : CpuColor);
            if (_frames[index].CpuMarkers is { Length: > 0 })
            {
                drawList.AddRectangle(x, cpuTop, x + barWidth,
                    cpuTop + 2f, index == _hoveredHistoryIndex
                        || _selectedFrame?.FrameNumber == _frames[index].FrameNumber
                            ? Color.White : SampledFrameColor);
            }
            drawList.AddRectangle(x, gcTop + height - gcHeight,
                x + barWidth, gcTop + height,
                index == _hoveredHistoryIndex ? HoverColor : GcColor);
        }
    }

    /// <summary>Paints details for the frame currently under the pointer.</summary>
    /// <param name="drawList">Draw list receiving the hover card.</param>
    /// <param name="cpuTop">CPU chart top edge.</param>
    /// <param name="gcTop">Allocation chart top edge.</param>
    private void PaintHoveredFrame(UIDrawList drawList, float cpuTop, float gcTop)
    {
        if (_hoveredHistoryIndex < 0)
            return;
        var sample = _frames[_hoveredHistoryIndex];
        const float tooltipWidth = 270f;
        const float tooltipHeight = 24f;
        var x = Math.Clamp(_hoverPosition.X + 10f, Left + GraphInset,
            MathF.Max(Left + GraphInset, Right - GraphInset - tooltipWidth));
        var y = _hoverPosition.Y <= cpuTop + GraphHeight
            ? gcTop + GraphHeight - tooltipHeight
            : cpuTop;
        drawList.AddRectangle(x, y, x + tooltipWidth, y + tooltipHeight, _theme.SurfaceHover);
        drawList.AddText(string.Create(CultureInfo.InvariantCulture,
                $"Frame {sample.FrameNumber}  Time {sample.CpuMilliseconds:F2} ms  GC {FormatBytes(sample.GcAllocatedBytes)}"),
            x + 6f, y + 4f, _theme.CaptionFontSize, _theme.TextPrimary, _theme.SurfaceHover);
    }

    /// <summary>Maps a chart position to its retained history slot.</summary>
    /// <param name="position">Screen-space position to test.</param>
    /// <param name="historyIndex">Receives the circular-buffer index.</param>
    /// <returns>True when the position intersects a populated frame slot.</returns>
    private bool TryGetHistoryIndex(Vector2 position, out int historyIndex)
    {
        historyIndex = -1;
        var graphTop = Top + GraphInset + SummaryHeight + CaptionHeight;
        var graphBottom = graphTop + GraphHeight * 2f + GraphGap + CaptionHeight;
        var graphWidth = MathF.Max(0f, Width - GraphInset * 2f);
        if (_sampleCount == 0 || graphWidth <= 0f || position.X < Left + GraphInset
            || position.X > Right - GraphInset || position.Y < graphTop
            || position.Y > graphBottom)
            return false;
        var slotWidth = graphWidth / HistoryCapacity;
        var firstX = Left + GraphInset + graphWidth - _sampleCount * slotWidth;
        if (position.X < firstX)
            return false;
        var offset = Math.Clamp((int)((position.X - firstX) / slotWidth), 0, _sampleCount - 1);
        var oldest = _sampleCount == HistoryCapacity ? _nextSample : 0;
        historyIndex = (oldest + offset) % HistoryCapacity;
        return true;
    }

    /// <summary>Moves the selected history frame by a chronological offset.</summary>
    /// <param name="direction">Negative for an older frame or positive for a newer frame.</param>
    private void MoveSelectedFrame(int direction)
    {
        if (_sampleCount == 0)
            return;
        var oldest = _sampleCount == HistoryCapacity ? _nextSample : 0;
        var currentOffset = _sampleCount - 1;
        var activeFrameNumber = _selectedFrame?.FrameNumber
            ?? (_hoveredHistoryIndex >= 0 ? _frames[_hoveredHistoryIndex].FrameNumber : 0UL);
        if (activeFrameNumber != 0)
        {
            for (var offset = 0; offset < _sampleCount; offset++)
            {
                if (_frames[(oldest + offset) % HistoryCapacity].FrameNumber != activeFrameNumber)
                    continue;
                currentOffset = offset;
                break;
            }
        }
        var targetOffset = Math.Clamp(currentOffset + Math.Sign(direction), 0, _sampleCount - 1);
        var target = _frames[(oldest + targetOffset) % HistoryCapacity];
        _selectedFrame = target;
        IsPaused = true;
        UpdateDisplay(target, updateCallTree: true);
    }

    /// <summary>Returns the largest retained CPU duration.</summary>
    /// <returns>Maximum CPU duration in milliseconds.</returns>
    private float GetMaximumCpu()
    {
        var maximum = 0f;
        for (var index = 0; index < _sampleCount; index++)
            maximum = MathF.Max(maximum, _cpuHistory[index]);
        return maximum;
    }

    /// <summary>Returns the largest retained managed allocation.</summary>
    /// <returns>Maximum allocation in bytes.</returns>
    private long GetMaximumGc()
    {
        var maximum = 0L;
        for (var index = 0; index < _sampleCount; index++)
            maximum = Math.Max(maximum, _gcHistory[index]);
        return maximum;
    }

    /// <summary>Formats a byte count using compact binary units.</summary>
    /// <param name="bytes">Byte count to format.</param>
    /// <returns>Human-readable allocation text.</returns>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        if (bytes < 1024 * 1024)
            return string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024d:F1} KB");
        return string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024d * 1024d):F1} MB");
    }

    /// <summary>Adapts one flat profiler marker to the engine node hierarchy used by TreeView.</summary>
    private sealed class ProfilerCallTreeNode : Node
    {
        /// <summary>Creates a node for one profiler marker.</summary>
        /// <param name="marker">Captured method measurement.</param>
        internal ProfilerCallTreeNode(CpuProfileMarker marker)
        {
            Marker = marker;
            Name = marker.Name;
        }

        internal CpuProfileMarker Marker { get; }
    }
}
