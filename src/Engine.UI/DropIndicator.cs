using Engine.Graphics;

namespace Engine.UI;

/// <summary>Draws a non-interactive outline around the currently accepted drop region.</summary>
public sealed class DropIndicator : UIElement
{
    /// <summary>Gets or sets outline thickness in logical pixels.</summary>
    public float BorderThickness { get; set; } = 2f;

    /// <summary>Creates a drop indicator using the supplied accent color.</summary>
    /// <param name="color">Outline color.</param>
    public DropIndicator(Color color)
    {
        ForegroundColor = color;
        IsOverlay = true;
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        var thickness = Math.Clamp(BorderThickness, 1f, MathF.Min(Width, Height) * 0.5f);
        drawList.AddRectangle(Left, Top, Right, Top + thickness, ForegroundColor);
        drawList.AddRectangle(Left, Bottom - thickness, Right, Bottom, ForegroundColor);
        drawList.AddRectangle(Left, Top + thickness, Left + thickness, Bottom - thickness, ForegroundColor);
        drawList.AddRectangle(Right - thickness, Top + thickness, Right, Bottom - thickness, ForegroundColor);
    }
}
