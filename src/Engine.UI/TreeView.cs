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
    private readonly List<TreeViewColumn> _columns = [];
    private Node? _selectedItem;
    private int _scrollRow;
    private readonly UITheme _theme;
    private Vector2 _arrangedSize;
    private float _rowHeight;
    private Func<Node, string>? _itemText;
    private bool _showColumnHeaders;
    private float _columnHeaderHeight = 20f;

    /// <summary>Gets or sets the height of one hierarchy row.</summary>
    public float RowHeight
    {
        get => _rowHeight;
        set
        {
            if (_rowHeight == value)
                return;
            _rowHeight = value;
            RebuildRows();
        }
    }

    /// <summary>Gets or sets an optional formatter for each node's visible row text.</summary>
    public Func<Node, string>? ItemText
    {
        get => _itemText;
        set
        {
            if (ReferenceEquals(_itemText, value))
                return;
            _itemText = value;
            RebuildRows();
        }
    }

    /// <summary>Gets the configured data columns.</summary>
    public IReadOnlyList<TreeViewColumn> Columns => _columns;

    /// <summary>Gets or sets whether configured column headers are visible.</summary>
    public bool ShowColumnHeaders
    {
        get => _showColumnHeaders;
        set
        {
            if (_showColumnHeaders == value)
                return;
            _showColumnHeaders = value;
            RebuildRows();
        }
    }

    /// <summary>Gets or sets the column-header height.</summary>
    public float ColumnHeaderHeight
    {
        get => _columnHeaderHeight;
        set
        {
            value = MathF.Max(0f, value);
            if (_columnHeaderHeight == value)
                return;
            _columnHeaderHeight = value;
            RebuildRows();
        }
    }

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

    /// <summary>Replaces the optional aligned data columns.</summary>
    /// <param name="columns">Columns to display, with zero-width columns sharing flexible space.</param>
    public void SetColumns(IEnumerable<TreeViewColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        _columns.Clear();
        _columns.AddRange(columns);
        RebuildRows();
    }

    /// <summary>Selects a node, or clears selection.</summary>
    /// <param name="item">Node to select.</param>
    public void Select(Node? item)
    {
        if (ReferenceEquals(item, _selectedItem))
            return;
        _selectedItem = item;
        UpdateSelectionRows();
        SelectionChanged?.Invoke(item);
    }

    /// <summary>Updates selection styling without recreating the visible row controls.</summary>
    private void UpdateSelectionRows()
    {
        foreach (var child in Children)
        {
            if (child is TreeViewItem row)
                row.IsSelected = ReferenceEquals(row.Item, _selectedItem);
        }
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
        var visibleCount = GetVisibleRowCount(roundUp: false);
        var maximum = Math.Max(0, rows.Count - visibleCount);
        _scrollRow = Math.Clamp(_scrollRow - Math.Sign(offset) * 3, 0, maximum);
        RebuildRows();
    }

    /// <summary>Recreates only the currently visible row elements.</summary>
    private void RebuildRows()
    {
        ClearChildren();
        var rows = Flatten();
        var visibleCount = GetVisibleRowCount(roundUp: true);
        foreach (var (item, depth) in rows.Skip(_scrollRow).Take(visibleCount))
        {
            var row = new TreeViewItem(
                Width,
                RowHeight,
                item,
                depth,
                _expanded.Contains(item),
                _theme,
                _itemText?.Invoke(item),
                _columns)
            {
                IsSelected = ReferenceEquals(item, _selectedItem)
            };
            row.Click += () => Select(item);
            row.DoubleClick += () =>
            {
                Toggle(item);
                ItemActivated?.Invoke(item);
            };
            row.KeyDown += HandleKeyDown;
            row.Scroll += ScrollRows;
            AddChild(row);
        }
        if (Width > 0f && Height > 0f)
            ArrangeRows(new Vector2(ContentWidth, ContentHeight));
    }

    /// <inheritdoc/>
    protected override void OnKeyDown(int keyCode)
    {
        HandleKeyDown(keyCode);
        base.OnKeyDown(keyCode);
    }

    /// <summary>Handles keyboard selection and expansion for a focused tree or row.</summary>
    /// <param name="keyCode">Engine input key code.</param>
    private void HandleKeyDown(int keyCode)
    {
        var key = (InputKey)keyCode;
        var rows = Flatten();
        if (rows.Count == 0)
            return;

        var selectedIndex = rows.FindIndex(row => ReferenceEquals(row.Item, _selectedItem));
        switch (key)
        {
            case InputKey.Up:
                MoveSelection(rows, selectedIndex < 0 ? rows.Count - 1 : selectedIndex - 1);
                break;
            case InputKey.Down:
                MoveSelection(rows, selectedIndex < 0 ? 0 : selectedIndex + 1);
                break;
            case InputKey.Home:
                MoveSelection(rows, 0);
                break;
            case InputKey.End:
                MoveSelection(rows, rows.Count - 1);
                break;
            case InputKey.Right:
                NavigateRight(rows, selectedIndex);
                break;
            case InputKey.Left:
                NavigateLeft(rows, selectedIndex);
                break;
        }
    }

    /// <summary>Moves selection to a visible row and scrolls it into view.</summary>
    /// <param name="rows">Current expanded rows.</param>
    /// <param name="index">Requested row index.</param>
    private void MoveSelection(List<(Node Item, int Depth)> rows, int index)
    {
        index = Math.Clamp(index, 0, rows.Count - 1);
        Select(rows[index].Item);
        EnsureRowVisible(index, rows.Count);
    }

    /// <summary>Expands the selected node or enters its first child.</summary>
    /// <param name="rows">Current expanded rows.</param>
    /// <param name="selectedIndex">Selected visible-row index.</param>
    private void NavigateRight(List<(Node Item, int Depth)> rows, int selectedIndex)
    {
        if (selectedIndex < 0)
        {
            MoveSelection(rows, 0);
            return;
        }

        var selected = rows[selectedIndex].Item;
        if (!selected.CanHaveChildren)
            return;
        if (!_expanded.Contains(selected))
        {
            _expanded.Add(selected);
            RebuildRows();
            return;
        }
        if (selected.Children.Count == 0)
            return;
        var expandedRows = Flatten();
        var childIndex = expandedRows.FindIndex(row => ReferenceEquals(row.Item, selected.Children[0]));
        if (childIndex >= 0)
            MoveSelection(expandedRows, childIndex);
    }

    /// <summary>Collapses the selected node or moves selection to its parent.</summary>
    /// <param name="rows">Current expanded rows.</param>
    /// <param name="selectedIndex">Selected visible-row index.</param>
    private void NavigateLeft(List<(Node Item, int Depth)> rows, int selectedIndex)
    {
        if (selectedIndex < 0)
        {
            MoveSelection(rows, 0);
            return;
        }

        var selected = rows[selectedIndex].Item;
        if (selected.CanHaveChildren && _expanded.Remove(selected))
        {
            RebuildRows();
            return;
        }
        if (selected.Parent is null)
            return;
        var parentIndex = rows.FindIndex(row => ReferenceEquals(row.Item, selected.Parent));
        if (parentIndex >= 0)
            MoveSelection(rows, parentIndex);
    }

    /// <summary>Adjusts the scroll window so the selected row remains visible.</summary>
    /// <param name="rowIndex">Selected row index.</param>
    /// <param name="rowCount">Total visible row count.</param>
    private void EnsureRowVisible(int rowIndex, int rowCount)
    {
        var visibleCount = GetVisibleRowCount(roundUp: false);
        var nextScroll = _scrollRow;
        if (rowIndex < nextScroll)
            nextScroll = rowIndex;
        else if (rowIndex >= nextScroll + visibleCount)
            nextScroll = rowIndex - visibleCount + 1;
        nextScroll = Math.Clamp(nextScroll, 0, Math.Max(0, rowCount - visibleCount));
        if (nextScroll == _scrollRow)
            return;
        _scrollRow = nextScroll;
        RebuildRows();
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
        var y = GetColumnHeaderHeight();
        foreach (var child in Children.OfType<UIElement>())
        {
            child.Measure(new Vector2(contentSize.X, RowHeight));
            child.Arrange(new Vector2(0f, y), new Vector2(contentSize.X, RowHeight));
            y += RowHeight;
        }
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        base.Paint(drawList);
        if (!_showColumnHeaders || _columns.Count == 0)
            return;

        var x = Left;
        foreach (var column in _columns)
        {
            var width = TreeViewColumnLayout.ResolveWidth(_columns, column, Width);
            var textWidth = Label.MeasureTextWidth(column.Header, _theme.CaptionFontSize);
            var textX = column.Alignment == TreeViewColumnAlignment.Right
                ? x + MathF.Max(4f, width - textWidth - 6f)
                : x + 6f;
            drawList.AddText(column.Header, textX,
                Top + MathF.Max(0f, (_columnHeaderHeight - _theme.CaptionFontSize) / 2f),
                _theme.CaptionFontSize, _theme.TextSecondary, BackgroundColor);
            x += width;
        }
    }

    /// <summary>Returns the vertical space reserved for visible column headers.</summary>
    /// <returns>Header height, or zero when headers are hidden.</returns>
    private float GetColumnHeaderHeight()
    {
        return _showColumnHeaders && _columns.Count > 0 ? _columnHeaderHeight : 0f;
    }

    /// <summary>Calculates the number of body rows fitting below optional headers.</summary>
    /// <param name="roundUp">Whether to include a partially visible final row.</param>
    /// <returns>At least one row.</returns>
    private int GetVisibleRowCount(bool roundUp)
    {
        var bodyHeight = MathF.Max(0f, Height - GetColumnHeaderHeight());
        var rowCount = roundUp
            ? (int)MathF.Ceiling(bodyHeight / RowHeight)
            : (int)MathF.Floor(bodyHeight / RowHeight);
        return Math.Max(1, rowCount);
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
