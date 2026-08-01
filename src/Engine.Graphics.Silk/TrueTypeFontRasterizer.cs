using System.Reflection;
using System.Text;
using static StbTrueTypeSharp.StbTrueType;

namespace Engine.Graphics;

/// <summary>
/// Rasterizes cached Inter glyphs with stb_truetype and converts coverage runs to UI vertices.
/// </summary>
internal unsafe sealed class TrueTypeFontRasterizer : IDisposable
{
    private readonly stbtt_fontinfo _font;
    private readonly Dictionary<(int Codepoint, int PixelHeight), RasterizedGlyph> _glyphs = [];
    private bool _disposed;

    /// <summary>Loads the embedded Inter font.</summary>
    internal TrueTypeFontRasterizer()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Inter-Regular.ttf")
            ?? throw new InvalidOperationException("Embedded Inter font was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        _font = CreateFont(memory.ToArray(), 0)
            ?? throw new InvalidOperationException("Embedded Inter font could not be initialized.");
    }

    /// <summary>Appends colored geometry for one semantic text command.</summary>
    /// <param name="vertices">Destination vertex collection.</param>
    /// <param name="command">Text command.</param>
    /// <param name="framebufferScale">Physical framebuffer pixels per logical UI pixel.</param>
    internal void AppendVertices(List<Vertex> vertices, UIDrawCommand command, float framebufferScale)
    {
        framebufferScale = MathF.Max(1f, framebufferScale);
        var pixelHeight = Math.Max(1, (int)MathF.Round(command.FontPixelHeight * framebufferScale));
        var scale = stbtt_ScaleForPixelHeight(_font, pixelHeight);
        int ascent;
        stbtt_GetFontVMetrics(_font, &ascent, null, null);
        var baseline = command.Top + ascent * scale / framebufferScale;
        var cursor = command.Left;
        var previousCodepoint = -1;

        foreach (var rune in command.Text.EnumerateRunes())
        {
            var codepoint = rune.Value;
            if (previousCodepoint >= 0)
                cursor += stbtt_GetCodepointKernAdvance(_font, previousCodepoint, codepoint)
                    * scale / framebufferScale;

            var glyph = GetGlyph(codepoint, pixelHeight, scale);
            AppendGlyph(vertices, glyph, cursor + glyph.XOffset / framebufferScale,
                baseline + glyph.YOffset / framebufferScale,
                framebufferScale, command.Color, command.BackgroundColor);
            cursor += glyph.Advance / framebufferScale;
            previousCodepoint = codepoint;
        }
    }

    /// <summary>Releases unmanaged font data.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _font.Dispose();
    }

    /// <summary>Gets or creates one cached glyph bitmap.</summary>
    /// <param name="codepoint">Unicode codepoint.</param>
    /// <param name="pixelHeight">Rounded font height.</param>
    /// <param name="scale">Font scale for this height.</param>
    /// <returns>Rasterized glyph metrics and coverage.</returns>
    private RasterizedGlyph GetGlyph(int codepoint, int pixelHeight, float scale)
    {
        var key = (codepoint, pixelHeight);
        if (_glyphs.TryGetValue(key, out var cached))
            return cached;

        int width;
        int height;
        int xOffset;
        int yOffset;
        var bitmap = stbtt_GetCodepointBitmap(
            _font, scale, scale, codepoint, &width, &height, &xOffset, &yOffset);
        var coverage = new byte[Math.Max(0, width * height)];
        if (bitmap != null && coverage.Length > 0)
        {
            new ReadOnlySpan<byte>(bitmap, coverage.Length).CopyTo(coverage);
            stbtt_FreeBitmap(bitmap, null);
        }

        int advance;
        stbtt_GetCodepointHMetrics(_font, codepoint, &advance, null);
        var glyph = new RasterizedGlyph(width, height, xOffset, yOffset, advance * scale, coverage);
        _glyphs.Add(key, glyph);
        return glyph;
    }

    /// <summary>Appends horizontally merged coverage runs for one glyph.</summary>
    /// <param name="vertices">Destination vertices.</param>
    /// <param name="glyph">Rasterized glyph.</param>
    /// <param name="left">Glyph bitmap left edge.</param>
    /// <param name="top">Glyph bitmap top edge.</param>
    /// <param name="framebufferScale">Physical pixels per logical pixel.</param>
    /// <param name="foreground">Text color.</param>
    /// <param name="background">Background color.</param>
    private static void AppendGlyph(
        List<Vertex> vertices,
        RasterizedGlyph glyph,
        float left,
        float top,
        float framebufferScale,
        Color foreground,
        Color background)
    {
        var logicalPixel = 1f / framebufferScale;
        for (var row = 0; row < glyph.Height; row++)
        {
            var column = 0;
            while (column < glyph.Width)
            {
                var coverage = Quantize(glyph.Coverage[row * glyph.Width + column]);
                if (coverage == 0)
                {
                    column++;
                    continue;
                }

                var start = column++;
                while (column < glyph.Width
                    && Quantize(glyph.Coverage[row * glyph.Width + column]) == coverage)
                    column++;

                var color = Color.Lerp(background, foreground, coverage / 15f);
                AppendRectangle(vertices,
                    left + start * logicalPixel,
                    top + row * logicalPixel,
                    left + column * logicalPixel,
                    top + (row + 1f) * logicalPixel,
                    color);
            }
        }
    }

    /// <summary>Quantizes coverage to reduce adjacent geometry without losing smooth edges.</summary>
    /// <param name="coverage">Eight-bit stb coverage.</param>
    /// <returns>Four-bit coverage.</returns>
    private static byte Quantize(byte coverage)
    {
        return (byte)((coverage + 8) / 17);
    }

    /// <summary>Appends two triangles for a solid rectangle.</summary>
    /// <param name="vertices">Destination vertices.</param>
    /// <param name="left">Left edge.</param>
    /// <param name="top">Top edge.</param>
    /// <param name="right">Right edge.</param>
    /// <param name="bottom">Bottom edge.</param>
    /// <param name="color">Rectangle color.</param>
    private static void AppendRectangle(
        List<Vertex> vertices,
        float left,
        float top,
        float right,
        float bottom,
        Color color)
    {
        vertices.Add(new Vertex(new(left, top, 0f), color));
        vertices.Add(new Vertex(new(left, bottom, 0f), color));
        vertices.Add(new Vertex(new(right, bottom, 0f), color));
        vertices.Add(new Vertex(new(right, bottom, 0f), color));
        vertices.Add(new Vertex(new(right, top, 0f), color));
        vertices.Add(new Vertex(new(left, top, 0f), color));
    }

    /// <summary>Stores cached glyph metrics and coverage.</summary>
    /// <param name="Width">Bitmap width.</param>
    /// <param name="Height">Bitmap height.</param>
    /// <param name="XOffset">Horizontal bearing.</param>
    /// <param name="YOffset">Vertical bearing relative to baseline.</param>
    /// <param name="Advance">Horizontal cursor advance.</param>
    /// <param name="Coverage">Eight-bit coverage bitmap.</param>
    private sealed record RasterizedGlyph(
        int Width,
        int Height,
        int XOffset,
        int YOffset,
        float Advance,
        byte[] Coverage);
}
