using Engine.Graphics;

namespace Engine.UI;

/// <summary>A compact toggle rendered as a track and sliding knob.</summary>
public sealed class ToggleSwitch : ToggleButton
{
    /// <inheritdoc/>
    public override UISemanticInfo GetSemanticInfo() => base.GetSemanticInfo() with
    {
        Role = UISemanticRole.Switch
    };

    private readonly UITheme _theme;

    /// <summary>Creates a toggle switch.</summary>
    /// <param name="width">Switch width.</param>
    /// <param name="height">Switch height.</param>
    /// <param name="theme">Theme supplying colors.</param>
    public ToggleSwitch(float width = 42f, float height = 22f, UITheme? theme = null)
        : base(width, height, string.Empty, theme)
    {
        _theme = theme ?? UITheme.Dark;
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        var radius = Height / 2f;
        drawList.AddRoundedRectangle(Left, Top, Right, Bottom, radius,
            IsChecked ? _theme.AccentPressed : _theme.SurfaceRaised);
        var knobSize = MathF.Max(0f, Height - 4f);
        var knobLeft = IsChecked ? Right - knobSize - 2f : Left + 2f;
        drawList.AddEllipse(knobLeft, Top + 2f, knobLeft + knobSize, Top + 2f + knobSize,
            IsChecked ? _theme.Accent : _theme.TextSecondary);
    }
}
