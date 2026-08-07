using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Displays a selectable and vertically scrollable list of text items.
/// </summary>
public sealed class ListView : Panel
{
    private readonly List<string> _items = [];
    private readonly List<ListViewItem> _rows = [];
    private readonly UITheme _theme;
    private int _scrollIndex;
    private Vector2 _arrangedSize;
    private float _rowHeight;
    private readonly UISelectionModel _selection = new();
    private string _typeAhead = string.Empty;
    private double _typeAheadElapsed;

    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        UISemanticRole.List,
        Name,
        SelectedItem,
        IsEnabled,
        true,
        false,
        null,
        NumericValue: SelectedIndex,
        Minimum: _items.Count == 0 ? null : 0d,
        Maximum: _items.Count == 0 ? null : _items.Count - 1d);

    /// <summary>Gets or sets the height of one item row.</summary>
    public float RowHeight
    {
        get => _rowHeight;
        set
        {
            if (value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_rowHeight == value)
                return;
            _rowHeight = value;
            RebuildRows();
        }
    }

    /// <summary>Gets the selected item index, or -1 when selection is empty.</summary>
    public int SelectedIndex => _selection.PrimaryIndex;

    /// <summary>Gets selected logical indices in ascending order.</summary>
    public IReadOnlyList<int> SelectedIndices => _selection.SelectedIndices;

    /// <summary>Gets or sets single, multiple, or extended selection behavior.</summary>
    public UISelectionMode SelectionMode { get; set; } = UISelectionMode.Single;

    /// <summary>Gets the selected item text, or null when selection is empty.</summary>
    public string? SelectedItem => SelectedIndex >= 0 ? _items[SelectedIndex] : null;

    /// <summary>Occurs when list selection changes.</summary>
    public event Action<int, string?>? SelectionChanged;

    /// <summary>Occurs after the complete selected-index set changes.</summary>
    public event Action<UISelectionModel>? SelectionSetChanged;

    /// <summary>Occurs when an item is double-clicked.</summary>
    public event Action<int, string>? ItemActivated;

    /// <summary>
    /// Creates a selectable list.
    /// </summary>
    /// <param name="width">List width.</param>
    /// <param name="height">List height.</param>
    /// <param name="theme">Theme supplying list colors.</param>
    public ListView(float width, float height, UITheme? theme = null)
        : base((theme ?? UITheme.Dark).Surface, width, height)
    {
        _theme = theme ?? UITheme.Dark;
        RowHeight = _theme.ItemRowHeight;
        PaintBackground = false;
        Scroll += ScrollRows;
        Key += OnKey;
        RoutedTextInput += OnTextInput;
    }

    /// <summary>Replaces all list items and clears selection.</summary>
    /// <param name="items">Text items to display.</param>
    public void SetItems(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.Clear();
        _items.AddRange(items);
        _selection.Clear();
        _scrollIndex = 0;
        RebuildRows();
    }

    /// <summary>Selects an item by index.</summary>
    /// <param name="index">Item index, or -1 to clear selection.</param>
    public void Select(int index)
    {
        Select(index, UISelectionIntent.Replace);
    }

    /// <summary>Selects an item using a device-neutral replace, toggle, or range intent.</summary>
    /// <param name="index">Item index, or -1 to clear selection.</param>
    /// <param name="intent">Requested selection operation.</param>
    public void Select(int index, UISelectionIntent intent)
    {
        if (!_selection.Select(index, _items.Count, SelectionMode, intent))
            return;
        if (index >= 0)
            EnsureIndexVisible(index);
        RebuildRows();
        SelectionChanged?.Invoke(SelectedIndex, SelectedItem);
        SelectionSetChanged?.Invoke(_selection);
    }

    /// <summary>Maps pointer or keyboard modifiers into one selection operation.</summary>
    /// <param name="index">Logical item index.</param>
    /// <param name="modifiers">Held device-neutral modifiers.</param>
    private void SelectWithModifiers(int index, InputModifiers modifiers)
    {
        var toggle = (modifiers & (InputModifiers.Control | InputModifiers.Super)) != 0;
        var range = (modifiers & InputModifiers.Shift) != 0;
        Select(index, range
            ? toggle ? UISelectionIntent.AddRange : UISelectionIntent.Range
            : toggle || SelectionMode == UISelectionMode.Multiple
                ? UISelectionIntent.Toggle
                : UISelectionIntent.Replace);
    }

    /// <summary>Scrolls the visible list window.</summary>
    /// <param name="offset">Wheel offset.</param>
    private void ScrollRows(float offset)
    {
        var visibleCount = Math.Max(1, (int)MathF.Floor(Height / RowHeight));
        var maximum = Math.Max(0, _items.Count - visibleCount);
        var nextScrollIndex = Math.Clamp(
            _scrollIndex - Math.Sign(offset) * 3, 0, maximum);
        if (nextScrollIndex == _scrollIndex)
            return;
        _scrollIndex = nextScrollIndex;
        RebuildRows();
    }

    /// <summary>Reuses and rebinds the bounded visible row pool.</summary>
    private void RebuildRows()
    {
        var visibleCount = Math.Max(1, (int)MathF.Ceiling(Height / RowHeight));
        var end = Math.Min(_items.Count, _scrollIndex + visibleCount);
        EnsureRowCount(end - _scrollIndex);
        var rowIndex = 0;
        for (var index = _scrollIndex; index < end; index++)
        {
            _rows[rowIndex++].Bind(
                index, _items[index], _selection.IsSelected(index), Width, RowHeight);
        }
        if (Width > 0f && Height > 0f)
            ArrangeRows(new Vector2(ContentWidth, ContentHeight));
    }

    /// <summary>Resizes the retained row pool only when viewport capacity changes.</summary>
    /// <param name="requiredCount">Number of currently visible containers.</param>
    private void EnsureRowCount(int requiredCount)
    {
        while (_rows.Count < requiredCount)
        {
            var row = new ListViewItem(Width, RowHeight, string.Empty, _theme);
            row.BindOwner(SelectWithModifiers, ActivateItem, ScrollRows);
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

    /// <summary>Raises activation for one currently bound item.</summary>
    /// <param name="index">Logical item index.</param>
    private void ActivateItem(int index)
    {
        Select(index);
        ItemActivated?.Invoke(index, _items[index]);
    }

    /// <summary>Moves the retained viewport so one logical index is visible.</summary>
    /// <param name="index">Logical item index.</param>
    private void EnsureIndexVisible(int index)
    {
        var visibleCount = Math.Max(1, (int)MathF.Floor(Height / RowHeight));
        var next = _scrollIndex;
        if (index < next)
            next = index;
        else if (index >= next + visibleCount)
            next = index - visibleCount + 1;
        var maximum = Math.Max(0, _items.Count - visibleCount);
        _scrollIndex = Math.Clamp(next, 0, maximum);
    }

    /// <summary>Handles focused-row navigation with optional anchored range extension.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="keyEvent">Routed key transition.</param>
    private void OnKey(UIElement sender, UIKeyEventArgs keyEvent)
    {
        if (keyEvent.Kind != UIKeyEventKind.KeyDown || keyEvent.RoutePhase != UIRoutePhase.Bubble
            || _items.Count == 0)
            return;
        var index = keyEvent.Key switch
        {
            InputKey.Up => Math.Max(0, SelectedIndex < 0 ? 0 : SelectedIndex - 1),
            InputKey.Down => Math.Min(_items.Count - 1, SelectedIndex + 1),
            InputKey.Home => 0,
            InputKey.End => _items.Count - 1,
            _ => -1
        };
        if (index < 0)
            return;
        SelectWithModifiers(index, keyEvent.Modifiers);
        var visibleIndex = index - _scrollIndex;
        if ((uint)visibleIndex < (uint)_rows.Count)
            keyEvent.Focus(_rows[visibleIndex]);
        keyEvent.Handled = true;
    }

    /// <summary>Matches committed text against logical items using inherited culture.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="textEvent">Committed text input.</param>
    private void OnTextInput(UIElement sender, UITextInputEventArgs textEvent)
    {
        if (textEvent.Text.Length == 0 || _items.Count == 0)
            return;
        var repeated = _typeAhead.Length == 1 && textEvent.Text.Length == 1
            && Culture.CompareInfo.Compare(_typeAhead, textEvent.Text,
                System.Globalization.CompareOptions.IgnoreCase) == 0;
        _typeAhead = repeated ? textEvent.Text : _typeAhead + textEvent.Text;
        _typeAheadElapsed = 0d;
        var start = Math.Max(-1, SelectedIndex);
        for (var offset = 1; offset <= _items.Count; offset++)
        {
            var index = (start + offset) % _items.Count;
            if (!Culture.CompareInfo.IsPrefix(_items[index], _typeAhead,
                    System.Globalization.CompareOptions.IgnoreCase
                    | System.Globalization.CompareOptions.IgnoreNonSpace))
                continue;
            Select(index);
            var visibleIndex = index - _scrollIndex;
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

    /// <summary>Arranges current rows sequentially in the list viewport.</summary>
    /// <param name="contentSize">Available list content size.</param>
    private void ArrangeRows(Vector2 contentSize)
    {
        var y = 0f;
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
}

/// <summary>
/// Displays one selectable row inside a <see cref="ListView"/>.
/// </summary>
public sealed class ListViewItem : Button
{
    private readonly UITheme _theme;
    private readonly Label _label;
    private bool _isSelected;
    private int _itemIndex = -1;
    private Action<int, InputModifiers>? _selectItem;
    private Action<int>? _activateItem;
    private InputModifiers _selectionModifiers;

    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => base.GetSemanticInfo() with
    {
        Role = UISemanticRole.ListItem,
        Name = Text,
        Value = Text,
        Actions = UISemanticAction.Invoke | UISemanticAction.Select,
        IsSelected = IsSelected
    };

    /// <inheritdoc/>
    public override bool PerformSemanticAction(UISemanticAction action, double? value = null)
    {
        if (!IsEnabled)
            return false;
        if (action == UISemanticAction.Select)
            OnSelect();
        else if (action == UISemanticAction.Invoke)
            OnActivate();
        else
            return false;
        return true;
    }

    /// <summary>Gets the displayed item text.</summary>
    public string Text => _label.Text;

    /// <summary>Gets or sets whether this row is selected.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;
            _isSelected = value;
            NormalColor = value ? _theme.SurfacePressed : _theme.Surface;
            PaintNormalBackground = value;
        }
    }

    /// <summary>
    /// Creates a list row.
    /// </summary>
    /// <param name="width">Row width.</param>
    /// <param name="height">Row height.</param>
    /// <param name="text">Displayed item text.</param>
    /// <param name="theme">Theme supplying row colors.</param>
    public ListViewItem(float width, float height, string text, UITheme? theme = null)
        : base(width, height, theme ?? UITheme.Dark)
    {
        _theme = theme ?? UITheme.Dark;
        ForegroundColor = _theme.TextPrimary;
        _label = new Label(text)
        {
            FontSize = _theme.FontSize,
            ForegroundColor = _theme.TextPrimary,
            PaddingLeft = 0f,
            IsHitTestVisible = false
        };
        Content = _label;
        PaddingLeft = _theme.ItemRowPadding;
        NormalColor = _theme.Surface;
        HoverColor = _theme.SurfaceHover;
        PressedColor = _theme.SurfacePressed;
        PaintNormalBackground = false;
        CornerRadius = 0f;
        Click += OnSelect;
        DoubleClick += OnActivate;
        Pointer += CaptureSelectionModifiers;
    }

    /// <summary>Assigns stable owner callbacks once when the row container is created.</summary>
    /// <param name="selectItem">Selection callback.</param>
    /// <param name="activateItem">Double-click activation callback.</param>
    /// <param name="scrollRows">Wheel callback.</param>
    internal void BindOwner(
        Action<int, InputModifiers> selectItem,
        Action<int> activateItem,
        Action<float> scrollRows)
    {
        _selectItem = selectItem;
        _activateItem = activateItem;
        Scroll += scrollRows;
    }

    /// <summary>Rebinds this retained container to one logical list item.</summary>
    /// <param name="itemIndex">Logical item index.</param>
    /// <param name="text">Displayed item text.</param>
    /// <param name="isSelected">Whether the logical item is selected.</param>
    /// <param name="width">Current viewport row width.</param>
    /// <param name="height">Current row height.</param>
    internal void Bind(
        int itemIndex,
        string text,
        bool isSelected,
        float width,
        float height)
    {
        _itemIndex = itemIndex;
        if (Width != width)
            Width = width;
        if (Height != height)
            Height = height;
        _label.Text = text;
        IsSelected = isSelected;
    }

    /// <summary>Selects the logical item currently bound to this container.</summary>
    private void OnSelect()
    {
        if (_itemIndex >= 0)
            _selectItem?.Invoke(_itemIndex, _selectionModifiers);
        _selectionModifiers = InputModifiers.None;
    }

    /// <summary>Captures modifiers from the release that will invoke this row.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="pointerEvent">Routed pointer transition.</param>
    private void CaptureSelectionModifiers(UIElement sender, UIPointerEventArgs pointerEvent)
    {
        if (pointerEvent.RoutePhase == UIRoutePhase.Target
            && pointerEvent.Kind == UIPointerEventKind.Release)
            _selectionModifiers = pointerEvent.Modifiers;
    }

    /// <summary>Activates the logical item currently bound to this container.</summary>
    private void OnActivate()
    {
        if (_itemIndex >= 0)
            _activateItem?.Invoke(_itemIndex);
    }
}
