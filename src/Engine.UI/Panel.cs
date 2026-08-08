using System.Numerics;
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
    /// <param name="backgroundColor">Optional panel background color.</param>
    /// <param name="theme">Theme supplying the panel's default corner radius.</param>
    public Panel(Color? backgroundColor = null, float width = 0f, float height = 0f, UITheme? theme = null)
        : base(width, height)
    {
        if (backgroundColor is { } color)
            BackgroundColor = color;
        CornerRadius = (theme ?? UITheme.Dark).PanelCornerRadius;
    }

    /// <summary>Measures children as layered content and derives an intrinsic auto size.</summary>
    /// <param name="availableSize">Space offered by the parent.</param>
    /// <returns>The largest desired child extent including padding.</returns>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var inner = new Vector2(
            MathF.Max(0f, availableSize.X - Padding.Horizontal),
            MathF.Max(0f, availableSize.Y - Padding.Vertical));
        var desired = Vector2.Zero;
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is not UIElement child)
                continue;
            child.Measure(inner);
            desired = Vector2.Max(desired, child.DesiredSize);
        }
        return desired + new Vector2(Padding.Horizontal, Padding.Vertical);
    }

    /// <summary>Arranges layered panel children within the complete content box.</summary>
    /// <param name="contentSize">Size inside this panel's padding.</param>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is UIElement child)
                child.Arrange(new Vector2(Padding.Left, Padding.Top), contentSize);
        }
    }
}
