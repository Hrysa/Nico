using System.Globalization;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Provides shared grapheme-safe fitting and trimming for retained text controls.</summary>
internal static class UITextFitting
{
    /// <summary>Finds the greatest text-element boundary whose measured prefix fits.</summary>
    /// <param name="text">Complete source text.</param>
    /// <param name="boundaries">Sorted UTF-16 text-element starts for the source.</param>
    /// <param name="start">Inclusive source range start.</param>
    /// <param name="end">Exclusive source range end.</param>
    /// <param name="availableWidth">Maximum measured prefix width.</param>
    /// <param name="layout">Text measurement service.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <returns>The exclusive UTF-16 boundary of the greatest fitting prefix.</returns>
    internal static int FindFittingEnd(
        string text,
        ReadOnlySpan<int> boundaries,
        int start,
        int end,
        float availableWidth,
        ITextLayoutService layout,
        float fontSize,
        TextFlowDirection direction)
    {
        var low = 0;
        var high = boundaries.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (boundaries[middle] <= start)
                low = middle + 1;
            else
                high = middle;
        }
        var fit = start;
        for (var index = low; index <= boundaries.Length; index++)
        {
            var candidate = index < boundaries.Length ? Math.Min(end, boundaries[index]) : end;
            if (candidate <= fit)
                continue;
            if (layout.MeasureWidth(
                    text.AsSpan(start, candidate - start), fontSize, direction) > availableWidth)
                break;
            fit = candidate;
            if (candidate == end)
                break;
        }
        return fit;
    }

    /// <summary>Trims a string to a measured width and appends one shared ellipsis glyph.</summary>
    /// <param name="text">Text to fit.</param>
    /// <param name="availableWidth">Maximum measured width.</param>
    /// <param name="layout">Text measurement service.</param>
    /// <param name="fontSize">Font height in logical pixels.</param>
    /// <param name="direction">Paragraph base direction.</param>
    /// <param name="force">Whether to append an ellipsis even when the supplied text fits.</param>
    /// <returns>The original text, a grapheme-safe ellipsized prefix, or an empty string.</returns>
    internal static string Ellipsize(
        string text,
        float availableWidth,
        ITextLayoutService layout,
        float fontSize,
        TextFlowDirection direction,
        bool force = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!force && layout.MeasureWidth(text.AsSpan(), fontSize, direction) <= availableWidth)
            return text;
        const string ellipsis = "\u2026";
        var ellipsisWidth = layout.MeasureWidth(ellipsis.AsSpan(), fontSize, direction);
        if (ellipsisWidth > availableWidth)
            return string.Empty;
        var boundaries = StringInfo.ParseCombiningCharacters(text);
        var fit = FindFittingEnd(
            text,
            boundaries,
            0,
            text.Length,
            MathF.Max(0f, availableWidth - ellipsisWidth),
            layout,
            fontSize,
            direction);
        return text[..fit] + ellipsis;
    }
}
