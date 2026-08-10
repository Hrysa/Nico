using Engine.Graphics;
using Xunit;

namespace Editor.Tests;

/// <summary>Verifies renderer-local decoded text-run caching.</summary>
public sealed class SilkTextRunCacheTests
{
    /// <summary>Verifies Windows system text selects the native hinted rasterization backend.</summary>
    [Fact]
    public void Rasterizer_WindowsLatin_UsesDirectWrite()
    {
        using var rasterizer = new TrueTypeFontRasterizer();

        Assert.Equal(OperatingSystem.IsWindows(), rasterizer.UsesDirectWriteFor("Hierarchy"));
        Assert.Equal(OperatingSystem.IsWindows(), rasterizer.UsesRgbSubpixelCoverage);
    }

    /// <summary>Verifies Windows preserves distinct RGB subpixel coverage in atlas pixels.</summary>
    [Fact]
    public void Atlas_WindowsLatin_PreservesRgbSubpixelCoverage()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var rasterizer = new TrueTypeFontRasterizer();
        using var vertices = new NativeBuffer<VertexT>();
        var command = new UIDrawCommand(
            0f, 0f, 0f, 0f, Color.White,
            UIDrawCommandType.Text, "A", 16f, Color.Black);

        rasterizer.AppendVertices(vertices, command, 1f);

        Assert.True(rasterizer.TryTakeAtlasUpdate(out var update));
        var foundSubpixel = false;
        for (var pixel = 0; pixel < update.Pixels.Length; pixel += 4)
        {
            var red = update.Pixels[pixel];
            var green = update.Pixels[pixel + 1];
            var blue = update.Pixels[pixel + 2];
            foundSubpixel |= update.Pixels[pixel + 3] is > 0 and < byte.MaxValue &&
                (red != green || green != blue);
        }
        Assert.True(foundSubpixel);
    }

    /// <summary>Verifies a sparse ClearType glyph does not bake an opaque background rectangle.</summary>
    [Fact]
    public void Atlas_WindowsSlash_KeepsUncoveredPixelsTransparent()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var rasterizer = new TrueTypeFontRasterizer();
        using var vertices = new NativeBuffer<VertexT>();
        var command = new UIDrawCommand(
            0f, 0f, 0f, 0f, Color.White,
            UIDrawCommandType.Text, "/", 20f, Color.FromSrgb(0x19, 0x1A, 0x1C));

        rasterizer.AppendVertices(vertices, command, 1f);

        Assert.True(rasterizer.TryTakeAtlasUpdate(out var update));
        var foundTransparentInterior = false;
        var foundCoveredInterior = false;
        for (var y = 2; y < update.Height - 2; y++)
        {
            for (var x = 2; x < update.Width - 2; x++)
            {
                var alpha = update.Pixels[(y * update.Width + x) * 4 + 3];
                foundTransparentInterior |= alpha == 0;
                foundCoveredInterior |= alpha != 0;
            }
        }
        Assert.True(foundTransparentInterior);
        Assert.True(foundCoveredInterior);
    }

    /// <summary>Verifies system fonts shape Arabic without emitting missing glyphs.</summary>
    [Fact]
    public void ShapeText_Arabic_SelectsFallbackFace()
    {
        using var rasterizer = new TrueTypeFontRasterizer();

        Assert.True(rasterizer.GetSelectedFontIndex("Profiler") >= 0);
        Assert.True(rasterizer.GetShapedGlyphCount("مرحبا", 20) > 0);
        Assert.Equal(0, rasterizer.GetMissingGlyphCount("مرحبا", 20));
    }

    /// <summary>Verifies Hebrew is resolved through an installed system face.</summary>
    [Fact]
    public void ShapeText_Hebrew_HasSystemGlyphCoverage()
    {
        using var rasterizer = new TrueTypeFontRasterizer();

        Assert.Equal(0, rasterizer.GetMissingGlyphCount("מנוע", 20));
    }

    /// <summary>Verifies CJK text resolves to an installed system face and rasterizes.</summary>
    [Fact]
    public void ShapeText_Chinese_HasVisibleSystemGlyphCoverage()
    {
        using var rasterizer = new TrueTypeFontRasterizer();

        Assert.Equal(0, rasterizer.GetMissingGlyphCount("啊啊啊", 20));
        Assert.Equal(3, rasterizer.GetVisibleGlyphCount("啊啊啊", 20));
    }

    /// <summary>Verifies an emoji joiner cannot switch surrounding Latin text to another face.</summary>
    [Fact]
    public void ShapeText_EmojiSequence_PreservesSurroundingLatinFace()
    {
        using var rasterizer = new TrueTypeFontRasterizer();
        const string text = "Graphemes: 👨‍👩‍👧‍👦  e\u0301  🇨🇳";
        var latinIndex = rasterizer.GetSelectedFontIndex("G");
        var composedIndex = text.IndexOf('e');

        Assert.Equal(latinIndex, rasterizer.GetShapedFontIndex(text, 20, 0));
        Assert.Equal(rasterizer.GetSelectedFontIndex("e\u0301"),
            rasterizer.GetShapedFontIndex(text, 20, composedIndex));
        Assert.Equal(0, rasterizer.GetMissingGlyphCount("Graphemes: e\u0301", 20));
        Assert.True(rasterizer.GetVisibleGlyphCount("Graphemes: e\u0301", 20) > 0);
    }

    /// <summary>Verifies installed Windows emoji faces provide rasterizable family and flag glyphs.</summary>
    [Fact]
    public void ShapeText_WindowsEmoji_ProducesVisibleSystemGlyphs()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using var rasterizer = new TrueTypeFontRasterizer();

        Assert.Equal(0, rasterizer.GetMissingGlyphCount("👨‍👩‍👧‍👦 🇨🇳", 24));
        Assert.True(rasterizer.GetVisibleGlyphCount("👨‍👩‍👧‍👦 🇨🇳", 24) >= 2);
    }

    /// <summary>Verifies canonically composable text is emitted as one shaped glyph.</summary>
    [Fact]
    public void ShapeText_ComposableCluster_ReducesGlyphCount()
    {
        using var rasterizer = new TrueTypeFontRasterizer();

        const string text = "A\u030A";
        var glyphCount = rasterizer.GetShapedGlyphCount(text, 20);

        Assert.True(glyphCount < text.Length);
    }

    /// <summary>Verifies equal text and size reuse one shaped run.</summary>
    [Fact]
    public void PrepareText_EqualKey_ReusesShapedRun()
    {
        using var rasterizer = new TrueTypeFontRasterizer();

        rasterizer.PrepareText("Profiler", 16);
        rasterizer.PrepareText("Profiler", 16);

        Assert.Equal(1, rasterizer.ShapedRunCount);
    }

    /// <summary>Verifies font size participates in shaped-run identity.</summary>
    [Fact]
    public void PrepareText_DifferentPixelHeight_CachesDistinctRun()
    {
        using var rasterizer = new TrueTypeFontRasterizer();

        rasterizer.PrepareText("Profiler", 16);
        rasterizer.PrepareText("Profiler", 24);

        Assert.Equal(2, rasterizer.ShapedRunCount);
    }

    /// <summary>Verifies paragraph direction participates in shaped-line cache identity.</summary>
    [Fact]
    public void PrepareText_DifferentDirection_CachesDistinctLine()
    {
        using var rasterizer = new TrueTypeFontRasterizer();

        rasterizer.PrepareText("123", 16, TextFlowDirection.LeftToRight);
        rasterizer.PrepareText("123", 16, TextFlowDirection.RightToLeft);

        Assert.Equal(2, rasterizer.ShapedRunCount);
    }

    /// <summary>Verifies the bundled Codicon face rasterizes its official check glyph.</summary>
    [Fact]
    public void AppendVertices_CodiconGlyph_UsesBundledFont()
    {
        using var rasterizer = new TrueTypeFontRasterizer();
        using var vertices = new NativeBuffer<VertexT>();
        var command = new UIDrawCommand(
            0f, 0f, 0f, 0f, Color.White,
            UIDrawCommandType.Text,
            "\uEAB2",
            20f,
            FontFamily: UIFontFamily.Codicon);

        rasterizer.AppendVertices(vertices, command, 1f);

        Assert.Equal(6, vertices.Count);
    }

    /// <summary>Verifies Latin glyph uploads include transparent texels for linear filtering.</summary>
    [Fact]
    public void TryTakeAtlasUpdate_LatinGlyph_IncludesTransparentSamplingHalo()
    {
        using var rasterizer = new TrueTypeFontRasterizer();
        using var vertices = new NativeBuffer<VertexT>();
        var command = new UIDrawCommand(
            0f, 0f, 0f, 0f, Color.White,
            UIDrawCommandType.Text,
            "A",
            20f);

        rasterizer.AppendVertices(vertices, command, 1f);

        Assert.True(rasterizer.TryTakeAtlasUpdate(out var update));
        Assert.Equal(0, update.X);
        Assert.Equal(0, update.Y);
        Assert.True(update.Width > 4);
        Assert.True(update.Height > 4);
        var foundCoverage = false;
        for (var y = 0; y < update.Height; y++)
        {
            for (var x = 0; x < update.Width; x++)
            {
                var alpha = update.Pixels[(y * update.Width + x) * 4 + 3];
                if (x < 2 || x >= update.Width - 2 ||
                    y < 2 || y >= update.Height - 2)
                    Assert.Equal(0, alpha);
                else
                    foundCoverage |= alpha != 0;
            }
        }
        Assert.True(foundCoverage);
    }

    /// <summary>Verifies mixed text exposes visual caret order without losing logical indices.</summary>
    [Fact]
    public void ShapeText_MixedDirection_ProducesReorderedCaretSequence()
    {
        using var rasterizer = new TrueTypeFontRasterizer();

        var carets = rasterizer.GetVisualCaretOrder(
            "abc אבג 123", 20, TextFlowDirection.LeftToRight);

        Assert.Equal(0, carets[0]);
        Assert.Equal(4, carets[^1]);
        Assert.True(Array.IndexOf(carets, 8) < Array.IndexOf(carets, 7));
        Assert.True(Array.IndexOf(carets, 11) < Array.IndexOf(carets, 7));
    }

    /// <summary>Verifies the renderer-local cache remains bounded under changing text.</summary>
    [Fact]
    public void PrepareText_ManyKeys_BoundsCache()
    {
        using var rasterizer = new TrueTypeFontRasterizer();
        for (var index = 0; index < 1100; index++)
            rasterizer.PrepareText($"Row {index}", 16);

        Assert.Equal(1024, rasterizer.ShapedRunCount);
    }
}
