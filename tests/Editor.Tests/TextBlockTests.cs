using Engine.Graphics;
using Engine.UI;
using Xunit;

namespace Editor.Tests;

/// <summary>Verifies retained text display layout.</summary>
public sealed class TextBlockTests
{
    /// <summary>Verifies logical start alignment follows inherited right-to-left flow.</summary>
    [Fact]
    public void StartAlignment_RightToLeft_PlacesTextAtRightEdge()
    {
        var root = new Canvas { Width = 100f, Height = 30f, FlowDirection = UIFlowDirection.RightToLeft };
        var text = new TextBlock("abc", 100f, 30f)
        {
            TextLayoutOverride = new FixedTextLayoutService()
        };
        root.Add(text, System.Numerics.Vector2.Zero);

        var command = Assert.Single(root.BuildDrawList().Commands,
            candidate => candidate.Type == UIDrawCommandType.Text);

        Assert.Equal(70f, command.Left);
    }

    /// <summary>Verifies wrapping prefers whitespace and retains cached lines across unchanged paint.</summary>
    [Fact]
    public void Wrap_UsesWhitespaceAndReusesCachedLayout()
    {
        var metrics = new FixedTextLayoutService();
        var text = new TextBlock("aaaa aaaa", 50f, 60f)
        {
            Wrapping = TextWrapMode.Wrap,
            TextLayoutOverride = metrics
        };

        var first = text.BuildDrawList();
        var callsAfterFirstPaint = metrics.MeasureCount;
        var second = text.BuildDrawList();

        Assert.Equal(2, first.Commands.Count);
        Assert.Equal("aaaa", first.Commands[0].Text);
        Assert.Equal("aaaa", first.Commands[1].Text);
        Assert.Same(first, second);
        Assert.Equal(callsAfterFirstPaint, metrics.MeasureCount);
    }

    /// <summary>Verifies trimming preserves grapheme boundaries and emits a fitting ellipsis.</summary>
    [Fact]
    public void CharacterEllipsis_TrimsToAvailableWidth()
    {
        var text = new TextBlock("abcdef", 35f, 24f)
        {
            Trimming = TextTrimming.CharacterEllipsis,
            TextLayoutOverride = new FixedTextLayoutService()
        };

        var command = Assert.Single(text.BuildDrawList().Commands);

        Assert.Equal("ab\u2026", command.Text);
    }

    /// <summary>Verifies trailing alignment uses measured line width inside the content box.</summary>
    [Fact]
    public void RightAlignment_OffsetsLineWithinContentWidth()
    {
        var text = new TextBlock("aa", 100f, 24f)
        {
            TextAlignment = TextAlignment.Right,
            TextLayoutOverride = new FixedTextLayoutService()
        };

        var command = Assert.Single(text.BuildDrawList().Commands);

        Assert.Equal(80f, command.Left);
    }

    /// <summary>Verifies wrapping never separates a combining mark from its base character.</summary>
    [Fact]
    public void Wrap_PreservesCombiningCharacterGrapheme()
    {
        var text = new TextBlock("e\u0301x", 15f, 48f)
        {
            Wrapping = TextWrapMode.Wrap,
            TextLayoutOverride = new FixedTextLayoutService()
        };

        var commands = text.BuildDrawList().Commands;

        Assert.Equal(2, commands.Count);
        Assert.Equal("e\u0301", commands[0].Text);
        Assert.Equal("x", commands[1].Text);
    }

    /// <summary>Verifies a bounded wrapped block marks omitted lines with an ellipsis.</summary>
    [Fact]
    public void MaxLines_TrimsFinalVisibleLine()
    {
        var text = new TextBlock("aa aa aa", 20f, 48f)
        {
            Wrapping = TextWrapMode.Wrap,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLines = 2,
            TextLayoutOverride = new FixedTextLayoutService()
        };

        var commands = text.BuildDrawList().Commands;

        Assert.Equal(2, commands.Count);
        Assert.Equal("a\u2026", commands[1].Text);
    }

    /// <summary>Provides deterministic fixed-width test metrics.</summary>
    private sealed class FixedTextLayoutService : ITextLayoutService
    {
        /// <summary>Gets the number of measurement calls.</summary>
        internal int MeasureCount { get; private set; }

        /// <inheritdoc/>
        public float MeasureWidth(ReadOnlySpan<char> text, float fontSize)
        {
            MeasureCount++;
            return text.Length * 10f;
        }

        /// <inheritdoc/>
        public int HitTestCaret(
            ReadOnlySpan<char> text,
            float fontSize,
            float horizontalPosition) =>
            Math.Clamp((int)MathF.Round(horizontalPosition / 10f), 0, text.Length);
    }
}
