using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// A rectangular panel container. Can hold child UI elements.
/// </summary>
public class Panel : Box
{
    /// <summary>
    /// Creates a new Panel at the specified position and size.
    /// </summary>
    /// <param name="width">The panel width.</param>
    /// <param name="height">The panel height.</param>
    /// <param name="backgroundColor">The panel background color.</param>
    /// <param name="theme">Theme supplying the panel's default corner radius.</param>
    public Panel(Color backgroundColor, float width = 0f, float height = 0f, UITheme? theme = null)
        : base(width, height)
    {
        BackgroundColor = backgroundColor;
        CornerRadius = (theme ?? UITheme.Dark).PanelCornerRadius;
    }
}
