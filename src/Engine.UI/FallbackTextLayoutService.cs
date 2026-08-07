using System.Buffers;
using System.Globalization;
using System.Text;
using Engine.Graphics;

namespace Engine.UI;

/// <summary>Provides allocation-free approximate startup metrics until a platform shaper is installed.</summary>
public sealed class FallbackTextLayoutService : ITextLayoutService
{
    private const float InterVerticalMetricsUnits = 2478f;
    private static readonly ushort[] _uppercaseAdvances =
    [
        1413, 1340, 1496, 1478, 1231, 1209, 1528, 1522, 550, 1169, 1376, 1158, 1850,
        1543, 1566, 1308, 1566, 1318, 1314, 1322, 1524, 1413, 2018, 1397, 1390, 1288
    ];
    private static readonly ushort[] _lowercaseAdvances =
    [
        1150, 1254, 1170, 1254, 1194, 758, 1256, 1211, 496, 496, 1124, 496, 1794,
        1210, 1228, 1254, 1254, 771, 1081, 670, 1211, 1151, 1676, 1118, 1151, 1131
    ];
    private static readonly ushort[] _digitAdvances =
    [
        1292, 833, 1249, 1265, 1323, 1215, 1270, 1159, 1267, 1270
    ];

    /// <summary>Gets the shared fallback service.</summary>
    public static FallbackTextLayoutService Instance { get; } = new();

    /// <summary>Creates the fallback text service.</summary>
    private FallbackTextLayoutService()
    {
    }

    /// <inheritdoc/>
    public float MeasureWidth(ReadOnlySpan<char> text, float fontSize)
    {
        var advanceUnits = 0f;
        while (!text.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(text, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                consumed = 1;
                rune = Rune.ReplacementChar;
            }
            advanceUnits += GetInterAdvanceUnits(rune);
            text = text[consumed..];
        }
        return advanceUnits * fontSize / InterVerticalMetricsUnits;
    }

    /// <inheritdoc/>
    public int HitTestCaret(ReadOnlySpan<char> text, float fontSize, float horizontalPosition)
    {
        if (horizontalPosition <= 0f)
            return 0;
        var index = 0;
        var previousWidth = 0f;
        while (index < text.Length)
        {
            var status = Rune.DecodeFromUtf16(text[index..], out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                consumed = 1;
                rune = Rune.ReplacementChar;
            }
            var width = previousWidth +
                GetInterAdvanceUnits(rune) * fontSize / InterVerticalMetricsUnits;
            if (horizontalPosition < (previousWidth + width) * 0.5f)
                return index;
            previousWidth = width;
            index += consumed;
        }
        return text.Length;
    }

    /// <summary>Returns the fallback Inter advance for one scalar value.</summary>
    /// <param name="rune">Unicode scalar value to measure.</param>
    /// <returns>Horizontal advance in Inter font units.</returns>
    private static float GetInterAdvanceUnits(Rune rune)
    {
        var codepoint = rune.Value;
        if (codepoint is >= 'A' and <= 'Z')
            return _uppercaseAdvances[codepoint - 'A'];
        if (codepoint is >= 'a' and <= 'z')
            return _lowercaseAdvances[codepoint - 'a'];
        if (codepoint is >= '0' and <= '9')
            return _digitAdvances[codepoint - '0'];
        var category = Rune.GetUnicodeCategory(rune);
        if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or
            UnicodeCategory.SpacingCombiningMark)
            return 0f;
        return codepoint == ' ' ? 576f : 1200f;
    }
}
