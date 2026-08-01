using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// A rectangular panel container. Can hold child UI elements.
/// </summary>
public class Panel : UIElement
{
    /// <summary>Gets or sets whether the panel emits a background rectangle.</summary>
    public bool PaintBackground { get; set; } = true;

    /// <summary>
    /// Creates a new Panel at the specified position and size.
    /// </summary>
    /// <param name="x">The X position (left edge).</param>
    /// <param name="y">The Y position (top edge).</param>
    /// <param name="width">The panel width.</param>
    /// <param name="height">The panel height.</param>
    /// <param name="backgroundColor">The panel background color.</param>
    public Panel(float x, float y, float width, float height, Color backgroundColor)
        : base(x, y, width, height)
    {
        BackgroundColor = backgroundColor;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        if (PaintBackground)
            base.Paint(drawList);
    }
}
