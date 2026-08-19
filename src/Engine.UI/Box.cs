using Engine.Graphics;

namespace Engine.UI;

/// <summary>Controls whether a box paints interaction-driven visual state.</summary>
public enum BoxVisualStateMode
{
    /// <summary>Allows controls to paint hover, pressed, selected, or checked state.</summary>
    Interactive,

    /// <summary>Keeps the box visually static while preserving all component behavior.</summary>
    Static
}

/// <summary>Selects individual box corners that retain rounding.</summary>
[Flags]
public enum BoxCornerMode
{
    /// <summary>Squares all four corners.</summary>
    None = 0,

    /// <summary>Rounds the top-left corner.</summary>
    TopLeft = 1 << 0,

    /// <summary>Rounds the top-right corner.</summary>
    TopRight = 1 << 1,

    /// <summary>Rounds the bottom-right corner.</summary>
    BottomRight = 1 << 2,

    /// <summary>Rounds the bottom-left corner.</summary>
    BottomLeft = 1 << 3,

    /// <summary>Rounds only the top-left and top-right corners.</summary>
    Top = TopLeft | TopRight,

    /// <summary>Rounds only the bottom-left and bottom-right corners.</summary>
    Bottom = BottomLeft | BottomRight,

    /// <summary>Rounds all four corners.</summary>
    All = Top | Bottom
}

/// <summary>Paints the visual border box used by compositional controls.</summary>
public class Box : UIElement
{
    /// <summary>Gets or sets the background corner radius.</summary>
    public float CornerRadius
    {
        get;
        set { if (field != value) { field = value; InvalidateVisual(); } }
    }

    /// <summary>Gets or sets which edge retains rounded background corners.</summary>
    public BoxCornerMode CornerMode
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            InvalidateVisual();
        }
    } = BoxCornerMode.All;

    /// <summary>Gets or sets whether interaction state may alter this box's presentation.</summary>
    public BoxVisualStateMode VisualStateMode
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            OnVisualStateModeChanged();
            InvalidateVisual();
        }
    }

    /// <summary>Allows derived controls to reset presentation when the visual-state policy changes.</summary>
    protected virtual void OnVisualStateModeChanged()
    {
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
        if (!PaintBackground || !HasBackgroundColor)
            return;
        PaintBox(drawList, BackgroundColor);
    }

    /// <summary>Paints this box shape with the configured radius and rounded edges.</summary>
    /// <param name="drawList">Destination draw list.</param>
    /// <param name="color">Fill color.</param>
    protected void PaintBox(UIDrawList drawList, Color color)
    {
        if (CornerRadius <= 0f)
        {
            drawList.AddRectangle(Left, Top, Right, Bottom, color);
            return;
        }
        var radius = MathF.Min(CornerRadius, MathF.Min(Width, Height) * 0.5f);
        if (radius <= 0f || CornerMode == BoxCornerMode.None)
        {
            drawList.AddRectangle(Left, Top, Right, Bottom, color);
            return;
        }
        drawList.AddRoundedRectangle(Left, Top, Right, Bottom, radius, color);
        if ((CornerMode & BoxCornerMode.TopLeft) == 0)
            drawList.AddRectangle(Left, Top, Left + radius, Top + radius, color);
        if ((CornerMode & BoxCornerMode.TopRight) == 0)
            drawList.AddRectangle(Right - radius, Top, Right, Top + radius, color);
        if ((CornerMode & BoxCornerMode.BottomRight) == 0)
            drawList.AddRectangle(Right - radius, Bottom - radius, Right, Bottom, color);
        if ((CornerMode & BoxCornerMode.BottomLeft) == 0)
            drawList.AddRectangle(Left, Bottom - radius, Left + radius, Bottom, color);
    }
}
