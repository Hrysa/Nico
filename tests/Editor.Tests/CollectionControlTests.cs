using System.ComponentModel;
using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

public class CollectionControlTests
{
    /// <summary>Verifies sorted selection supports replace, toggle, anchored range, and trimming.</summary>
    [Fact]
    public void SelectionModel_ExtendedOperations_PreserveSortedUniqueIndices()
    {
        var selection = new UISelectionModel();

        selection.Select(2, 10, UISelectionMode.Extended, UISelectionIntent.Replace);
        selection.Select(5, 10, UISelectionMode.Extended, UISelectionIntent.Range);
        Assert.Equal([2, 3, 4, 5], selection.SelectedIndices);
        selection.Select(3, 10, UISelectionMode.Extended, UISelectionIntent.Toggle);
        Assert.Equal([2, 4, 5], selection.SelectedIndices);
        selection.Select(7, 10, UISelectionMode.Extended, UISelectionIntent.AddRange);
        Assert.Equal([2, 3, 4, 5, 6, 7], selection.SelectedIndices);

        Assert.True(selection.Trim(5));
        Assert.Equal([2, 3, 4], selection.SelectedIndices);
        Assert.Equal(4, selection.PrimaryIndex);
    }

    /// <summary>Verifies unchanged selection queries and range requests allocate no managed memory.</summary>
    [Fact]
    public void SelectionModel_SteadyState_IsAllocationFree()
    {
        var selection = new UISelectionModel();
        selection.Select(100, 1000, UISelectionMode.Extended, UISelectionIntent.Replace);
        selection.Select(500, 1000, UISelectionMode.Extended, UISelectionIntent.Range);
        var selected = false;

        var allocationStart = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1000; index++)
        {
            selected |= selection.IsSelected(250);
            selection.Select(500, 1000, UISelectionMode.Extended, UISelectionIntent.Range);
        }

        Assert.True(selected);
        Assert.Equal(allocationStart, GC.GetAllocatedBytesForCurrentThread());
    }

    /// <summary>Verifies generated containers inherit item data and release bindings when regenerated.</summary>
    [Fact]
    public void ItemsControl_Regeneration_DisposesContainerBindings()
    {
        var first = new ItemModel("First");
        var second = new ItemModel("Second");
        var items = new ItemsControl<ItemModel>();
        items.ItemTemplate = new UIDataTemplate<ItemModel>(modelItem =>
        {
            var label = new Label(string.Empty);
            _ = UIBinding.BindDataContext<ItemModel, string>(
                label, nameof(ItemModel.Text), model => model.Text, value => label.Text = value);
            return label;
        });
        items.SetItems([first]);
        var discarded = Assert.IsType<Label>(Assert.Single(items.Containers));
        Assert.Equal("First", discarded.Text);

        items.SetItems([second]);
        first.Text = "Detached";
        var current = Assert.IsType<Label>(Assert.Single(items.Containers));

        Assert.Equal("First", discarded.Text);
        Assert.Equal("Second", current.Text);
        Assert.Same(second, current.DataContext);
    }

    /// <summary>Verifies selector pointer modifiers produce range and toggle selection.</summary>
    [Fact]
    public void Selector_ExtendedPointerSelection_UsesModifiers()
    {
        var selector = new Selector<string>
        {
            Width = 200f,
            Height = 100f,
            SelectionMode = UISelectionMode.Extended
        };
        selector.SetItems(["Alpha", "Beta", "Gamma"]);
        selector.BuildDrawList();
        var router = new UIEventRouter(selector, () => { });

        Click(router, new Vector2(20f, 15f), InputModifiers.None);
        Click(router, new Vector2(20f, 75f), InputModifiers.Shift);
        Assert.Equal([0, 1, 2], selector.Selection.SelectedIndices);
        Click(router, new Vector2(20f, 45f), InputModifiers.Control);

        Assert.Equal([0, 2], selector.Selection.SelectedIndices);
    }

    /// <summary>Verifies selector type-ahead uses culture-aware prefixes and repeated-letter cycling.</summary>
    [Fact]
    public void Selector_TypeAhead_SelectsAndCyclesMatchingItems()
    {
        var selector = new Selector<string>
        {
            Width = 200f,
            Height = 100f
        };
        selector.SetItems(["Alpha", "Beta", "Blue"]);
        selector.BuildDrawList();
        var router = new UIEventRouter(selector, () => { });
        router.Focus(selector.Containers[0]);

        router.RouteText("b");
        Assert.Equal(1, selector.SelectedIndex);
        router.RouteText("b");

        Assert.Equal(2, selector.SelectedIndex);
    }

    /// <summary>Verifies virtualized ListView applies extended range and toggle selection.</summary>
    [Fact]
    public void ListView_ExtendedSelection_RetainsViewportBoundedRows()
    {
        var list = new ListView(200f, 100f)
        {
            SelectionMode = UISelectionMode.Extended
        };
        list.SetItems(Enumerable.Range(0, 100_000).Select(index => $"Item {index}"));
        list.BuildDrawList();
        var router = new UIEventRouter(list, () => { });

        Click(router, new Vector2(20f, 15f), InputModifiers.None);
        Click(router, new Vector2(20f, 75f), InputModifiers.Shift);
        Click(router, new Vector2(20f, 45f), InputModifiers.Control);

        Assert.Equal([0, 2], list.SelectedIndices);
        Assert.True(list.Children.Count <= (int)MathF.Ceiling(list.Height / list.RowHeight));
    }

    /// <summary>Verifies ListView type-ahead selects matching logical items and scrolls them into view.</summary>
    [Fact]
    public void ListView_TypeAhead_SelectsOffscreenLogicalItem()
    {
        var list = new ListView(200f, 60f);
        list.SetItems(["Alpha", "Beta", "Charlie", "Delta", "Echo"]);
        list.BuildDrawList();
        var router = new UIEventRouter(list, () => { });
        router.Focus(Assert.IsType<ListViewItem>(list.Children[0]));

        router.RouteText("e");

        Assert.Equal(4, list.SelectedIndex);
        Assert.Contains(list.Children, child => child is ListViewItem row && row.Text == "Echo");
    }

    /// <summary>Verifies column resizing clamps width and captured divider dragging updates it.</summary>
    [Fact]
    public void TreeView_ColumnDividerDrag_ResizesWithCapture()
    {
        var tree = new TreeView(300f, 120f) { ShowColumnHeaders = true };
        var first = new TreeViewColumn("Name", 100f, node => node.Name) { MinWidth = 60f };
        tree.SetColumns([first, new TreeViewColumn("Type", 200f, _ => "Node")]);
        tree.SetRoots([new Node { Name = "Root" }]);
        tree.BuildDrawList();
        var router = new UIEventRouter(tree, () => { });

        router.Press(PointerButton(new Vector2(100f, 10f), true, InputModifiers.None));
        Assert.Same(tree, router.CapturedElement);
        router.RoutePointerMove(new PointerMoveEvent(
            0, new Vector2(135f, 10f), new Vector2(35f, 0f), PointerDeviceKind.Mouse,
            InputModifiers.None, PointerButtons.Primary));
        router.Release(PointerButton(new Vector2(135f, 10f), false, InputModifiers.None), true);

        Assert.Equal(135f, first.Width);
        Assert.Null(router.CapturedElement);
        tree.ResizeColumn(0, 1f);
        Assert.Equal(60f, first.Width);
    }

    /// <summary>Verifies hierarchical sorting orders every sibling group without mutating scene children.</summary>
    [Fact]
    public void TreeView_SortByColumn_UsesCachedDisplayOrderOnly()
    {
        var parent = new Node { Name = "Parent" };
        var zulu = new Node { Name = "Zulu" };
        var alpha = new Node { Name = "Alpha" };
        parent.AddChild(zulu);
        parent.AddChild(alpha);
        var other = new Node { Name = "Other" };
        var authoredRoots = new[] { parent, other };
        var tree = new TreeView(240f, 180f) { ShowColumnHeaders = true };
        tree.SetColumns([new TreeViewColumn("Name", 240f, node => node.Name)]);
        tree.SetRoots(authoredRoots);

        tree.SortByColumn(0, TreeViewSortDirection.Ascending);
        tree.BuildDrawList();
        var displayed = tree.Children.OfType<TreeViewItem>().Select(row => row.Item.Name).ToArray();

        Assert.Equal(["Other", "Parent", "Alpha", "Zulu"], displayed);
        Assert.Same(zulu, parent.Children[0]);
        Assert.Same(alpha, parent.Children[1]);
        Assert.Same(parent, authoredRoots[0]);
    }

    /// <summary>Verifies TreeView extended pointer selection uses node identity across retained rows.</summary>
    [Fact]
    public void TreeView_ExtendedSelection_UsesRangesAndModifierToggle()
    {
        var roots = new[]
        {
            new Node { Name = "Alpha" },
            new Node { Name = "Beta" },
            new Node { Name = "Gamma" }
        };
        var tree = new TreeView(200f, 120f) { SelectionMode = UISelectionMode.Extended };
        tree.SetRoots(roots);
        tree.BuildDrawList();
        var router = new UIEventRouter(tree, () => { });

        Click(router, new Vector2(20f, 15f), InputModifiers.None);
        Click(router, new Vector2(20f, 75f), InputModifiers.Shift);
        Assert.Equal(3, tree.SelectedItems.Count);
        Click(router, new Vector2(20f, 45f), InputModifiers.Control);

        Assert.Equal(2, tree.SelectedItems.Count);
        Assert.Contains(roots[0], tree.SelectedItems);
        Assert.Contains(roots[2], tree.SelectedItems);
    }

    /// <summary>Verifies TreeView type-ahead selects an offscreen visible node and scrolls to it.</summary>
    [Fact]
    public void TreeView_TypeAhead_SelectsAndRevealsVisibleNode()
    {
        var roots = new[]
        {
            new Node { Name = "Alpha" },
            new Node { Name = "Beta" },
            new Node { Name = "Charlie" },
            new Node { Name = "Gamma" }
        };
        var tree = new TreeView(200f, 60f);
        tree.SetRoots(roots);
        tree.BuildDrawList();
        var router = new UIEventRouter(tree, () => { });
        router.Focus(Assert.IsType<TreeViewItem>(tree.Children[0]));

        router.RouteText("g");

        Assert.Same(roots[3], tree.SelectedItem);
        Assert.Contains(tree.Children, child => child is TreeViewItem row && row.Item == roots[3]);
    }

    /// <summary>Creates one routed primary-button transition.</summary>
    /// <param name="position">Host-space pointer position.</param>
    /// <param name="pressed">Whether the button is pressed.</param>
    /// <param name="modifiers">Held modifiers.</param>
    /// <returns>Device-neutral pointer-button event.</returns>
    private static PointerButtonEvent PointerButton(
        Vector2 position,
        bool pressed,
        InputModifiers modifiers) => new(
            0, position, InputPointerButton.Primary, pressed, 1,
            PointerDeviceKind.Mouse, modifiers,
            pressed ? PointerButtons.Primary : PointerButtons.None);

    /// <summary>Routes one complete primary click with modifiers.</summary>
    /// <param name="router">Input router.</param>
    /// <param name="position">Host-space pointer position.</param>
    /// <param name="modifiers">Held modifiers.</param>
    private static void Click(UIEventRouter router, Vector2 position, InputModifiers modifiers)
    {
        router.RoutePointerMove(new PointerMoveEvent(
            0, position, Vector2.Zero, PointerDeviceKind.Mouse, modifiers, PointerButtons.None));
        router.Press(PointerButton(position, true, modifiers));
        router.Release(PointerButton(position, false, modifiers), true);
    }

    /// <summary>Observable item used to verify container binding lifetimes.</summary>
    private sealed class ItemModel : INotifyPropertyChanged
    {
        private string _text;

        /// <summary>Creates an item.</summary>
        /// <param name="text">Initial text.</param>
        public ItemModel(string text) => _text = text;

        /// <summary>Gets or sets observable display text.</summary>
        public string Text
        {
            get => _text;
            set
            {
                if (_text == value)
                    return;
                _text = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
        }

        /// <inheritdoc/>
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
