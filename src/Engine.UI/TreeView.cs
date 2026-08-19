using System.Numerics;
using Engine.Core;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Displays a selectable, expandable, vertically scrollable tree of engine nodes.
/// </summary>
public sealed class TreeView : Panel, IScrollViewportContent
{
    private readonly List<Node> _roots = [];
    private readonly HashSet<Node> _expanded = [];
    private readonly List<TreeViewColumn> _columns = [];
    private readonly List<(Node Item, int Depth)> _flattenedRows = [];
    private readonly Dictionary<Node, int> _flattenedIndices = [];
    private readonly List<TreeViewItem> _rows = [];
    private bool _flattenedRowsValid;
    private Node? _selectedItem;
    private float _viewportOffsetY;
    private readonly UITheme _theme;
    private Vector2 _arrangedSize;
    private Func<Node, string>? _itemText;
    private bool _showColumnHeaders;
    private float _columnHeaderHeight = 20f;
    private readonly Dictionary<Node, Node[]> _sortedChildren = [];
    private Node[]? _sortedRoots;
    private int _sortColumnIndex = -1;
    private TreeViewSortDirection _sortDirection;
    private int _resizingColumnIndex = -1;
    private int _pressedHeaderColumnIndex = -1;
    private float _resizePointerStart;
    private float _resizeWidthStart;
    private readonly HashSet<Node> _selectedItems = [];
    private Node? _selectionAnchor;
    private string _typeAhead = string.Empty;
    private double _typeAheadElapsed;
    private Func<Node, UIDragData?>? _itemDragData;
    private readonly Dictionary<Node, UIDragData?> _itemDragDataCache = [];

    /// <summary>Gets or sets the policy deciding whether a row exposes an inside drop zone.</summary>
    /// <remarks>When unset, <see cref="Node.CanHaveChildren"/> supplies the default policy.</remarks>
    public Func<Node, bool>? CanDropInsideItem { get; set; }

    /// <summary>Gets or sets the typed drag-data factory for each visible item row.</summary>
    public Func<Node, UIDragData?>? ItemDragData
    {
        get => _itemDragData;
        set
        {
            if (ReferenceEquals(_itemDragData, value))
                return;
            _itemDragData = value;
            _itemDragDataCache.Clear();
            RebuildRows();
        }
    }

    /// <summary>Gets or sets the effects allowed by draggable item rows.</summary>
    public UIDragEffect ItemDragEffects
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            RebuildRows();
        }
    } = UIDragEffect.Copy;

    /// <summary>Gets or sets whether item rows can be routed drop targets.</summary>
    public bool AllowItemDrop
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            RebuildRows();
        }
    }

    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        UISemanticRole.Tree,
        Name,
        SelectedItem?.Name,
        IsEnabled,
        true,
        false,
        null);

    /// <summary>Gets or sets the height of one hierarchy row.</summary>
    public float RowHeight
    {
        get;
        set
        {
            if (value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
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

    /// <summary>Gets all selected nodes.</summary>
    public IReadOnlyCollection<Node> SelectedItems => _selectedItems;

    /// <summary>Gets or sets single, multiple, or extended selection behavior.</summary>
    public UISelectionMode SelectionMode { get; set; } = UISelectionMode.Single;

    /// <summary>Gets the nodes whose children are currently visible.</summary>
    public IReadOnlyCollection<Node> ExpandedItems => _expanded;

    /// <summary>Gets the actively sorted column index, or -1.</summary>
    public int SortColumnIndex => _sortColumnIndex;

    /// <summary>Gets the active hierarchical sort direction.</summary>
    public TreeViewSortDirection SortDirection => _sortDirection;

    /// <summary>Occurs when selection changes.</summary>
    public event Action<Node?>? SelectionChanged;

    /// <summary>Occurs when a row is double-clicked.</summary>
    public event Action<Node>? ItemActivated;

    /// <summary>Occurs after a column receives a new explicit width.</summary>
    public event Action<int, float>? ColumnWidthChanged;

    /// <summary>Occurs after hierarchical display sorting changes.</summary>
    public event Action<int, TreeViewSortDirection>? SortChanged;

    /// <summary>Resolves a routed pointer position into an above, inside, or below tree drop.</summary>
    /// <param name="dragEvent">Drag event routed through this tree.</param>
    /// <returns>The semantic destination and insertion index.</returns>
    public TreeViewDropTarget ResolveDropTarget(UIDragEventArgs dragEvent)
    {
        ArgumentNullException.ThrowIfNull(dragEvent);
        if (dragEvent.Target is not TreeViewItem row)
        {
            var emptyIndicatorY = _rows.Count > 0
                ? MathF.Min(_rows[^1].Bottom, Bottom)
                : Top;
            dragEvent.DropIndicatorBounds = new UIClipRect(
                Left, emptyIndicatorY - 1f, Right, emptyIndicatorY + 1f);
            return new TreeViewDropTarget(null, TreeViewDropPosition.Inside, null, _roots.Count);
        }

        var localY = Math.Clamp(dragEvent.Position.Y - row.Top, 0f, row.Height);
        var position = localY < row.Height * 0.25f
            ? TreeViewDropPosition.Above
            : localY > row.Height * 0.75f
                ? TreeViewDropPosition.Below
                : TreeViewDropPosition.Inside;
        var canDropInside = CanDropInsideItem?.Invoke(row.Item) ?? row.Item.CanHaveChildren;
        if (position == TreeViewDropPosition.Inside && !canDropInside)
            position = localY < row.Height * 0.5f
                ? TreeViewDropPosition.Above
                : TreeViewDropPosition.Below;

        if (position == TreeViewDropPosition.Inside)
        {
            dragEvent.DropIndicatorBounds = new UIClipRect(row.Left, row.Top, row.Right, row.Bottom);
            return new TreeViewDropTarget(row.Item, position, row.Item, row.Item.Children.Count);
        }

        var siblings = row.Item.Parent?.Children ?? _roots;
        var rowIndex = IndexOfNode(siblings, row.Item);
        var insertionIndex = Math.Max(0, rowIndex + (position == TreeViewDropPosition.Below ? 1 : 0));
        var indicatorY = position == TreeViewDropPosition.Above ? row.Top : row.Bottom;
        dragEvent.DropIndicatorBounds = new UIClipRect(row.Left, indicatorY - 1f, row.Right, indicatorY + 1f);
        return new TreeViewDropTarget(row.Item, position, row.Item.Parent, insertionIndex);
    }

    /// <summary>Finds a node by identity in an ordered collection without interface enumeration.</summary>
    /// <param name="items">Collection to inspect.</param>
    /// <param name="item">Node to find.</param>
    /// <returns>Zero-based index, or -1 when absent.</returns>
    private static int IndexOfNode(IReadOnlyList<Node> items, Node item)
    {
        for (var index = 0; index < items.Count; index++)
        {
            if (ReferenceEquals(items[index], item))
                return index;
        }
        return -1;
    }

    /// <summary>
    /// Creates a node tree view.
    /// </summary>
    /// <param name="width">Tree width.</param>
    /// <param name="height">Tree height.</param>
    /// <param name="theme">Theme supplying tree colors and typography.</param>
    public TreeView(float width, float height, UITheme? theme = null)
        : base(null, width, height)
    {
        _theme = theme ?? UITheme.Dark;
        RowHeight = _theme.ItemRowHeight;
        ForegroundColor = _theme.TextPrimary;
        Pointer += OnPointer;
        RoutedTextInput += OnTextInput;
    }

    /// <summary>Replaces the tree roots.</summary>
    /// <param name="roots">Root nodes to display.</param>
    public void SetRoots(IEnumerable<Node> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        _roots.Clear();
        _roots.AddRange(roots);
        _itemDragDataCache.Clear();
        _expanded.Clear();
        _selectedItems.Clear();
        _selectedItem = null;
        _selectionAnchor = null;
        foreach (var root in _roots)
            _expanded.Add(root);
        _viewportOffsetY = 0f;
        RebuildSortCache();
        InvalidateFlattenedRows();
        RebuildRows();
    }

    /// <summary>Replaces the optional aligned data columns.</summary>
    /// <param name="columns">Columns to display, with zero-width columns sharing flexible space.</param>
    public void SetColumns(IEnumerable<TreeViewColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        _columns.Clear();
        _columns.AddRange(columns);
        if (_sortColumnIndex >= _columns.Count)
        {
            _sortColumnIndex = -1;
            _sortDirection = TreeViewSortDirection.None;
        }
        RebuildSortCache();
        ClearChildren();
        _rows.Clear();
        RebuildRows();
    }

    /// <summary>Selects a node, or clears selection.</summary>
    /// <param name="item">Node to select.</param>
    public void Select(Node? item)
    {
        Select(item, UISelectionIntent.Replace);
    }

    /// <summary>Selects a node with an explicit replace, toggle, or anchored range intent.</summary>
    /// <param name="item">Node to select, or null to clear.</param>
    /// <param name="intent">Requested selection operation.</param>
    public void Select(Node? item, UISelectionIntent intent)
    {
        if (item is null)
        {
            if (_selectedItems.Count == 0 && _selectedItem is null)
                return;
            _selectedItems.Clear();
            _selectedItem = null;
            _selectionAnchor = null;
            UpdateSelectionRows();
            SelectionChanged?.Invoke(null);
            return;
        }
        if (SelectionMode == UISelectionMode.Single)
            intent = UISelectionIntent.Replace;
        var changed = ApplySelection(item, intent);
        if (!changed)
            return;
        UpdateSelectionRows();
        SelectionChanged?.Invoke(_selectedItem);
    }

    /// <summary>Applies one node selection operation.</summary>
    /// <param name="item">Target node.</param>
    /// <param name="intent">Selection intent.</param>
    /// <returns>True when selected state changed.</returns>
    private bool ApplySelection(Node item, UISelectionIntent intent)
    {
        if (intent == UISelectionIntent.Toggle)
        {
            var changed = _selectedItems.Remove(item);
            if (!changed)
                _selectedItems.Add(item);
            _selectedItem = _selectedItems.Contains(item) ? item : FindLastSelectedVisible();
            _selectionAnchor = item;
            return true;
        }
        if (intent is UISelectionIntent.Range or UISelectionIntent.AddRange)
        {
            var rows = Flatten();
            var anchor = _selectionAnchor is not null
                && _flattenedIndices.TryGetValue(_selectionAnchor, out var anchorIndex)
                ? anchorIndex
                : _flattenedIndices.GetValueOrDefault(item, -1);
            var target = _flattenedIndices.GetValueOrDefault(item, -1);
            if (anchor < 0 || target < 0)
                intent = UISelectionIntent.Replace;
            else
            {
                if (intent == UISelectionIntent.Range)
                    _selectedItems.Clear();
                var minimum = Math.Min(anchor, target);
                var maximum = Math.Max(anchor, target);
                for (var index = minimum; index <= maximum; index++)
                    _selectedItems.Add(rows[index].Item);
                _selectedItem = item;
                return true;
            }
        }
        var alreadyOnly = _selectedItems.Count == 1 && _selectedItems.Contains(item)
            && ReferenceEquals(_selectedItem, item);
        _selectedItems.Clear();
        _selectedItems.Add(item);
        _selectedItem = item;
        _selectionAnchor = item;
        return !alreadyOnly;
    }

    /// <summary>Maps pointer modifiers into the configured selection behavior.</summary>
    /// <param name="item">Target node.</param>
    /// <param name="modifiers">Held device-neutral modifiers.</param>
    private void SelectWithModifiers(Node item, InputModifiers modifiers)
    {
        var toggle = (modifiers & (InputModifiers.Control | InputModifiers.Super)) != 0;
        var range = (modifiers & InputModifiers.Shift) != 0;
        Select(item, range
            ? toggle ? UISelectionIntent.AddRange : UISelectionIntent.Range
            : toggle || SelectionMode == UISelectionMode.Multiple
                ? UISelectionIntent.Toggle
                : UISelectionIntent.Replace);
    }

    /// <summary>Finds the last selected node in current display order.</summary>
    /// <returns>Visible selected node, or null.</returns>
    private Node? FindLastSelectedVisible()
    {
        var rows = Flatten();
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            if (_selectedItems.Contains(rows[index].Item))
                return rows[index].Item;
        }
        return null;
    }

    /// <summary>Updates selection styling without recreating the visible row controls.</summary>
    private void UpdateSelectionRows()
    {
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            var child = children[index];
            if (child is TreeViewItem row)
                row.IsSelected = _selectedItems.Contains(row.Item);
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
        InvalidateFlattenedRows();
        RebuildRows();
    }

    /// <summary>Expands a node and refreshes the visible rows.</summary>
    /// <param name="item">Node to expand.</param>
    public void Expand(Node item)
    {
        _expanded.Add(item);
        InvalidateFlattenedRows();
        RebuildRows();
    }

    /// <summary>Replaces the complete expanded-node set.</summary>
    /// <param name="items">Nodes whose children should be visible.</param>
    public void SetExpanded(IEnumerable<Node> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _expanded.Clear();
        _expanded.UnionWith(items);
        InvalidateFlattenedRows();
        RebuildRows();
    }

    /// <summary>Refreshes rows after the bound node hierarchy changes.</summary>
    public void Refresh()
    {
        RebuildSortCache();
        InvalidateFlattenedRows();
        RebuildRows();
    }

    /// <summary>Sets one column's explicit logical width.</summary>
    /// <param name="columnIndex">Configured column index.</param>
    /// <param name="width">Requested logical width.</param>
    public void ResizeColumn(int columnIndex, float width)
    {
        if ((uint)columnIndex >= (uint)_columns.Count)
            throw new ArgumentOutOfRangeException(nameof(columnIndex));
        var column = _columns[columnIndex];
        if (!column.CanResize || !column.Resize(width))
            return;
        RebuildRows();
        InvalidateVisual();
        ColumnWidthChanged?.Invoke(columnIndex, column.Width);
    }

    /// <summary>Sorts every displayed sibling group without mutating the scene hierarchy.</summary>
    /// <param name="columnIndex">Configured column index, or -1 to restore authored order.</param>
    /// <param name="direction">Requested display direction.</param>
    public void SortByColumn(int columnIndex, TreeViewSortDirection direction)
    {
        if (direction == TreeViewSortDirection.None)
            columnIndex = -1;
        else if ((uint)columnIndex >= (uint)_columns.Count)
            throw new ArgumentOutOfRangeException(nameof(columnIndex));
        if (_sortColumnIndex == columnIndex && _sortDirection == direction)
            return;
        _sortColumnIndex = columnIndex;
        _sortDirection = direction;
        RebuildSortCache();
        InvalidateFlattenedRows();
        RebuildRows();
        InvalidateVisual();
        SortChanged?.Invoke(columnIndex, direction);
    }

    /// <summary>Handles captured header-divider resizing and header sorting.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="pointerEvent">Routed pointer transition.</param>
    private void OnPointer(UIElement sender, UIPointerEventArgs pointerEvent)
    {
        if (pointerEvent.RoutePhase != UIRoutePhase.Target || !ShowColumnHeaders)
            return;
        if (pointerEvent.Kind == UIPointerEventKind.Press
            && pointerEvent.Button == InputPointerButton.Primary)
        {
            var divider = FindColumnDivider(pointerEvent.Position);
            if (divider >= 0 && _columns[divider].CanResize)
            {
                _resizingColumnIndex = divider;
                _resizePointerStart = pointerEvent.Position.X;
                _resizeWidthStart = TreeViewColumnLayout.ResolveWidth(
                    _columns, _columns[divider], Width);
                pointerEvent.CapturePointer();
                pointerEvent.Handled = true;
                return;
            }
            _pressedHeaderColumnIndex = FindHeaderColumn(pointerEvent.Position);
        }
        else if (pointerEvent.Kind == UIPointerEventKind.Move && _resizingColumnIndex >= 0)
        {
            ResizeColumn(_resizingColumnIndex,
                _resizeWidthStart + pointerEvent.Position.X - _resizePointerStart);
            pointerEvent.Handled = true;
        }
        else if (pointerEvent.Kind == UIPointerEventKind.Release)
        {
            if (_resizingColumnIndex >= 0)
            {
                _resizingColumnIndex = -1;
                pointerEvent.ReleasePointerCapture();
                pointerEvent.Handled = true;
                return;
            }
            var releasedColumn = FindHeaderColumn(pointerEvent.Position);
            if (releasedColumn >= 0 && releasedColumn == _pressedHeaderColumnIndex)
            {
                var nextDirection = _sortColumnIndex == releasedColumn
                    && _sortDirection == TreeViewSortDirection.Ascending
                    ? TreeViewSortDirection.Descending
                    : TreeViewSortDirection.Ascending;
                SortByColumn(releasedColumn, nextDirection);
                pointerEvent.Handled = true;
            }
            _pressedHeaderColumnIndex = -1;
        }
    }

    /// <summary>Matches committed text against visible hierarchy rows using inherited culture.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="textEvent">Committed text input.</param>
    private void OnTextInput(UIElement sender, UITextInputEventArgs textEvent)
    {
        var rows = Flatten();
        if (textEvent.Text.Length == 0 || rows.Count == 0)
            return;
        var repeated = _typeAhead.Length == 1 && textEvent.Text.Length == 1
            && Culture.CompareInfo.Compare(_typeAhead, textEvent.Text,
                System.Globalization.CompareOptions.IgnoreCase) == 0;
        _typeAhead = repeated ? textEvent.Text : _typeAhead + textEvent.Text;
        _typeAheadElapsed = 0d;
        var start = _selectedItem is not null
            ? _flattenedIndices.GetValueOrDefault(_selectedItem, -1)
            : -1;
        for (var offset = 1; offset <= rows.Count; offset++)
        {
            var index = (start + offset) % rows.Count;
            var item = rows[index].Item;
            var text = _itemText?.Invoke(item)
                ?? (string.IsNullOrWhiteSpace(item.Name) ? item.GetType().Name : item.Name);
            if (!Culture.CompareInfo.IsPrefix(text, _typeAhead,
                    System.Globalization.CompareOptions.IgnoreCase
                    | System.Globalization.CompareOptions.IgnoreNonSpace))
                continue;
            Select(item);
            EnsureRowVisible(index);
            var visibleIndex = index - GetFirstVisibleRowIndex();
            if ((uint)visibleIndex < (uint)_rows.Count)
                textEvent.Focus(_rows[visibleIndex]);
            textEvent.Handled = true;
            return;
        }
    }

    /// <inheritdoc/>
    protected override bool UpdateElement(double deltaTime)
    {
        if (_typeAhead.Length == 0 || deltaTime <= 0d)
            return false;
        _typeAheadElapsed += deltaTime;
        if (_typeAheadElapsed < 0.75d)
            return false;
        _typeAhead = string.Empty;
        _typeAheadElapsed = 0d;
        return false;
    }

    /// <inheritdoc/>
    protected override bool IsTimeUpdateActive => _typeAhead.Length > 0;

    /// <summary>Finds a resizable divider near a host-space pointer.</summary>
    /// <param name="position">Host-space pointer position.</param>
    /// <returns>Column ending at the divider, or -1.</returns>
    private int FindColumnDivider(Vector2 position)
    {
        if (position.Y < Top || position.Y > Top + GetColumnHeaderHeight())
            return -1;
        var x = Left;
        for (var index = 0; index < _columns.Count; index++)
        {
            x += TreeViewColumnLayout.ResolveWidth(_columns, _columns[index], Width);
            if (MathF.Abs(position.X - x) <= 4f)
                return index;
        }
        return -1;
    }

    /// <summary>Finds the header cell containing a host-space pointer.</summary>
    /// <param name="position">Host-space pointer position.</param>
    /// <returns>Column index, or -1.</returns>
    private int FindHeaderColumn(Vector2 position)
    {
        if (position.Y < Top || position.Y > Top + GetColumnHeaderHeight())
            return -1;
        var x = Left;
        for (var index = 0; index < _columns.Count; index++)
        {
            x += TreeViewColumnLayout.ResolveWidth(_columns, _columns[index], Width);
            if (position.X < x)
                return index;
        }
        return -1;
    }

    /// <summary>Reuses and rebinds the bounded visible row pool.</summary>
    private void RebuildRows()
    {
        var rows = Flatten();
        var first = GetFirstVisibleRowIndex();
        var visibleCount = GetVisibleRowCount(roundUp: true);
        var end = Math.Min(rows.Count, first + visibleCount);
        EnsureRowCount(end - first);
        var rowIndex = 0;
        for (var index = first; index < end; index++)
        {
            var (item, depth) = rows[index];
            _rows[rowIndex++].Bind(item, depth, _expanded.Contains(item),
                _itemText?.Invoke(item), _selectedItems.Contains(item), Width, RowHeight,
                GetItemDragData(item), ItemDragEffects, AllowItemDrop);
        }
        if (Width > 0f && Height > 0f)
            ArrangeRows(new Vector2(ContentWidth, ContentHeight));
    }

    /// <summary>Gets stable drag data for a logical item without allocating while rows recycle.</summary>
    /// <param name="item">Logical row item.</param>
    /// <returns>Cached typed drag data, or null when dragging is disabled.</returns>
    private UIDragData? GetItemDragData(Node item)
    {
        if (_itemDragData is null)
            return null;
        if (_itemDragDataCache.TryGetValue(item, out var cached))
            return cached;
        var created = _itemDragData(item);
        _itemDragDataCache.Add(item, created);
        return created;
    }

    /// <summary>Resizes the retained row pool only when viewport capacity changes.</summary>
    /// <param name="requiredCount">Number of currently visible containers.</param>
    private void EnsureRowCount(int requiredCount)
    {
        while (_rows.Count < requiredCount)
        {
            var row = new TreeViewItem(Width, RowHeight, new Node(), 0, false,
                _theme, string.Empty, _columns);
            row.BindOwner(SelectWithModifiers, ActivateItem, HandleKeyDown);
            _rows.Add(row);
            AddChild(row);
        }
        while (_rows.Count > requiredCount)
        {
            var lastIndex = _rows.Count - 1;
            var row = _rows[lastIndex];
            _rows.RemoveAt(lastIndex);
            RemoveChild(row);
        }
    }

    /// <summary>Toggles and raises activation for one currently bound node.</summary>
    /// <param name="item">Activated node.</param>
    private void ActivateItem(Node item)
    {
        Toggle(item);
        ItemActivated?.Invoke(item);
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

        var selectedIndex = _selectedItem is not null &&
            _flattenedIndices.TryGetValue(_selectedItem, out var cachedSelectedIndex)
                ? cachedSelectedIndex
                : -1;
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
        EnsureRowVisible(index);
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
            InvalidateFlattenedRows();
            RebuildRows();
            return;
        }
        if (selected.Children.Count == 0)
            return;
        var expandedRows = Flatten();
        var firstChild = GetFirstDisplayedChild(selected);
        var childIndex = firstChild is not null
            && _flattenedIndices.TryGetValue(firstChild, out var cachedChildIndex)
            ? cachedChildIndex
            : -1;
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
            InvalidateFlattenedRows();
            RebuildRows();
            return;
        }
        if (selected.Parent is null)
            return;
        var parentIndex = _flattenedIndices.TryGetValue(selected.Parent, out var cachedParentIndex)
            ? cachedParentIndex
            : -1;
        if (parentIndex >= 0)
            MoveSelection(rows, parentIndex);
    }

    /// <summary>Adjusts the scroll window so the selected row remains visible.</summary>
    /// <param name="rowIndex">Selected row index.</param>
    private void EnsureRowVisible(int rowIndex)
    {
        if (Parent is not ScrollViewer viewer)
            return;
        var bodyHeight = MathF.Max(0f, Height - GetColumnHeaderHeight());
        var rowTop = rowIndex * RowHeight;
        var rowBottom = rowTop + RowHeight;
        var nextOffset = _viewportOffsetY;
        if (rowTop < nextOffset)
            nextOffset = rowTop;
        else if (rowBottom > nextOffset + bodyHeight)
            nextOffset = rowBottom - bodyHeight;
        viewer.ScrollTo(viewer.HorizontalOffset, nextOffset);
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
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is not UIElement child)
                continue;
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
        for (var index = 0; index < _columns.Count; index++)
        {
            var column = _columns[index];
            var width = TreeViewColumnLayout.ResolveWidth(_columns, column, Width);
            var textWidth = TextLayout.MeasureWidth(
                column.Header.AsSpan(), _theme.CaptionFontSize,
                FlowDirection.ToTextFlowDirection());
            var textX = column.Alignment == TreeViewColumnAlignment.Right
                ? x + MathF.Max(4f, width - textWidth - 6f)
                : x + 6f;
            drawList.AddText(column.Header, textX,
                Top + MathF.Max(0f, (_columnHeaderHeight - _theme.CaptionFontSize) / 2f),
                _theme.CaptionFontSize, _theme.TextSecondary, BackgroundColor,
                FlowDirection.ToTextFlowDirection());
            if (_sortColumnIndex == index && _sortDirection != TreeViewSortDirection.None)
            {
                var marker = _sortDirection == TreeViewSortDirection.Ascending ? "▲" : "▼";
                drawList.AddText(marker, x + MathF.Max(4f, width - 16f),
                    Top + MathF.Max(0f, (_columnHeaderHeight - _theme.CaptionFontSize) / 2f),
                    _theme.CaptionFontSize, _theme.TextSecondary, BackgroundColor,
                    FlowDirection.ToTextFlowDirection());
            }
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
        var viewportHeight = _arrangedSize.Y > 0f ? _arrangedSize.Y : Height;
        var bodyHeight = MathF.Max(0f, viewportHeight - GetColumnHeaderHeight());
        var rowCount = roundUp
            ? (int)MathF.Ceiling(bodyHeight / RowHeight)
            : (int)MathF.Floor(bodyHeight / RowHeight);
        return Math.Max(1, rowCount);
    }

    /// <summary>Gets the first logical row intersecting the parent-owned viewport.</summary>
    /// <returns>Zero-based flattened row index.</returns>
    private int GetFirstVisibleRowIndex() => Math.Max(0, (int)(_viewportOffsetY / RowHeight));

    /// <inheritdoc/>
    Vector2 IScrollViewportContent.GetScrollExtent(Vector2 viewportSize)
    {
        var rows = Flatten();
        return new Vector2(viewportSize.X, GetColumnHeaderHeight() + rows.Count * RowHeight);
    }

    /// <inheritdoc/>
    void IScrollViewportContent.SetScrollViewport(Vector2 offset, Vector2 viewportSize)
    {
        var offsetY = MathF.Max(0f, offset.Y);
        if (_viewportOffsetY == offsetY && _arrangedSize == viewportSize)
            return;
        _viewportOffsetY = offsetY;
        _arrangedSize = viewportSize;
        RebuildRows();
        InvalidateArrange();
    }

    /// <summary>Flattens expanded nodes in display order.</summary>
    /// <returns>Node/depth rows.</returns>
    private List<(Node Item, int Depth)> Flatten()
    {
        if (_flattenedRowsValid)
            return _flattenedRows;
        _flattenedRows.Clear();
        _flattenedIndices.Clear();
        if (_sortedRoots is not null)
        {
            for (var index = 0; index < _sortedRoots.Length; index++)
                AddVisible(_sortedRoots[index], 0, _flattenedRows);
        }
        else
        {
            for (var index = 0; index < _roots.Count; index++)
                AddVisible(_roots[index], 0, _flattenedRows);
        }
        _flattenedRowsValid = true;
        return _flattenedRows;
    }

    /// <summary>Marks the cached expanded hierarchy for reconstruction on next access.</summary>
    private void InvalidateFlattenedRows()
    {
        _flattenedRowsValid = false;
    }

    /// <summary>Adds one visible subtree to a flattened row list.</summary>
    /// <param name="item">Current node.</param>
    /// <param name="depth">Current depth.</param>
    /// <param name="rows">Destination rows.</param>
    private void AddVisible(Node item, int depth, List<(Node Item, int Depth)> rows)
    {
        _flattenedIndices[item] = rows.Count;
        rows.Add((item, depth));
        if (!_expanded.Contains(item))
            return;
        if (_sortedChildren.TryGetValue(item, out var sortedChildren))
        {
            for (var index = 0; index < sortedChildren.Length; index++)
                AddVisible(sortedChildren[index], depth + 1, rows);
            return;
        }
        var children = item.Children;
        for (var index = 0; index < children.Count; index++)
            AddVisible(children[index], depth + 1, rows);
    }

    /// <summary>Rebuilds cached sibling arrays for the active display sort.</summary>
    private void RebuildSortCache()
    {
        _sortedChildren.Clear();
        _sortedRoots = null;
        if (_sortDirection == TreeViewSortDirection.None
            || (uint)_sortColumnIndex >= (uint)_columns.Count)
            return;
        var column = _columns[_sortColumnIndex];
        Comparison<Node> comparison = _sortDirection == TreeViewSortDirection.Ascending
            ? (left, right) => CompareNodes(column, left, right)
            : (left, right) => CompareNodes(column, right, left);
        _sortedRoots = _roots.ToArray();
        Array.Sort(_sortedRoots, comparison);
        for (var index = 0; index < _sortedRoots.Length; index++)
            CacheSortedChildren(_sortedRoots[index], comparison);
    }

    /// <summary>Caches one node's sorted children and all descendant sibling groups.</summary>
    /// <param name="item">Parent node.</param>
    /// <param name="comparison">Active node comparison.</param>
    private void CacheSortedChildren(Node item, Comparison<Node> comparison)
    {
        var children = item.Children;
        if (children.Count == 0)
            return;
        var sorted = new Node[children.Count];
        for (var index = 0; index < children.Count; index++)
            sorted[index] = children[index];
        Array.Sort(sorted, comparison);
        _sortedChildren[item] = sorted;
        for (var index = 0; index < sorted.Length; index++)
            CacheSortedChildren(sorted[index], comparison);
    }

    /// <summary>Compares two nodes through an explicit comparer or culture-aware cell text.</summary>
    /// <param name="column">Active sort column.</param>
    /// <param name="left">Left node.</param>
    /// <param name="right">Right node.</param>
    /// <returns>Negative, zero, or positive ordering result.</returns>
    private int CompareNodes(TreeViewColumn column, Node left, Node right)
    {
        if (column.SortComparison is not null)
            return column.SortComparison(left, right);
        return Culture.CompareInfo.Compare(
            column.Value(left), column.Value(right),
            System.Globalization.CompareOptions.IgnoreCase
            | System.Globalization.CompareOptions.IgnoreNonSpace);
    }

    /// <summary>Gets the first child in active display order.</summary>
    /// <param name="item">Parent node.</param>
    /// <returns>First displayed child, or null.</returns>
    private Node? GetFirstDisplayedChild(Node item)
    {
        if (_sortedChildren.TryGetValue(item, out var sorted))
            return sorted.Length == 0 ? null : sorted[0];
        return item.Children.Count == 0 ? null : item.Children[0];
    }
}
