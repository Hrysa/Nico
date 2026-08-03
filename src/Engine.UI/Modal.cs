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
        : base(width, height)
    {
        _theme = theme ?? UITheme.Dark;
        IsOverlay = true;
        Dialog = new Surface(_theme.SurfaceRaised, _theme.BorderStrong,
            MathF.Min(width, dialogWidth), MathF.Min(height, dialogHeight))
        {
            Name = "Dialog"
        };
        AddChild(Dialog);
    }

    /// <inheritdoc/>
    protected override System.Numerics.Vector2 MeasureOverride(System.Numerics.Vector2 availableSize)
    {
        Dialog.Measure(availableSize);
        return availableSize;
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(System.Numerics.Vector2 contentSize)
    {
        var dialogSize = new System.Numerics.Vector2(
            MathF.Min(contentSize.X, Dialog.DesiredSize.X),
            MathF.Min(contentSize.Y, Dialog.DesiredSize.Y));
        Dialog.Arrange(new System.Numerics.Vector2(
            MathF.Max(0f, (contentSize.X - dialogSize.X) / 2f),
            MathF.Max(0f, (contentSize.Y - dialogSize.Y) / 2f)), dialogSize);
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
