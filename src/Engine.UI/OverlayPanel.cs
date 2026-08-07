using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Layers every child into the same content rectangle.</summary>
public sealed class OverlayPanel : Panel
{
    /// <summary>Creates an overlay panel.</summary>
    /// <param name="backgroundColor">Optional painted background; null creates a layout-only panel.</param>
    public OverlayPanel(Color? backgroundColor = null)
        : base(backgroundColor ?? Color.Black)
    {
        PaintBackground = backgroundColor.HasValue;
    }

    /// <summary>Measures all layers and returns the largest intrinsic extent.</summary>
    /// <param name="availableSize">Space offered by the parent.</param>
    /// <returns>Largest desired child size including padding.</returns>
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

    /// <summary>Arranges every layer over the complete content rectangle.</summary>
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
