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

/// <summary>Paints the visual border box used by compositional controls.</summary>
public class Box : UIElement
{
    private float _cornerRadius;
    private BoxVisualStateMode _visualStateMode;

    /// <summary>Gets or sets the background corner radius.</summary>
    public float CornerRadius
    {
        get => _cornerRadius;
        set { if (_cornerRadius != value) { _cornerRadius = value; InvalidateVisual(); } }
    }

    /// <summary>Gets or sets whether interaction state may alter this box's presentation.</summary>
    public BoxVisualStateMode VisualStateMode
    {
        get => _visualStateMode;
        set
        {
            if (_visualStateMode == value)
                return;
            _visualStateMode = value;
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
        if (CornerRadius > 0f)
            drawList.AddRoundedRectangle(Left, Top, Right, Bottom, CornerRadius, BackgroundColor);
        else
            base.Paint(drawList);
    }
}
