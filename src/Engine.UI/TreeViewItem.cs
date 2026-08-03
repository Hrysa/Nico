using Engine.Core;

namespace Engine.UI;

/// <summary>Displays one standardized selectable hierarchy row.</summary>
public sealed class TreeViewItem : Button
{
    private readonly UITheme _theme;
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
            _isSelected = value;
            NormalColor = value ? _theme.SurfacePressed : _theme.Surface;
            PaintNormalBackground = value;
        }
    }

    /// <summary>Creates a hierarchy row from the shared item-row visual tokens.</summary>
    /// <param name="width">Row width.</param>
    /// <param name="height">Row height.</param>
    /// <param name="item">Represented node.</param>
    /// <param name="depth">Hierarchy depth.</param>
    /// <param name="isExpanded">Whether the node is expanded.</param>
    /// <param name="theme">Theme supplying row colors, spacing, and typography.</param>
    public TreeViewItem(float width, float height, Node item, int depth, bool isExpanded,
        UITheme? theme = null)
        : base(width, height, BuildLabel(item, isExpanded), theme ?? UITheme.Dark)
    {
        Item = item;
        Depth = depth;
        IsExpanded = isExpanded;
        _theme = theme ?? UITheme.Dark;
        ForegroundColor = _theme.TextPrimary;
        FontSize = _theme.FontSize;
        PaddingLeft = _theme.ItemRowPadding + depth * _theme.TreeIndent;
        NormalColor = _theme.Surface;
        HoverColor = _theme.SurfaceHover;
        PressedColor = _theme.SurfacePressed;
        PaintNormalBackground = false;
        CornerRadius = 0f;
    }

    /// <summary>Builds row text with a fixed plus/minus disclosure column.</summary>
    /// <param name="item">Represented node.</param>
    /// <param name="isExpanded">Whether the node is expanded.</param>
    /// <returns>Display text for the row label.</returns>
    private static string BuildLabel(Node item, bool isExpanded)
    {
        var name = string.IsNullOrWhiteSpace(item.Name) ? item.GetType().Name : item.Name;
        var marker = item.CanHaveChildren ? isExpanded ? "-" : "+" : " ";
        return $"{marker} {name}";
    }
}
