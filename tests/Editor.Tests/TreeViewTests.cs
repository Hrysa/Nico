using Editor;
using Engine.Assets;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class TreeViewTests
{
    /// <summary>Verifies a parent scroll viewer owns the bar for overflowing tree rows.</summary>
    [Fact]
    public void Overflow_ParentScrollViewerShowsVerticalBar()
    {
        var roots = Enumerable.Range(0, 10)
            .Select(index => new Node { Name = $"Node {index}" }).ToArray();
        var tree = new TreeView(200f, 48f);
        tree.SetRoots(roots);
        var viewer = new ScrollViewer(200f, 48f) { Content = tree };

        viewer.BuildDrawList();

        Assert.True(viewer.VerticalScrollBar.IsVisible);
        Assert.Equal(roots.Length * tree.RowHeight, viewer.ExtentHeight);
        Assert.InRange(tree.Children.Count, 1, 3);
    }

    /// <summary>Verifies tree columns inherit renderer-backed measurement for fitting and alignment.</summary>
    [Fact]
    public void Columns_UseInheritedTextLayoutService()
    {
        var layout = new CountingTextLayoutService();
        var tree = new TreeView(200f, 100f)
        {
            TextLayoutOverride = layout,
            ShowColumnHeaders = true
        };
        tree.SetColumns([
            new TreeViewColumn("Name", 100f, node => node.Name),
            new TreeViewColumn("Type", 100f, _ => "Node", TreeViewColumnAlignment.Right)
        ]);
        tree.SetRoots([new Node { Name = "Root" }]);

        tree.BuildDrawList();

        Assert.True(layout.MeasureCallCount >= 4);
    }

    /// <summary>Verifies scrolling against a boundary preserves visible row containers.</summary>
    [Fact]
    public void BoundaryScroll_DoesNotRebuildRows()
    {
        var root = new Node { Name = "Root" };
        var tree = new TreeView(200f, 100f);
        tree.SetRoots([root]);
        var viewer = new ScrollViewer(200f, 100f) { Content = tree };
        viewer.BuildDrawList();
        var row = Assert.IsType<TreeViewItem>(tree.Children[0]);
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 100; index++)
            viewer.ScrollTo(0f, 100f);

        Assert.Same(row, tree.Children[0]);
        Assert.Equal(allocationStart, GC.GetAllocatedBytesForCurrentThread());
    }

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

    /// <summary>Builds categorized GLB nodes, skeletons, and animations with source parentage.</summary>
    [Fact]
    public void ImportedAssetTreeBuilder_GlbObjects_ReconstructsArmatureHierarchy()
    {
        var source = new FileSystemNode(Path.Combine(Path.GetTempPath(), "Character.glb"), false);
        AssetImportObject[] objects =
        [
            new("node/0", "Armature", "node"),
            new("node/1", "Hips", "node", "node/0"),
            new("skeleton/0", "Armature", "skeleton"),
            new("animation/0", "Walk", "animation")
        ];

        ImportedAssetTreeBuilder.AddObjects(source, objects);

        Assert.Equal(new[] { "Nodes", "Skeletons", "Animations" },
            source.Children.Select(child => child.Name));
        var armature = Assert.IsType<ImportedAssetObjectNode>(
            source.Children[0].Children[0]);
        Assert.Equal("Armature", armature.Name);
        Assert.Equal("Hips", Assert.Single(armature.Children).Name);
        Assert.Equal("Armature", Assert.Single(source.Children[1].Children).Name);
        Assert.Equal("Walk", Assert.Single(source.Children[2].Children).Name);
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
        var viewer = new ScrollViewer(200f, 48f) { Content = tree };
        viewer.BuildDrawList();
        tree.Select(roots[0]);
        var focusedRow = Assert.IsType<TreeViewItem>(tree.Children[0]);

        for (var index = 0; index < 4; index++)
            focusedRow.InvokeKeyDown((int)InputKey.Down);
        viewer.BuildDrawList();

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
        var viewer = new ScrollViewer(200f, 48f) { Content = tree };
        viewer.BuildDrawList();
        var firstBefore = Assert.IsType<TreeViewItem>(tree.Children[0]).Item;

        viewer.ScrollTo(0f, 32f);
        viewer.BuildDrawList();

        var firstAfter = Assert.IsType<TreeViewItem>(tree.Children[0]).Item;
        Assert.NotSame(firstBefore, firstAfter);
    }

    /// <summary>Verifies scrolling rebinds retained visible row containers.</summary>
    [Fact]
    public void Scroll_LongTree_ReusesVisibleRowControls()
    {
        var roots = Enumerable.Range(0, 10).Select(index => new Node { Name = $"Node {index}" }).ToArray();
        var tree = new TreeView(200f, 48f);
        tree.SetRoots(roots);
        var viewer = new ScrollViewer(200f, 48f) { Content = tree };
        viewer.BuildDrawList();
        var firstRow = Assert.IsType<TreeViewItem>(tree.Children[0]);
        var secondRow = Assert.IsType<TreeViewItem>(tree.Children[1]);

        viewer.ScrollTo(0f, 32f);
        viewer.BuildDrawList();

        Assert.Same(firstRow, tree.Children[0]);
        Assert.Same(secondRow, tree.Children[1]);
        Assert.Same(roots[1], firstRow.Item);
        Assert.Same(roots[2], secondRow.Item);
    }

    /// <summary>Verifies scrolling a large tree retains a viewport-bounded visual pool.</summary>
    [Fact]
    public void Scroll_VeryLargeTree_KeepsVisibleChildrenBounded()
    {
        var roots = new Node[100_000];
        for (var index = 0; index < roots.Length; index++)
            roots[index] = new Node { Name = $"Node {index}" };
        var tree = new TreeView(200f, 240f);
        tree.SetRoots(roots);
        var viewer = new ScrollViewer(200f, 240f) { Content = tree };
        viewer.BuildDrawList();
        var childCount = tree.Children.Count;

        viewer.ScrollTo(0f, 300f * tree.RowHeight);
        viewer.BuildDrawList();

        Assert.InRange(childCount, 1, 10);
        Assert.Equal(childCount, tree.Children.Count);
        Assert.Same(roots[300], Assert.IsType<TreeViewItem>(tree.Children[0]).Item);
    }

    /// <summary>Verifies navigation uses the retained logical index after a large scroll.</summary>
    [Fact]
    public void ArrowDown_VeryLargeTree_UsesRetainedLogicalIndex()
    {
        var roots = new Node[100_000];
        for (var index = 0; index < roots.Length; index++)
            roots[index] = new Node { Name = $"Node {index}" };
        var tree = new TreeView(200f, 240f);
        tree.SetRoots(roots);
        var viewer = new ScrollViewer(200f, 240f) { Content = tree };
        viewer.BuildDrawList();
        tree.Select(roots[50_000]);

        tree.InvokeKeyDown((int)InputKey.Down);
        viewer.BuildDrawList();

        Assert.Same(roots[50_001], tree.SelectedItem);
        Assert.Contains(tree.Children, child =>
            ReferenceEquals(Assert.IsType<TreeViewItem>(child).Item, roots[50_001]));
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

    /// <summary>Verifies hovering a submenu item requests its child menu after the configured delay.</summary>
    [Fact]
    public void ContextMenu_HoverSubmenuItem_RequestsChildMenu()
    {
        ContextMenuItem? hoveredItem = null;
        var menu = new ContextMenu(160f);
        menu.AddSubmenu("Add", item => hoveredItem = item);
        var router = new UIEventRouter(menu, () => { });

        router.MovePointer(new(20f, 20f));

        Assert.Null(hoveredItem);
        menu.AdvanceTime(menu.SubmenuOpenDelay + 0.01d);

        Assert.NotNull(hoveredItem);
        Assert.Equal(2f, hoveredItem.Left);
        Assert.Equal(2f, hoveredItem.Top);
    }

    /// <summary>Verifies column ellipsis never cuts a supplementary grapheme in half.</summary>
    [Fact]
    public void TreeViewItem_ColumnEllipsis_PreservesSurrogatePairs()
    {
        var node = new Node { Name = "😀ABCDE" };
        var columns = new[]
        {
            new TreeViewColumn("Name", 95f, item => item.Name)
        };
        var row = new TreeViewItem(95f, 24f, node, 0, false, columns: columns)
        {
            TextLayoutOverride = new CountingTextLayoutService()
        };

        var text = Assert.Single(row.BuildDrawList().Commands,
            command => command.Type == UIDrawCommandType.Text).Text!;

        for (var index = 0; index < text.Length; index++)
        {
            if (!char.IsSurrogate(text[index]))
                continue;
            Assert.True(index + 1 < text.Length && char.IsSurrogatePair(text, index));
            index++;
        }
    }

    /// <summary>Records calls while providing deterministic text metrics.</summary>
    private sealed class CountingTextLayoutService : ITextLayoutService
    {
        /// <summary>Gets the number of measurement requests.</summary>
        public int MeasureCallCount { get; private set; }

        /// <inheritdoc/>
        public float MeasureWidth(ReadOnlySpan<char> text, float fontSize)
        {
            MeasureCallCount++;
            return text.Length * fontSize;
        }

        /// <inheritdoc/>
        public int HitTestCaret(
            ReadOnlySpan<char> text,
            float fontSize,
            float horizontalPosition)
        {
            return Math.Clamp((int)(horizontalPosition / fontSize), 0, text.Length);
        }
    }
}
