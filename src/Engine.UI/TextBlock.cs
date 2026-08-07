using System.Globalization;
using System.Numerics;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Controls whether a text block wraps lines to its available width.</summary>
public enum TextWrapMode
{
    /// <summary>Only explicit newline characters create lines.</summary>
    NoWrap,

    /// <summary>Text wraps at whitespace or grapheme boundaries.</summary>
    Wrap
}

/// <summary>Controls how text that exceeds a non-wrapping line is displayed.</summary>
public enum TextTrimming
{
    /// <summary>Overflowing text remains intact and relies on ancestor clipping.</summary>
    None,

    /// <summary>Overflowing text is shortened at a grapheme boundary and ends with an ellipsis.</summary>
    CharacterEllipsis
}

/// <summary>Controls horizontal text placement within a text block.</summary>
public enum TextAlignment
{
    /// <summary>Aligns lines to the leading edge.</summary>
    Left,

    /// <summary>Centers lines horizontally.</summary>
    Center,

    /// <summary>Aligns lines to the trailing edge.</summary>
    Right,

    /// <summary>Aligns lines to the logical leading edge for the inherited flow direction.</summary>
    Start,

    /// <summary>Aligns lines to the logical trailing edge for the inherited flow direction.</summary>
    End
}

/// <summary>Displays retained multiline text with wrapping, trimming, and alignment.</summary>
public sealed class TextBlock : UIElement
{
    private readonly List<TextLine> _lines = [];
    private string _text = string.Empty;
    private float _fontSize = UITheme.Dark.FontSize;
    private float _lineHeight;
    private TextWrapMode _wrapping;
    private TextTrimming _trimming;
    private TextAlignment _textAlignment = TextAlignment.Start;
    private int _maxLines;
    private int[] _textElementBoundaries = [];
    private bool _lineCacheValid;
    private float _cachedWidth;
    private ITextLayoutService? _cachedTextLayout;

    /// <summary>Creates a retained text block.</summary>
    /// <param name="text">Initial displayed text.</param>
    /// <param name="width">Optional explicit width.</param>
    /// <param name="height">Optional explicit height.</param>
    public TextBlock(string text = "", float width = 0f, float height = 0f)
        : base(width, height)
    {
        Text = text;
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    /// <summary>Gets or sets the displayed text.</summary>
    public string Text
    {
        get => _text;
        set
        {
            var resolved = value ?? string.Empty;
            if (_text == resolved)
                return;
            _text = resolved;
            _textElementBoundaries = StringInfo.ParseCombiningCharacters(_text);
            InvalidateTextLayout();
        }
    }

    /// <summary>Gets or sets the font height in logical pixels.</summary>
    public float FontSize
    {
        get => _fontSize;
        set
        {
            if (value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_fontSize == value)
                return;
            _fontSize = value;
            InvalidateTextLayout();
        }
    }

    /// <summary>Gets or sets the line advance, or zero to use 1.2 times the font size.</summary>
    public float LineHeight
    {
        get => _lineHeight;
        set
        {
            if (value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_lineHeight == value)
                return;
            _lineHeight = value;
            InvalidateTextLayout();
        }
    }

    /// <summary>Gets or sets line wrapping behavior.</summary>
    public TextWrapMode Wrapping
    {
        get => _wrapping;
        set { if (_wrapping != value) { _wrapping = value; InvalidateTextLayout(); } }
    }

    /// <summary>Gets or sets non-wrapping overflow behavior.</summary>
    public TextTrimming Trimming
    {
        get => _trimming;
        set { if (_trimming != value) { _trimming = value; InvalidateTextLayout(); } }
    }

    /// <summary>Gets or sets horizontal line alignment.</summary>
    public TextAlignment TextAlignment
    {
        get => _textAlignment;
        set { if (_textAlignment != value) { _textAlignment = value; InvalidateVisual(); } }
    }

    /// <summary>Gets or sets the maximum displayed line count, or zero for no limit.</summary>
    public int MaxLines
    {
        get => _maxLines;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_maxLines == value)
                return;
            _maxLines = value;
            InvalidateTextLayout();
        }
    }

    /// <inheritdoc/>
    protected override Vector2 MeasureOverride(Vector2 availableSize)
    {
        var contentWidth = MathF.Max(0f, availableSize.X - Padding.Horizontal);
        EnsureLines(contentWidth);
        var width = 0f;
        for (var index = 0; index < _lines.Count; index++)
            width = MathF.Max(width, _lines[index].Width);
        return new Vector2(width + Padding.Horizontal,
            _lines.Count * GetLineHeight() + Padding.Vertical);
    }

    /// <inheritdoc/>
    protected override void ArrangeOverride(Vector2 contentSize)
    {
        EnsureLines(contentSize.X);
    }

    /// <inheritdoc/>
    protected override void Paint(UIDrawList drawList)
    {
        EnsureLines(ContentWidth);
        var lineHeight = GetLineHeight();
        for (var index = 0; index < _lines.Count; index++)
        {
            var line = _lines[index];
            var offset = _textAlignment switch
            {
                TextAlignment.Center => MathF.Max(0f, (ContentWidth - line.Width) * 0.5f),
                TextAlignment.Right => MathF.Max(0f, ContentWidth - line.Width),
                TextAlignment.Start when FlowDirection == UIFlowDirection.RightToLeft =>
                    MathF.Max(0f, ContentWidth - line.Width),
                TextAlignment.End when FlowDirection == UIFlowDirection.LeftToRight =>
                    MathF.Max(0f, ContentWidth - line.Width),
                _ => 0f
            };
            drawList.AddText(line.Text, ContentLeft + offset, ContentTop + index * lineHeight,
                FontSize, ForegroundColor, BackgroundColor, FlowDirection.ToTextFlowDirection());
        }
    }

    /// <summary>Invalidates cached line construction and desired size.</summary>
    private void InvalidateTextLayout()
    {
        _lineCacheValid = false;
        InvalidateMeasure();
    }

    /// <summary>Gets the effective vertical line advance.</summary>
    /// <returns>Line advance in logical pixels.</returns>
    private float GetLineHeight() => _lineHeight > 0f ? _lineHeight : FontSize * 1.2f;

    /// <summary>Builds cached display lines for an available content width.</summary>
    /// <param name="availableWidth">Available content width.</param>
    private void EnsureLines(float availableWidth)
    {
        if (_lineCacheValid && _cachedWidth == availableWidth &&
            ReferenceEquals(_cachedTextLayout, TextLayout))
            return;
        _lines.Clear();
        _cachedWidth = availableWidth;
        _cachedTextLayout = TextLayout;
        BuildLines(availableWidth);
        LimitLines(availableWidth);
        if (_lines.Count == 0)
            _lines.Add(new TextLine(string.Empty, 0f));
        _lineCacheValid = true;
    }

    /// <summary>Splits explicit lines and applies wrapping or trimming.</summary>
    /// <param name="availableWidth">Available content width.</param>
    private void BuildLines(float availableWidth)
    {
        var start = 0;
        while (start <= _text.Length)
        {
            var end = _text.IndexOf('\n', start);
            if (end < 0)
                end = _text.Length;
            var logicalEnd = end > start && _text[end - 1] == '\r' ? end - 1 : end;
            if (_wrapping == TextWrapMode.Wrap && float.IsFinite(availableWidth))
                AppendWrappedLine(start, logicalEnd, availableWidth);
            else
                AppendUnwrappedLine(start, logicalEnd, availableWidth);
            if (end == _text.Length)
                break;
            start = end + 1;
        }
    }

    /// <summary>Adds a logical line, applying optional character ellipsis.</summary>
    /// <param name="start">UTF-16 line start.</param>
    /// <param name="end">Exclusive UTF-16 line end.</param>
    /// <param name="availableWidth">Available content width.</param>
    private void AppendUnwrappedLine(int start, int end, float availableWidth)
    {
        var span = _text.AsSpan(start, end - start);
        var width = MeasureTextWidth(span);
        if (_trimming != TextTrimming.CharacterEllipsis || !float.IsFinite(availableWidth) ||
            width <= availableWidth)
        {
            _lines.Add(new TextLine(_text.Substring(start, end - start), width));
            return;
        }
        const string ellipsis = "\u2026";
        var ellipsisWidth = MeasureTextWidth(ellipsis.AsSpan());
        var fit = FindFittingEnd(start, end, MathF.Max(0f, availableWidth - ellipsisWidth));
        var displayed = _text.Substring(start, fit - start) + ellipsis;
        _lines.Add(new TextLine(displayed, MathF.Min(availableWidth,
            MeasureTextWidth(displayed.AsSpan()))));
    }

    /// <summary>Adds one logical line as width-bounded wrapped display lines.</summary>
    /// <param name="start">UTF-16 line start.</param>
    /// <param name="end">Exclusive UTF-16 line end.</param>
    /// <param name="availableWidth">Available content width.</param>
    private void AppendWrappedLine(int start, int end, float availableWidth)
    {
        if (start == end)
        {
            _lines.Add(new TextLine(string.Empty, 0f));
            return;
        }
        while (start < end)
        {
            while (start < end && char.IsWhiteSpace(_text[start]))
                start++;
            if (start == end)
                break;
            var fit = FindFittingEnd(start, end, availableWidth);
            if (fit == start)
                fit = NextTextElementIndex(start, end);
            var breakEnd = fit;
            var next = fit;
            if (fit < end)
            {
                var whitespace = FindLastWhitespace(start, fit);
                if (whitespace > start)
                {
                    breakEnd = whitespace;
                    next = whitespace;
                    while (next < end && char.IsWhiteSpace(_text[next]))
                        next++;
                }
            }
            var displayed = _text.Substring(start, breakEnd - start);
            _lines.Add(new TextLine(displayed,
                MeasureTextWidth(displayed.AsSpan())));
            start = next;
        }
    }

    /// <summary>Finds the greatest grapheme boundary whose prefix fits a width.</summary>
    /// <param name="start">UTF-16 range start.</param>
    /// <param name="end">Exclusive UTF-16 range end.</param>
    /// <param name="availableWidth">Maximum width.</param>
    /// <returns>Exclusive fitting UTF-16 boundary.</returns>
    private int FindFittingEnd(int start, int end, float availableWidth)
    {
        var fit = start;
        var candidate = start;
        while (candidate < end)
        {
            candidate = NextTextElementIndex(candidate, end);
            if (MeasureTextWidth(_text.AsSpan(start, candidate - start)) > availableWidth)
                break;
            fit = candidate;
        }
        return fit;
    }

    /// <summary>Advances one Unicode text-element boundary.</summary>
    /// <param name="index">Current UTF-16 index.</param>
    /// <param name="end">Exclusive range end.</param>
    /// <returns>Next valid text-element boundary.</returns>
    private int NextTextElementIndex(int index, int end)
    {
        var position = Array.BinarySearch(_textElementBoundaries, index);
        var nextPosition = position >= 0 ? position + 1 : ~position;
        return nextPosition < _textElementBoundaries.Length
            ? Math.Min(end, _textElementBoundaries[nextPosition])
            : end;
    }

    /// <summary>Applies the configured line limit and optional final-line ellipsis.</summary>
    /// <param name="availableWidth">Available content width.</param>
    private void LimitLines(float availableWidth)
    {
        if (_maxLines == 0 || _lines.Count <= _maxLines)
            return;
        _lines.RemoveRange(_maxLines, _lines.Count - _maxLines);
        if (_trimming != TextTrimming.CharacterEllipsis || _lines.Count == 0)
            return;
        var lastIndex = _lines.Count - 1;
        _lines[lastIndex] = Ellipsize(_lines[lastIndex].Text, availableWidth);
    }

    /// <summary>Adds an ellipsis while preserving complete Unicode text elements.</summary>
    /// <param name="text">Last visible line.</param>
    /// <param name="availableWidth">Available content width.</param>
    /// <returns>Ellipsized cached line.</returns>
    private TextLine Ellipsize(string text, float availableWidth)
    {
        const string ellipsis = "\u2026";
        var ellipsisWidth = MeasureTextWidth(ellipsis.AsSpan());
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        var fit = 0;
        for (var index = 0; index < boundaries.Length; index++)
        {
            var end = index + 1 < boundaries.Length ? boundaries[index + 1] : text.Length;
            if (MeasureTextWidth(text.AsSpan(0, end)) + ellipsisWidth > availableWidth)
                break;
            fit = end;
        }
        var displayed = text[..fit] + ellipsis;
        return new TextLine(displayed,
            MeasureTextWidth(displayed.AsSpan()));
    }

    /// <summary>Measures a span using the inherited paragraph direction.</summary>
    /// <param name="text">Text to measure.</param>
    /// <returns>Horizontal advance.</returns>
    private float MeasureTextWidth(ReadOnlySpan<char> text) =>
        TextLayout.MeasureWidth(text, FontSize, FlowDirection.ToTextFlowDirection());

    /// <summary>Finds the last whitespace boundary within a fitting range.</summary>
    /// <param name="start">Range start.</param>
    /// <param name="end">Exclusive range end.</param>
    /// <returns>Whitespace index, or the range start when none exists.</returns>
    private int FindLastWhitespace(int start, int end)
    {
        for (var index = end - 1; index >= start; index--)
        {
            if (char.IsWhiteSpace(_text[index]))
                return index;
        }
        return start;
    }

    /// <summary>Stores one cached display line and its measured advance.</summary>
    /// <param name="Text">Displayed line text.</param>
    /// <param name="Width">Measured line width.</param>
    private readonly record struct TextLine(string Text, float Width);
}
