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
        var panels = Descendants(view.Root).OfType<ToolPanel>().ToDictionary(panel => panel.Name);
        var hierarchyHeight = panels["Hierarchy"].Height;
        var fileSystemHeight = panels["FileSystem"].Height;

        view.Root.Measure(new Vector2(1280f, 920f));
        view.Root.Arrange(Vector2.Zero, new Vector2(1280f, 920f));

        Assert.True(panels["Hierarchy"].Height > hierarchyHeight);
        Assert.True(panels["FileSystem"].Height > fileSystemHeight);
        Assert.Equal(panels["Hierarchy"].Content.Height,
            view.HierarchyTree.Height);
        Assert.Equal(panels["FileSystem"].Content.Height,
            view.FileSystemTree.Height);
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

    /// <summary>Verifies unchanged draw-list builds reuse cached measurement and arrangement.</summary>
    [Fact]
    public void BuildDrawList_UnchangedLayout_DoesNotRerunMeasure()
    {
        var element = new MeasureProbe(100f, 50f);

        element.BuildDrawList();
        element.BuildDrawList();

        Assert.Equal(1, element.MeasureCount);
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
