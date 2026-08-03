using Engine.Graphics;

namespace Engine.UI;

/// <summary>Paints the visual border box used by compositional controls.</summary>
public class Box : UIElement
{
    private bool _paintBackground = true;
    private float _cornerRadius;

    /// <summary>Gets or sets whether the box paints its background.</summary>
    public bool PaintBackground
    {
        get => _paintBackground;
        set { if (_paintBackground != value) { _paintBackground = value; InvalidateVisual(); } }
    }

    /// <summary>Gets or sets the background corner radius.</summary>
    public float CornerRadius
    {
        get => _cornerRadius;
        set { if (_cornerRadius != value) { _cornerRadius = value; InvalidateVisual(); } }
    }

    /// <summary>Creates a visual box.</summary>
    /// <param name="width">Box width.</param>
    /// <param name="height">Box height.</param>
    public Box(float width = 0f, float height = 0f)
        : base(width, height)
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
