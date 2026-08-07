// Unicode 17 trie data is generated from the Unicode Character Database.
// Trie implementation derived from RichTextKit/Avalonia under Apache License 2.0.
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Engine.Graphics.Bidi;

/// <summary>Looks up Unicode 17 bidirectional properties.</summary>
internal static partial class BidiUnicodeData
{
    private const int PairedBracketBits = 16;
    private const int PairedBracketTypeBits = 2;
    private const int BidiClassBits = 5;
    private const int PairedBracketTypeShift = PairedBracketBits;
    private const int BidiClassShift = PairedBracketBits + PairedBracketTypeBits;
    private const int PairedBracketMask = (1 << PairedBracketBits) - 1;
    private const int PairedBracketTypeMask = (1 << PairedBracketTypeBits) - 1;
    private const int BidiClassMask = (1 << BidiClassBits) - 1;

    /// <summary>Gets the bidi class for a scalar.</summary>
    /// <param name="codepoint">Unicode scalar.</param>
    /// <returns>The scalar's bidi class.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static BidiClass GetClass(uint codepoint) =>
        (BidiClass)((BidiTrie.Get(codepoint) >> BidiClassShift) & BidiClassMask);

    /// <summary>Gets the paired-bracket role for a scalar.</summary>
    /// <param name="codepoint">Unicode scalar.</param>
    /// <returns>The scalar's bracket role.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static BidiPairedBracketType GetPairedBracketType(uint codepoint) =>
        (BidiPairedBracketType)((BidiTrie.Get(codepoint) >> PairedBracketTypeShift) &
            PairedBracketTypeMask);

    /// <summary>Gets the canonical paired-bracket scalar.</summary>
    /// <param name="codepoint">Unicode scalar.</param>
    /// <returns>The paired scalar, canonically normalized for U+3008/U+3009.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetPairedBracket(uint codepoint)
    {
        var paired = BidiTrie.Get(codepoint) & PairedBracketMask;
        return checked((int)(paired switch
        {
            0x3008 => 0x2329u,
            0x3009 => 0x232Au,
            _ => paired
        }));
    }

    /// <summary>Gets the canonical representation used to compare a closing bracket.</summary>
    /// <param name="codepoint">Closing bracket scalar.</param>
    /// <returns>Canonical bracket value.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int GetCanonicalBracket(uint codepoint) => checked((int)(codepoint switch
    {
        0x3008 => 0x2329u,
        0x3009 => 0x232Au,
        _ => codepoint
    }));
}

/// <summary>Reads a compact serialized Unicode trie.</summary>
internal readonly ref struct BidiUnicodeTrie
{
    private const int Shift1 = 11;
    private const int Shift2 = 5;
    private const int OmittedBmpIndex1Length = 0x10000 >> Shift1;
    private const int Index2Mask = (1 << (Shift1 - Shift2)) - 1;
    private const int DataMask = (1 << Shift2) - 1;
    private const int IndexShift = 2;
    private const int DataGranularity = 1 << IndexShift;
    private const int LscpIndex2Offset = 0x10000 >> Shift2;
    private const int Index1Offset = LscpIndex2Offset + (0x400 >> Shift2) + (0x800 >> 6);
    private readonly ReadOnlySpan<uint> _data;
    private readonly int _highStart;
    private readonly uint _errorValue;

    /// <summary>Initializes a trie reader.</summary>
    /// <param name="data">Serialized trie words.</param>
    /// <param name="highStart">Start of the final uniform range.</param>
    /// <param name="errorValue">Value returned for invalid scalars.</param>
    internal BidiUnicodeTrie(ReadOnlySpan<uint> data, int highStart, uint errorValue)
    {
        _data = data;
        _highStart = highStart;
        _errorValue = errorValue;
    }

    /// <summary>Gets the packed value for a Unicode scalar.</summary>
    /// <param name="codepoint">Unicode scalar.</param>
    /// <returns>Packed bidi properties.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal uint Get(uint codepoint)
    {
        uint index;
        ref uint dataBase = ref MemoryMarshal.GetReference(_data);
        if (codepoint is < 0xD800 or (> 0xDBFF and <= 0xFFFF))
        {
            index = _data[(int)(codepoint >> Shift2)];
            index = (index << IndexShift) + (codepoint & DataMask);
            return Unsafe.Add(ref dataBase, (nint)index);
        }
        if (codepoint <= 0xFFFF)
        {
            index = _data[LscpIndex2Offset + (int)((codepoint - 0xD800) >> Shift2)];
            index = (index << IndexShift) + (codepoint & DataMask);
            return Unsafe.Add(ref dataBase, (nint)index);
        }
        if (codepoint < _highStart)
        {
            index = Index1Offset - OmittedBmpIndex1Length + (codepoint >> Shift1);
            index = _data[(int)index];
            index += (codepoint >> Shift2) & Index2Mask;
            index = _data[(int)index];
            index = (index << IndexShift) + (codepoint & DataMask);
            return Unsafe.Add(ref dataBase, (nint)index);
        }
        return codepoint <= 0x10FFFF ? _data[^DataGranularity] : _errorValue;
    }
}

// __BIDI_TRIE_DATA__
