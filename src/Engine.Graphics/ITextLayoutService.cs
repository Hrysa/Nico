namespace Engine.Graphics;

/// <summary>Specifies the base direction used to resolve bidirectional text.</summary>
public enum TextFlowDirection
{
    /// <summary>Resolve the paragraph from a left-to-right base level.</summary>
    LeftToRight,

    /// <summary>Resolve the paragraph from a right-to-left base level.</summary>
    RightToLeft,

    /// <summary>Derive the base level from the first strong character.</summary>
    Auto
}

/// <summary>Describes one contiguous visual portion of a logical text selection.</summary>
/// <param name="Left">Horizontal offset from the text origin.</param>
/// <param name="Width">Non-negative visual width.</param>
public readonly record struct TextSelectionRange(float Left, float Width);

/// <summary>Measures renderer-independent text and maps horizontal positions to caret indices.</summary>
public interface ITextLayoutService
{
    /// <summary>Measures the horizontal advance of text.</summary>
    /// <param name="text">Text to measure.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <returns>Horizontal advance in logical pixels.</returns>
    float MeasureWidth(ReadOnlySpan<char> text, float fontSize);

    /// <summary>Measures text after resolving it with an explicit paragraph direction.</summary>
    /// <param name="text">Text to measure.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <returns>Horizontal advance in logical pixels.</returns>
    float MeasureWidth(
        ReadOnlySpan<char> text,
        float fontSize,
        TextFlowDirection direction) => MeasureWidth(text, fontSize);

    /// <summary>Maps a horizontal position to the nearest UTF-16 caret index.</summary>
    /// <param name="text">Text whose caret positions are tested.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <param name="horizontalPosition">Position relative to the text origin.</param>
    /// <returns>A UTF-16 index between zero and the text length.</returns>
    int HitTestCaret(ReadOnlySpan<char> text, float fontSize, float horizontalPosition);

    /// <summary>Maps a horizontal position to a caret after bidirectional resolution.</summary>
    /// <param name="text">Text whose caret positions are tested.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <param name="horizontalPosition">Position relative to the visual text origin.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <returns>A UTF-16 index between zero and the text length.</returns>
    int HitTestCaret(
        ReadOnlySpan<char> text,
        float fontSize,
        float horizontalPosition,
        TextFlowDirection direction) => HitTestCaret(text, fontSize, horizontalPosition);

    /// <summary>Maps a logical caret index to its visual horizontal position.</summary>
    /// <param name="text">Text containing the caret.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <param name="caretIndex">UTF-16 caret index.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <returns>Visual horizontal position relative to the text origin.</returns>
    float GetCaretPosition(
        ReadOnlySpan<char> text,
        float fontSize,
        int caretIndex,
        TextFlowDirection direction)
    {
        if ((uint)caretIndex > (uint)text.Length)
            throw new ArgumentOutOfRangeException(nameof(caretIndex));
        var prefix = MeasureWidth(text[..caretIndex], fontSize);
        return direction == TextFlowDirection.RightToLeft
            ? MeasureWidth(text, fontSize) - prefix
            : prefix;
    }

    /// <summary>Resolves a logical selection into contiguous visual ranges.</summary>
    /// <param name="text">Text containing the selection.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <param name="selectionStart">Logical UTF-16 selection start.</param>
    /// <param name="selectionLength">Logical UTF-16 selection length.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <returns>Visual selection ranges ordered from left to right.</returns>
    TextSelectionRange[] GetSelectionRanges(
        ReadOnlySpan<char> text,
        float fontSize,
        int selectionStart,
        int selectionLength,
        TextFlowDirection direction)
    {
        if (selectionStart < 0 || selectionLength < 0 ||
            selectionStart + selectionLength > text.Length)
            throw new ArgumentOutOfRangeException(nameof(selectionStart));
        if (selectionLength == 0)
            return [];
        var first = GetCaretPosition(text, fontSize, selectionStart, direction);
        var last = GetCaretPosition(
            text, fontSize, selectionStart + selectionLength, direction);
        return [new TextSelectionRange(MathF.Min(first, last), MathF.Abs(last - first))];
    }
}
