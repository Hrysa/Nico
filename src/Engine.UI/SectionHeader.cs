namespace Engine.UI;

/// <summary>A standardized panel-title box containing one label.</summary>
public sealed class SectionHeader : ContentControl
{
    /// <summary>Gets the label displaying the section title.</summary>
    public Label TitleLabel { get; }

    /// <summary>Creates a section header using only shared panel theme metrics.</summary>
    /// <param name="width">Header width.</param>
    /// <param name="text">Header caption.</param>
    /// <param name="theme">Theme supplying standardized panel tokens.</param>
    public SectionHeader(float width, string text, UITheme? theme = null)
        : base(width, (theme ?? UITheme.Dark).PanelHeaderHeight)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        BackgroundColor = resolvedTheme.Surface;
        ForegroundColor = resolvedTheme.TextSecondary;
        Padding = new Thickness(resolvedTheme.PanelHeaderPadding, 0f);
        CornerRadius = 0f;
        TitleLabel = new Label(text)
        {
            Name = "Title",
            FontSize = resolvedTheme.PanelTitleFontSize,
            ForegroundColor = resolvedTheme.TextSecondary,
            BackgroundColor = resolvedTheme.Surface,
            PaddingLeft = 0f,
            IsHitTestVisible = false
        };
        Content = TitleLabel;
    }
}
