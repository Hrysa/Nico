using System.Numerics;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

/// <summary>Exercises the renderer-independent measure and arrange system.</summary>
public sealed class UILayoutTests
{
    /// <summary>Verifies fixed tracks are allocated before proportional tracks.</summary>
    [Fact]
    public void Grid_FixedAndStarColumns_AllocateExpectedBounds()
    {
        var grid = new Grid(Color.Black);
        grid.Columns.Add(GridLength.Pixels(100f));
        grid.Columns.Add(GridLength.Star());
        grid.Columns.Add(GridLength.Pixels(50f));
        grid.Rows.Add(GridLength.Star());
        var left = new Panel(Color.Red);
        var center = new Panel(Color.Green);
        var right = new Panel(Color.Blue);
        grid.Add(left, 0, 0);
        grid.Add(center, 0, 1);
        grid.Add(right, 0, 2);

        grid.Measure(new Vector2(400f, 200f));
        grid.Arrange(Vector2.Zero, new Vector2(400f, 200f));

        Assert.Equal(100f, left.Width);
        Assert.Equal(250f, center.Width);
        Assert.Equal(50f, right.Width);
        Assert.Equal(100f, center.Left);
        Assert.Equal(350f, right.Left);
    }

    /// <summary>Verifies a fixed child can align within a larger grid cell.</summary>
    [Fact]
    public void Grid_Alignment_PositionsFixedChildInsideCell()
    {
        var grid = new Grid(Color.Black);
        grid.Columns.Add(GridLength.Star());
        grid.Rows.Add(GridLength.Star());
        var child = new Panel(Color.Red, 80f, 20f)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Add(child, 0, 0);

        grid.Measure(new Vector2(300f, 100f));
        grid.Arrange(Vector2.Zero, new Vector2(300f, 100f));

        Assert.Equal(220f, child.Left);
        Assert.Equal(40f, child.Top);
        Assert.Equal(80f, child.Width);
        Assert.Equal(20f, child.Height);
    }

    /// <summary>Verifies both left-dock panels consume proportional growth after a root resize.</summary>
    [Fact]
    public void EditorLeftDock_Resize_GrowsHierarchyAndFileSystem()
    {
        var view = EditorUI.BuildView(1280f, 720f);
        var hierarchy = view.HierarchyTree;
        var fileSystem = view.FileSystemTree;
        var hierarchyHeight = hierarchy.Height;
        var fileSystemHeight = fileSystem.Height;

        view.Root.Measure(new Vector2(1280f, 920f));
        view.Root.Arrange(Vector2.Zero, new Vector2(1280f, 920f));

        Assert.True(hierarchy.Height > hierarchyHeight);
        Assert.True(fileSystem.Height > fileSystemHeight);
    }

    /// <summary>Verifies canvas coordinates are owned and updated by the overlay container.</summary>
    [Fact]
    public void Canvas_SetPosition_MovesFloatingChild()
    {
        var canvas = new Canvas { Width = 400f, Height = 300f };
        var menu = new ContextMenu(160f);
        menu.AddItem("Open", () => { });
        canvas.Add(menu, new Vector2(20f, 30f));

        canvas.BuildDrawList();
        Assert.Equal(20f, menu.Left);
        Assert.Equal(30f, menu.Top);

        canvas.SetPosition(menu, new Vector2(80f, 90f));
        canvas.BuildDrawList();
        Assert.Equal(80f, menu.Left);
        Assert.Equal(90f, menu.Top);
    }

    /// <summary>Verifies popup placement remains fully inside the overlay canvas.</summary>
    [Fact]
    public void Canvas_PopupNearEdge_ClampsPlacementToBounds()
    {
        var canvas = new Canvas { Width = 200f, Height = 100f };
        var popup = new Popup(Color.Black, Color.White, 80f, 30f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        canvas.Add(popup, new Vector2(180f, 90f));

        canvas.BuildDrawList();

        Assert.Equal(120f, popup.Left);
        Assert.Equal(70f, popup.Top);
        Assert.Equal(200f, popup.Right);
        Assert.Equal(100f, popup.Bottom);
    }

    /// <summary>Verifies owner-relative placement flips above an obstructed lower edge.</summary>
    [Fact]
    public void Canvas_PlacePopupBelowNearBottom_FlipsAboveOwner()
    {
        var canvas = new Canvas { Width = 200f, Height = 100f };
        var owner = new Panel(Color.Red, 40f, 20f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var popup = new Popup(Color.Black, Color.White, 80f, 30f)
        {
            Owner = owner,
            Placement = PopupPlacement.Below,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        canvas.Add(owner, new Vector2(20f, 75f));
        canvas.Add(popup, Vector2.Zero);
        canvas.BuildDrawList();

        canvas.PlacePopup(popup);
        canvas.BuildDrawList();

        Assert.Equal(PopupPlacement.Above, popup.ActualPlacement);
        Assert.Equal(20f, popup.Left);
        Assert.Equal(45f, popup.Top);
    }

    /// <summary>Verifies popup placement uses monitor-specific logical work-area bounds.</summary>
    [Fact]
    public void Canvas_PlacePopup_UsesProvidedWorkArea()
    {
        var canvas = new Canvas
        {
            Width = 300f,
            Height = 200f,
            PopupWorkAreaProvider = new FixedWorkAreaProvider(
                new UIPopupWorkArea(100f, 20f, 200f, 100f, 1.5f))
        };
        var owner = new Panel(Color.Red, 30f, 20f)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        var popup = new Popup(Color.Black, Color.White, 80f, 30f)
        {
            Owner = owner,
            Placement = PopupPlacement.Below,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        };
        canvas.Add(owner, new Vector2(160f, 70f));
        canvas.Add(popup, Vector2.Zero);
        canvas.BuildDrawList();

        canvas.PlacePopup(popup);
        canvas.BuildDrawList();

        Assert.Equal(PopupPlacement.Above, popup.ActualPlacement);
        Assert.Equal(120f, popup.Left);
        Assert.Equal(40f, popup.Top);
    }

    /// <summary>Verifies popup work-area DPI conversion is reversible.</summary>
    [Fact]
    public void PopupWorkArea_DpiConversion_RoundTrips()
    {
        var workArea = new UIPopupWorkArea(0f, 0f, 100f, 100f, 1.5f);
        var logical = new Vector2(20f, 30f);

        var physical = workArea.LogicalToPhysical(logical);

        Assert.Equal(new Vector2(30f, 45f), physical);
        Assert.Equal(logical, workArea.PhysicalToLogical(physical));
    }

    /// <summary>Verifies renderer-level display services adapt without leaking platform types into UI.</summary>
    [Fact]
    public void DisplayPopupWorkAreaProvider_AdaptsGraphicsService()
    {
        var provider = new DisplayPopupWorkAreaProvider(new FixedDisplayService());

        var area = provider.GetWorkArea(new Vector2(10f, 20f));

        Assert.Equal(new UIPopupWorkArea(5f, 6f, 105f, 106f, 1.25f), area);
    }

    /// <summary>Returns one deterministic popup work area for placement tests.</summary>
    private sealed class FixedWorkAreaProvider : IPopupWorkAreaProvider
    {
        private readonly UIPopupWorkArea _workArea;

        /// <summary>Creates a fixed provider.</summary>
        /// <param name="workArea">Area returned for every anchor.</param>
        public FixedWorkAreaProvider(UIPopupWorkArea workArea) => _workArea = workArea;

        /// <inheritdoc/>
        public UIPopupWorkArea GetWorkArea(Vector2 anchorPoint) => _workArea;
    }

    /// <summary>Returns deterministic renderer-level display data.</summary>
    private sealed class FixedDisplayService : IDisplayService
    {
        /// <inheritdoc/>
        public DisplayWorkArea GetWorkArea(Vector2 clientAnchor) =>
            new(5f, 6f, 105f, 106f, 1.25f);
    }

    /// <summary>Verifies root resize preserves the existing editor element instances.</summary>
    [Fact]
    public void EditorRoot_Resize_ReusesElementTree()
    {
        var view = EditorUI.BuildView(1280f, 720f);
        var hierarchy = view.HierarchyTree;
        var sceneViewport = view.SceneViewport;

        view.Root.Measure(new Vector2(1440f, 900f));
        view.Root.Arrange(Vector2.Zero, new Vector2(1440f, 900f));

        Assert.Same(hierarchy, view.HierarchyTree);
        Assert.Same(sceneViewport, view.SceneViewport);
        Assert.Equal(1440f, view.Root.Width);
        Assert.Equal(900f, view.Root.Height);
    }

    /// <summary>Verifies the Profiler is retained as the initially inactive Game-well tab.</summary>
    [Fact]
    public void EditorBottomDock_ProfilerStartsCollapsed()
    {
        var view = EditorUI.BuildView(1280f, 720f);

        Assert.False(view.ProfilerContent.IsVisible);
        Assert.Equal("HierarchyButton", view.HierarchyButton.Name);
        Assert.Equal("FileSystemButton", view.FileSystemButton.Name);
        Assert.Equal("SceneButton", view.SceneButton.Name);
        Assert.Equal("GameButton", view.GameButton.Name);
        Assert.Equal("InspectorButton", view.InspectorButton.Name);
        Assert.Equal("ProfilerButton", view.ProfilerButton.Name);
        Assert.Contains(view.Profiler, Descendants(view.ProfilerContent));
        Assert.Contains(view.ProfilerPauseButton, Descendants(view.ProfilerContent));
    }

    /// <summary>Verifies unchanged draw-list builds reuse cached measurement and arrangement.</summary>
    [Fact]
    public void BuildDrawList_UnchangedLayout_DoesNotRerunMeasure()
    {
        var element = new MeasureProbe(100f, 50f);

        element.BuildDrawList();
        element.BuildDrawList();

        Assert.Equal(1, element.MeasureCount);
    }

    /// <summary>Verifies repeated invalid base layout traversal does not allocate after warmup.</summary>
    [Fact]
    public void BaseLayout_RepeatedMeasure_DoesNotAllocate()
    {
        var root = new UIElement();
        for (var index = 0; index < 20; index++)
            root.AddChild(new UIElement(10f, 10f));
        var available = new Vector2(200f, 100f);
        root.Measure(available);
        root.InvalidateMeasure();
        root.Measure(available);
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 100; index++)
        {
            root.InvalidateMeasure();
            root.Measure(available);
        }

        Assert.Equal(allocationStart, GC.GetAllocatedBytesForCurrentThread());
    }

    /// <summary>Verifies dock content can leave and return to the same grid instance.</summary>
    [Fact]
    public void Grid_RemoveAndAdd_ReparentsDockContentCleanly()
    {
        var grid = new Grid(Color.Black);
        grid.Rows.Add(GridLength.Star());
        grid.Columns.Add(GridLength.Star());
        var content = new Panel(Color.Red);
        grid.Add(content, 0, 0);

        Assert.True(grid.Remove(content));
        Assert.Null(content.Parent);
        Assert.Empty(grid.Children);

        grid.Add(content, 0, 0);
        grid.Measure(new Vector2(320f, 200f));
        grid.Arrange(Vector2.Zero, new Vector2(320f, 200f));
        Assert.Same(grid, content.Parent);
        Assert.Equal(320f, content.Width);
        Assert.Equal(200f, content.Height);
    }

    /// <summary>Enumerates all UI descendants beneath one root.</summary>
    /// <param name="root">Subtree root.</param>
    /// <returns>Descendants in depth-first order.</returns>
    private static IEnumerable<UIElement> Descendants(UIElement root)
    {
        foreach (var child in root.Children.OfType<UIElement>())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }

    /// <summary>Counts measure override calls for cache verification.</summary>
    private sealed class MeasureProbe : UIElement
    {
        /// <summary>Gets the number of measurement executions.</summary>
        public int MeasureCount { get; private set; }

        /// <summary>Creates a fixed-size measurement probe.</summary>
        /// <param name="width">Probe width.</param>
        /// <param name="height">Probe height.</param>
        public MeasureProbe(float width, float height) : base(width, height)
        {
        }

        /// <inheritdoc/>
        protected override Vector2 MeasureOverride(Vector2 availableSize)
        {
            MeasureCount++;
            return base.MeasureOverride(availableSize);
        }
    }
}
