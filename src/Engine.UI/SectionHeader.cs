namespace Engine.UI;

/// <summary>
/// A consistent titled header for panels and viewport sections.
/// </summary>
public sealed class SectionHeader : Label
{
    /// <summary>
    /// Creates a themed section header.
    /// </summary>
    /// <param name="x">Local X position.</param>
    /// <param name="y">Local Y position.</param>
    /// <param name="width">Header width.</param>
    /// <param name="height">Header height.</param>
    /// <param name="text">Header caption.</param>
    /// <param name="theme">Theme supplying colors and typography.</param>
    public SectionHeader(float x, float y, float width, float height, string text, UITheme? theme = null)
        : base(x, y, width, height, text)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        BackgroundColor = resolvedTheme.Surface;
        ForegroundColor = resolvedTheme.TextSecondary;
        PaintBackground = true;
        FontSize = resolvedTheme.PanelTitleFontSize;
        PaddingLeft = resolvedTheme.Spacing;
    }
}
