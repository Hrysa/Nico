using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// A themed panel with an optional inset border.
/// </summary>
public class Surface : Panel
{
    /// <summary>Gets or sets the border color.</summary>
    public Color BorderColor { get; set; }

    /// <summary>Gets or sets the border thickness in logical pixels.</summary>
    public float BorderThickness { get; set; } = 1f;

    /// <summary>
    /// Creates a themed surface.
    /// </summary>
    /// <param name="width">Surface width.</param>
    /// <param name="height">Surface height.</param>
    /// <param name="backgroundColor">Surface fill color.</param>
    /// <param name="borderColor">Surface border color.</param>
    public Surface(Color backgroundColor, Color borderColor, float width = 0f, float height = 0f)
        : base(backgroundColor, width, height)
    {
        BorderColor = borderColor;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        base.Paint(drawList);
        var thickness = Math.Clamp(BorderThickness, 0f, MathF.Min(Width, Height) / 2f);
        if (thickness <= 0f)
            return;
        drawList.AddRectangle(Left, Top, Right, Top + thickness, BorderColor);
        drawList.AddRectangle(Left, Bottom - thickness, Right, Bottom, BorderColor);
        drawList.AddRectangle(Left, Top + thickness, Left + thickness, Bottom - thickness, BorderColor);
        drawList.AddRectangle(Right - thickness, Top + thickness, Right, Bottom - thickness, BorderColor);
    }
}
