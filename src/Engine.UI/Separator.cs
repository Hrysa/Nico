using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// A themed horizontal or vertical dividing line.
/// </summary>
public sealed class Separator : Panel
{
    /// <summary>
    /// Creates a separator rectangle.
    /// </summary>
    /// <param name="width">Separator width.</param>
    /// <param name="height">Separator height.</param>
    /// <param name="theme">Theme supplying the separator color.</param>
    public Separator(float width, float height, UITheme? theme = null)
        : base((theme ?? UITheme.Dark).Border, width, height)
    {
    }
}
