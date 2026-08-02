using Engine.Graphics;

namespace Engine.UI;

/// <summary>A standardized editor panel composed from a header and content region.</summary>
public sealed class ToolPanel : Surface
{
    /// <summary>Gets the standardized panel header.</summary>
    public SectionHeader Header { get; }

    /// <summary>Gets the region that owns panel-specific content.</summary>
    public Panel Content { get; }

    /// <summary>Creates a standard docked tool panel.</summary>
    /// <param name="x">Local X position.</param>
    /// <param name="y">Local Y position.</param>
    /// <param name="width">Panel width.</param>
    /// <param name="height">Panel height.</param>
    /// <param name="title">Panel title.</param>
    /// <param name="theme">Theme supplying standardized panel tokens.</param>
    public ToolPanel(float x, float y, float width, float height, string title, UITheme? theme = null)
        : base(x, y, width, height, (theme ?? UITheme.Dark).Surface,
            (theme ?? UITheme.Dark).Border)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        PaintBackground = false;
        Header = new SectionHeader(0f, 0f, width, title, resolvedTheme)
            { Name = "Header" };
        Content = new Panel(0f, Header.Height, width,
            MathF.Max(0f, height - Header.Height), resolvedTheme.Surface)
        {
            Name = "Content",
            PaintBackground = false
        };
        AddChild(Header);
        AddChild(Content);
    }
}
