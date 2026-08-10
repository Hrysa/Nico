namespace Engine.Graphics;

/// <summary>Identifies how a rasterized glyph's coverage bytes are encoded.</summary>
internal enum GlyphCoverageFormat
{
    /// <summary>One alpha coverage byte per pixel.</summary>
    Grayscale,

    /// <summary>Red, green, and blue subpixel coverage bytes per pixel.</summary>
    RgbSubpixel
}

/// <summary>Stores format-tagged glyph coverage independent of its rasterization backend.</summary>
/// <param name="Format">Coverage encoding.</param>
/// <param name="Pixels">Tightly packed coverage bytes.</param>
internal readonly record struct GlyphCoverage(GlyphCoverageFormat Format, byte[] Pixels);
