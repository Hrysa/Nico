using Engine.Graphics;

namespace Engine.UI;

/// <summary>Identifies the common interaction state resolved by retained controls.</summary>
[Flags]
public enum UIInteractionState
{
    /// <summary>Idle enabled state.</summary>
    Normal = 0,

    /// <summary>Pointer is inside the control.</summary>
    Hovered = 1 << 0,

    /// <summary>Primary interaction is actively pressed.</summary>
    Pressed = 1 << 1,

    /// <summary>Control represents a persistent selected or checked value.</summary>
    Selected = 1 << 2,

    /// <summary>Control is unavailable for interaction.</summary>
    Disabled = 1 << 3
}

/// <summary>Stores and resolves the shared background colors of an interactive control.</summary>
/// <param name="Normal">Idle enabled color.</param>
/// <param name="Hovered">Pointer-hover color.</param>
/// <param name="Pressed">Active press color.</param>
/// <param name="Selected">Idle selected or checked color.</param>
/// <param name="SelectedHovered">Hovered selected or checked color.</param>
/// <param name="Disabled">Disabled color.</param>
public readonly record struct UIInteractionColors(
    Color Normal,
    Color Hovered,
    Color Pressed,
    Color Selected,
    Color SelectedHovered,
    Color Disabled)
{
    /// <summary>Resolves one color using the common interaction-state priority.</summary>
    /// <param name="state">Combined retained interaction state.</param>
    /// <returns>Color for the effective visual state.</returns>
    public Color Resolve(UIInteractionState state)
    {
        if ((state & UIInteractionState.Disabled) != 0)
            return Disabled;
        if ((state & UIInteractionState.Pressed) != 0)
            return Pressed;
        if ((state & UIInteractionState.Selected) != 0)
        {
            return (state & UIInteractionState.Hovered) != 0
                ? SelectedHovered
                : Selected;
        }
        return (state & UIInteractionState.Hovered) != 0 ? Hovered : Normal;
    }
}

/// <summary>Stores the complete reusable visual style of a themed button.</summary>
/// <param name="InteractionColors">Background palette for every interaction state.</param>
/// <param name="ForegroundColor">Button content foreground color.</param>
/// <param name="PaintNormalBackground">Whether the idle background is visible.</param>
/// <param name="CornerRadius">Background corner radius.</param>
public readonly record struct UIButtonVisualStyle(
    UIInteractionColors InteractionColors,
    Color ForegroundColor,
    bool PaintNormalBackground,
    float CornerRadius);
