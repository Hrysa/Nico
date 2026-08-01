using Engine.Core;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Displays one flattened row of a <see cref="TreeView"/>.
/// </summary>
public sealed class TreeViewItem : UIElement
{
    private readonly Color _normalColor;
    private readonly Color _hoverColor;
    private readonly Color _selectedColor;

    /// <summary>Gets the node represented by this row.</summary>
    public Node Item { get; }

    /// <summary>Gets the hierarchy depth.</summary>
    public int Depth { get; }

    /// <summary>Gets whether the represented node is expanded.</summary>
    public bool IsExpanded { get; }

    /// <summary>Gets or sets whether this row is selected.</summary>
    public bool IsSelected { get; set; }

    /// <summary>
    /// Creates a hierarchy row.
    /// </summary>
    /// <param name="width">Row width.</param>
    /// <param name="height">Row height.</param>
    /// <param name="item">Represented node.</param>
    /// <param name="depth">Hierarchy depth.</param>
    /// <param name="isExpanded">Whether the node is expanded.</param>
    public TreeViewItem(float width, float height, Node item, int depth, bool isExpanded)
        : base(0f, 0f, width, height)
    {
        Item = item;
        Depth = depth;
        IsExpanded = isExpanded;
        _normalColor = Color.EditorPanel;
        _hoverColor = Color.Lerp(Color.EditorPanel, Color.White, 0.08f);
        _selectedColor = new Color(0.18f, 0.36f, 0.58f);
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        var color = IsSelected ? _selectedColor : IsHovered ? _hoverColor : _normalColor;
        drawList.AddRectangle(Left, Top, Right, Bottom, color);
        var indent = 6f + Depth * 14f;
        if (Item.HasChildren)
        {
            var marker = IsExpanded ? "-" : ">";
            drawList.AddText(marker, Left + indent, Top + 7f, 1.5f, ForegroundColor);
        }
        drawList.AddText(string.IsNullOrWhiteSpace(Item.Name) ? Item.GetType().Name : Item.Name,
            Left + indent + 12f, Top + 6f, 1.5f, ForegroundColor);
    }
}
