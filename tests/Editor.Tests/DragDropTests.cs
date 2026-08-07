using System.Numerics;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

/// <summary>Exercises automatic, routed drag-and-drop behavior.</summary>
public sealed class DragDropTests
{
    /// <summary>Verifies movement beyond the threshold routes enter, over, and a typed drop.</summary>
    [Fact]
    public void PointerDrag_AutomaticallyRoutesTypedDrop()
    {
        var root = new Canvas { Width = 240f, Height = 100f };
        var source = new Panel(Color.Red, 80f, 80f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            DragData = new UIDragData("asset.mesh"),
            AllowedDragEffects = UIDragEffect.Copy
        };
        var target = new Panel(Color.Blue, 80f, 80f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            AllowDrop = true
        };
        root.Add(source, new Vector2(10f, 10f));
        root.Add(target, new Vector2(140f, 10f));
        root.BuildDrawList();
        var events = new List<UIDragEventKind>();
        string? dropped = null;
        target.Drag += (_, dragEvent) =>
        {
            events.Add(dragEvent.Kind);
            if (dragEvent.Kind is UIDragEventKind.Enter or UIDragEventKind.Over)
                dragEvent.Effect = UIDragEffect.Copy;
            if (dragEvent.Kind == UIDragEventKind.Drop && dragEvent.Data.TryGet<string>(out var value))
                dropped = value;
        };
        var router = new UIEventRouter(root, () => { });

        router.MovePointer(new Vector2(20f, 20f));
        router.Press();
        router.MovePointer(new Vector2(160f, 20f));
        router.Release(invokeClick: true);

        Assert.Equal("asset.mesh", dropped);
        Assert.Equal([UIDragEventKind.Enter, UIDragEventKind.Over, UIDragEventKind.Drop], events);
        Assert.False(router.IsDragging);
        Assert.Null(router.CapturedElement);
    }

    /// <summary>Verifies unsupported target effects are constrained by the source.</summary>
    [Fact]
    public void PointerDrag_UnsupportedEffect_DoesNotDrop()
    {
        var root = CreateDragTree(out var source, out var target);
        source.DragData = new UIDragData(42);
        source.AllowedDragEffects = UIDragEffect.Copy;
        var dropped = false;
        target.Drag += (_, dragEvent) =>
        {
            dragEvent.Effect = UIDragEffect.Move;
            if (dragEvent.Kind == UIDragEventKind.Drop)
                dropped = true;
        };
        var router = new UIEventRouter(root, () => { });

        router.MovePointer(new Vector2(20f, 20f));
        router.Press();
        router.MovePointer(new Vector2(160f, 20f));
        router.Release(invokeClick: true);

        Assert.False(dropped);
    }

    /// <summary>Verifies unexpected capture loss cancels an active drag.</summary>
    [Fact]
    public void ReleasePointerCapture_ActiveDrag_RoutesCancel()
    {
        var root = CreateDragTree(out var source, out _);
        source.DragData = new UIDragData("node");
        var cancelled = false;
        source.Drag += (_, dragEvent) => cancelled |= dragEvent.Kind == UIDragEventKind.Cancel;
        var router = new UIEventRouter(root, () => { });

        router.MovePointer(new Vector2(20f, 20f));
        router.Press();
        router.MovePointer(new Vector2(40f, 20f));
        Assert.True(router.IsDragging);

        router.ReleasePointerCapture();

        Assert.True(cancelled);
        Assert.False(router.IsDragging);
    }

    /// <summary>Verifies the overlay manager follows drag state and removes transient visuals on drop.</summary>
    [Fact]
    public void OverlayManager_ActiveDrag_ShowsPreviewAndAcceptedTargetIndicator()
    {
        var root = CreateDragTree(out var source, out var target);
        source.DragData = new UIDragData("asset-id", "Cube.mesh");
        target.Drag += (_, dragEvent) =>
        {
            if (dragEvent.Kind is UIDragEventKind.Enter or UIDragEventKind.Over)
                dragEvent.Effect = UIDragEffect.Copy;
        };
        var overlay = new Canvas { Width = 240f, Height = 100f };
        root.Add(overlay, Vector2.Zero);
        root.BuildDrawList();
        var router = new UIEventRouter(root, () => { });
        using var manager = new UIOverlayManager(overlay, router);

        router.MovePointer(new Vector2(20f, 20f));
        router.Press();
        router.MovePointer(new Vector2(160f, 20f));

        Assert.Equal("Cube.mesh", manager.DragPreview?.ItemLabel.Text);
        Assert.NotNull(manager.DropIndicator);
        Assert.Equal(target.Width, manager.DropIndicator!.Width);

        router.Release(invokeClick: true);

        Assert.Null(manager.DragPreview);
        Assert.Null(manager.DropIndicator);
    }

    /// <summary>Creates two fixed panels used by drag tests.</summary>
    /// <param name="source">Created source panel.</param>
    /// <param name="target">Created drop panel.</param>
    /// <returns>Arranged canvas containing both panels.</returns>
    private static Canvas CreateDragTree(out Panel source, out Panel target)
    {
        var root = new Canvas { Width = 240f, Height = 100f };
        source = new Panel(Color.Red, 80f, 80f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        target = new Panel(Color.Blue, 80f, 80f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            AllowDrop = true
        };
        root.Add(source, new Vector2(10f, 10f));
        root.Add(target, new Vector2(140f, 10f));
        root.BuildDrawList();
        return root;
    }
}
