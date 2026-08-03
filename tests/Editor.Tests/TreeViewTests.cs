using Editor;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class TreeViewTests
{
    /// <summary>Verifies that nested UI bounds are absolute for drawing and hit testing.</summary>
    [Fact]
    public void NestedElement_UsesAbsoluteBounds()
    {
        var root = new Canvas { Width = 500f, Height = 500f };
        var parent = new Canvas { Width = 300f, Height = 300f };
        var child = new Panel(Color.Red, 30f, 40f);
        root.Add(parent, new(100f, 200f));
        parent.Add(child, new(10f, 20f));
        root.BuildDrawList();

        Assert.Equal(110f, child.Left);
        Assert.Equal(140f, child.Right);
        Assert.Equal(220f, child.Top);
        Assert.Equal(260f, child.Bottom);
        Assert.True(child.ContainsPoint(new(120f, 230f)));
    }

    /// <summary>Verifies that labels emit pixel-font paint commands.</summary>
    [Fact]
    public void Label_WithText_EmitsGlyphRectangles()
    {
        var label = new Label("Scene", 100f, 20f);

        var drawList = label.BuildDrawList();

        Assert.NotEmpty(drawList.Commands);
    }

    /// <summary>Verifies expanded roots expose children and can be collapsed.</summary>
    [Fact]
    public void Toggle_ExpandedRoot_CollapsesChildren()
    {
        var root = new Node { Name = "Scene" };
        root.AddChild(new Node { Name = "Cube" });
        var tree = new TreeView(200f, 200f);
        tree.SetRoots([root]);
        Assert.Equal(2, tree.Children.Count);

        tree.Toggle(root);

        Assert.Single(tree.Children);
    }

    /// <summary>Verifies an empty container still exposes and toggles its disclosure state.</summary>
    [Fact]
    public void Toggle_EmptyContainer_ChangesExpandedState()
    {
        var directory = new FileSystemNode(Path.GetTempPath(), isDirectory: true);
        var tree = new TreeView(200f, 200f);
        tree.SetRoots([directory]);
        Assert.Contains(directory, tree.ExpandedItems);

        tree.Toggle(directory);

        Assert.DoesNotContain(directory, tree.ExpandedItems);
        var row = Assert.IsType<TreeViewItem>(Assert.Single(tree.Children));
        Assert.StartsWith("+", row.Label);
    }

    /// <summary>Verifies clicking a row updates tree selection.</summary>
    [Fact]
    public void Click_Row_SelectsRepresentedNode()
    {
        var root = new Node { Name = "Scene" };
        var tree = new TreeView(200f, 200f);
        tree.SetRoots([root]);
        var router = new UIEventRouter(tree, () => { });
        router.MovePointer(new(10f, 10f));
        router.Press();

        router.Release(invokeClick: true);

        Assert.Same(root, tree.SelectedItem);
    }

    /// <summary>Verifies wheel scrolling replaces the first visible row.</summary>
    [Fact]
    public void Scroll_LongTree_AdvancesVisibleRows()
    {
        var roots = Enumerable.Range(0, 10).Select(index => new Node { Name = $"Node {index}" }).ToArray();
        var tree = new TreeView(200f, 48f);
        tree.SetRoots(roots);
        var firstBefore = Assert.IsType<TreeViewItem>(tree.Children[0]).Item;

        tree.InvokeScroll(-1f);

        var firstAfter = Assert.IsType<TreeViewItem>(tree.Children[0]).Item;
        Assert.NotSame(firstBefore, firstAfter);
    }

    /// <summary>Verifies refreshing after insertion exposes a newly added child.</summary>
    [Fact]
    public void Refresh_AfterChildAdded_ShowsNewRow()
    {
        var root = new Node { Name = "Scene" };
        var tree = new TreeView(200f, 200f);
        tree.SetRoots([root]);
        root.AddChild(new Node { Name = "New Object" });

        tree.Expand(root);

        Assert.Equal(2, tree.Children.Count);
    }

    /// <summary>Verifies context-menu items invoke their configured actions.</summary>
    [Fact]
    public void ContextMenu_ClickItem_InvokesAction()
    {
        var invoked = false;
        var menu = new ContextMenu(160f);
        menu.AddItem("Add Cube", () => invoked = true);
        var router = new UIEventRouter(menu, () => { });
        router.MovePointer(new(20f, 20f));
        router.Press();

        router.Release(invokeClick: true);

        Assert.True(invoked);
    }

    /// <summary>Verifies hovering a submenu item requests its child menu.</summary>
    [Fact]
    public void ContextMenu_HoverSubmenuItem_RequestsChildMenu()
    {
        ContextMenuItem? hoveredItem = null;
        var menu = new ContextMenu(160f);
        menu.AddSubmenu("Add", item => hoveredItem = item);
        var router = new UIEventRouter(menu, () => { });

        router.MovePointer(new(20f, 20f));

        Assert.NotNull(hoveredItem);
        Assert.Equal(2f, hoveredItem.Left);
        Assert.Equal(2f, hoveredItem.Top);
    }
}
