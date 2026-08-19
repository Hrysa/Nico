using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Displays one selected text item and an overlay list of available choices.</summary>
public sealed class ComboBox : UIElement
{
    private readonly List<string> _items = [];
    private readonly UITheme _theme;
    private readonly Button _header;
    private readonly Popup _popup;
    private readonly UISelectionModel _selection = new();

    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => new(
        UISemanticRole.ComboBox,
        Name,
        SelectedItem,
        IsEnabled,
        false,
        false,
        null,
        Actions: UISemanticAction.Invoke | UISemanticAction.ExpandCollapse
            | UISemanticAction.Increment | UISemanticAction.Decrement | UISemanticAction.SetValue,
        IsExpanded: IsDropDownOpen,
        NumericValue: SelectedIndex,
        Minimum: _items.Count == 0 ? null : 0d,
        Maximum: _items.Count == 0 ? null : _items.Count - 1d);

    /// <inheritdoc/>
    public override bool PerformSemanticAction(UISemanticAction action, double? value = null)
    {
        if (!IsEnabled)
            return false;
        if (action is UISemanticAction.Invoke or UISemanticAction.ExpandCollapse)
            ToggleDropDown();
        else if (action == UISemanticAction.Increment && _items.Count > 0)
            Select(Math.Min(_items.Count - 1, SelectedIndex + 1));
        else if (action == UISemanticAction.Decrement && _items.Count > 0)
            Select(SelectedIndex <= 0 ? 0 : SelectedIndex - 1);
        else if (action == UISemanticAction.SetValue && value is double requested
            && double.IsFinite(requested) && requested == Math.Truncate(requested)
            && requested >= -1d && requested < _items.Count)
            Select((int)requested);
        else
            return false;
        return true;
    }

    /// <summary>Gets the selected item index, or -1.</summary>
    public int SelectedIndex => _selection.PrimaryIndex;

    /// <summary>Gets the selected item text, or null.</summary>
    public string? SelectedItem => SelectedIndex >= 0 ? _items[SelectedIndex] : null;

    /// <summary>Gets whether the choice popup is open.</summary>
    public bool IsDropDownOpen => _popup.IsVisible;

    /// <summary>Occurs when selection changes.</summary>
    public event Action<int, string?>? SelectionChanged;

    /// <summary>Creates an empty combo box.</summary>
    /// <param name="width">Control width.</param>
    /// <param name="height">Header and row height.</param>
    /// <param name="theme">Theme supplying popup visuals.</param>
    public ComboBox(float width, float height, UITheme? theme = null) : base(width, height)
    {
        _theme = theme ?? UITheme.Dark;
        _header = new Button(width, height, string.Empty, _theme);
        _header.Click += ToggleDropDown;
        _header.Key += OnHeaderKey;
        _popup = new Popup(_theme.SurfaceRaised, _theme.BorderStrong, width)
        {
            Owner = _header,
            IsOverlay = true,
            IsVisible = false
        };
        AddChild(_header);
        AddChild(_popup);
        UpdateHeader();
    }

    /// <summary>Replaces all choices and clears selection.</summary>
    /// <param name="items">Choice labels.</param>
    public void SetItems(IEnumerable<string> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items.Clear();
        _items.AddRange(items);
        _selection.Clear();
        RebuildPopup();
        UpdateHeader();
    }

    /// <summary>Selects one item or clears selection with -1.</summary>
    /// <param name="index">Choice index.</param>
    public void Select(int index)
    {
        if (!_selection.Select(index, _items.Count, UISelectionMode.Single,
            UISelectionIntent.Replace))
            return;
        UpdateHeader();
        SelectionChanged?.Invoke(index, SelectedItem);
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        _header.Measure(new Vector2(availableSize.X, Height));
        _popup.Measure(new Vector2(availableSize.X, _popup.Height));
        return new Vector2(availableSize.X, Height);
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        _header.Arrange(Vector2.Zero, new Vector2(contentSize.X, contentSize.Y));
        _popup.Arrange(new Vector2(0f, contentSize.Y),
            new Vector2(contentSize.X, _popup.Height));
        var rows = _popup.Children;
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index] is UIElement row)
                row.Arrange(new Vector2(2f, 2f + index * Height),
                    new Vector2(MathF.Max(0f, contentSize.X - 4f), Height));
        }
    }

    /// <summary>Opens or closes the choice overlay.</summary>
    private void ToggleDropDown()
    {
        if (_popup.IsOpen)
            _popup.Close();
        else
            _popup.Open();
        InvalidateMeasure();
    }

    /// <summary>Handles selection navigation from the focused header.</summary>
    /// <param name="sender">Current receiver.</param>
    /// <param name="keyEvent">Routed key data.</param>
    private void OnHeaderKey(UIElement sender, UIKeyEventArgs keyEvent)
    {
        if (keyEvent.RoutePhase != UIRoutePhase.Target || keyEvent.Kind != UIKeyEventKind.KeyDown)
            return;
        if (keyEvent.Key == InputKey.Down && _items.Count > 0)
            Select(Math.Min(_items.Count - 1, SelectedIndex + 1));
        else if (keyEvent.Key == InputKey.Up && _items.Count > 0)
            Select(SelectedIndex <= 0 ? 0 : SelectedIndex - 1);
        else if (keyEvent.Key is InputKey.Space)
            ToggleDropDown();
        else if (keyEvent.Key == InputKey.Escape && IsDropDownOpen)
            _popup.Close();
        else
            return;
        keyEvent.Handled = true;
    }

    /// <summary>Rebuilds popup rows after choices change.</summary>
    private void RebuildPopup()
    {
        _popup.ClearChildren();
        for (var index = 0; index < _items.Count; index++)
        {
            var itemIndex = index;
            var row = new ContextMenuItem(Width - 4f, Height, _items[index], _theme);
            row.Click += () =>
            {
                Select(itemIndex);
                _popup.Close();
            };
            _popup.AddChild(row);
        }
        _popup.Height = _items.Count * Height + 4f;
        InvalidateMeasure();
    }

    /// <summary>Updates the header label from current selection.</summary>
    private void UpdateHeader()
    {
        _header.Content = new Label(SelectedItem ?? "Select…")
        {
            TextStyle = _theme.GetTextStyle(UITextRole.Body),
            IsHitTestVisible = false
        };
    }
}
