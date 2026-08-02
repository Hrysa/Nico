using System.Numerics;

namespace Engine.UI;

/// <summary>Displays the item currently moving during a drag gesture.</summary>
public sealed class DragPreview : ContentControl
{
    /// <summary>Gets the label describing the dragged item.</summary>
    public Label ItemLabel { get; }

    /// <summary>Creates a floating, non-interactive drag preview.</summary>
    /// <param name="position">Initial screen position.</param>
    /// <param name="text">Dragged item name.</param>
    /// <param name="theme">Theme supplying preview visuals.</param>
    public DragPreview(Vector2 position, string text, UITheme? theme = null)
        : base(position.X, position.Y, 0f, 28f)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        IsOverlay = true;
        IsHitTestVisible = false;
        Padding = new Thickness(8f, 0f);
        CornerRadius = 4f;
        BackgroundColor = resolvedTheme.SurfaceRaised;
        ForegroundColor = resolvedTheme.TextPrimary;
        ItemLabel = new Label(0f, 0f, 0f, 0f, text)
        {
            FontSize = resolvedTheme.FontSize,
            ForegroundColor = resolvedTheme.TextPrimary,
            BackgroundColor = resolvedTheme.SurfaceRaised,
            PaddingLeft = 0f,
            IsHitTestVisible = false
        };
        Width = MathF.Ceiling(ItemLabel.MeasureTextWidth() + Padding.Horizontal);
        Content = ItemLabel;
    }
}
