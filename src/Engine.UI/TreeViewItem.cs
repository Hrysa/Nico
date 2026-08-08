using Engine.Core;
using System.Globalization;

namespace Engine.UI;

/// <summary>Displays one standardized selectable hierarchy row.</summary>
public sealed class TreeViewItem : Button
{
    private readonly UITheme _theme;
    private readonly Label? _label;
    private readonly IReadOnlyList<TreeViewColumn> _columns;
    private readonly string[] _columnTexts;
    private bool _isSelected;

    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => base.GetSemanticInfo() with
    {
        Role = UISemanticRole.TreeItem,
        Name = string.IsNullOrWhiteSpace(Item.Name) ? Item.GetType().Name : Item.Name,
        Value = string.IsNullOrWhiteSpace(Item.Name) ? Item.GetType().Name : Item.Name,
        Actions = UISemanticAction.Invoke | UISemanticAction.Select
            | (Item.CanHaveChildren ? UISemanticAction.ExpandCollapse : UISemanticAction.None),
        IsSelected = IsSelected,
        IsExpanded = Item.CanHaveChildren ? IsExpanded : null
    };

    /// <inheritdoc/>
    public override bool PerformSemanticAction(UISemanticAction action, double? value = null)
    {
        if (!IsEnabled)
            return false;
        if (action == UISemanticAction.Select)
            OnSelect();
        else if (action is UISemanticAction.Invoke or UISemanticAction.ExpandCollapse)
        {
            if (action == UISemanticAction.ExpandCollapse && !Item.CanHaveChildren)
                return false;
            OnActivate();
        }
        else
            return false;
        return true;
    }

    /// <summary>Gets the node represented by this row.</summary>
    public Node Item { get; private set; }

    /// <summary>Gets the hierarchy depth.</summary>
    public int Depth { get; private set; }

    /// <summary>Gets whether the represented node is expanded.</summary>
    public bool IsExpanded { get; private set; }

    private Action<Node, Engine.Graphics.InputModifiers>? _selectItem;
    private Action<Node>? _activateItem;
    private Engine.Graphics.InputModifiers _selectionModifiers;

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
            InvalidateVisual();
        }
    }

    /// <summary>Creates a hierarchy row from the shared item-row visual tokens.</summary>
    /// <param name="width">Row width.</param>
    /// <param name="height">Row height.</param>
    /// <param name="item">Represented node.</param>
    /// <param name="depth">Hierarchy depth.</param>
    /// <param name="isExpanded">Whether the node is expanded.</param>
    /// <param name="theme">Theme supplying row colors, spacing, and typography.</param>
    /// <param name="displayText">Optional text replacing the node's name.</param>
    /// <param name="columns">Optional aligned data columns.</param>
    public TreeViewItem(float width, float height, Node item, int depth, bool isExpanded,
        UITheme? theme = null, string? displayText = null,
        IReadOnlyList<TreeViewColumn>? columns = null)
        : base(width, height, theme ?? UITheme.Dark)
    {
        Item = item;
        Depth = depth;
        IsExpanded = isExpanded;
        _theme = theme ?? UITheme.Dark;
        _columns = columns ?? [];
        _columnTexts = new string[_columns.Count];
        for (var index = 0; index < _columns.Count; index++)
            _columnTexts[index] = _columns[index].Value(item);
        ForegroundColor = _theme.TextPrimary;
        if (_columns.Count == 0)
        {
            _label = new Label(BuildLabel(item, isExpanded, displayText))
            {
                FontSize = _theme.FontSize,
                ForegroundColor = _theme.TextPrimary,
                PaddingLeft = 0f,
                IsHitTestVisible = false
            };
            Content = _label;
            PaddingLeft = _theme.ItemRowPadding + depth * _theme.TreeIndent;
        }
        else
        {
            PaddingLeft = 0f;
            PaddingRight = 0f;
        }
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
    /// <param name="activateItem">Activation callback.</param>
    /// <param name="handleKeyDown">Keyboard callback.</param>
    internal void BindOwner(
        Action<Node, Engine.Graphics.InputModifiers> selectItem,
        Action<Node> activateItem,
        Action<int> handleKeyDown)
    {
        _selectItem = selectItem;
        _activateItem = activateItem;
        KeyDown += handleKeyDown;
    }

    /// <summary>Rebinds this retained row container to a logical tree node.</summary>
    /// <param name="item">Represented node.</param>
    /// <param name="depth">Hierarchy depth.</param>
    /// <param name="isExpanded">Whether the node is expanded.</param>
    /// <param name="displayText">Optional display text.</param>
    /// <param name="isSelected">Whether the node is selected.</param>
    /// <param name="width">Current row width.</param>
    /// <param name="height">Current row height.</param>
    internal void Bind(
        Node item,
        int depth,
        bool isExpanded,
        string? displayText,
        bool isSelected,
        float width,
        float height)
    {
        Item = item;
        Depth = depth;
        IsExpanded = isExpanded;
        if (Width != width)
            Width = width;
        if (Height != height)
            Height = height;
        if (_label is not null)
        {
            _label.Text = BuildLabel(item, isExpanded, displayText);
            PaddingLeft = _theme.ItemRowPadding + depth * _theme.TreeIndent;
        }
        for (var index = 0; index < _columns.Count; index++)
            _columnTexts[index] = _columns[index].Value(item);
        IsSelected = isSelected;
        InvalidateVisual();
    }

    /// <summary>Selects the node currently bound to this container.</summary>
    private void OnSelect()
    {
        _selectItem?.Invoke(Item, _selectionModifiers);
        _selectionModifiers = Engine.Graphics.InputModifiers.None;
    }

    /// <summary>Captures modifiers from the release that invokes this row.</summary>
    /// <param name="sender">Current routed receiver.</param>
    /// <param name="pointerEvent">Routed pointer transition.</param>
    private void CaptureSelectionModifiers(UIElement sender, UIPointerEventArgs pointerEvent)
    {
        if (pointerEvent.RoutePhase == UIRoutePhase.Target
            && pointerEvent.Kind == UIPointerEventKind.Release)
            _selectionModifiers = pointerEvent.Modifiers;
    }

    /// <summary>Activates the node currently bound to this container.</summary>
    private void OnActivate() => _activateItem?.Invoke(Item);

    /// <inheritdoc/>
    protected override void Paint(Engine.Graphics.UIDrawList drawList)
    {
        base.Paint(drawList);
        if (_columns.Count == 0)
            return;

        var x = Left;
        for (var index = 0; index < _columns.Count; index++)
        {
            var column = _columns[index];
            var width = TreeViewColumnLayout.ResolveWidth(_columns, column, Width);
            var leadingInset = index == 0
                ? _theme.ItemRowPadding + Depth * _theme.TreeIndent
                : 6f;
            var trailingInset = 6f;
            var marker = index == 0
                ? Item.CanHaveChildren ? IsExpanded ? "- " : "+ " : "  "
                : string.Empty;
            var availableTextWidth = MathF.Max(0f, width - leadingInset - trailingInset);
            var text = FitText(marker + _columnTexts[index], availableTextWidth, _theme.FontSize);
            var measuredWidth = MeasureTextWidth(text.AsSpan(), _theme.FontSize);
            var textX = column.Alignment == TreeViewColumnAlignment.Right
                ? x + MathF.Max(leadingInset, width - measuredWidth - trailingInset)
                : x + leadingInset;
            drawList.AddText(text, textX,
                Top + MathF.Max(0f, (Height - _theme.FontSize) / 2f),
                _theme.FontSize, _theme.TextPrimary, BackgroundColor,
                FlowDirection.ToTextFlowDirection());
            x += width;
            if (index + 1 < _columns.Count)
                drawList.AddRectangle(x - 1f, Top, x, Bottom, _theme.Border);
        }
    }

    /// <summary>Truncates text to the available width and appends an ellipsis.</summary>
    /// <param name="text">Text to fit.</param>
    /// <param name="availableWidth">Available horizontal space.</param>
    /// <param name="fontSize">Text font size.</param>
    /// <returns>Original or truncated text.</returns>
    private string FitText(string text, float availableWidth, float fontSize)
    {
        if (MeasureTextWidth(text.AsSpan(), fontSize) <= availableWidth)
            return text;
        const string ellipsis = "...";
        var ellipsisWidth = MeasureTextWidth(ellipsis.AsSpan(), fontSize);
        if (ellipsisWidth >= availableWidth)
            return string.Empty;
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        var low = 0;
        var high = boundaries.Length;
        while (low < high)
        {
            var boundaryCount = (low + high + 1) / 2;
            var length = boundaryCount < boundaries.Length
                ? boundaries[boundaryCount]
                : text.Length;
            if (MeasureTextWidth(text.AsSpan(0, length), fontSize) + ellipsisWidth <= availableWidth)
                low = boundaryCount;
            else
                high = boundaryCount - 1;
        }
        var fit = low < boundaries.Length ? boundaries[low] : text.Length;
        return text[..fit] + ellipsis;
    }

    /// <summary>Measures text using the inherited paragraph direction.</summary>
    /// <param name="text">Text to measure.</param>
    /// <param name="fontSize">Font height.</param>
    /// <returns>Horizontal advance.</returns>
    private float MeasureTextWidth(ReadOnlySpan<char> text, float fontSize) =>
        TextLayout.MeasureWidth(text, fontSize, FlowDirection.ToTextFlowDirection());

    /// <summary>Builds row text with a fixed plus/minus disclosure column.</summary>
    /// <param name="item">Represented node.</param>
    /// <param name="isExpanded">Whether the node is expanded.</param>
    /// <param name="displayText">Optional text replacing the node's name.</param>
    /// <returns>Display text for the row label.</returns>
    private static string BuildLabel(Node item, bool isExpanded, string? displayText)
    {
        var name = displayText ?? (string.IsNullOrWhiteSpace(item.Name) ? item.GetType().Name : item.Name);
        var marker = item.CanHaveChildren ? isExpanded ? "-" : "+" : " ";
        return $"{marker} {name}";
    }
}
