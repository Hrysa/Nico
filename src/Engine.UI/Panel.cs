using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// A rectangular panel container. Can hold child UI elements.
/// </summary>
public class Panel : UIElement
{
    private bool _paintBackground = true;

    /// <summary>Gets or sets whether the panel emits a background rectangle.</summary>
    public bool PaintBackground
    {
        get => _paintBackground;
        set { if (_paintBackground != value) { _paintBackground = value; InvalidateVisual(); } }
    }

    /// <summary>
    /// Creates a new Panel at the specified position and size.
    /// </summary>
    /// <param name="width">The panel width.</param>
    /// <param name="height">The panel height.</param>
    /// <param name="backgroundColor">The panel background color.</param>
    public Panel(Color backgroundColor, float width = 0f, float height = 0f)
        : base(width, height)
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
