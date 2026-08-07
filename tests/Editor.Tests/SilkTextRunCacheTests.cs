using Engine.Graphics;
using Xunit;

namespace Editor.Tests;

/// <summary>Verifies renderer-local decoded text-run caching.</summary>
public sealed class SilkTextRunCacheTests
{
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
