using Engine.Graphics;

namespace Engine.UI;

/// <summary>
/// Defines the visual tokens shared by the engine's UI component pack.
/// </summary>
public sealed record UITheme
{
    /// <summary>Gets the default modern dark editor theme.</summary>
    public static UITheme Dark { get; } = new();

    /// <summary>Gets a maximum-contrast dark theme for low-vision accessibility.</summary>
    public static UITheme HighContrast { get; } = new()
    {
        Canvas = Color.Black,
        Surface = Color.Black,
        SurfaceRaised = Color.Black,
        Field = Color.Black,
        SurfaceHover = Color.FromSrgb(0x1A, 0x1A, 0x1A),
        SurfacePressed = Color.FromSrgb(0x33, 0x33, 0x33),
        Viewport = Color.Black,
        Border = Color.White,
        BorderStrong = Color.White,
        TextPrimary = Color.White,
        TextSecondary = Color.White,
        TextMuted = Color.FromSrgb(0xD0, 0xD0, 0xD0),
        Accent = Color.Yellow,
        AccentHover = Color.Cyan,
        AccentPressed = Color.Yellow,
        Error = Color.FromSrgb(0xFF, 0x66, 0x66)
    };

    /// <summary>Gets the application background color.</summary>
    public Color Canvas { get; init; } = Color.FromSrgb(0x12, 0x13, 0x14);

    /// <summary>Gets the primary surface color.</summary>
    public Color Surface { get; init; } = Color.FromSrgb(0x12, 0x13, 0x14);

    /// <summary>Gets the raised surface color.</summary>
    public Color SurfaceRaised { get; init; } = Color.FromSrgb(0x29, 0x29, 0x29);

    /// <summary>Gets the recessed input-field color.</summary>
    public Color Field { get; init; } = Color.FromSrgb(0x1C, 0x1C, 0x1C);

    /// <summary>Gets the hover surface color.</summary>
    public Color SurfaceHover { get; init; } = Color.FromSrgb(0x23, 0x24, 0x25);

    /// <summary>Gets the pressed surface color.</summary>
    public Color SurfacePressed { get; init; } = Color.FromSrgb(0x2C, 0x2D, 0x2E);

    /// <summary>Gets the viewport background color.</summary>
    public Color Viewport { get; init; } = Color.FromSrgb(0x12, 0x13, 0x14);

    /// <summary>Gets the subtle border and separator color.</summary>
    public Color Border { get; init; } = Color.FromSrgb(0x14, 0x14, 0x14);

    /// <summary>Gets the strong border color.</summary>
    public Color BorderStrong { get; init; } = Color.FromSrgb(0x4A, 0x4A, 0x4A);

    /// <summary>Gets the primary text color.</summary>
    public Color TextPrimary { get; init; } = Color.FromSrgb(0xC9, 0xC9, 0xC9);

    /// <summary>Gets the secondary text color.</summary>
    public Color TextSecondary { get; init; } = Color.FromSrgb(0xA0, 0xA0, 0xA0);

    /// <summary>Gets the muted text color.</summary>
    public Color TextMuted { get; init; } = Color.FromSrgb(0x70, 0x70, 0x70);

    /// <summary>Gets the primary accent color.</summary>
    public Color Accent { get; init; } = Color.FromSrgb(0x68, 0x9C, 0xF8);

    /// <summary>Gets the hovered accent color.</summary>
    public Color AccentHover { get; init; } = Color.FromSrgb(0x78, 0xAA, 0xFF);

    /// <summary>Gets the pressed accent color.</summary>
    public Color AccentPressed { get; init; } = Color.FromSrgb(0x4C, 0x75, 0xC7);

    /// <summary>Gets the semantic color used for invalid controls and error text.</summary>
    public Color Error { get; init; } = Color.FromSrgb(0xE5, 0x73, 0x73);

    /// <summary>Gets the standard body font size.</summary>
    public float FontSize { get; init; } = 15.5f;

    /// <summary>Gets the compact caption font size.</summary>
    public float CaptionFontSize { get; init; } = 14f;

    /// <summary>Gets the font size used by panel titles.</summary>
    public float PanelTitleFontSize { get; init; } = 16f;

    /// <summary>Gets the standard height of every docked panel header.</summary>
    public float PanelHeaderHeight { get; init; } = 32f;

    /// <summary>Gets the horizontal inset used by every docked panel title.</summary>
    public float PanelHeaderPadding { get; init; } = 10f;

    /// <summary>Gets the standard height of hierarchy and filesystem rows.</summary>
    public float ItemRowHeight { get; init; } = 30f;

    /// <summary>Gets the standard horizontal inset of hierarchy and filesystem rows.</summary>
    public float ItemRowPadding { get; init; } = 5f;

    /// <summary>Gets the additional horizontal inset for each hierarchy depth.</summary>
    public float TreeIndent { get; init; } = 14f;

    /// <summary>Gets the normal control height.</summary>
    public float ControlHeight { get; init; } = 30f;

    /// <summary>Gets the standard small spacing unit.</summary>
    public float SpacingSmall { get; init; } = 5f;

    /// <summary>Gets the standard spacing unit.</summary>
    public float Spacing { get; init; } = 10f;
}
