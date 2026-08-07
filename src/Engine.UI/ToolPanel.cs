using Engine.Graphics;
using System.Numerics;

namespace Engine.UI;

/// <summary>A standardized editor panel composed from a header and content region.</summary>
public sealed class ToolPanel : Surface
{
    /// <summary>Gets the standardized panel header.</summary>
    public SectionHeader Header { get; }

    /// <summary>Gets the region that owns panel-specific content.</summary>
    public Panel Content { get; }

    /// <summary>Creates a standard docked tool panel.</summary>
    /// <param name="width">Panel width.</param>
    /// <param name="height">Panel height.</param>
    /// <param name="title">Panel title.</param>
    /// <param name="theme">Theme supplying standardized panel tokens.</param>
    public ToolPanel(float width, float height, string title, UITheme? theme = null)
        : base((theme ?? UITheme.Dark).Surface, (theme ?? UITheme.Dark).Border, width, height)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        PaintBackground = false;
        Header = new SectionHeader(width, title, resolvedTheme)
            { Name = "Header" };
        Header.Width = 0f;
        Content = new Panel(resolvedTheme.Surface, width,
            MathF.Max(0f, height - Header.Height))
        {
            Name = "Content",
            PaintBackground = false
        };
        Content.Width = 0f;
        Content.Height = 0f;
        AddChild(Header);
        AddChild(Content);
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        Header.Measure(new Vector2(contentSize.X, Header.Height));
        Header.Arrange(Vector2.Zero, new Vector2(contentSize.X, Header.Height));
        Content.Measure(new Vector2(contentSize.X, MathF.Max(0f, contentSize.Y - Header.Height)));
        Content.Arrange(new Vector2(0f, Header.Height),
            new Vector2(contentSize.X, MathF.Max(0f, contentSize.Y - Header.Height)));
        var childSize = new Vector2(Content.ContentWidth, Content.ContentHeight);
        var children = Content.Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is not UIElement child)
                continue;
            child.Measure(childSize);
            child.Arrange(Vector2.Zero, childSize);
        }
    }
}
