using Engine.Graphics;

namespace Engine.UI;

/// <summary>Identifies how an overlay popup is positioned relative to its owner or pointer.</summary>
public enum PopupPlacement
{
    /// <summary>Use the popup's explicit position.</summary>
    Absolute,
    /// <summary>Place below the owner.</summary>
    Below,
    /// <summary>Place above the owner.</summary>
    Above,
    /// <summary>Place to the owner's right.</summary>
    Right,
    /// <summary>Place to the owner's left.</summary>
    Left,
    /// <summary>Place at the pointer.</summary>
    Pointer
}

/// <summary>A floating owned surface with shared open and dismissal semantics.</summary>
public class Popup : Surface
{
    /// <summary>Gets or sets the element that opened this popup.</summary>
    public UIElement? Owner { get; set; }

    /// <summary>Gets or sets whether outside presses are ignored.</summary>
    public bool StaysOpen { get; set; }

    /// <summary>Gets or sets whether the overlay host clamps this popup inside its arranged bounds.</summary>
    public bool ConstrainToOverlayBounds { get; set; } = true;

    /// <summary>Gets or sets the preferred placement relative to the owner.</summary>
    public PopupPlacement Placement { get; set; } = PopupPlacement.Below;

    /// <summary>Gets or sets the logical offset applied after placement.</summary>
    public System.Numerics.Vector2 PlacementOffset { get; set; }

    /// <summary>Gets the placement selected after edge-aware flipping.</summary>
    public PopupPlacement ActualPlacement { get; internal set; } = PopupPlacement.Absolute;

    /// <summary>Gets whether the popup is currently open.</summary>
    public bool IsOpen => IsVisible;

    /// <summary>Occurs after an open popup closes.</summary>
    public event Action? Closed;

    /// <summary>Creates a popup surface.</summary>
    /// <param name="backgroundColor">Surface background.</param>
    /// <param name="borderColor">Border color.</param>
    /// <param name="width">Popup width.</param>
    /// <param name="height">Popup height.</param>
    public Popup(Color backgroundColor, Color borderColor, float width = 0f, float height = 0f)
        : base(backgroundColor, borderColor, width, height)
    {
        IsOverlay = true;
    }

    /// <summary>Opens the popup.</summary>
    public void Open() => IsVisible = true;

    /// <summary>Closes the popup and raises <see cref="Closed"/> once.</summary>
    public void Close()
    {
        if (!IsVisible)
            return;
        IsVisible = false;
        Closed?.Invoke();
    }
}
