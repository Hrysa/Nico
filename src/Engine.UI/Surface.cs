using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// A themed panel with an optional inset border.
/// </summary>
public class Surface : Panel
{
    private Color _borderColor;
    private float _borderThickness = 1f;

    /// <summary>Gets or sets the border color.</summary>
    public Color BorderColor
    {
        get => _borderColor;
        set { if (!_borderColor.Equals(value)) { _borderColor = value; InvalidateVisual(); } }
    }

    /// <summary>Gets or sets the border thickness in logical pixels.</summary>
    public float BorderThickness
    {
        get => _borderThickness;
        set { if (_borderThickness != value) { _borderThickness = value; InvalidateVisual(); } }
    }

    /// <summary>
    /// Creates a themed surface.
    /// </summary>
    /// <param name="width">Surface width.</param>
    /// <param name="height">Surface height.</param>
    /// <param name="backgroundColor">Optional surface fill color.</param>
    /// <param name="borderColor">Surface border color.</param>
    /// <param name="theme">Theme supplying the default panel corner radius.</param>
    public Surface(Color? backgroundColor, Color borderColor, float width = 0f, float height = 0f, UITheme? theme = null)
        : base(backgroundColor, width, height, theme)
    {
        BorderColor = borderColor;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        var thickness = Math.Clamp(BorderThickness, 0f, MathF.Min(Width, Height) / 2f);
        if (thickness <= 0f || CornerRadius <= 0f || CornerMode != BoxCornerMode.All)
        {
            base.Paint(drawList);
            if (thickness <= 0f)
                return;
            drawList.AddRectangle(Left, Top, Right, Top + thickness, BorderColor);
            drawList.AddRectangle(Left, Bottom - thickness, Right, Bottom, BorderColor);
            drawList.AddRectangle(Left, Top + thickness, Left + thickness, Bottom - thickness, BorderColor);
            drawList.AddRectangle(Right - thickness, Top + thickness, Right, Bottom - thickness, BorderColor);
            return;
        }

        var radius = MathF.Min(CornerRadius, MathF.Min(Width, Height) * 0.5f);
        drawList.AddRoundedRectangle(Left, Top, Right, Bottom, radius, BorderColor);
        if (!PaintBackground || !HasBackgroundColor || Width <= thickness * 2f ||
            Height <= thickness * 2f)
            return;
        drawList.AddRoundedRectangle(
            Left + thickness, Top + thickness, Right - thickness, Bottom - thickness,
            MathF.Max(0f, radius - thickness), BackgroundColor);
    }
}
