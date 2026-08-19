using Engine.Graphics;

namespace Engine.UI;

/// <summary>Identifies a semantic typography role resolved by a UI theme.</summary>
public enum UITextRole
{
    /// <summary>Primary body text.</summary>
    Body,
    /// <summary>Secondary body text.</summary>
    Secondary,
    /// <summary>Muted body text.</summary>
    Muted,
    /// <summary>Compact primary caption text.</summary>
    Caption,
    /// <summary>Compact secondary caption text.</summary>
    SecondaryCaption,
    /// <summary>Compact muted supporting text.</summary>
    MutedCaption,
    /// <summary>Panel-heading text.</summary>
    PanelTitle,
    /// <summary>Primary-color panel-heading text.</summary>
    PrimaryPanelTitle,
    /// <summary>Large primary dialog-heading text.</summary>
    DialogTitle,
    /// <summary>Compact accent-colored supporting text.</summary>
    AccentCaption,
    /// <summary>Compact validation or error text.</summary>
    Error
}

/// <summary>Stores reusable renderer-independent typography properties.</summary>
/// <param name="FontSize">Font height in logical pixels.</param>
/// <param name="ForegroundColor">Text foreground color.</param>
public readonly record struct UITextStyle(float FontSize, Color ForegroundColor);
