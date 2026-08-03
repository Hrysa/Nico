using System.Numerics;
using Engine.Core;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Displays a selectable, expandable, vertically scrollable tree of engine nodes.
/// </summary>
public sealed class TreeView : Panel
{
    private readonly List<Node> _roots = [];
    private readonly HashSet<Node> _expanded = [];
    private Node? _selectedItem;
    private int _scrollRow;
    private readonly UITheme _theme;
    private Vector2 _arrangedSize;

    /// <summary>Gets or sets the height of one hierarchy row.</summary>
    public float RowHeight { get; set; }

    /// <summary>Gets the selected node.</summary>
    public Node? SelectedItem => _selectedItem;

    /// <summary>Gets the nodes whose children are currently visible.</summary>
    public IReadOnlyCollection<Node> ExpandedItems => _expanded;

    /// <summary>Occurs when selection changes.</summary>
    public event Action<Node?>? SelectionChanged;

    /// <summary>Occurs when a row is double-clicked.</summary>
    public event Action<Node>? ItemActivated;

    /// <summary>
    /// Creates a node tree view.
    /// </summary>
    /// <param name="width">Tree width.</param>
    /// <param name="height">Tree height.</param>
    /// <param name="theme">Theme supplying tree colors and typography.</param>
    public TreeView(float width, float height, UITheme? theme = null)
        : base((theme ?? UITheme.Dark).Surface, width, height)
    {
        _theme = theme ?? UITheme.Dark;
        RowHeight = _theme.ItemRowHeight;
        ForegroundColor = _theme.TextPrimary;
        PaintBackground = false;
        Scroll += ScrollRows;
    }

    /// <summary>Replaces the tree roots.</summary>
    /// <param name="roots">Root nodes to display.</param>
    public void SetRoots(IEnumerable<Node> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots.Clear();
        _roots.AddRange(roots);
        _expanded.Clear();
        foreach (var root in _roots)
            _expanded.Add(root);
        _scrollRow = 0;
        RebuildRows();
    }

    /// <summary>Selects a node, or clears selection.</summary>
    /// <param name="item">Node to select.</param>
    public void Select(Node? item)
    {
        if (ReferenceEquals(item, _selectedItem))
            return;
        _selectedItem = item;
        RebuildRows();
        SelectionChanged?.Invoke(item);
    }

    /// <summary>Toggles one node's expanded state.</summary>
    /// <param name="item">Node to toggle.</param>
    public void Toggle(Node item)
    {
        if (!item.CanHaveChildren)
            return;
        if (!_expanded.Remove(item))
            _expanded.Add(item);
        RebuildRows();
    }

    /// <summary>Expands a node and refreshes the visible rows.</summary>
    /// <param name="item">Node to expand.</param>
    public void Expand(Node item)
    {
        _expanded.Add(item);
        RebuildRows();
    }

    /// <summary>Replaces the complete expanded-node set.</summary>
    /// <param name="items">Nodes whose children should be visible.</param>
    public void SetExpanded(IEnumerable<Node> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _expanded.Clear();
        _expanded.UnionWith(items);
        RebuildRows();
    }

    /// <summary>Refreshes rows after the bound node hierarchy changes.</summary>
    public void Refresh()
    {
        RebuildRows();
    }

    /// <summary>Scrolls by wheel rows.</summary>
    /// <param name="offset">Wheel offset.</param>
    private void ScrollRows(float offset)
    {
        var rows = Flatten();
        var visibleCount = Math.Max(1, (int)MathF.Floor(Height / RowHeight));
        var maximum = Math.Max(0, rows.Count - visibleCount);
        _scrollRow = Math.Clamp(_scrollRow - Math.Sign(offset) * 3, 0, maximum);
        RebuildRows();
    }

    /// <summary>Recreates only the currently visible row elements.</summary>
    private void RebuildRows()
    {
        ClearChildren();
        var rows = Flatten();
        var visibleCount = Math.Max(1, (int)MathF.Ceiling(Height / RowHeight));
        foreach (var (item, depth) in rows.Skip(_scrollRow).Take(visibleCount))
        {
            var row = new TreeViewItem(Width, RowHeight, item, depth, _expanded.Contains(item), _theme)
            {
                IsSelected = ReferenceEquals(item, _selectedItem)
            };
            row.Click += () => Select(item);
            row.DoubleClick += () =>
            {
                Toggle(item);
                ItemActivated?.Invoke(item);
            };
            row.Scroll += ScrollRows;
            AddChild(row);
        }
        if (Width > 0f && Height > 0f)
            ArrangeRows(new Vector2(ContentWidth, ContentHeight));
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        if (_arrangedSize != contentSize)
        {
            _arrangedSize = contentSize;
            RebuildRows();
        }
        ArrangeRows(contentSize);
    }

    /// <summary>Arranges current rows sequentially in the tree viewport.</summary>
    /// <param name="contentSize">Available tree content size.</param>
    private void ArrangeRows(Vector2 contentSize)
    {
        var y = 0f;
        foreach (var child in Children.OfType<UIElement>())
        {
            child.Measure(new Vector2(contentSize.X, RowHeight));
            child.Arrange(new Vector2(0f, y), new Vector2(contentSize.X, RowHeight));
            y += RowHeight;
        }
    }

    /// <summary>Flattens expanded nodes in display order.</summary>
    /// <returns>Node/depth rows.</returns>
    private List<(Node Item, int Depth)> Flatten()
    {
        var rows = new List<(Node, int)>();
        foreach (var root in _roots)
            AddVisible(root, 0, rows);
        return rows;
    }

    /// <summary>Adds one visible subtree to a flattened row list.</summary>
    /// <param name="item">Current node.</param>
    /// <param name="depth">Current depth.</param>
    /// <param name="rows">Destination rows.</param>
    private void AddVisible(Node item, int depth, List<(Node Item, int Depth)> rows)
    {
        rows.Add((item, depth));
        if (!_expanded.Contains(item))
            return;
        foreach (var child in item.Children)
            AddVisible(child, depth + 1, rows);
    }
}
