using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Captures interaction above the application and hosts a centered dialog surface.
/// </summary>
public class Modal : UIElement
{
    private readonly UITheme _theme;

    /// <summary>Gets the dialog surface hosted by the modal.</summary>
    public Surface Dialog { get; }

    /// <summary>Occurs when the backdrop is clicked.</summary>
    public event Action? DismissRequested;

    /// <summary>
    /// Creates a modal backdrop and centered dialog surface.
    /// </summary>
    /// <param name="width">Backdrop width.</param>
    /// <param name="height">Backdrop height.</param>
    /// <param name="dialogWidth">Dialog width.</param>
    /// <param name="dialogHeight">Dialog height.</param>
    /// <param name="theme">Theme supplying modal colors.</param>
    public Modal(float width, float height, float dialogWidth, float dialogHeight, UITheme? theme = null)
        : base(0f, 0f, width, height)
    {
        _theme = theme ?? UITheme.Dark;
        IsOverlay = true;
        Dialog = new Surface(
            MathF.Max(0f, (width - dialogWidth) / 2f),
            MathF.Max(0f, (height - dialogHeight) / 2f),
            MathF.Min(width, dialogWidth),
            MathF.Min(height, dialogHeight),
            _theme.SurfaceRaised,
            _theme.BorderStrong)
        {
            Name = "Dialog"
        };
        AddChild(Dialog);
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        drawList.AddRectangle(Left, Top, Right, Bottom, Color.Lerp(_theme.Canvas, Color.Black, 0.42f));
    }

    /// <inheritdoc/>
    protected override void OnClick()
    {
        DismissRequested?.Invoke();
        base.OnClick();
    }
}
