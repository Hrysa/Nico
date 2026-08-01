using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Displays text using the built-in renderer-independent pixel font.
/// </summary>
public class Label : UIElement
{
    /// <summary>Gets or sets the displayed text.</summary>
    public string Text { get; set; }

    /// <summary>Gets or sets the size of one font pixel.</summary>
    public float PixelSize { get; set; } = 2f;

    /// <summary>Gets or sets the left text inset.</summary>
    public float PaddingLeft { get; set; } = 4f;

    /// <summary>Gets or sets whether the label paints its background.</summary>
    public bool PaintBackground { get; set; }

    /// <summary>
    /// Creates a text label.
    /// </summary>
    /// <param name="x">Local X position.</param>
    /// <param name="y">Local Y position.</param>
    /// <param name="width">Label width.</param>
    /// <param name="height">Label height.</param>
    /// <param name="text">Displayed text.</param>
    public Label(float x, float y, float width, float height, string text)
        : base(x, y, width, height)
    {
        Text = text;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        if (PaintBackground)
            base.Paint(drawList);

        var textHeight = 7f * PixelSize;
        drawList.AddText(Text, Left + PaddingLeft, Top + MathF.Max(0f, (Height - textHeight) / 2f),
            PixelSize, ForegroundColor);
    }
}
