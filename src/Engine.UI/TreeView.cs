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

    /// <summary>Gets or sets the height of one hierarchy row.</summary>
    public float RowHeight { get; set; } = 24f;

    /// <summary>Gets the selected node.</summary>
    public Node? SelectedItem => _selectedItem;

    /// <summary>Occurs when selection changes.</summary>
    public event Action<Node?>? SelectionChanged;

    /// <summary>
    /// Creates a node tree view.
    /// </summary>
    /// <param name="x">Local X position.</param>
    /// <param name="y">Local Y position.</param>
    /// <param name="width">Tree width.</param>
    /// <param name="height">Tree height.</param>
    public TreeView(float x, float y, float width, float height)
        : base(x, y, width, height, Color.EditorPanel)
    {
        Scroll += ScrollRows;
    }

    /// <summary>Replaces the tree roots.</summary>
    /// <param name="roots">Root nodes to display.</param>
    public void SetRoots(IEnumerable<Node> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots.Clear();
        _roots.AddRange(roots);
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
        if (!item.HasChildren)
            return;
        if (!_expanded.Remove(item))
            _expanded.Add(item);
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
            var row = new TreeViewItem(Width, RowHeight, item, depth, _expanded.Contains(item))
            {
                Position = new Vector3(0f, Children.Count * RowHeight, 0f),
                IsSelected = ReferenceEquals(item, _selectedItem)
            };
            row.Click += () => Select(item);
            row.DoubleClick += () => Toggle(item);
            row.Scroll += ScrollRows;
            AddChild(row);
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
