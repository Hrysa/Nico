using Editor;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

/// <summary>Exercises retained profiler history and chart generation.</summary>
public sealed class ProfilerViewTests
{
    /// <summary>Verifies the post-build hook records a real Editor method and its allocations.</summary>
    [Fact]
    public void AddSample_InstrumentedBuild_AppearsInManagedCallTree()
    {
        CpuProfiler.Enabled = true;
        try
        {
            var profiler = new ProfilerView();
            CpuProfiler.BeginFrame();

            profiler.AddSample(new FrameProfileSample(1, 5d, 2d, 3d, 512L));

            var tree = CpuProfiler.EndFrame();
            var method = Assert.Single(tree, marker =>
                marker.Name == "Editor.ProfilerView.AddSample(FrameProfileSample)");
            Assert.Equal(1, method.SampleCount);
            Assert.True(method.TotalMilliseconds >= 0d);
            Assert.True(method.GcAllocatedBytes > 0L);
        }
        finally
        {
            CpuProfiler.Enabled = false;
        }
    }

    /// <summary>Verifies profiler history remains bounded while accepting new frames.</summary>
    [Fact]
    public void AddSample_MoreThanCapacity_RetainsBoundedHistory()
    {
        var profiler = new ProfilerView { Width = 800f, Height = 260f };

        for (var index = 0; index < ProfilerView.HistoryCapacity + 20; index++)
        {
            profiler.AddSample(new FrameProfileSample(
                (ulong)(index + 1), 4d + index % 8, 2d, 2d, index * 128L));
        }

        Assert.Equal(ProfilerView.HistoryCapacity, profiler.SampleCount);
        Assert.NotEmpty(profiler.BuildDrawList().Commands);
    }

    /// <summary>Verifies visual refreshes are throttled after the first displayed frame.</summary>
    [Fact]
    public void AddSample_ActiveHistory_ThrottlesDisplayRefresh()
    {
        var profiler = new ProfilerView();
        var sample = new FrameProfileSample(1, 5d, 2d, 3d, 512L);

        Assert.True(profiler.AddSample(sample));
        Assert.False(profiler.AddSample(sample with { FrameNumber = 2 }));
    }

    /// <summary>Verifies adjacent frame bars retain a visible horizontal gap.</summary>
    [Fact]
    public void BuildDrawList_HistoryBars_HaveHorizontalPadding()
    {
        var profiler = new ProfilerView { Width = 800f, Height = 260f };
        profiler.AddSample(new FrameProfileSample(1, 5d, 2d, 3d, 0L));
        profiler.AddSample(new FrameProfileSample(2, 6d, 3d, 3d, 0L));

        var cpuBars = profiler.BuildDrawList().Commands
            .Where(command => command.Type == UIDrawCommandType.Rectangle
                && command.Color == Color.FromSrgb(0x68, 0x9C, 0xF8))
            .OrderBy(command => command.Left)
            .ToArray();

        Assert.Equal(2, cpuBars.Length);
        Assert.True(cpuBars[0].Right < cpuBars[1].Left);
    }

    /// <summary>Verifies hovering a history slot identifies and describes its frame.</summary>
    [Fact]
    public void MovePointer_HistoryBar_PaintsFrameHoverCard()
    {
        var profiler = new ProfilerView { Width = 800f, Height = 260f };
        profiler.AddSample(new FrameProfileSample(7, 5d, 2d, 3d, 512L));
        profiler.BuildDrawList();

        Assert.True(profiler.MovePointer(new System.Numerics.Vector2(
            profiler.Right - 13f, profiler.Top + 70f)));

        Assert.Equal(7UL, profiler.HoveredFrameNumber);
        Assert.Contains(profiler.BuildDrawList().Commands,
            command => command.Text?.Contains("Frame 7  Time 5.00 ms  GC 512 B") == true);
    }

    /// <summary>Verifies the sampled-frame cap becomes white while its bar is active.</summary>
    [Fact]
    public void MovePointer_SampledHistoryBar_PaintsWhiteActiveCap()
    {
        var profiler = new ProfilerView { Width = 800f, Height = 260f };
        profiler.AddSample(new FrameProfileSample(7, 0.01d, 0.01d, 0d, 0L,
        [
            new CpuProfileMarker("Update", -1, 0, 0.01d, 0.01d, 0L, 0L)
        ]));
        profiler.BuildDrawList();
        profiler.MovePointer(new System.Numerics.Vector2(
            profiler.Right - 13f, profiler.Top + 70f));

        Assert.Contains(profiler.BuildDrawList().Commands,
            command => command.Type == UIDrawCommandType.Rectangle
                && command.Color == Color.White
                && MathF.Abs((command.Bottom - command.Top) - 2f) < 0.01f);
    }

    /// <summary>Verifies horizontal arrows navigate older and newer history frames.</summary>
    [Fact]
    public void InvokeKeyDown_LeftRight_NavigatesHistoryFrames()
    {
        var profiler = new ProfilerView { Width = 800f, Height = 260f };
        for (ulong frame = 1; frame <= 3; frame++)
            profiler.AddSample(new FrameProfileSample(frame, 5d, 2d, 3d, 0L));
        profiler.SetPaused(true);

        profiler.InvokeKeyDown((int)InputKey.Left);
        Assert.Equal(2UL, profiler.SelectedFrameNumber);

        profiler.InvokeKeyDown((int)InputKey.Right);
        Assert.Equal(3UL, profiler.SelectedFrameNumber);
    }

    /// <summary>Verifies selecting a graph frame pauses capture and exposes its frame number.</summary>
    [Fact]
    public void SelectFrame_GraphPosition_PausesOnCapturedFrame()
    {
        var profiler = new ProfilerView { Width = 800f, Height = 390f };
        profiler.AddSample(new FrameProfileSample(42, 5d, 2d, 3d, 512L,
        [
            new CpuProfileMarker("Update", -1, 0, 2d, 2d, 128L, 128L)
        ]));
        profiler.BuildDrawList();

        var selected = profiler.SelectFrame(new System.Numerics.Vector2(
            profiler.Right - 13f, profiler.Top + 70f));

        Assert.True(selected);
        Assert.True(profiler.IsPaused);
        Assert.Equal(42UL, profiler.SelectedFrameNumber);
    }

    /// <summary>Verifies a selected frame paints its nested method names.</summary>
    [Fact]
    public void BuildDrawList_SelectedFrame_PaintsMethodCallStack()
    {
        var profiler = new ProfilerView { Width = 800f, Height = 390f };
        profiler.AddSample(new FrameProfileSample(1, 5d, 2d, 3d, 512L,
        [
            new CpuProfileMarker("SilkWindow.OnRender", -1, 0, 3d, 1d, 256L, 64L),
            new CpuProfileMarker("SilkWindow.DrawFrame", 0, 1, 2d, 2d, 192L, 192L)
        ]));
        profiler.SetPaused(true);
        Layout(profiler);

        var commands = profiler.BuildDrawList().Commands;

        Assert.Contains(commands, command => command.Text == "Instrumented Call Tree");
        Assert.Contains(commands, command => command.Text == "Method");
        Assert.Contains(commands, command => command.Text == "Elapsed");
        Assert.Contains(commands, command => command.Text == "Wait");
        Assert.Contains(commands, command => command.Text == "Self GC");
        Assert.Contains(commands, command => command.Text?.Contains("SilkWindow.OnRender") == true);
        Assert.Contains(commands, command => command.Text?.Contains("SilkWindow.DrawFrame") == true);
        var root = Assert.IsType<TreeViewItem>(profiler.CallTree.Children[0]);
        var child = Assert.IsType<TreeViewItem>(profiler.CallTree.Children[1]);
        Assert.Equal(0, root.Depth);
        Assert.Equal(1, child.Depth);
    }

    /// <summary>Verifies metric columns shrink instead of painting beyond a narrow profiler panel.</summary>
    [Fact]
    public void Layout_NarrowPanel_KeepsCallTreeColumnsWithinViewport()
    {
        var profiler = new ProfilerView { Width = 360f, Height = 390f };

        Layout(profiler);

        var fixedWidth = profiler.CallTree.Columns.Skip(1).Sum(column => column.Width);
        Assert.True(profiler.ClipToBounds);
        Assert.True(fixedWidth <= profiler.CallTree.Width);
        Assert.True(profiler.CallTree.Columns[0].Width <= 0f);
    }

    /// <summary>Verifies one wheel unit advances the dense profiler call tree by one row.</summary>
    [Fact]
    public void InvokeScroll_CallTree_UsesOneRowPerWheelUnit()
    {
        var profiler = new ProfilerView { Width = 800f, Height = 390f };
        var markers = Enumerable.Range(0, 20)
            .Select(index => new CpuProfileMarker(
                $"Method{index}", index - 1, index, 1d, 1d, 0L, 0L, 1))
            .ToArray();
        profiler.AddSample(new FrameProfileSample(1, 5d, 2d, 3d, 0L, markers));
        profiler.SetPaused(true);
        Layout(profiler);
        var router = new UIEventRouter(profiler, () => { });

        router.Scroll(new PointerWheelEvent(
            0,
            new System.Numerics.Vector2(
                profiler.CallTreeScroller.Left + 10f,
                profiler.CallTreeScroller.Top + 30f),
            new System.Numerics.Vector2(0f, -1f),
            InputModifiers.None));

        Assert.Equal(profiler.CallTree.RowHeight, profiler.CallTreeScroller.VerticalOffset);
    }

    /// <summary>Verifies the mouse wheel reveals instrumented call-tree rows below the viewport.</summary>
    [Fact]
    public void InvokeScroll_LongCallTree_RevealsLaterRows()
    {
        var profiler = new ProfilerView { Width = 800f, Height = 390f };
        var markers = Enumerable.Range(0, 20)
            .Select(index => new CpuProfileMarker(
                $"Method{index}", index - 1, index, 1d, 1d, 0L, 0L, 1))
            .ToArray();
        profiler.AddSample(new FrameProfileSample(1, 5d, 2d, 3d, 0L, markers));
        profiler.SetPaused(true);
        Layout(profiler);

        Assert.DoesNotContain(profiler.BuildDrawList().Commands,
            command => command.Text?.Contains("Method10") == true);

        profiler.CallTreeScroller.ScrollTo(0f, 10f * profiler.CallTree.RowHeight);

        Assert.Contains(profiler.BuildDrawList().Commands,
            command => command.Text?.Contains("Method10") == true);
    }

    /// <summary>Runs the profiler through a normal measure and arrange pass.</summary>
    /// <param name="profiler">Profiler view to lay out.</param>
    private static void Layout(ProfilerView profiler)
    {
        var size = new System.Numerics.Vector2(profiler.Width, profiler.Height);
        profiler.Measure(size);
        profiler.Arrange(System.Numerics.Vector2.Zero, size);
    }
}
