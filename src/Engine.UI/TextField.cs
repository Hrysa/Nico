namespace Engine.UI;

/// <summary>A single-line specialization of <see cref="TextBox"/>.</summary>
public class TextField : TextBox
{
    /// <summary>Creates a single-line text field.</summary>
    /// <param name="width">Field width.</param>
    /// <param name="height">Field height.</param>
    /// <param name="theme">Theme supplying colors and typography.</param>
    public TextField(float width, float height, UITheme? theme = null)
        : base(width, height, false, theme)
    {
    }
}
