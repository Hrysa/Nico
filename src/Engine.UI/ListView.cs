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

    /// <summary>Gets or sets the height of one item row.</summary>
    public float RowHeight { get; set; } = 30f;

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
    /// <param name="x">Local X position.</param>
    /// <param name="y">Local Y position.</param>
    /// <param name="width">List width.</param>
    /// <param name="height">List height.</param>
    /// <param name="theme">Theme supplying list colors.</param>
    public ListView(float x, float y, float width, float height, UITheme? theme = null)
        : base(x, y, width, height, (theme ?? UITheme.Dark).Surface)
    {
        _theme = theme ?? UITheme.Dark;
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
                Position = new Vector3(0f, Children.Count * RowHeight, 0f),
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
    }
}

/// <summary>
/// Displays one selectable row inside a <see cref="ListView"/>.
/// </summary>
public sealed class ListViewItem : UIElement
{
    private readonly UITheme _theme;

    /// <summary>Gets the displayed item text.</summary>
    public string Text { get; }

    /// <summary>Gets or sets whether this row is selected.</summary>
    public bool IsSelected { get; set; }

    /// <summary>
    /// Creates a list row.
    /// </summary>
    /// <param name="width">Row width.</param>
    /// <param name="height">Row height.</param>
    /// <param name="text">Displayed item text.</param>
    /// <param name="theme">Theme supplying row colors.</param>
    public ListViewItem(float width, float height, string text, UITheme? theme = null)
        : base(0f, 0f, width, height)
    {
        Text = text;
        _theme = theme ?? UITheme.Dark;
        ForegroundColor = _theme.TextPrimary;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        var background = IsSelected ? _theme.SurfacePressed
            : IsHovered ? _theme.SurfaceHover : _theme.Surface;
        if (IsSelected || IsHovered)
            drawList.AddRectangle(Left, Top, Right, Bottom, background);
        drawList.AddText(Text, Left + 10f, Top + MathF.Max(0f, (Height - _theme.FontSize) / 2f),
            _theme.FontSize, ForegroundColor, background);
    }
}
