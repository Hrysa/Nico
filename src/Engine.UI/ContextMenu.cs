using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// A floating menu containing vertically arranged actions.
/// </summary>
public sealed class ContextMenu : Surface
{
    private const float ItemHeight = 26f;
    private readonly UITheme _theme;

    /// <summary>
    /// Creates an empty context menu.
    /// </summary>
    /// <param name="x">Local X position.</param>
    /// <param name="y">Local Y position.</param>
    /// <param name="width">Menu width.</param>
    /// <param name="theme">Theme supplying menu colors and typography.</param>
    public ContextMenu(float x, float y, float width, UITheme? theme = null)
        : base(x, y, width, 0f, (theme ?? UITheme.Dark).SurfaceRaised, (theme ?? UITheme.Dark).BorderStrong)
    {
        _theme = theme ?? UITheme.Dark;
        IsOverlay = true;
    }

    /// <summary>Adds an action to the menu.</summary>
    /// <param name="label">Action label.</param>
    /// <param name="action">Action invoked when clicked.</param>
    public void AddItem(string label, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var item = new ContextMenuItem(2f, 2f + Children.Count * ItemHeight,
            Width - 4f, ItemHeight, label, _theme);
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
    private readonly UITheme _theme;
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
    /// <param name="theme">Theme supplying row colors and typography.</param>
    public ContextMenuItem(float x, float y, float width, float height, string label, UITheme? theme = null)
        : base(x, y, width, height)
    {
        Label = label;
        _theme = theme ?? UITheme.Dark;
        ForegroundColor = _theme.TextPrimary;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        var color = IsPressed ? _theme.SurfacePressed
            : IsHovered ? _theme.SurfaceHover : _theme.SurfaceRaised;
        drawList.AddRectangle(Left, Top, Right, Bottom, color);
        drawList.AddText(Label, Left + 10f, Top + 6f, _theme.FontSize, ForegroundColor, color);
    }
}
