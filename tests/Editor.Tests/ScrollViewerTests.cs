using System.Numerics;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class ScrollViewerTests
{
    /// <summary>Verifies overflowing content paints before the thumb on a transparent scroll bar.</summary>
    [Fact]
    public void Content_FirstAssignment_PaintsVisibleThumbAboveContent()
    {
        var viewer = new ScrollViewer(100f, 100f)
        {
            Content = new Panel(Color.Red, 100f, 300f)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            }
        };

        var commands = viewer.BuildDrawList().Commands;
        var contentIndex = -1;
        var scrollThumbIndex = -1;
        for (var index = 0; index < commands.Count; index++)
        {
            if (commands[index].Color == Color.Red)
                contentIndex = index;
            if (commands[index].Color == UITheme.Dark.BorderStrong)
                scrollThumbIndex = index;
        }

        Assert.True(viewer.VerticalScrollBar.IsVisible);
        Assert.True(contentIndex >= 0);
        Assert.True(scrollThumbIndex > contentIndex);
        Assert.DoesNotContain(commands, command =>
            command.Color == UITheme.Dark.SurfaceRaised);
    }

    /// <summary>Verifies vertical wheel input moves content and synchronizes the visible bar.</summary>
    [Fact]
    public void Wheel_VerticalOverflow_OffsetsContentAndScrollBar()
    {
        var viewer = new ScrollViewer(100f, 100f);
        var content = new Panel(Color.Red, 90f, 300f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        viewer.Content = content;
        viewer.BuildDrawList();
        var router = new UIEventRouter(viewer, () => { });
        router.MovePointer(new Vector2(20f, 20f));

        router.Scroll(new PointerWheelEvent(
            0, new Vector2(20f, 20f), new Vector2(0f, -1f), InputModifiers.None));
        viewer.BuildDrawList();

        Assert.Equal(32f, viewer.VerticalOffset);
        Assert.Equal(-32f, content.Top);
        Assert.True(viewer.VerticalScrollBar.IsVisible);
        Assert.Equal(viewer.VerticalOffset, viewer.VerticalScrollBar.Value);
    }

    /// <summary>Verifies fractional horizontal touchpad deltas retain precision.</summary>
    [Fact]
    public void Wheel_FractionalHorizontalDelta_ScrollsHorizontally()
    {
        var viewer = new ScrollViewer(100f, 60f) { CanScrollHorizontally = true };
        viewer.Content = new Panel(Color.Red, 300f, 40f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        viewer.BuildDrawList();
        var router = new UIEventRouter(viewer, () => { });

        router.Scroll(new PointerWheelEvent(
            0, new Vector2(20f, 20f), new Vector2(-0.5f, 0f), InputModifiers.None));

        Assert.Equal(16f, viewer.HorizontalOffset);
        Assert.True(viewer.HorizontalScrollBar.IsVisible);
    }

    /// <summary>Verifies offsets clamp when content or viewport limits are exceeded.</summary>
    [Fact]
    public void ScrollTo_BeyondExtent_ClampsToMaximum()
    {
        var viewer = new ScrollViewer(100f, 100f);
        viewer.Content = new Panel(Color.Red, 90f, 250f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        viewer.BuildDrawList();

        viewer.ScrollTo(1000f, 1000f);

        Assert.Equal(viewer.ExtentHeight - 100f, viewer.VerticalOffset);
        Assert.Equal(0f, viewer.HorizontalOffset);
    }

    /// <summary>Verifies clicking the vertical bar updates the owning viewer.</summary>
    [Fact]
    public void VerticalScrollBar_PointerPress_UpdatesViewerOffset()
    {
        var viewer = new ScrollViewer(100f, 100f);
        viewer.Content = new Panel(Color.Red, 90f, 300f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        viewer.BuildDrawList();
        var router = new UIEventRouter(viewer, () => { });

        router.MovePointer(new Vector2(95f, 50f));
        Assert.Same(viewer.VerticalScrollBar, router.HoveredElement);
        router.Press(new PointerButtonEvent(
            0, new Vector2(95f, 50f), InputPointerButton.Primary, true, 1,
            PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.Primary));

        Assert.True(viewer.VerticalOffset > 0f);
        Assert.Equal(viewer.VerticalScrollBar.Value, viewer.VerticalOffset);
    }

    /// <summary>Verifies bar dragging captures input and continues outside its bounds.</summary>
    [Fact]
    public void VerticalScrollBar_DragOutsideBar_UsesPointerCaptureUntilRelease()
    {
        var viewer = new ScrollViewer(100f, 100f)
        {
            Content = new Panel(Color.Red, 90f, 300f)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            }
        };
        viewer.BuildDrawList();
        var router = new UIEventRouter(viewer, () => { });
        router.Press(new PointerButtonEvent(
            0, new Vector2(95f, 20f), InputPointerButton.Primary, true, 1,
            PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.Primary));
        Assert.Same(viewer.VerticalScrollBar.Thumb, router.CapturedElement);

        router.RoutePointerMove(new PointerMoveEvent(
            0, new Vector2(20f, 90f), new Vector2(-75f, 70f), PointerDeviceKind.Mouse,
            InputModifiers.None, PointerButtons.Primary));
        var draggedOffset = viewer.VerticalOffset;
        router.Release(new PointerButtonEvent(
            0, new Vector2(20f, 90f), InputPointerButton.Primary, false, 1,
            PointerDeviceKind.Mouse, InputModifiers.None, PointerButtons.None), invokeClick: true);

        Assert.True(draggedOffset > 100f);
        Assert.Null(router.CapturedElement);
    }

    /// <summary>Verifies a viewer at its limit leaves wheel input for an ancestor viewer.</summary>
    [Fact]
    public void Wheel_InnerViewerAtLimit_BubblesToOuterViewer()
    {
        var inner = new ScrollViewer(90f, 200f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Content = new Panel(Color.Red, 80f, 400f)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            }
        };
        var outer = new ScrollViewer(100f, 100f) { Content = inner };
        outer.BuildDrawList();
        inner.ScrollTo(0f, 1000f);
        var router = new UIEventRouter(outer, () => { });

        router.Scroll(new PointerWheelEvent(
            0, new Vector2(20f, 20f), new Vector2(0f, -1f), InputModifiers.None));

        Assert.Equal(32f, outer.VerticalOffset);
        Assert.Equal(inner.ExtentHeight - 200f, inner.VerticalOffset);
    }

    /// <summary>Verifies steady-state wheel routing and offset updates allocate no managed memory.</summary>
    [Fact]
    public void Wheel_AfterWarmup_DoesNotAllocate()
    {
        var viewer = new ScrollViewer(100f, 100f)
        {
            Content = new Panel(Color.Red, 90f, 300f)
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top
            }
        };
        viewer.BuildDrawList();
        var router = new UIEventRouter(viewer, () => { });
        router.Scroll(new PointerWheelEvent(
            0, new Vector2(20f, 20f), new Vector2(0f, -1f), InputModifiers.None));
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 100; index++)
        {
            var delta = (index & 1) == 0 ? 1f : -1f;
            router.Scroll(new PointerWheelEvent(
                0, new Vector2(20f, 20f), new Vector2(0f, delta), InputModifiers.None));
        }

        Assert.Equal(allocationStart, GC.GetAllocatedBytesForCurrentThread());
    }
}
