using Engine.Graphics;

namespace Engine.UI;

/// <summary>Paints the visual border box used by compositional controls.</summary>
public class Box : UIElement
{
    /// <summary>Gets or sets whether the box paints its background.</summary>
    public bool PaintBackground { get; set; } = true;

    /// <summary>Gets or sets the background corner radius.</summary>
    public float CornerRadius { get; set; }

    /// <summary>Creates a visual box.</summary>
    /// <param name="x">Local X position.</param>
    /// <param name="y">Local Y position.</param>
    /// <param name="width">Box width.</param>
    /// <param name="height">Box height.</param>
    public Box(float x, float y, float width, float height)
        : base(x, y, width, height)
    {
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        if (!PaintBackground)
            return;
        if (CornerRadius > 0f)
            drawList.AddRoundedRectangle(Left, Top, Right, Bottom, CornerRadius, BackgroundColor);
        else
            base.Paint(drawList);
    }
}
