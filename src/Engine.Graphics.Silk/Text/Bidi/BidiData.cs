// Portions derived from SixLabors.Fonts and Avalonia under the Apache License 2.0.
using System.Buffers;
using System.Text;

namespace Engine.Graphics.Bidi;

/// <summary>Stores scalar-level Unicode properties consumed by the bidi algorithm.</summary>
internal sealed class BidiData
{
    /// <summary>Initializes bidi data for one text line.</summary>
    /// <param name="text">UTF-16 text.</param>
    /// <param name="paragraphEmbeddingLevel">Zero for LTR, one for RTL, or two for automatic.</param>
    internal BidiData(ReadOnlySpan<char> text, sbyte paragraphEmbeddingLevel)
    {
        ParagraphEmbeddingLevel = paragraphEmbeddingLevel;
        var classes = new BidiClass[text.Length];
        var bracketTypes = new BidiPairedBracketType[text.Length];
        var bracketValues = new int[text.Length];
        ScalarUtf16Starts = new int[text.Length];
        var remaining = text;
        var utf16Index = 0;
        var scalarIndex = 0;
        while (!remaining.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(remaining, out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                rune = Rune.ReplacementChar;
                consumed = 1;
            }
            var codepoint = checked((uint)rune.Value);
            var bidiClass = BidiUnicodeData.GetClass(codepoint);
            var bracketType = BidiUnicodeData.GetPairedBracketType(codepoint);
            classes[scalarIndex] = bidiClass;
            bracketTypes[scalarIndex] = bracketType;
            bracketValues[scalarIndex] = bracketType switch
            {
                BidiPairedBracketType.Open => BidiUnicodeData.GetPairedBracket(codepoint),
                BidiPairedBracketType.Close => BidiUnicodeData.GetCanonicalBracket(codepoint),
                _ => 0
            };
            ScalarUtf16Starts[scalarIndex] = utf16Index;
            HasBrackets |= bracketType != BidiPairedBracketType.None;
            HasEmbeddings |= IsEmbedding(bidiClass);
            HasIsolates |= IsIsolate(bidiClass);
            scalarIndex++;
            utf16Index += consumed;
            remaining = remaining[consumed..];
        }
        Length = scalarIndex;
        Classes = new BidiArraySlice<BidiClass>(classes, 0, scalarIndex);
        PairedBracketTypes = new BidiArraySlice<BidiPairedBracketType>(
            bracketTypes, 0, scalarIndex);
        PairedBracketValues = new BidiArraySlice<int>(bracketValues, 0, scalarIndex);
    }

    /// <summary>Gets the requested paragraph embedding level.</summary>
    internal sbyte ParagraphEmbeddingLevel { get; }

    /// <summary>Gets whether paired brackets occur.</summary>
    internal bool HasBrackets { get; }

    /// <summary>Gets whether explicit embedding controls occur.</summary>
    internal bool HasEmbeddings { get; }

    /// <summary>Gets whether isolate controls occur.</summary>
    internal bool HasIsolates { get; }

    /// <summary>Gets the scalar count.</summary>
    internal int Length { get; }

    /// <summary>Gets bidi classes by scalar index.</summary>
    internal BidiArraySlice<BidiClass> Classes { get; }

    /// <summary>Gets paired-bracket roles by scalar index.</summary>
    internal BidiArraySlice<BidiPairedBracketType> PairedBracketTypes { get; }

    /// <summary>Gets canonical paired-bracket values by scalar index.</summary>
    internal BidiArraySlice<int> PairedBracketValues { get; }

    /// <summary>Gets each scalar's source UTF-16 start.</summary>
    internal int[] ScalarUtf16Starts { get; }

    /// <summary>Tests whether a class is an explicit embedding control.</summary>
    /// <param name="bidiClass">Class to test.</param>
    /// <returns>True for explicit embedding controls.</returns>
    private static bool IsEmbedding(BidiClass bidiClass) => bidiClass is
        BidiClass.LeftToRightEmbedding or BidiClass.LeftToRightOverride or
        BidiClass.RightToLeftEmbedding or BidiClass.RightToLeftOverride or
        BidiClass.PopDirectionalFormat;

    /// <summary>Tests whether a class is an isolate control.</summary>
    /// <param name="bidiClass">Class to test.</param>
    /// <returns>True for isolate controls.</returns>
    private static bool IsIsolate(BidiClass bidiClass) => bidiClass is
        BidiClass.LeftToRightIsolate or BidiClass.RightToLeftIsolate or
        BidiClass.FirstStrongIsolate or BidiClass.PopDirectionalIsolate;
}
