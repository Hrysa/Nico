using Engine.Core;

namespace Engine.UI;

/// <summary>Displays one standardized selectable hierarchy row.</summary>
public sealed class TreeViewItem : Button
{
    private readonly UITheme _theme;
    private readonly Label? _label;
    private readonly IReadOnlyList<TreeViewColumn> _columns;
    private readonly string[] _columnTexts;
    private bool _isSelected;

    /// <summary>Gets the node represented by this row.</summary>
    public Node Item { get; }

    /// <summary>Gets the hierarchy depth.</summary>
    public int Depth { get; }

    /// <summary>Gets whether the represented node is expanded.</summary>
    public bool IsExpanded { get; }

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
        _columnTexts = _columns.Select(column => column.Value(item)).ToArray();
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
    }

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
            var measuredWidth = Label.MeasureTextWidth(text, _theme.FontSize);
            var textX = column.Alignment == TreeViewColumnAlignment.Right
                ? x + MathF.Max(leadingInset, width - measuredWidth - trailingInset)
                : x + leadingInset;
            drawList.AddText(text, textX,
                Top + MathF.Max(0f, (Height - _theme.FontSize) / 2f),
                _theme.FontSize, _theme.TextPrimary, BackgroundColor);
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
    private static string FitText(string text, float availableWidth, float fontSize)
    {
        if (Label.MeasureTextWidth(text, fontSize) <= availableWidth)
            return text;
        const string ellipsis = "...";
        var ellipsisWidth = Label.MeasureTextWidth(ellipsis, fontSize);
        if (ellipsisWidth >= availableWidth)
            return string.Empty;
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var length = (low + high + 1) / 2;
            if (Label.MeasureTextWidth(text[..length], fontSize) + ellipsisWidth <= availableWidth)
                low = length;
            else
                high = length - 1;
        }
        return text[..low] + ellipsis;
    }

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
