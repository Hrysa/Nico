namespace Engine.UI;

/// <summary>A labeled two-state checkbox.</summary>
public sealed class CheckBox : ToggleButton
{
    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => base.GetSemanticInfo() with
    {
        Role = UISemanticRole.CheckBox
    };

    /// <summary>Creates a checkbox.</summary>
    /// <param name="width">Control width.</param>
    /// <param name="height">Control height.</param>
    /// <param name="label">Checkbox label.</param>
    /// <param name="theme">Theme supplying visual states.</param>
    public CheckBox(float width, float height, string label, UITheme? theme = null)
        : base(width, height, label, theme)
    {
    }
}
