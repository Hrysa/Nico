using System.Numerics;

namespace Engine.Graphics;

public readonly struct Color
{
    public Vector3 Rgb { get; }

    public float R => Rgb.X;
    public float G => Rgb.Y;
    public float B => Rgb.Z;

    public Color(float r, float g, float b)
    {
        Rgb = new Vector3(r, g, b);
    }

    public Color(Vector3 rgb)
    {
        Rgb = rgb;
    }

    public static implicit operator Vector3(Color c) => c.Rgb;
    public static implicit operator Color(Vector3 v) => new(v);

    public static Color Lerp(Color a, Color b, float t)
        => new(Vector3.Lerp(a.Rgb, b.Rgb, t));

    public override string ToString() => $"Color({R:F2}, {G:F2}, {B:F2})";

    // ── Common colors ──────────────────────────────────────────

    public static readonly Color Black   = new(0.00f, 0.00f, 0.00f); // #000
    public static readonly Color White   = new(1.00f, 1.00f, 1.00f); // #FFF
    public static readonly Color Red     = new(1.00f, 0.00f, 0.00f); // #F00
    public static readonly Color Green   = new(0.00f, 1.00f, 0.00f); // #0F0
    public static readonly Color Blue    = new(0.00f, 0.00f, 1.00f); // #00F
    public static readonly Color Yellow  = new(1.00f, 1.00f, 0.00f); // #FF0
    public static readonly Color Cyan    = new(0.00f, 1.00f, 1.00f); // #0FF
    public static readonly Color Magenta = new(1.00f, 0.00f, 1.00f); // #F0F
    public static readonly Color Gray    = new(0.50f, 0.50f, 0.50f); // #888

    // ── Editor theme ───────────────────────────────────────────

    public static readonly Color EditorBackground     = new(0.15f, 0.15f, 0.15f); // #262626
    public static readonly Color EditorMenuBar        = Red; // #333338
    public static readonly Color EditorStatusBar      = Green; // #2E2E33
    public static readonly Color EditorPanel          = new(0.17f, 0.17f, 0.19f); // #2C2C30
    public static readonly Color EditorPanelHeader    = new(0.22f, 0.22f, 0.25f); // #383840
    public static readonly Color EditorSeparator      = new(0.30f, 0.30f, 0.32f); // #4D4D52
    public static readonly Color EditorViewport       = Black; // #1A1A1F
    public static readonly Color EditorViewportBorder = new(0.35f, 0.35f, 0.38f); // #595961
}
