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
    /// <param name="width">Menu width.</param>
    /// <param name="theme">Theme supplying menu colors and typography.</param>
    public ContextMenu(float width, UITheme? theme = null)
        : base((theme ?? UITheme.Dark).SurfaceRaised, (theme ?? UITheme.Dark).BorderStrong, width)
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
        var item = CreateItem(label);
        item.Click += action;
        AddChild(item);
        Height = 4f + Children.Count * ItemHeight;
        RefreshLayout();
    }

    /// <summary>Adds an item that opens a child menu when hovered.</summary>
    /// <param name="label">Item label.</param>
    /// <param name="showSubmenu">Action that displays the child menu beside the hovered item.</param>
    public void AddSubmenu(string label, Action<ContextMenuItem> showSubmenu)
    {
        ArgumentNullException.ThrowIfNull(showSubmenu);
        var item = CreateItem($"{label}  ›");
        item.SubmenuRequested += () => showSubmenu(item);
        AddChild(item);
        Height = 4f + Children.Count * ItemHeight;
        RefreshLayout();
    }

    /// <summary>Creates a context-menu row at the next vertical position.</summary>
    /// <param name="label">Displayed row label.</param>
    /// <returns>The unparented context-menu item.</returns>
    private ContextMenuItem CreateItem(string label)
    {
        return new ContextMenuItem(Width - 4f, ItemHeight, label, _theme);
    }

    /// <summary>Refreshes row bounds after menu contents change.</summary>
    private void RefreshLayout()
    {
        var size = new System.Numerics.Vector2(Width, Height);
        Measure(size);
        Arrange(System.Numerics.Vector2.Zero, size);
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(System.Numerics.Vector2 contentSize)
    {
        var y = 2f;
        foreach (var child in Children.OfType<UIElement>())
        {
            child.Measure(new System.Numerics.Vector2(MathF.Max(0f, contentSize.X - 4f), ItemHeight));
            child.Arrange(new System.Numerics.Vector2(2f, y),
                new System.Numerics.Vector2(MathF.Max(0f, contentSize.X - 4f), ItemHeight));
            y += ItemHeight;
        }
    }
}

/// <summary>
/// One clickable text row in a <see cref="ContextMenu"/>.
/// </summary>
public sealed class ContextMenuItem : Button
{
    /// <summary>Occurs when hovering this item should display its child menu.</summary>
    public event Action? SubmenuRequested;

    /// <summary>
    /// Creates a context-menu item.
    /// </summary>
    /// <param name="width">Item width.</param>
    /// <param name="height">Item height.</param>
    /// <param name="label">Displayed label.</param>
    /// <param name="theme">Theme supplying row colors and typography.</param>
    public ContextMenuItem(float width, float height, string label, UITheme? theme = null)
        : base(width, height, label, theme ?? UITheme.Dark)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        PaddingLeft = 10f;
        NormalColor = resolvedTheme.SurfaceRaised;
        HoverColor = resolvedTheme.SurfaceHover;
        PressedColor = resolvedTheme.SurfacePressed;
        PaintNormalBackground = true;
        CornerRadius = 0f;
    }

    /// <inheritdoc/>
    protected override void OnMouseEnter()
    {
        base.OnMouseEnter();
        SubmenuRequested?.Invoke();
    }
}
