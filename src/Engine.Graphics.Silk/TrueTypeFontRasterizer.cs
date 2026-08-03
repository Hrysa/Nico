using System.Reflection;
using System.Text;
using static StbTrueTypeSharp.StbTrueType;

namespace Engine.Graphics;

/// <summary>Rasterizes Inter glyphs once into an RGBA atlas and emits one textured quad per glyph.</summary>
internal unsafe sealed class TrueTypeFontRasterizer : IDisposable
{
    internal const uint AtlasWidth = 2048;
    internal const uint AtlasHeight = 2048;
    private const int AtlasPadding = 2;
    private const int GlyphOversampling = 2;
    private readonly stbtt_fontinfo _font;
    private readonly Dictionary<GlyphKey, AtlasGlyph> _glyphs = [];
    private int _nextX = AtlasPadding;
    private int _nextY = AtlasPadding;
    private int _rowHeight;
    private int _dirtyLeft = int.MaxValue;
    private int _dirtyTop = int.MaxValue;
    private int _dirtyRight;
    private int _dirtyBottom;
    private bool _disposed;

    /// <summary>Gets the atlas RGBA pixels.</summary>
    internal byte[] AtlasPixels { get; } = new byte[AtlasWidth * AtlasHeight * 4];

    /// <summary>Gets the atlas content generation.</summary>
    internal ulong AtlasGeneration { get; private set; }

    /// <summary>Copies and clears the smallest atlas rectangle containing new glyph pixels.</summary>
    /// <param name="update">Packed RGBA update when dirty pixels exist.</param>
    /// <returns>True when an update was produced.</returns>
    internal bool TryTakeAtlasUpdate(out AtlasUpdate update)
    {
        if (_dirtyLeft == int.MaxValue)
        {
            update = default;
            return false;
        }
        var width = _dirtyRight - _dirtyLeft;
        var height = _dirtyBottom - _dirtyTop;
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            var sourceOffset = ((_dirtyTop + row) * (int)AtlasWidth + _dirtyLeft) * 4;
            AtlasPixels.AsSpan(sourceOffset, width * 4)
                .CopyTo(pixels.AsSpan(row * width * 4, width * 4));
        }
        update = new AtlasUpdate(_dirtyLeft, _dirtyTop, width, height, pixels);
        _dirtyLeft = int.MaxValue;
        _dirtyTop = int.MaxValue;
        _dirtyRight = 0;
        _dirtyBottom = 0;
        return true;
    }

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

    /// <summary>Appends six textured vertices per visible glyph.</summary>
    /// <param name="vertices">Destination textured vertices.</param>
    /// <param name="command">Text paint command.</param>
    /// <param name="framebufferScale">Physical pixels per logical UI pixel.</param>
    internal void AppendVertices(List<VertexT> vertices, UIDrawCommand command, float framebufferScale)
    {
        framebufferScale = MathF.Max(1f, framebufferScale);
        var pixelHeight = Math.Max(1, (int)MathF.Round(command.FontPixelHeight * framebufferScale));
        var layoutScale = stbtt_ScaleForPixelHeight(_font, pixelHeight);
        var rasterScale = stbtt_ScaleForPixelHeight(_font, pixelHeight * GlyphOversampling);
        int ascent;
        stbtt_GetFontVMetrics(_font, &ascent, null, null);
        var baseline = command.Top + ascent * layoutScale / framebufferScale;
        var cursor = command.Left;
        var previousCodepoint = -1;

        foreach (var rune in command.Text.EnumerateRunes())
        {
            var codepoint = rune.Value;
            if (previousCodepoint >= 0)
                cursor += stbtt_GetCodepointKernAdvance(_font, previousCodepoint, codepoint)
                    * layoutScale / framebufferScale;
            var glyph = GetGlyph(codepoint, pixelHeight, layoutScale, rasterScale, command.Color);
            if (glyph.Width > 0 && glyph.Height > 0)
            {
                var glyphPixelScale = framebufferScale * GlyphOversampling;
                var left = cursor + glyph.XOffset / glyphPixelScale;
                var top = baseline + glyph.YOffset / glyphPixelScale;
                var right = left + glyph.Width / glyphPixelScale;
                var bottom = top + glyph.Height / glyphPixelScale;
                AppendQuad(vertices, left, top, right, bottom, glyph);
            }
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

    /// <summary>Gets or rasterizes one colored atlas glyph.</summary>
    /// <param name="codepoint">Unicode codepoint.</param>
    /// <param name="pixelHeight">Physical glyph height.</param>
    /// <param name="layoutScale">Font scale used for layout metrics.</param>
    /// <param name="rasterScale">Oversampled font scale used for atlas pixels.</param>
    /// <param name="color">Glyph foreground color.</param>
    /// <returns>Atlas placement and metrics.</returns>
    private AtlasGlyph GetGlyph(
        int codepoint,
        int pixelHeight,
        float layoutScale,
        float rasterScale,
        Color color)
    {
        var key = new GlyphKey(codepoint, pixelHeight, color);
        if (_glyphs.TryGetValue(key, out var cached))
            return cached;

        int width;
        int height;
        int xOffset;
        int yOffset;
        var bitmap = stbtt_GetCodepointBitmap(
            _font, rasterScale, rasterScale, codepoint, &width, &height, &xOffset, &yOffset);
        int advance;
        stbtt_GetCodepointHMetrics(_font, codepoint, &advance, null);
        if (width == 0 || height == 0)
        {
            var empty = new AtlasGlyph(0, 0, xOffset, yOffset, advance * layoutScale, 0, 0);
            _glyphs.Add(key, empty);
            if (bitmap != null)
                stbtt_FreeBitmap(bitmap, null);
            return empty;
        }

        if (_nextX + width + AtlasPadding > AtlasWidth)
        {
            _nextX = AtlasPadding;
            _nextY += _rowHeight + AtlasPadding;
            _rowHeight = 0;
        }
        if (_nextY + height + AtlasPadding > AtlasHeight)
        {
            if (bitmap != null)
                stbtt_FreeBitmap(bitmap, null);
            throw new InvalidOperationException("Inter glyph atlas is full.");
        }

        var red = ToByte(color.R);
        var green = ToByte(color.G);
        var blue = ToByte(color.B);
        for (var row = 0; row < height; row++)
        {
            for (var column = 0; column < width; column++)
            {
                var destination = ((_nextY + row) * (int)AtlasWidth + _nextX + column) * 4;
                AtlasPixels[destination] = red;
                AtlasPixels[destination + 1] = green;
                AtlasPixels[destination + 2] = blue;
                AtlasPixels[destination + 3] = bitmap[row * width + column];
            }
        }
        stbtt_FreeBitmap(bitmap, null);
        var glyph = new AtlasGlyph(
            width, height, xOffset, yOffset, advance * layoutScale, _nextX, _nextY);
        _glyphs.Add(key, glyph);
        _nextX += width + AtlasPadding;
        _rowHeight = Math.Max(_rowHeight, height);
        _dirtyLeft = Math.Min(_dirtyLeft, glyph.AtlasX);
        _dirtyTop = Math.Min(_dirtyTop, glyph.AtlasY);
        _dirtyRight = Math.Max(_dirtyRight, glyph.AtlasX + glyph.Width);
        _dirtyBottom = Math.Max(_dirtyBottom, glyph.AtlasY + glyph.Height);
        AtlasGeneration++;
        return glyph;
    }

    /// <summary>Converts a normalized linear component to an atlas byte.</summary>
    /// <param name="component">Color component.</param>
    /// <returns>Clamped byte.</returns>
    private static byte ToByte(float component) => (byte)MathF.Round(Math.Clamp(component, 0f, 1f) * 255f);

    /// <summary>Appends a textured glyph quad.</summary>
    /// <param name="vertices">Destination vertices.</param>
    /// <param name="left">Logical left edge.</param>
    /// <param name="top">Logical top edge.</param>
    /// <param name="right">Logical right edge.</param>
    /// <param name="bottom">Logical bottom edge.</param>
    /// <param name="glyph">Atlas glyph.</param>
    private static void AppendQuad(
        List<VertexT> vertices,
        float left,
        float top,
        float right,
        float bottom,
        AtlasGlyph glyph)
    {
        var u0 = glyph.AtlasX / (float)AtlasWidth;
        var v0 = glyph.AtlasY / (float)AtlasHeight;
        var u1 = (glyph.AtlasX + glyph.Width) / (float)AtlasWidth;
        var v1 = (glyph.AtlasY + glyph.Height) / (float)AtlasHeight;
        vertices.Add(new VertexT(new(left, top, 0f), new(u0, v0)));
        vertices.Add(new VertexT(new(left, bottom, 0f), new(u0, v1)));
        vertices.Add(new VertexT(new(right, bottom, 0f), new(u1, v1)));
        vertices.Add(new VertexT(new(right, bottom, 0f), new(u1, v1)));
        vertices.Add(new VertexT(new(right, top, 0f), new(u1, v0)));
        vertices.Add(new VertexT(new(left, top, 0f), new(u0, v0)));
    }

    /// <summary>Keys glyphs by shape and baked foreground color.</summary>
    /// <param name="Codepoint">Unicode codepoint.</param>
    /// <param name="PixelHeight">Physical pixel height.</param>
    /// <param name="Color">Baked foreground color.</param>
    private readonly record struct GlyphKey(int Codepoint, int PixelHeight, Color Color);

    /// <summary>Stores glyph metrics and atlas placement.</summary>
    /// <param name="Width">Bitmap width.</param>
    /// <param name="Height">Bitmap height.</param>
    /// <param name="XOffset">Horizontal bearing.</param>
    /// <param name="YOffset">Vertical bearing.</param>
    /// <param name="Advance">Scaled horizontal advance.</param>
    /// <param name="AtlasX">Atlas left coordinate.</param>
    /// <param name="AtlasY">Atlas top coordinate.</param>
    private sealed record AtlasGlyph(
        int Width,
        int Height,
        int XOffset,
        int YOffset,
        float Advance,
        int AtlasX,
        int AtlasY);

    /// <summary>Describes one packed dirty atlas rectangle.</summary>
    /// <param name="X">Atlas destination X.</param>
    /// <param name="Y">Atlas destination Y.</param>
    /// <param name="Width">Update width.</param>
    /// <param name="Height">Update height.</param>
    /// <param name="Pixels">Tightly packed RGBA pixels.</param>
    internal readonly record struct AtlasUpdate(
        int X,
        int Y,
        int Width,
        int Height,
        byte[] Pixels);
}
