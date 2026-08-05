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

    /// <summary>Verifies optional columns align values while preserving hierarchy indentation.</summary>
    [Fact]
    public void Columns_NestedRows_AlignValuesAndIndentHierarchy()
    {
        var root = new Node { Name = "Root" };
        var child = new Node { Name = "Child" };
        root.AddChild(child);
        var tree = new TreeView(300f, 200f) { ShowColumnHeaders = true };
        tree.SetColumns(
        [
            new TreeViewColumn("Name", 0f, node => node.Name),
            new TreeViewColumn("Children", 80f,
                node => node.Children.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TreeViewColumnAlignment.Right)
        ]);
        tree.SetRoots([root]);

        var commands = tree.BuildDrawList().Commands;
        var rootText = Assert.Single(commands, command => command.Text == "- Root");
        var childText = Assert.Single(commands, command => command.Text == "  Child");

        Assert.Contains(commands, command => command.Text == "Name");
        Assert.Contains(commands, command => command.Text == "Children");
        Assert.True(childText.Left > rootText.Left);
        var rootCount = Assert.Single(commands, command => command.Text == "1");
        var childCount = Assert.Single(commands, command => command.Text == "0");
        Assert.Equal(
            rootCount.Left + Label.MeasureTextWidth(rootCount.Text, rootCount.FontPixelHeight),
            childCount.Left + Label.MeasureTextWidth(childCount.Text, childCount.FontPixelHeight),
            precision: 3);
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
        Assert.StartsWith("+", Assert.IsType<Label>(row.Content).Text);
    }

    /// <summary>Verifies an imported resource makes its physical source expandable.</summary>
    [Fact]
    public void FileNode_ImportedSubAsset_AppearsAsExpandableChild()
    {
        var source = new FileSystemNode(Path.Combine(Path.GetTempPath(), "Character.glb"), false);
        var reference = new AssetReference(AssetId.New(), "mesh/Body/0");
        var mesh = new ImportedSubAssetNode(source.FullPath, reference,
            "nico/static-mesh", "Body [Mesh]");
        source.AddChild(mesh);
        var tree = new TreeView(200f, 200f);

        tree.SetRoots([source]);

        Assert.True(source.CanHaveChildren);
        Assert.Equal(2, tree.Children.Count);
        Assert.Same(mesh, Assert.IsType<TreeViewItem>(tree.Children[1]).Item);
        Assert.Equal(reference, mesh.Reference);
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

    /// <summary>Verifies selection changes retain visible rows instead of allocating replacements.</summary>
    [Fact]
    public void Select_DifferentRow_ReusesVisibleRowControls()
    {
        var first = new Node { Name = "First" };
        var second = new Node { Name = "Second" };
        var tree = new TreeView(200f, 200f);
        tree.SetRoots([first, second]);
        var firstRow = Assert.IsType<TreeViewItem>(tree.Children[0]);
        var secondRow = Assert.IsType<TreeViewItem>(tree.Children[1]);

        tree.Select(first);
        tree.Select(second);

        Assert.Same(firstRow, tree.Children[0]);
        Assert.Same(secondRow, tree.Children[1]);
        Assert.False(firstRow.IsSelected);
        Assert.True(secondRow.IsSelected);
    }

    /// <summary>Verifies up and down arrows move selection through visible rows.</summary>
    [Fact]
    public void ArrowUpDown_FocusedRow_MovesSelection()
    {
        var first = new Node { Name = "First" };
        var second = new Node { Name = "Second" };
        var third = new Node { Name = "Third" };
        var tree = new TreeView(200f, 200f);
        tree.SetRoots([first, second, third]);
        var router = new UIEventRouter(tree, () => { });
        router.MovePointer(new(10f, 10f));
        router.Press();
        router.Release(invokeClick: true);

        router.KeyDown((int)InputKey.Down);
        Assert.Same(second, tree.SelectedItem);

        router.KeyDown((int)InputKey.Up);
        Assert.Same(first, tree.SelectedItem);
    }

    /// <summary>Verifies right enters or expands children and left returns or collapses.</summary>
    [Fact]
    public void ArrowLeftRight_NestedSelection_NavigatesHierarchy()
    {
        var root = new Node { Name = "Root" };
        var child = new Node { Name = "Child" };
        var grandchild = new Node { Name = "Grandchild" };
        child.AddChild(grandchild);
        root.AddChild(child);
        var tree = new TreeView(200f, 200f);
        tree.SetRoots([root]);
        tree.Select(root);
        var row = Assert.IsType<TreeViewItem>(tree.Children[0]);

        row.InvokeKeyDown((int)InputKey.Right);
        Assert.Same(child, tree.SelectedItem);

        row.InvokeKeyDown((int)InputKey.Right);
        Assert.Contains(child, tree.ExpandedItems);
        row.InvokeKeyDown((int)InputKey.Right);
        Assert.Same(grandchild, tree.SelectedItem);

        row.InvokeKeyDown((int)InputKey.Left);
        Assert.Same(child, tree.SelectedItem);
        row.InvokeKeyDown((int)InputKey.Left);
        Assert.DoesNotContain(child, tree.ExpandedItems);
        row.InvokeKeyDown((int)InputKey.Left);
        Assert.Same(root, tree.SelectedItem);
    }

    /// <summary>Verifies keyboard selection scrolls a destination row into view.</summary>
    [Fact]
    public void ArrowDown_SmallViewport_ScrollsSelectionIntoView()
    {
        var roots = Enumerable.Range(0, 10)
            .Select(index => new Node { Name = $"Node {index}" }).ToArray();
        var tree = new TreeView(200f, 48f);
        tree.SetRoots(roots);
        tree.Select(roots[0]);
        var focusedRow = Assert.IsType<TreeViewItem>(tree.Children[0]);

        for (var index = 0; index < 4; index++)
            focusedRow.InvokeKeyDown((int)InputKey.Down);

        Assert.Same(roots[4], tree.SelectedItem);
        Assert.Contains(tree.Children.OfType<TreeViewItem>(), row => row.Item == roots[4]);
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
