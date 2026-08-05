using System.Numerics;

namespace Engine.Graphics;

public readonly struct Color : IEquatable<Color>
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

    /// <summary>Compares two colors without boxing.</summary>
    /// <param name="other">Color to compare.</param>
    /// <returns>True when all linear RGB components match.</returns>
    public bool Equals(Color other) => Rgb.Equals(other.Rgb);

    /// <summary>Compares this color with an object.</summary>
    /// <param name="obj">Object to compare.</param>
    /// <returns>True when the object is an equal color.</returns>
    public override bool Equals(object? obj) => obj is Color other && Equals(other);

    /// <summary>Returns an allocation-free hash for the linear RGB components.</summary>
    /// <returns>Hash code for this color.</returns>
    public override int GetHashCode() => Rgb.GetHashCode();

    /// <summary>Tests two colors for equality.</summary>
    /// <param name="left">Left color.</param>
    /// <param name="right">Right color.</param>
    /// <returns>True when the colors match.</returns>
    public static bool operator ==(Color left, Color right) => left.Equals(right);

    /// <summary>Tests two colors for inequality.</summary>
    /// <param name="left">Left color.</param>
    /// <param name="right">Right color.</param>
    /// <returns>True when the colors differ.</returns>
    public static bool operator !=(Color left, Color right) => !left.Equals(right);

    /// <summary>
    /// Converts display-referred sRGB bytes into linear color components suitable for shader output.
    /// </summary>
    /// <param name="red">sRGB red byte.</param>
    /// <param name="green">sRGB green byte.</param>
    /// <param name="blue">sRGB blue byte.</param>
    /// <returns>A linear color that displays as the supplied sRGB value on an sRGB target.</returns>
    public static Color FromSrgb(byte red, byte green, byte blue)
    {
        return new Color(ToLinear(red), ToLinear(green), ToLinear(blue));
    }

    /// <summary>Converts one sRGB byte to a linear floating-point component.</summary>
    /// <param name="value">sRGB byte.</param>
    /// <returns>Linear component in the range zero through one.</returns>
    private static float ToLinear(byte value)
    {
        var srgb = value / 255f;
        return srgb <= 0.04045f
            ? srgb / 12.92f
            : MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);
    }

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

}
