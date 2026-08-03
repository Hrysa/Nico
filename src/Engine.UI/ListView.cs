using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Displays a selectable and vertically scrollable list of text items.
/// </summary>
public sealed class ListView : Panel
{
    private readonly List<string> _items = [];
    private readonly UITheme _theme;
    private int _scrollIndex;
    private Vector2 _arrangedSize;
    private float _rowHeight;

    /// <summary>Gets or sets the height of one item row.</summary>
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

    /// <summary>Gets the selected item index, or -1 when selection is empty.</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>Gets the selected item text, or null when selection is empty.</summary>
    public string? SelectedItem => SelectedIndex >= 0 ? _items[SelectedIndex] : null;

    /// <summary>Occurs when list selection changes.</summary>
    public event Action<int, string?>? SelectionChanged;

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
    }

    /// <summary>Replaces all list items and clears selection.</summary>
    /// <param name="items">Text items to display.</param>
    public void SetItems(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.Clear();
        _items.AddRange(items);
        SelectedIndex = -1;
        _scrollIndex = 0;
        RebuildRows();
    }

    /// <summary>Selects an item by index.</summary>
    /// <param name="index">Item index, or -1 to clear selection.</param>
    public void Select(int index)
    {
        if (index < -1 || index >= _items.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (SelectedIndex == index)
            return;
        SelectedIndex = index;
        RebuildRows();
        SelectionChanged?.Invoke(index, SelectedItem);
    }

    /// <summary>Scrolls the visible list window.</summary>
    /// <param name="offset">Wheel offset.</param>
    private void ScrollRows(float offset)
    {
        var visibleCount = Math.Max(1, (int)MathF.Floor(Height / RowHeight));
        var maximum = Math.Max(0, _items.Count - visibleCount);
        _scrollIndex = Math.Clamp(_scrollIndex - Math.Sign(offset) * 3, 0, maximum);
        RebuildRows();
    }

    /// <summary>Rebuilds visible row elements.</summary>
    private void RebuildRows()
    {
        ClearChildren();
        var visibleCount = Math.Max(1, (int)MathF.Ceiling(Height / RowHeight));
        foreach (var (item, index) in _items.Select((item, index) => (item, index))
                     .Skip(_scrollIndex).Take(visibleCount))
        {
            var row = new ListViewItem(Width, RowHeight, item, _theme)
            {
                IsSelected = index == SelectedIndex
            };
            row.Click += () => Select(index);
            row.DoubleClick += () =>
            {
                Select(index);
                ItemActivated?.Invoke(index, item);
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

    /// <summary>Arranges current rows sequentially in the list viewport.</summary>
    /// <param name="contentSize">Available list content size.</param>
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
}

/// <summary>
/// Displays one selectable row inside a <see cref="ListView"/>.
/// </summary>
public sealed class ListViewItem : Button
{
    private readonly UITheme _theme;
    private readonly Label _label;
    private bool _isSelected;

    /// <summary>Gets the displayed item text.</summary>
    public string Text => _label.Text;

    /// <summary>Gets or sets whether this row is selected.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
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
    }
}
