using System.Buffers;
using System.Globalization;
using System.Text;
using Engine.Graphics.Bidi;
using HarfBuzzSharp;
using HarfBuzzBuffer = HarfBuzzSharp.Buffer;
using HarfBuzzFont = HarfBuzzSharp.Font;
using static StbTrueTypeSharp.StbTrueType;

namespace Engine.Graphics;

/// <summary>Rasterizes system-font glyphs once into an RGBA atlas and emits textured quads.</summary>
internal unsafe sealed class TrueTypeFontRasterizer : IDisposable
{
    internal const uint AtlasWidth = 2048;
    internal const uint AtlasHeight = 2048;
    private const int AtlasPadding = 2;
    private const int GlyphOversampling = 2;
    private const int MaximumShapedRuns = 1024;
    private const string CodiconResourceName = "codicon.ttf";
    private static readonly object SharedGlyphLock = new();
    private static readonly Dictionary<GlyphShapeKey, RasterizedGlyph> SharedGlyphs = [];
    private readonly List<FontFace> _fonts = [];
    private readonly Dictionary<GlyphKey, AtlasGlyph> _glyphs = [];
    private readonly Dictionary<ShapedRunKey, ShapedLine> _shapedRuns = [];
    private readonly Queue<ShapedRunKey> _shapedRunOrder = [];
    private readonly BidiResolver _bidiResolver = new();
    private readonly int _codiconFontIndex;
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

    /// <summary>Gets the number of renderer-local decoded and kerned text runs.</summary>
    internal int ShapedRunCount => _shapedRuns.Count;

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
        var uploadLeft = Math.Max(0, _dirtyLeft - AtlasPadding);
        var uploadTop = Math.Max(0, _dirtyTop - AtlasPadding);
        var uploadRight = Math.Min((int)AtlasWidth, _dirtyRight + AtlasPadding);
        var uploadBottom = Math.Min((int)AtlasHeight, _dirtyBottom + AtlasPadding);
        var width = uploadRight - uploadLeft;
        var height = uploadBottom - uploadTop;
        var pixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            var sourceOffset = ((uploadTop + row) * (int)AtlasWidth + uploadLeft) * 4;
            AtlasPixels.AsSpan(sourceOffset, width * 4)
                .CopyTo(pixels.AsSpan(row * width * 4, width * 4));
        }
        update = new AtlasUpdate(uploadLeft, uploadTop, width, height, pixels);
        _dirtyLeft = int.MaxValue;
        _dirtyTop = int.MaxValue;
        _dirtyRight = 0;
        _dirtyBottom = 0;
        return true;
    }

    /// <summary>Loads the operating system's ordered UI font fallback chain.</summary>
    internal TrueTypeFontRasterizer()
    {
        var sources = SystemFontResolver.Resolve();
        for (var index = 0; index < sources.Length; index++)
        {
            var face = CreateFace(sources[index]);
            if (face is not null)
                _fonts.Add(face);
        }
        if (_fonts.Count == 0)
            throw new InvalidOperationException("No usable operating-system UI font was found.");
        _codiconFontIndex = _fonts.Count;
        _fonts.Add(LoadEmbeddedFace(CodiconResourceName));
    }

    /// <summary>Creates one rasterization and shaping face from a system font source.</summary>
    /// <param name="source">System font file and collection index.</param>
    /// <returns>Owned face, or null when the file is not a supported TrueType/OpenType face.</returns>
    private static FontFace? CreateFace(SystemFontSource source)
    {
        try
        {
            return CreateFace(File.ReadAllBytes(source.Path), source.FaceIndex);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Creates one rasterization and shaping face from owned font bytes.</summary>
    /// <param name="data">Complete TrueType or OpenType font data.</param>
    /// <param name="faceIndex">Font collection face index.</param>
    /// <returns>Owned face, or null when the bytes do not contain the requested face.</returns>
    private static FontFace? CreateFace(byte[] data, int faceIndex)
    {
        stbtt_fontinfo? rasterFont = null;
        Blob? blob = null;
        Face? shapingFace = null;
        HarfBuzzFont? shapingFont = null;
        try
        {
            int fontOffset;
            fixed (byte* pointer = data)
                fontOffset = stbtt_GetFontOffsetForIndex(pointer, faceIndex);
            if (fontOffset < 0)
                return null;
            rasterFont = CreateFont(data, fontOffset);
            if (rasterFont is null)
                return null;
            fixed (byte* pointer = data)
                blob = new Blob((IntPtr)pointer, data.Length, MemoryMode.Duplicate);
            shapingFace = new Face(blob, checked((uint)faceIndex));
            shapingFont = new HarfBuzzFont(shapingFace);
            shapingFont.SetFunctionsOpenType();
            shapingFont.SetScale(shapingFace.UnitsPerEm, shapingFace.UnitsPerEm);
            return new FontFace(rasterFont, blob, shapingFace, shapingFont);
        }
        catch
        {
            DisposePartialFace(rasterFont, blob, shapingFace, shapingFont);
            throw;
        }
    }

    /// <summary>Loads one required font face embedded in this renderer assembly.</summary>
    /// <param name="resourceName">Manifest resource name.</param>
    /// <returns>Owned rasterization and shaping face.</returns>
    private static FontFace LoadEmbeddedFace(string resourceName)
    {
        using var stream = typeof(TrueTypeFontRasterizer).Assembly
            .GetManifestResourceStream(resourceName) ??
            throw new InvalidOperationException(
                $"Required embedded font resource '{resourceName}' was not found.");
        var data = new byte[checked((int)stream.Length)];
        stream.ReadExactly(data);
        return CreateFace(data, 0) ??
            throw new InvalidOperationException(
                $"Embedded font resource '{resourceName}' is not a supported TrueType face.");
    }

    /// <summary>Releases a partially constructed system font face after loading fails.</summary>
    /// <param name="rasterFont">Optional stb face.</param>
    /// <param name="blob">Optional HarfBuzz blob.</param>
    /// <param name="shapingFace">Optional HarfBuzz face.</param>
    /// <param name="shapingFont">Optional HarfBuzz font.</param>
    private static void DisposePartialFace(
        stbtt_fontinfo? rasterFont,
        Blob? blob,
        Face? shapingFace,
        HarfBuzzFont? shapingFont)
    {
        shapingFont?.Dispose();
        shapingFace?.Dispose();
        blob?.Dispose();
        rasterFont?.Dispose();
    }

    /// <summary>Measures a line with the same glyph advances and kerning used for rendering.</summary>
    /// <param name="text">UTF-16 text to measure.</param>
    /// <param name="fontSize">Logical font height.</param>
    /// <returns>Horizontal advance in logical pixels.</returns>
    internal float MeasureWidth(ReadOnlySpan<char> text, float fontSize) =>
        MeasureWidth(text, fontSize, TextFlowDirection.Auto);

    /// <summary>Measures a bidirectionally resolved line using rendered glyph advances.</summary>
    /// <param name="text">UTF-16 text to measure.</param>
    /// <param name="fontSize">Logical font height.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <returns>Horizontal advance in logical pixels.</returns>
    internal float MeasureWidth(
        ReadOnlySpan<char> text,
        float fontSize,
        TextFlowDirection direction)
    {
        if (fontSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        return BuildShapedLine(
            text.ToString(), fontSize, direction, UIFontFamily.Default).Width;
    }

    /// <summary>Finds the nearest caret using rendered glyph advances and kerning.</summary>
    /// <param name="text">UTF-16 text to hit test.</param>
    /// <param name="fontSize">Logical font height.</param>
    /// <param name="horizontalPosition">Position relative to the text origin.</param>
    /// <returns>Nearest UTF-16 caret index.</returns>
    internal int HitTestCaret(
        ReadOnlySpan<char> text,
        float fontSize,
        float horizontalPosition) =>
        HitTestCaret(text, fontSize, horizontalPosition, TextFlowDirection.Auto);

    /// <summary>Finds the nearest visual caret after bidirectional resolution.</summary>
    /// <param name="text">UTF-16 text to hit test.</param>
    /// <param name="fontSize">Logical font height.</param>
    /// <param name="horizontalPosition">Position relative to the text origin.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <returns>Nearest UTF-16 caret index.</returns>
    internal int HitTestCaret(
        ReadOnlySpan<char> text,
        float fontSize,
        float horizontalPosition,
        TextFlowDirection direction)
    {
        if (fontSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        var line = BuildShapedLine(
            text.ToString(), fontSize, direction, UIFontFamily.Default);
        if (line.Carets.Length == 0)
            return 0;
        if (horizontalPosition <= line.Carets[0].Position)
            return line.Carets[0].TextIndex;
        for (var index = 1; index < line.Carets.Length; index++)
        {
            var left = line.Carets[index - 1];
            var right = line.Carets[index];
            if (horizontalPosition < (left.Position + right.Position) * 0.5f)
                return left.TextIndex;
        }
        return line.Carets[^1].TextIndex;
    }

    /// <summary>Maps a logical caret index to its visual position after bidi resolution.</summary>
    /// <param name="text">UTF-16 text.</param>
    /// <param name="fontSize">Logical font height.</param>
    /// <param name="caretIndex">Logical UTF-16 caret index.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <returns>Visual horizontal position.</returns>
    internal float GetCaretPosition(
        ReadOnlySpan<char> text,
        float fontSize,
        int caretIndex,
        TextFlowDirection direction)
    {
        if (fontSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if ((uint)caretIndex > (uint)text.Length)
            throw new ArgumentOutOfRangeException(nameof(caretIndex));
        var line = BuildShapedLine(
            text.ToString(), fontSize, direction, UIFontFamily.Default);
        return FindCaretPosition(line.Carets, caretIndex);
    }

    /// <summary>Resolves a logical selection into contiguous visual ranges.</summary>
    /// <param name="text">UTF-16 text.</param>
    /// <param name="fontSize">Logical font height.</param>
    /// <param name="selectionStart">Logical selection start.</param>
    /// <param name="selectionLength">Logical selection length.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <returns>Visual selection ranges in left-to-right order.</returns>
    internal TextSelectionRange[] GetSelectionRanges(
        ReadOnlySpan<char> text,
        float fontSize,
        int selectionStart,
        int selectionLength,
        TextFlowDirection direction)
    {
        if (fontSize <= 0f)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (selectionStart < 0 || selectionLength < 0 ||
            selectionStart + selectionLength > text.Length)
            throw new ArgumentOutOfRangeException(nameof(selectionStart));
        if (selectionLength == 0)
            return [];
        var selectionEnd = selectionStart + selectionLength;
        var line = BuildShapedLine(
            text.ToString(), fontSize, direction, UIFontFamily.Default);
        var result = new List<TextSelectionRange>();
        var rangeStart = 0f;
        var rangeEnd = 0f;
        var hasRange = false;
        for (var index = 0; index < line.Cells.Length; index++)
        {
            var cell = line.Cells[index];
            if (cell.TextStart < selectionEnd && cell.TextEnd > selectionStart)
            {
                if (!hasRange)
                {
                    rangeStart = cell.VisualStart;
                    rangeEnd = cell.VisualEnd;
                    hasRange = true;
                }
                else if (MathF.Abs(cell.VisualStart - rangeEnd) < 0.0001f)
                {
                    rangeEnd = cell.VisualEnd;
                }
                else
                {
                    result.Add(new TextSelectionRange(rangeStart, rangeEnd - rangeStart));
                    rangeStart = cell.VisualStart;
                    rangeEnd = cell.VisualEnd;
                }
            }
            else if (hasRange)
            {
                result.Add(new TextSelectionRange(rangeStart, rangeEnd - rangeStart));
                hasRange = false;
            }
        }
        if (hasRange)
            result.Add(new TextSelectionRange(rangeStart, rangeEnd - rangeStart));
        return result.ToArray();
    }

    /// <summary>Finds the exclusive UTF-16 end of one shaped cluster.</summary>
    /// <param name="infos">Shaped glyph information.</param>
    /// <param name="cluster">Cluster start to resolve.</param>
    /// <param name="textLength">Complete UTF-16 text length.</param>
    /// <returns>Next greater cluster boundary, or the text length.</returns>
    private static int GetClusterEnd(
        ReadOnlySpan<GlyphInfo> infos,
        uint cluster,
        int textLength)
    {
        var end = checked((uint)textLength);
        for (var index = 0; index < infos.Length; index++)
        {
            var candidate = infos[index].Cluster;
            if (candidate > cluster && candidate < end)
                end = candidate;
        }
        return checked((int)end);
    }

    /// <summary>Shapes a UTF-16 span with inferred script, language, and direction.</summary>
    /// <param name="text">Text to shape.</param>
    /// <param name="rightToLeft">Explicit shaping direction, or null to infer it.</param>
    /// <param name="fontIndex">System fallback-chain face index.</param>
    /// <returns>Owned HarfBuzz buffer containing glyphs and positions.</returns>
    private HarfBuzzBuffer Shape(
        ReadOnlySpan<char> text,
        bool? rightToLeft,
        int fontIndex)
    {
        var buffer = new HarfBuzzBuffer
        {
            ClusterLevel = ClusterLevel.MonotoneCharacters
        };
        buffer.AddUtf16(text);
        if (rightToLeft.HasValue)
            buffer.Direction = rightToLeft.Value ? Direction.RightToLeft : Direction.LeftToRight;
        buffer.GuessSegmentProperties();
        GetShapingFont(fontIndex).Shape(buffer, []);
        return buffer;
    }

    /// <summary>Selects the system face with complete or greatest coverage for one grapheme.</summary>
    /// <param name="text">One Unicode text element.</param>
    /// <returns>Fallback-chain face index.</returns>
    private int SelectFont(ReadOnlySpan<char> text)
    {
        var bestIndex = 0;
        var bestCoverage = -1;
        for (var fontIndex = 0; fontIndex < _fonts.Count; fontIndex++)
        {
            var remaining = text;
            var coverage = 0;
            var required = 0;
            while (!remaining.IsEmpty)
            {
                DecodeRune(remaining, out var rune, out var consumed);
                remaining = remaining[consumed..];
                if (IsCoverageIgnorable(rune))
                    continue;
                required++;
                if (stbtt_FindGlyphIndex(GetFont(fontIndex), rune.Value) != 0)
                    coverage++;
            }
            if (coverage == required)
                return fontIndex;
            if (coverage > bestCoverage)
            {
                bestCoverage = coverage;
                bestIndex = fontIndex;
            }
        }
        return bestIndex;
    }

    /// <summary>Checks whether a scalar affects shaping without requiring a visible cmap glyph.</summary>
    /// <param name="rune">Scalar being inspected.</param>
    /// <returns>True for variation selectors and bidi formatting controls.</returns>
    private static bool IsCoverageIgnorable(Rune rune) =>
        rune.Value is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF or
        0x061C or 0x200E or 0x200F or >= 0x202A and <= 0x202E or
        >= 0x2066 and <= 0x2069;

    /// <summary>Gets one stb face by fallback-chain index.</summary>
    /// <param name="fontIndex">Face index.</param>
    /// <returns>Selected stb face.</returns>
    private stbtt_fontinfo GetFont(int fontIndex) => _fonts[fontIndex].RasterFont;

    /// <summary>Gets one shaping face by fallback-chain index.</summary>
    /// <param name="fontIndex">Face index.</param>
    /// <returns>Selected HarfBuzz font.</returns>
    private HarfBuzzFont GetShapingFont(int fontIndex) => _fonts[fontIndex].ShapingFont;

    /// <summary>Decodes one scalar while making progress across malformed UTF-16.</summary>
    /// <param name="text">Remaining text.</param>
    /// <param name="rune">Decoded scalar or replacement character.</param>
    /// <param name="consumed">Number of consumed UTF-16 code units.</param>
    private static void DecodeRune(ReadOnlySpan<char> text, out Rune rune, out int consumed)
    {
        if (Rune.DecodeFromUtf16(text, out rune, out consumed) == OperationStatus.Done)
            return;
        rune = Rune.ReplacementChar;
        consumed = 1;
    }

    /// <summary>Appends six textured vertices per visible glyph.</summary>
    /// <param name="vertices">Destination textured vertices.</param>
    /// <param name="command">Text paint command.</param>
    /// <param name="framebufferScale">Physical pixels per logical UI pixel.</param>
    /// <returns>The logical horizontal position of the requested caret.</returns>
    internal float AppendVertices(NativeBuffer<VertexT> vertices, UIDrawCommand command, float framebufferScale)
    {
        framebufferScale = MathF.Max(1f, framebufferScale);
        var pixelHeight = Math.Max(1, (int)MathF.Round(command.FontPixelHeight * framebufferScale));
        var line = GetShapedRun(
            command.Text, pixelHeight, command.TextDirection, command.FontFamily);
        var run = line.Glyphs;
        var baselineFont = GetFont(run.Length > 0 ? run[0].FontIndex : 0);
        int ascent;
        stbtt_GetFontVMetrics(baselineFont, &ascent, null, null);
        var layoutScale = stbtt_ScaleForPixelHeight(baselineFont, pixelHeight);
        var baseline = command.Top + ascent * layoutScale / framebufferScale;
        var cursor = command.Left;
        var caretLeft = command.Left + FindCaretPosition(line.Carets, command.CaretIndex) /
            framebufferScale;
        for (var index = 0; index < run.Length; index++)
        {
            var shapedGlyph = run[index];
            cursor += shapedGlyph.PreAdvance / framebufferScale;
            var glyphFont = GetFont(shapedGlyph.FontIndex);
            var glyphLayoutScale = stbtt_ScaleForPixelHeight(glyphFont, pixelHeight);
            var rasterScale = stbtt_ScaleForPixelHeight(
                glyphFont, pixelHeight * GlyphOversampling);
            var glyph = GetGlyph(
                shapedGlyph.FontIndex, shapedGlyph.Codepoint, pixelHeight,
                glyphLayoutScale, rasterScale, command.Color);
            if (glyph.Width > 0 && glyph.Height > 0)
            {
                var glyphPixelScale = framebufferScale * GlyphOversampling;
                var left = cursor + shapedGlyph.XOffset / framebufferScale +
                    glyph.XOffset / glyphPixelScale;
                var top = baseline - shapedGlyph.YOffset / framebufferScale +
                    glyph.YOffset / glyphPixelScale;
                var right = left + glyph.Width / glyphPixelScale;
                var bottom = top + glyph.Height / glyphPixelScale;
                AppendQuad(vertices, left, top, right, bottom, glyph, command.Opacity);
            }
            cursor += shapedGlyph.Advance / framebufferScale;
        }
        return caretLeft;
    }

    /// <summary>Prepares and caches one text run without rasterizing its glyph bitmaps.</summary>
    /// <param name="text">Text to decode and kern.</param>
    /// <param name="pixelHeight">Physical font height.</param>
    internal void PrepareText(string text, int pixelHeight)
    {
        PrepareText(text, pixelHeight, TextFlowDirection.Auto);
    }

    /// <summary>Prepares and caches a text line with an explicit paragraph direction.</summary>
    /// <param name="text">Text to shape.</param>
    /// <param name="pixelHeight">Physical font height.</param>
    /// <param name="direction">Paragraph base direction.</param>
    internal void PrepareText(
        string text,
        int pixelHeight,
        TextFlowDirection direction)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        GetShapedRun(text, pixelHeight, direction, UIFontFamily.Default);
    }

    /// <summary>Returns the shaped glyph count for diagnostics and regression tests.</summary>
    /// <param name="text">Text to shape.</param>
    /// <param name="pixelHeight">Physical font height.</param>
    /// <returns>Number of output glyphs after shaping.</returns>
    internal int GetShapedGlyphCount(string text, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        return GetShapedRun(
            text, pixelHeight, TextFlowDirection.Auto, UIFontFamily.Default).Glyphs.Length;
    }

    /// <summary>Returns the selected system fallback-chain face for diagnostics.</summary>
    /// <param name="text">Text whose coverage is inspected.</param>
    /// <returns>Selected fallback-chain index.</returns>
    internal int GetSelectedFontIndex(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SelectFont(text.AsSpan());
    }

    /// <summary>Returns the font face used by the shaped glyph at one logical text index.</summary>
    /// <param name="text">Text to shape.</param>
    /// <param name="pixelHeight">Physical font height.</param>
    /// <param name="textIndex">Logical UTF-16 index.</param>
    /// <returns>Fallback-chain face index, or minus one when no glyph starts there.</returns>
    internal int GetShapedFontIndex(string text, int pixelHeight, int textIndex)
    {
        ArgumentNullException.ThrowIfNull(text);
        var glyphs = GetShapedRun(
            text, pixelHeight, TextFlowDirection.Auto, UIFontFamily.Default).Glyphs;
        for (var index = 0; index < glyphs.Length; index++)
        {
            if (glyphs[index].TextIndex == textIndex)
                return glyphs[index].FontIndex;
        }
        return -1;
    }

    /// <summary>Counts missing-glyph identifiers emitted after shaping.</summary>
    /// <param name="text">Text to shape.</param>
    /// <param name="pixelHeight">Physical font height.</param>
    /// <returns>Number of shaped glyphs using glyph identifier zero.</returns>
    internal int GetMissingGlyphCount(string text, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(text);
        var glyphs = GetShapedRun(
            text, pixelHeight, TextFlowDirection.Auto, UIFontFamily.Default).Glyphs;
        var count = 0;
        for (var index = 0; index < glyphs.Length; index++)
        {
            if (glyphs[index].Codepoint == 0)
                count++;
        }
        return count;
    }

    /// <summary>Counts shaped glyphs that produce outline coverage through the atlas rasterizer.</summary>
    /// <param name="text">Text to shape and rasterize.</param>
    /// <param name="pixelHeight">Physical font height.</param>
    /// <returns>Number of glyphs with a nonempty outline bitmap.</returns>
    internal int GetVisibleGlyphCount(string text, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(text);
        var glyphs = GetShapedRun(
            text, pixelHeight, TextFlowDirection.Auto, UIFontFamily.Default).Glyphs;
        var count = 0;
        for (var index = 0; index < glyphs.Length; index++)
        {
            var glyph = glyphs[index];
            var font = GetFont(glyph.FontIndex);
            var layoutScale = stbtt_ScaleForPixelHeight(font, pixelHeight);
            var rasterScale = stbtt_ScaleForPixelHeight(font, pixelHeight * GlyphOversampling);
            var rasterized = GetRasterizedGlyph(
                glyph.FontIndex, glyph.Codepoint, pixelHeight, layoutScale, rasterScale);
            if (rasterized.Width > 0 && rasterized.Height > 0)
                count++;
        }
        return count;
    }

    /// <summary>Returns logical caret indices in resolved visual order for diagnostics.</summary>
    /// <param name="text">Text to shape.</param>
    /// <param name="pixelHeight">Physical font height.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <returns>Logical UTF-16 caret indices in visual order.</returns>
    internal int[] GetVisualCaretOrder(
        string text,
        int pixelHeight,
        TextFlowDirection direction)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (pixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        var carets = GetShapedRun(
            text, pixelHeight, direction, UIFontFamily.Default).Carets;
        var result = new int[carets.Length];
        for (var index = 0; index < carets.Length; index++)
            result[index] = carets[index].TextIndex;
        return result;
    }

    /// <summary>Releases unmanaged font data.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _shapedRuns.Clear();
        _shapedRunOrder.Clear();
        for (var index = 0; index < _fonts.Count; index++)
            _fonts[index].Dispose();
        _fonts.Clear();
    }

    /// <summary>Gets or creates a bounded decoded glyph run with scaled advances and kerning.</summary>
    /// <param name="text">Text to shape.</param>
    /// <param name="pixelHeight">Physical font height used as the cache key.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <param name="fontFamily">Requested renderer-provided typeface.</param>
    /// <returns>Cached visual glyph and caret sequence.</returns>
    private ShapedLine GetShapedRun(
        string text,
        int pixelHeight,
        TextFlowDirection direction,
        UIFontFamily fontFamily)
    {
        var key = new ShapedRunKey(text, pixelHeight, direction, fontFamily);
        if (_shapedRuns.TryGetValue(key, out var cached))
            return cached;
        var shaped = BuildShapedLine(text, pixelHeight, direction, fontFamily);
        while (_shapedRuns.Count >= MaximumShapedRuns && _shapedRunOrder.Count > 0)
            _shapedRuns.Remove(_shapedRunOrder.Dequeue());
        _shapedRuns.Add(key, shaped);
        _shapedRunOrder.Enqueue(key);
        return shaped;
    }

    /// <summary>Builds visual glyphs and grapheme-safe caret stops for one bidi line.</summary>
    /// <param name="text">UTF-16 source text.</param>
    /// <param name="pixelHeight">Physical or logical font height.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <param name="fontFamily">Requested renderer-provided typeface.</param>
    /// <returns>Resolved shaped line.</returns>
    private ShapedLine BuildShapedLine(
        string text,
        float pixelHeight,
        TextFlowDirection direction,
        UIFontFamily fontFamily)
    {
        var resolved = _bidiResolver.Resolve(text.AsSpan(), direction);
        var glyphs = new List<ShapedGlyph>();
        var carets = new List<CaretStop>();
        var cells = new List<CaretCell>();
        var cursor = 0f;
        for (var runIndex = 0; runIndex < resolved.Runs.Length; runIndex++)
        {
            var run = resolved.Runs[runIndex];
            var runText = text.AsSpan(run.Utf16Start, run.Utf16Length);
            var fontRuns = BuildFontRuns(runText, fontFamily);
            var fontRunIndex = run.IsRightToLeft ? fontRuns.Count - 1 : 0;
            var fontRunEnd = run.IsRightToLeft ? -1 : fontRuns.Count;
            var fontRunStep = run.IsRightToLeft ? -1 : 1;
            while (fontRunIndex != fontRunEnd)
            {
                var fontRun = fontRuns[fontRunIndex];
                var shapedText = runText.Slice(fontRun.Utf16Start, fontRun.Utf16Length);
                using var buffer = Shape(shapedText, run.IsRightToLeft, fontRun.FontIndex);
                var layoutScale = stbtt_ScaleForPixelHeight(
                    GetFont(fontRun.FontIndex), pixelHeight);
                var infos = buffer.GetGlyphInfoSpan();
                var positions = buffer.GetGlyphPositionSpan();
                var glyphIndex = 0;
                while (glyphIndex < infos.Length)
                {
                    var cluster = infos[glyphIndex].Cluster;
                    var clusterAdvance = 0f;
                    var groupEnd = glyphIndex;
                    while (groupEnd < infos.Length && infos[groupEnd].Cluster == cluster)
                    {
                        var position = positions[groupEnd];
                        var advance = MathF.Abs(position.XAdvance * layoutScale);
                        glyphs.Add(new ShapedGlyph(
                            fontRun.FontIndex,
                            checked((int)infos[groupEnd].Codepoint),
                            run.Utf16Start + fontRun.Utf16Start + checked((int)cluster),
                            0f,
                            advance,
                            position.XOffset * layoutScale,
                            position.YOffset * layoutScale));
                        clusterAdvance += advance;
                        groupEnd++;
                    }
                    var localStart = checked((int)cluster);
                    var localEnd = GetClusterEnd(infos, cluster, fontRun.Utf16Length);
                    AddClusterCarets(
                        text.AsSpan(
                            run.Utf16Start + fontRun.Utf16Start + localStart,
                            localEnd - localStart),
                        run.Utf16Start + fontRun.Utf16Start + localStart,
                        run.IsRightToLeft,
                        cursor,
                        clusterAdvance,
                        carets,
                        cells);
                    cursor += clusterAdvance;
                    glyphIndex = groupEnd;
                }
                fontRunIndex += fontRunStep;
            }
        }
        if (carets.Count == 0)
            carets.Add(new CaretStop(0, 0f));
        return new ShapedLine(glyphs.ToArray(), carets.ToArray(), cells.ToArray(), cursor);
    }

    /// <summary>Splits one directional run into grapheme-safe adjacent system-font runs.</summary>
    /// <param name="text">Logical UTF-16 directional run.</param>
    /// <param name="fontFamily">Requested renderer-provided typeface.</param>
    /// <returns>Font runs in logical order.</returns>
    private List<FontRun> BuildFontRuns(ReadOnlySpan<char> text, UIFontFamily fontFamily)
    {
        var runs = new List<FontRun>();
        if (fontFamily == UIFontFamily.Codicon)
        {
            if (!text.IsEmpty)
                runs.Add(new FontRun(0, text.Length, _codiconFontIndex));
            return runs;
        }
        var start = 0;
        while (start < text.Length)
        {
            var length = StringInfo.GetNextTextElementLength(text[start..]);
            var fontIndex = SelectFont(text.Slice(start, length));
            if (runs.Count > 0 && runs[^1].FontIndex == fontIndex)
            {
                var previous = runs[^1];
                runs[^1] = previous with { Utf16Length = previous.Utf16Length + length };
            }
            else
            {
                runs.Add(new FontRun(start, length, fontIndex));
            }
            start += length;
        }
        return runs;
    }

    /// <summary>Adds evenly distributed grapheme caret stops for one shaped cluster.</summary>
    /// <param name="clusterText">Cluster source text.</param>
    /// <param name="sourceStart">Cluster UTF-16 source start.</param>
    /// <param name="rightToLeft">Whether visual order is right-to-left.</param>
    /// <param name="visualStart">Cluster visual start.</param>
    /// <param name="advance">Cluster visual advance.</param>
    /// <param name="carets">Destination caret list.</param>
    /// <param name="cells">Destination visual grapheme cells.</param>
    private static void AddClusterCarets(
        ReadOnlySpan<char> clusterText,
        int sourceStart,
        bool rightToLeft,
        float visualStart,
        float advance,
        List<CaretStop> carets,
        List<CaretCell> cells)
    {
        var graphemeCount = 0;
        var remaining = clusterText;
        while (!remaining.IsEmpty)
        {
            var length = StringInfo.GetNextTextElementLength(remaining);
            remaining = remaining[length..];
            graphemeCount++;
        }
        graphemeCount = Math.Max(1, graphemeCount);
        var boundaries = new int[graphemeCount + 1];
        var offset = 0;
        boundaries[0] = sourceStart;
        remaining = clusterText;
        for (var index = 1; index <= graphemeCount; index++)
        {
            var length = remaining.IsEmpty ? 0 : StringInfo.GetNextTextElementLength(remaining);
            offset += length;
            boundaries[index] = sourceStart + offset;
            remaining = remaining[length..];
        }
        for (var index = 0; index <= graphemeCount; index++)
        {
            var sourceIndex = rightToLeft
                ? boundaries[graphemeCount - index]
                : boundaries[index];
            AddCaret(carets, sourceIndex, visualStart + advance * index / graphemeCount);
        }
        for (var index = 0; index < graphemeCount; index++)
        {
            var first = rightToLeft ? boundaries[graphemeCount - index] : boundaries[index];
            var second = rightToLeft ? boundaries[graphemeCount - index - 1] : boundaries[index + 1];
            cells.Add(new CaretCell(
                Math.Min(first, second),
                Math.Max(first, second),
                visualStart + advance * index / graphemeCount,
                visualStart + advance * (index + 1) / graphemeCount));
        }
    }

    /// <summary>Adds a caret unless it duplicates the previous visual stop.</summary>
    /// <param name="carets">Destination caret list.</param>
    /// <param name="textIndex">Logical UTF-16 index.</param>
    /// <param name="position">Visual position.</param>
    private static void AddCaret(List<CaretStop> carets, int textIndex, float position)
    {
        if (carets.Count > 0 && carets[^1].TextIndex == textIndex &&
            MathF.Abs(carets[^1].Position - position) < 0.0001f)
            return;
        carets.Add(new CaretStop(textIndex, position));
    }

    /// <summary>Finds the visual position of a logical caret.</summary>
    /// <param name="carets">Resolved visual caret stops.</param>
    /// <param name="textIndex">Logical UTF-16 index.</param>
    /// <returns>Visual position, or zero when no caret is requested.</returns>
    private static float FindCaretPosition(CaretStop[] carets, int textIndex)
    {
        if (textIndex < 0)
            return 0f;
        for (var index = 0; index < carets.Length; index++)
        {
            if (carets[index].TextIndex == textIndex)
                return carets[index].Position;
        }
        return carets.Length == 0 ? 0f : carets[^1].Position;
    }

    /// <summary>Gets or rasterizes one colored atlas glyph.</summary>
    /// <param name="fontIndex">Fallback-chain face index.</param>
    /// <param name="codepoint">Font glyph index.</param>
    /// <param name="pixelHeight">Physical glyph height.</param>
    /// <param name="layoutScale">Font scale used for layout metrics.</param>
    /// <param name="rasterScale">Oversampled font scale used for atlas pixels.</param>
    /// <param name="color">Glyph foreground color.</param>
    /// <returns>Atlas placement and metrics.</returns>
    private AtlasGlyph GetGlyph(
        int fontIndex,
        int codepoint,
        int pixelHeight,
        float layoutScale,
        float rasterScale,
        Color color)
    {
        var key = new GlyphKey(fontIndex, codepoint, pixelHeight, color);
        if (_glyphs.TryGetValue(key, out var cached))
            return cached;

        var rasterized = GetRasterizedGlyph(
            fontIndex, codepoint, pixelHeight, layoutScale, rasterScale);
        if (rasterized.Width == 0 || rasterized.Height == 0)
        {
            var empty = new AtlasGlyph(
                0, 0, rasterized.XOffset, rasterized.YOffset, rasterized.Advance, 0, 0);
            _glyphs.Add(key, empty);
            return empty;
        }

        if (_nextX + rasterized.Width + AtlasPadding > AtlasWidth)
        {
            _nextX = AtlasPadding;
            _nextY += _rowHeight + AtlasPadding;
            _rowHeight = 0;
        }
        if (_nextY + rasterized.Height + AtlasPadding > AtlasHeight)
            throw new InvalidOperationException("Inter glyph atlas is full.");

        var red = ToByte(color.R);
        var green = ToByte(color.G);
        var blue = ToByte(color.B);
        for (var row = 0; row < rasterized.Height; row++)
        {
            for (var column = 0; column < rasterized.Width; column++)
            {
                var destination = ((_nextY + row) * (int)AtlasWidth + _nextX + column) * 4;
                AtlasPixels[destination] = red;
                AtlasPixels[destination + 1] = green;
                AtlasPixels[destination + 2] = blue;
                AtlasPixels[destination + 3] =
                    rasterized.Coverage[row * rasterized.Width + column];
            }
        }
        var glyph = new AtlasGlyph(
            rasterized.Width, rasterized.Height, rasterized.XOffset, rasterized.YOffset,
            rasterized.Advance, _nextX, _nextY);
        _glyphs.Add(key, glyph);
        _nextX += rasterized.Width + AtlasPadding;
        _rowHeight = Math.Max(_rowHeight, rasterized.Height);
        _dirtyLeft = Math.Min(_dirtyLeft, glyph.AtlasX);
        _dirtyTop = Math.Min(_dirtyTop, glyph.AtlasY);
        _dirtyRight = Math.Max(_dirtyRight, glyph.AtlasX + glyph.Width);
        _dirtyBottom = Math.Max(_dirtyBottom, glyph.AtlasY + glyph.Height);
        AtlasGeneration++;
        return glyph;
    }

    /// <summary>Gets an immutable oversampled glyph shared by every native window.</summary>
    /// <param name="fontIndex">Fallback-chain face index.</param>
    /// <param name="codepoint">Unicode codepoint.</param>
    /// <param name="pixelHeight">Requested physical glyph height.</param>
    /// <param name="layoutScale">Scale used for layout metrics.</param>
    /// <param name="rasterScale">Scale used for oversampled coverage.</param>
    /// <returns>Shared glyph bitmap and metrics.</returns>
    private RasterizedGlyph GetRasterizedGlyph(
        int fontIndex,
        int codepoint,
        int pixelHeight,
        float layoutScale,
        float rasterScale)
    {
        var key = new GlyphShapeKey(fontIndex, codepoint, pixelHeight);
        lock (SharedGlyphLock)
        {
            if (SharedGlyphs.TryGetValue(key, out var cached))
                return cached;
            int width;
            int height;
            int xOffset;
            int yOffset;
            var font = GetFont(fontIndex);
            var bitmap = stbtt_GetGlyphBitmap(
                font, rasterScale, rasterScale, codepoint,
                &width, &height, &xOffset, &yOffset);
            int advance;
            stbtt_GetGlyphHMetrics(font, codepoint, &advance, null);
            var coverage = new byte[Math.Max(0, width * height)];
            if (bitmap != null)
            {
                new ReadOnlySpan<byte>(bitmap, coverage.Length).CopyTo(coverage);
                stbtt_FreeBitmap(bitmap, null);
            }
            var glyph = new RasterizedGlyph(
                width, height, xOffset, yOffset, advance * layoutScale, coverage);
            SharedGlyphs.Add(key, glyph);
            return glyph;
        }
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
    /// <param name="opacity">Glyph opacity.</param>
    private static void AppendQuad(
        NativeBuffer<VertexT> vertices,
        float left,
        float top,
        float right,
        float bottom,
        AtlasGlyph glyph,
        float opacity)
    {
        var u0 = glyph.AtlasX / (float)AtlasWidth;
        var v0 = glyph.AtlasY / (float)AtlasHeight;
        var u1 = (glyph.AtlasX + glyph.Width) / (float)AtlasWidth;
        var v1 = (glyph.AtlasY + glyph.Height) / (float)AtlasHeight;
        vertices.Add(new VertexT(new(left, top, 0f), new(u0, v0), opacity));
        vertices.Add(new VertexT(new(left, bottom, 0f), new(u0, v1), opacity));
        vertices.Add(new VertexT(new(right, bottom, 0f), new(u1, v1), opacity));
        vertices.Add(new VertexT(new(right, bottom, 0f), new(u1, v1), opacity));
        vertices.Add(new VertexT(new(right, top, 0f), new(u1, v0), opacity));
        vertices.Add(new VertexT(new(left, top, 0f), new(u0, v0), opacity));
    }

    /// <summary>Keys glyphs by shape and baked foreground color.</summary>
    /// <param name="FontIndex">Fallback-chain face index.</param>
    /// <param name="Codepoint">Unicode codepoint.</param>
    /// <param name="PixelHeight">Physical pixel height.</param>
    /// <param name="Color">Baked foreground color.</param>
    private readonly record struct GlyphKey(
        int FontIndex, int Codepoint, int PixelHeight, Color Color);

    /// <summary>Keys immutable glyph shapes shared across renderer windows.</summary>
    /// <param name="FontIndex">Fallback-chain face index.</param>
    /// <param name="Codepoint">Unicode codepoint.</param>
    /// <param name="PixelHeight">Physical pixel height.</param>
    private readonly record struct GlyphShapeKey(int FontIndex, int Codepoint, int PixelHeight);

    /// <summary>Keys renderer-local shaped lines by text, height, direction, and typeface.</summary>
    /// <param name="Text">Run text.</param>
    /// <param name="PixelHeight">Physical font height.</param>
    /// <param name="Direction">Paragraph base direction.</param>
    /// <param name="FontFamily">Renderer-provided typeface.</param>
    private readonly record struct ShapedRunKey(
        string Text,
        int PixelHeight,
        TextFlowDirection Direction,
        UIFontFamily FontFamily);

    /// <summary>Stores visual glyphs, caret stops, and total advance for one line.</summary>
    /// <param name="Glyphs">Glyphs in visual order.</param>
    /// <param name="Carets">Caret stops in visual order.</param>
    /// <param name="Width">Total horizontal advance.</param>
    /// <param name="Cells">Grapheme cells in visual order.</param>
    private sealed record ShapedLine(
        ShapedGlyph[] Glyphs,
        CaretStop[] Carets,
        CaretCell[] Cells,
        float Width);

    /// <summary>Maps one logical UTF-16 caret index to a visual position.</summary>
    /// <param name="TextIndex">Logical UTF-16 index.</param>
    /// <param name="Position">Visual horizontal position.</param>
    private readonly record struct CaretStop(int TextIndex, float Position);

    /// <summary>Maps one logical grapheme range to its visual cell.</summary>
    /// <param name="TextStart">Inclusive logical UTF-16 start.</param>
    /// <param name="TextEnd">Exclusive logical UTF-16 end.</param>
    /// <param name="VisualStart">Inclusive visual start.</param>
    /// <param name="VisualEnd">Exclusive visual end.</param>
    private readonly record struct CaretCell(
        int TextStart,
        int TextEnd,
        float VisualStart,
        float VisualEnd);

    /// <summary>Stores one decoded glyph's source index and scaled horizontal metrics.</summary>
    /// <param name="FontIndex">Fallback-chain face index.</param>
    /// <param name="Codepoint">Unicode scalar value.</param>
    /// <param name="TextIndex">UTF-16 source index.</param>
    /// <param name="PreAdvance">Adjustment before the glyph.</param>
    /// <param name="Advance">Glyph advance after drawing.</param>
    /// <param name="XOffset">Horizontal shaped-glyph offset.</param>
    /// <param name="YOffset">Vertical shaped-glyph offset.</param>
    private readonly record struct ShapedGlyph(
        int FontIndex,
        int Codepoint,
        int TextIndex,
        float PreAdvance,
        float Advance,
        float XOffset,
        float YOffset);

    /// <summary>Stores one grapheme-safe logical span assigned to a system font.</summary>
    /// <param name="Utf16Start">Start relative to its directional run.</param>
    /// <param name="Utf16Length">UTF-16 length.</param>
    /// <param name="FontIndex">System fallback-chain face index.</param>
    private readonly record struct FontRun(int Utf16Start, int Utf16Length, int FontIndex);

    /// <summary>Owns one font's stb and HarfBuzz representations.</summary>
    /// <param name="RasterFont">stb rasterization face.</param>
    /// <param name="ShapingBlob">HarfBuzz font bytes.</param>
    /// <param name="ShapingFace">HarfBuzz face.</param>
    /// <param name="ShapingFont">HarfBuzz shaping font.</param>
    private sealed record FontFace(
        stbtt_fontinfo RasterFont,
        Blob ShapingBlob,
        Face ShapingFace,
        HarfBuzzFont ShapingFont) : IDisposable
    {
        /// <summary>Releases native shaping and rasterization resources.</summary>
        public void Dispose()
        {
            ShapingFont.Dispose();
            ShapingFace.Dispose();
            ShapingBlob.Dispose();
            RasterFont.Dispose();
        }
    }

    /// <summary>Stores immutable oversampled glyph coverage and layout metrics.</summary>
    /// <param name="Width">Bitmap width.</param>
    /// <param name="Height">Bitmap height.</param>
    /// <param name="XOffset">Horizontal bearing.</param>
    /// <param name="YOffset">Vertical bearing.</param>
    /// <param name="Advance">Scaled horizontal advance.</param>
    /// <param name="Coverage">Oversampled alpha coverage.</param>
    private sealed record RasterizedGlyph(
        int Width,
        int Height,
        int XOffset,
        int YOffset,
        float Advance,
        byte[] Coverage);

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
