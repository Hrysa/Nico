namespace Engine.UI;

/// <summary>
/// Standard title and optional subtitle region for modal dialogs.
/// </summary>
public sealed class DialogHeader : Panel
{
    /// <summary>
    /// Creates a dialog header.
    /// </summary>
    /// <param name="x">Local X position.</param>
    /// <param name="y">Local Y position.</param>
    /// <param name="width">Header width.</param>
    /// <param name="title">Primary title.</param>
    /// <param name="subtitle">Optional secondary description.</param>
    /// <param name="theme">Theme supplying colors and typography.</param>
    public DialogHeader(
        float x,
        float y,
        float width,
        string title,
        string subtitle = "",
        UITheme? theme = null)
        : base(x, y, width, string.IsNullOrWhiteSpace(subtitle) ? 48f : 66f,
            (theme ?? UITheme.Dark).SurfaceRaised)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        AddChild(new Label(16f, 8f, width - 32f, 28f, title)
        {
            FontSize = 25.5f,
            ForegroundColor = resolvedTheme.TextPrimary,
            PaddingLeft = 0f
        });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            AddChild(new Label(16f, 34f, width - 32f, 22f, subtitle)
            {
                FontSize = resolvedTheme.CaptionFontSize,
                ForegroundColor = resolvedTheme.TextSecondary,
                PaddingLeft = 0f
            });
        }
        AddChild(new Separator(0f, Height - 1f, width, 1f, resolvedTheme));
    }
}
