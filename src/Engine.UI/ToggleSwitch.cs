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

    private readonly UIInteractionColors _trackColors;
    private readonly UIInteractionColors _knobColors;

    /// <summary>Creates a toggle switch.</summary>
    /// <param name="width">Switch width.</param>
    /// <param name="height">Switch height.</param>
    /// <param name="theme">Theme supplying colors.</param>
    public ToggleSwitch(float width = 42f, float height = 22f, UITheme? theme = null)
        : base(width, height, string.Empty, theme)
    {
        var resolvedTheme = theme ?? UITheme.Dark;
        _trackColors = resolvedTheme.GetToggleSwitchTrackColors();
        _knobColors = resolvedTheme.GetToggleSwitchKnobColors();
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        var radius = Height / 2f;
        var state = GetInteractionState(IsChecked);
        drawList.AddRoundedRectangle(Left, Top, Right, Bottom, radius,
            _trackColors.Resolve(state));
        var knobSize = MathF.Max(0f, Height - 4f);
        var knobLeft = IsChecked ? Right - knobSize - 2f : Left + 2f;
        drawList.AddEllipse(knobLeft, Top + 2f, knobLeft + knobSize, Top + 2f + knobSize,
            _knobColors.Resolve(state));
    }
}
