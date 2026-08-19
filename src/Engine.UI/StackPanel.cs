using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Arranges UI children sequentially along the vertical axis.
/// </summary>
public class StackPanel : Panel
{
    /// <summary>Gets or sets the space between children.</summary>
    public float Spacing
    {
        get;
        set { if (field != value) { field = value; InvalidateMeasure(); } }
    }

    /// <summary>
    /// Creates a vertical stack panel.
    /// </summary>
    /// <param name="width">Panel width.</param>
    /// <param name="height">Panel height.</param>
    /// <param name="backgroundColor">Optional panel background color.</param>
    public StackPanel(float width, float height, Color? backgroundColor = null)
        : base(backgroundColor, width, height)
    {
    }

    /// <summary>Adds and lays out one child.</summary>
    /// <param name="child">Child element.</param>
    public void AddItem(UIElement child)
    {
        AddChild(child);
        Measure(new Vector2(Width, Height));
        Arrange(new Vector2(Position.X, Position.Y), new Vector2(Width, Height));
    }

    /// <summary>Recomputes the vertical position and width of every UI child.</summary>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var desiredWidth = 0f;
        var desiredHeight = 0f;
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is not UIElement child)
                continue;
            child.Measure(new Vector2(availableSize.X, float.PositiveInfinity));
            desiredWidth = MathF.Max(desiredWidth, child.DesiredSize.X);
            desiredHeight += child.DesiredSize.Y + Spacing;
        }
        if (Children.Count > 0)
            desiredHeight -= Spacing;
        return new Vector2(desiredWidth + Padding.Horizontal, desiredHeight + Padding.Vertical);
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        var y = Padding.Top;
        var children = Children;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is not UIElement child)
                continue;
            child.Arrange(new Vector2(Padding.Left, y),
                new Vector2(contentSize.X, child.DesiredSize.Y));
            y += child.Height + child.Margin.Vertical + Spacing;
        }
    }
}
