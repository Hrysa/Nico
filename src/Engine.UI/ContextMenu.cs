using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// A floating menu containing vertically arranged actions.
/// </summary>
public sealed class ContextMenu : Panel
{
    private const float ItemHeight = 26f;

    /// <summary>
    /// Creates an empty context menu.
    /// </summary>
    /// <param name="x">Local X position.</param>
    /// <param name="y">Local Y position.</param>
    /// <param name="width">Menu width.</param>
    public ContextMenu(float x, float y, float width)
        : base(x, y, width, 0f, Color.EditorPanelHeader)
    {
    }

    /// <summary>Adds an action to the menu.</summary>
    /// <param name="label">Action label.</param>
    /// <param name="action">Action invoked when clicked.</param>
    public void AddItem(string label, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var item = new ContextMenuItem(2f, 2f + Children.Count * ItemHeight,
            Width - 4f, ItemHeight, label);
        item.Click += action;
        AddChild(item);
        Height = 4f + Children.Count * ItemHeight;
    }
}

/// <summary>
/// One clickable text row in a <see cref="ContextMenu"/>.
/// </summary>
public sealed class ContextMenuItem : UIElement
{
    /// <summary>Gets the action label.</summary>
    public string Label { get; }

    /// <summary>
    /// Creates a context-menu item.
    /// </summary>
    /// <param name="x">Local X position.</param>
    /// <param name="y">Local Y position.</param>
    /// <param name="width">Item width.</param>
    /// <param name="height">Item height.</param>
    /// <param name="label">Displayed label.</param>
    public ContextMenuItem(float x, float y, float width, float height, string label)
        : base(x, y, width, height)
    {
        Label = label;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        var color = IsPressed
            ? new Color(0.16f, 0.31f, 0.50f)
            : IsHovered ? Color.Lerp(Color.EditorPanelHeader, Color.White, 0.1f) : Color.EditorPanelHeader;
        drawList.AddRectangle(Left, Top, Right, Bottom, color);
        drawList.AddText(Label, Left + 8f, Top + 7f, 1.5f, ForegroundColor);
    }
}
