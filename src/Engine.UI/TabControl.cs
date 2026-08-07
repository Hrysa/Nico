using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Associates one tab title with its retained content element.</summary>
public sealed record TabItem(string Header, UIElement Content);

/// <summary>Displays a selectable header strip and exactly one active content page.</summary>
public sealed class TabControl : UIElement
{
    private const float DefaultHeaderWidth = 100f;
    private readonly List<TabItem> _items = [];
    private readonly List<ToggleButton> _headers = [];
    private readonly List<Button?> _closeButtons = [];
    private readonly UITheme _theme;

    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        UISemanticRole.TabList,
        Name,
        SelectedItem?.Header,
        IsEnabled,
        true,
        false,
        null,
        Actions: UISemanticAction.Increment | UISemanticAction.Decrement | UISemanticAction.SetValue,
        NumericValue: SelectedIndex,
        Minimum: _items.Count == 0 ? null : 0d,
        Maximum: _items.Count == 0 ? null : _items.Count - 1d);

    /// <inheritdoc/>
    public override bool PerformSemanticAction(UISemanticAction action, double? value = null)
    {
        if (!IsEnabled || _items.Count == 0)
            return false;
        if (action == UISemanticAction.Increment)
            Select(Math.Min(_items.Count - 1, SelectedIndex + 1));
        else if (action == UISemanticAction.Decrement)
            Select(Math.Max(0, SelectedIndex - 1));
        else if (action == UISemanticAction.SetValue && value is double requested
            && double.IsFinite(requested) && requested == Math.Truncate(requested)
            && requested >= 0d && requested < _items.Count)
            Select((int)requested);
        else
            return false;
        return true;
    }

    /// <summary>Gets the selected tab index, or -1.</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>Gets the selected tab, or null.</summary>
    public TabItem? SelectedItem => SelectedIndex >= 0 ? _items[SelectedIndex] : null;

    /// <summary>Occurs when the selected tab changes.</summary>
    public event Action<int, TabItem?>? SelectionChanged;

    /// <summary>Creates an empty tab control.</summary>
    /// <param name="width">Control width.</param>
    /// <param name="height">Control height.</param>
    /// <param name="headerHeight">Header-strip height.</param>
    /// <param name="theme">Theme supplying header visuals.</param>
    public TabControl(float width, float height, float headerHeight = 30f, UITheme? theme = null)
        : base(width, height)
    {
        HeaderHeight = headerHeight;
        _theme = theme ?? UITheme.Dark;
    }

    /// <summary>Gets or sets header-strip height.</summary>
    public float HeaderHeight { get; set; }

    /// <summary>Adds a retained tab and selects the first tab automatically.</summary>
    /// <param name="header">Header text.</param>
    /// <param name="content">Tab content.</param>
    /// <param name="close">Optional close action displayed in the tab header.</param>
    public void AddTab(string header, UIElement content, Action? close = null)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(content);
        var index = _items.Count;
        var item = new TabItem(header, content);
        var button = new ToggleButton(DefaultHeaderWidth, HeaderHeight, header, _theme);
        button.Click += () =>
        {
            if (SelectedIndex == index)
                button.IsChecked = true;
            else
                Select(index);
        };
        button.Key += (_, keyEvent) => NavigateHeader(index, keyEvent);
        _items.Add(item);
        _headers.Add(button);
        Button? closeButton = null;
        if (close is not null)
        {
            closeButton = new Button(24f, HeaderHeight, "×", _theme)
            {
                Name = $"Close {header}",
                TabIndex = button.TabIndex + 1
            };
            closeButton.Click += close;
        }
        _closeButtons.Add(closeButton);
        AddChild(button);
        AddChild(content);
        if (closeButton is not null)
            AddChild(closeButton);
        content.IsVisible = false;
        if (SelectedIndex < 0)
            Select(0);
        InvalidateMeasure();
    }

    /// <summary>Gets the retained header button for framework composition.</summary>
    /// <param name="index">Tab index.</param>
    /// <returns>Header button.</returns>
    internal ToggleButton GetHeader(int index)
    {
        if ((uint)index >= (uint)_headers.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _headers[index];
    }

    /// <summary>Maps a host-space horizontal pointer coordinate to a tab insertion index.</summary>
    /// <param name="hostX">Pointer X coordinate in host space.</param>
    /// <returns>Insertion index between zero and the tab count.</returns>
    internal int GetInsertionIndex(float hostX)
    {
        for (var index = 0; index < _headers.Count; index++)
        {
            var header = _headers[index];
            if (hostX < header.Left + header.Width * 0.5f)
                return index;
        }
        return _headers.Count;
    }

    /// <summary>Gets the host-space X coordinate for a tab insertion index.</summary>
    /// <param name="index">Insertion index.</param>
    /// <returns>Insertion marker X coordinate.</returns>
    internal float GetInsertionX(int index)
    {
        if (index <= 0)
            return Left;
        if (index >= _headers.Count)
            return _headers.Count == 0 ? Left : _headers[^1].Right;
        return _headers[index].Left;
    }

    /// <summary>Selects one tab.</summary>
    /// <param name="index">Tab index.</param>
    public void Select(int index)
    {
        if (index < 0 || index >= _items.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (SelectedIndex == index)
            return;
        if (SelectedIndex >= 0)
        {
            _headers[SelectedIndex].IsChecked = false;
            _items[SelectedIndex].Content.IsVisible = false;
        }
        SelectedIndex = index;
        _headers[index].IsChecked = true;
        _items[index].Content.IsVisible = true;
        InvalidateMeasure();
        SelectionChanged?.Invoke(index, _items[index]);
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        for (var index = 0; index < _headers.Count; index++)
        {
            _headers[index].Measure(new Vector2(DefaultHeaderWidth, HeaderHeight));
            _closeButtons[index]?.Measure(new Vector2(24f, HeaderHeight));
        }
        if (SelectedItem is { } selected)
            selected.Content.Measure(new Vector2(availableSize.X,
                MathF.Max(0f, availableSize.Y - HeaderHeight)));
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        for (var index = 0; index < _headers.Count; index++)
        {
            _headers[index].Arrange(new Vector2(index * DefaultHeaderWidth, 0f),
                new Vector2(DefaultHeaderWidth, HeaderHeight));
            _closeButtons[index]?.Arrange(
                new Vector2((index + 1) * DefaultHeaderWidth - 24f, 0f),
                new Vector2(24f, HeaderHeight));
        }
        if (SelectedItem is { } selected)
            selected.Content.Arrange(new Vector2(0f, HeaderHeight),
                new Vector2(contentSize.X, MathF.Max(0f, contentSize.Y - HeaderHeight)));
    }

    /// <summary>Moves selection from a focused header using Left/Right/Home/End.</summary>
    /// <param name="headerIndex">Header receiving the key.</param>
    /// <param name="keyEvent">Routed key data.</param>
    private void NavigateHeader(int headerIndex, UIKeyEventArgs keyEvent)
    {
        if (keyEvent.RoutePhase != UIRoutePhase.Target || keyEvent.Kind != UIKeyEventKind.KeyDown)
            return;
        var next = keyEvent.Key switch
        {
            InputKey.Left => Math.Max(0, headerIndex - 1),
            InputKey.Right => Math.Min(_items.Count - 1, headerIndex + 1),
            InputKey.Home => 0,
            InputKey.End => _items.Count - 1,
            _ => -1
        };
        if (next < 0)
            return;
        Select(next);
        keyEvent.Handled = true;
    }
}
