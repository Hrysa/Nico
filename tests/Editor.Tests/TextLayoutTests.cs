using Engine.Graphics;
using Xunit;

namespace Editor.Tests;

/// <summary>Verifies renderer-backed text measurement and caret placement.</summary>
public sealed class TextLayoutTests
{
    /// <summary>Verifies inferred RTL shaping maps visual edges to logical caret edges.</summary>
    [Fact]
    public void SilkTextLayout_HitTestCaret_MapsRightToLeftEdges()
    {
        using var window = new SilkWindow();
        const string text = "مرحبا";
        var width = window.MeasureWidth(text.AsSpan(), 20f);

        Assert.Equal(text.Length, window.HitTestCaret(text.AsSpan(), 20f, 0f));
        Assert.Equal(0, window.HitTestCaret(text.AsSpan(), 20f, width + 1f));
    }

    /// <summary>Verifies Silk measurement is deterministic and accumulates real glyph advances.</summary>
    [Fact]
    public void SilkTextLayout_MeasureWidth_UsesFontMetrics()
    {
        using var window = new SilkWindow();
        const float fontSize = 20f;

        var shortWidth = window.MeasureWidth("Inter".AsSpan(), fontSize);
        var longWidth = window.MeasureWidth("Inter UI".AsSpan(), fontSize);

        Assert.True(shortWidth > 0f);
        Assert.True(longWidth > shortWidth);
        Assert.Equal(shortWidth, window.MeasureWidth("Inter".AsSpan(), fontSize));
    }

    /// <summary>Verifies caret hit testing never returns the middle of a surrogate pair.</summary>
    [Fact]
    public void SilkTextLayout_HitTestCaret_PreservesUnicodeScalar()
    {
        using var window = new SilkWindow();
        const string text = "\U0001F642";
        var width = window.MeasureWidth(text.AsSpan(), 20f);

        var leftCaret = window.HitTestCaret(text.AsSpan(), 20f, width * 0.25f);
        var rightCaret = window.HitTestCaret(text.AsSpan(), 20f, width * 0.75f);

        Assert.Equal(0, leftCaret);
        Assert.Equal(text.Length, rightCaret);
    }

    /// <summary>Verifies mixed text is split into UAX #9 visual runs rather than one guessed run.</summary>
    [Fact]
    public void BidiResolver_MixedLeftToRightParagraph_ReordersNestedNumberRun()
    {
        var resolver = new Engine.Graphics.Bidi.BidiResolver();

        var line = resolver.Resolve("abc אבג 123".AsSpan(), TextFlowDirection.LeftToRight);

        Assert.Equal(0, line.ParagraphLevel);
        Assert.Collection(line.Runs,
            run => Assert.Equal((0, 4, 0), (run.Utf16Start, run.Utf16Length, run.Level)),
            run => Assert.Equal((8, 3, 2), (run.Utf16Start, run.Utf16Length, run.Level)),
            run => Assert.Equal((4, 4, 1), (run.Utf16Start, run.Utf16Length, run.Level)));
    }

    /// <summary>Verifies an isolate keeps following paragraph text outside its resolved run sequence.</summary>
    [Fact]
    public void BidiResolver_RightToLeftIsolate_ContainsNestedNumbers()
    {
        var data = new Engine.Graphics.Bidi.BidiData(
            "A \u2067אב 12\u2069 Z".AsSpan(), 0);
        var algorithm = new Engine.Graphics.Bidi.BidiAlgorithm();

        algorithm.Process(data);

        Assert.Equal(0, algorithm.ResolvedParagraphEmbeddingLevel);
        Assert.True(algorithm.ResolvedLevels[3] % 2 == 1);
        Assert.True(algorithm.ResolvedLevels[6] >= 2);
        Assert.Equal(0, algorithm.ResolvedLevels[^1]);
    }

    /// <summary>Verifies caret hit testing follows visual runs while returning logical UTF-16 indices.</summary>
    [Fact]
    public void SilkTextLayout_MixedDirection_CaretRoundTripsAtVisualEdges()
    {
        using var window = new SilkWindow();
        const string text = "abc אבג 123";
        const float size = 20f;
        var width = window.MeasureWidth(text.AsSpan(), size, TextFlowDirection.LeftToRight);

        Assert.Equal(0, window.HitTestCaret(
            text.AsSpan(), size, 0f, TextFlowDirection.LeftToRight));
        Assert.Equal(4, window.HitTestCaret(
            text.AsSpan(), size, width + 1f, TextFlowDirection.LeftToRight));
        for (var caret = 0; caret <= 3; caret++)
        {
            var position = window.GetCaretPosition(
                text.AsSpan(), size, caret, TextFlowDirection.LeftToRight);
            Assert.Equal(caret, window.HitTestCaret(
                text.AsSpan(), size, position, TextFlowDirection.LeftToRight));
        }
    }

    /// <summary>Verifies a logical selection crossing bidi runs produces disjoint visual highlights.</summary>
    [Fact]
    public void SilkTextLayout_MixedDirection_SelectionProducesVisualRanges()
    {
        using var window = new SilkWindow();
        const string text = "abc אבג 123";

        var ranges = window.GetSelectionRanges(
            text.AsSpan(), 20f, 1, 6, TextFlowDirection.LeftToRight);

        Assert.Equal(2, ranges.Length);
        Assert.True(ranges[0].Width > 0f);
        Assert.True(ranges[1].Width > 0f);
        Assert.True(ranges[0].Left + ranges[0].Width < ranges[1].Left);
    }
}
