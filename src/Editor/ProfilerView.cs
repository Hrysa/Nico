using System.Globalization;
using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>Displays a retained history of frame CPU duration and managed allocation.</summary>
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
    private static readonly Color CpuColor = Color.FromSrgb(0x68, 0x9C, 0xF8);
    private static readonly Color GcColor = Color.FromSrgb(0xF2, 0xA6, 0x5A);
    private static readonly Color SampledFrameColor = Color.FromSrgb(0x86, 0xD9, 0x8B);
    private static readonly Color GuideColor = Color.FromSrgb(0x4A, 0x4A, 0x4A);
    private readonly UITheme _theme;
    private readonly float[] _cpuHistory = new float[HistoryCapacity];
    private readonly long[] _gcHistory = new long[HistoryCapacity];
    private readonly FrameProfileSample[] _frames = new FrameProfileSample[HistoryCapacity];
    private readonly TreeView _callTree;
    private int _nextSample;
    private int _sampleCount;
    private int _samplesSinceRefresh;
    private string _summary = "Waiting for frame samples...";
    private string _cpuCaption = "CPU";
    private string _gcCaption = "GC Alloc";
    private FrameProfileSample? _selectedFrame;

    /// <summary>Gets whether incoming frames are currently paused.</summary>
    public bool IsPaused { get; private set; }

    /// <summary>Gets the selected captured frame number, or zero when no frame is available.</summary>
    public ulong SelectedFrameNumber => _selectedFrame?.FrameNumber ?? 0;

    /// <summary>Gets the total number of samples currently retained.</summary>
    public int SampleCount => _sampleCount;

    /// <summary>Gets the expandable tree displaying the selected frame's call paths.</summary>
    public TreeView CallTree => _callTree;

    /// <summary>Creates an empty profiler history view.</summary>
    /// <param name="theme">Theme supplying profiler colors and typography.</param>
    public ProfilerView(UITheme? theme = null)
        : base((theme ?? UITheme.Dark).Surface)
    {
        _theme = theme ?? UITheme.Dark;
        ForegroundColor = _theme.TextPrimary;
        _callTree = new TreeView(0f, 0f, _theme)
        {
            RowHeight = 19f,
            ShowColumnHeaders = true,
            ColumnHeaderHeight = 20f
        };
        _callTree.SetColumns(
        [
            new TreeViewColumn("Method", 0f, FormatMethodName),
            new TreeViewColumn("Total", 78f, FormatTotalTime, TreeViewColumnAlignment.Right),
            new TreeViewColumn("Self", 78f, FormatSelfTime, TreeViewColumnAlignment.Right),
            new TreeViewColumn("Calls", 54f, FormatCallCount, TreeViewColumnAlignment.Right),
            new TreeViewColumn("GC", 114f, FormatGcAllocation, TreeViewColumnAlignment.Right),
            new TreeViewColumn("Self GC", 76f, FormatSelfGcAllocation, TreeViewColumnAlignment.Right)
        ]);
        AddChild(_callTree);
        Scroll += _callTree.InvokeScroll;
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
        var graphTop = Top + GraphInset + SummaryHeight + CaptionHeight;
        var graphBottom = graphTop + GraphHeight * 2f + GraphGap + CaptionHeight;
        if (_sampleCount == 0 || position.X < Left + 12f || position.X > Right - 12f
            || position.Y < graphTop || position.Y > graphBottom)
            return false;
        var graphWidth = MathF.Max(0f, Width - GraphInset * 2f);
        if (graphWidth <= 0f)
            return false;
        var barWidth = graphWidth / HistoryCapacity;
        var firstX = Left + GraphInset + graphWidth - _sampleCount * barWidth;
        if (position.X < firstX)
            return false;
        var offset = Math.Clamp((int)((position.X - firstX) / barWidth), 0, _sampleCount - 1);
        var oldest = _sampleCount == HistoryCapacity ? _nextSample : 0;
        var selected = _frames[(oldest + offset) % HistoryCapacity];
        _selectedFrame = selected;
        IsPaused = true;
        UpdateDisplay(selected, updateCallTree: true);
        return true;
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
        PaintCallTreeHeader(drawList, gcTop + GraphHeight + GraphInset);
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        var treeTop = GetCallTreeTop() + CallTreeHeaderHeight;
        var treeSize = new Vector2(
            MathF.Max(0f, contentSize.X - GraphInset * 2f),
            MathF.Max(0f, contentSize.Y - treeTop));
        _callTree.Measure(treeSize);
        _callTree.Arrange(new Vector2(GraphInset, treeTop), treeSize);
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

    /// <summary>Formats the inclusive CPU-time column.</summary>
    /// <param name="node">Call-tree node to format.</param>
    /// <returns>Inclusive milliseconds.</returns>
    private static string FormatTotalTime(Node node)
    {
        return node is ProfilerCallTreeNode call
            ? string.Create(CultureInfo.InvariantCulture, $"{call.Marker.TotalMilliseconds:F2} ms")
            : string.Empty;
    }

    /// <summary>Formats the self CPU-time column.</summary>
    /// <param name="node">Call-tree node to format.</param>
    /// <returns>Self milliseconds.</returns>
    private static string FormatSelfTime(Node node)
    {
        return node is ProfilerCallTreeNode call
            ? string.Create(CultureInfo.InvariantCulture, $"{call.Marker.SelfMilliseconds:F2} ms")
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
        _summary = string.Create(CultureInfo.InvariantCulture,
            $"{state}Frame {sample.FrameNumber}    CPU {sample.CpuMilliseconds:F2} ms    Update {sample.UpdateMilliseconds:F2} ms    Render {sample.RenderMilliseconds:F2} ms    GC Alloc {FormatBytes(sample.GcAllocatedBytes)}");
        _cpuCaption = string.Create(CultureInfo.InvariantCulture,
            $"CPU Usage    scale {MathF.Max(33.33f, GetMaximumCpu()):F1} ms    green cap = call tree");
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

        var barWidth = width / HistoryCapacity;
        var firstX = left + width - _sampleCount * barWidth;
        var oldest = _sampleCount == HistoryCapacity ? _nextSample : 0;
        for (var offset = 0; offset < _sampleCount; offset++)
        {
            var index = (oldest + offset) % HistoryCapacity;
            var x = firstX + offset * barWidth;
            var cpuHeight = height * Math.Clamp(_cpuHistory[index] / cpuScale, 0f, 1f);
            var gcHeight = height * Math.Clamp(_gcHistory[index] / (float)gcScale, 0f, 1f);
            drawList.AddRectangle(x, cpuTop + height - cpuHeight,
                x + MathF.Max(1f, barWidth), cpuTop + height, CpuColor);
            if (_frames[index].CpuMarkers is { Length: > 0 })
            {
                drawList.AddRectangle(x, cpuTop, x + MathF.Max(1f, barWidth),
                    cpuTop + 2f, SampledFrameColor);
            }
            drawList.AddRectangle(x, gcTop + height - gcHeight,
                x + MathF.Max(1f, barWidth), gcTop + height, GcColor);
        }
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
