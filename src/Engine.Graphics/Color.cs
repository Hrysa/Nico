using System.Numerics;

namespace Engine.Graphics;

/// <summary>Represents a linear RGB color.</summary>
public readonly struct Color : IEquatable<Color>
{
    /// <summary>Gets the RGB components.</summary>
    public Vector3 Rgb { get; }

    /// <summary>Gets the red component.</summary>
    public float R => Rgb.X;
    /// <summary>Gets the green component.</summary>
    public float G => Rgb.Y;
    /// <summary>Gets the blue component.</summary>
    public float B => Rgb.Z;

    /// <summary>Creates a color from separate RGB components.</summary>
    /// <param name="r">Red component.</param>
    /// <param name="g">Green component.</param>
    /// <param name="b">Blue component.</param>
    public Color(float r, float g, float b)
    {
        Rgb = new Vector3(r, g, b);
    }

    /// <summary>Creates a color from an RGB vector.</summary>
    /// <param name="rgb">RGB components.</param>
    public Color(Vector3 rgb)
    {
        Rgb = rgb;
    }

    /// <summary>Converts a color to its RGB vector.</summary>
    /// <param name="c">Color to convert.</param>
    public static implicit operator Vector3(Color c) => c.Rgb;
    /// <summary>Converts an RGB vector to a color.</summary>
    /// <param name="v">RGB vector to convert.</param>
    public static implicit operator Color(Vector3 v) => new(v);

    /// <summary>Linearly interpolates between two colors.</summary>
    /// <param name="a">Start color.</param>
    /// <param name="b">End color.</param>
    /// <param name="t">Interpolation amount.</param>
    /// <returns>The interpolated color.</returns>
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

    /// <summary>Returns a readable representation of this color.</summary>
    /// <returns>The formatted color.</returns>
    public override string ToString() => $"Color({R:F2}, {G:F2}, {B:F2})";

    // ── Common colors ──────────────────────────────────────────

    /// <summary>Black.</summary>
    public static readonly Color Black   = new(0.00f, 0.00f, 0.00f); // #000
    /// <summary>White.</summary>
    public static readonly Color White   = new(1.00f, 1.00f, 1.00f); // #FFF
    /// <summary>Red.</summary>
    public static readonly Color Red     = new(1.00f, 0.00f, 0.00f); // #F00
    /// <summary>Green.</summary>
    public static readonly Color Green   = new(0.00f, 1.00f, 0.00f); // #0F0
    /// <summary>Blue.</summary>
    public static readonly Color Blue    = new(0.00f, 0.00f, 1.00f); // #00F
    /// <summary>Yellow.</summary>
    public static readonly Color Yellow  = new(1.00f, 1.00f, 0.00f); // #FF0
    /// <summary>Cyan.</summary>
    public static readonly Color Cyan    = new(0.00f, 1.00f, 1.00f); // #0FF
    /// <summary>Magenta.</summary>
    public static readonly Color Magenta = new(1.00f, 0.00f, 1.00f); // #F0F
    /// <summary>Middle gray.</summary>
    public static readonly Color Gray    = new(0.50f, 0.50f, 0.50f); // #888

}
