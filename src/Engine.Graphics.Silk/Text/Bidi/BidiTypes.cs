// Portions derived from SixLabors.Fonts and Avalonia under the Apache License 2.0.
namespace Engine.Graphics.Bidi;

/// <summary>Unicode bidirectional character class.</summary>
internal enum BidiClass
{
    LeftToRight,
    ArabicLetter,
    ArabicNumber,
    ParagraphSeparator,
    BoundaryNeutral,
    CommonSeparator,
    EuropeanNumber,
    EuropeanSeparator,
    EuropeanTerminator,
    FirstStrongIsolate,
    LeftToRightEmbedding,
    LeftToRightIsolate,
    LeftToRightOverride,
    NonspacingMark,
    OtherNeutral,
    PopDirectionalFormat,
    PopDirectionalIsolate,
    RightToLeft,
    RightToLeftEmbedding,
    RightToLeftIsolate,
    RightToLeftOverride,
    SegmentSeparator,
    WhiteSpace
}

/// <summary>Unicode paired-bracket role.</summary>
internal enum BidiPairedBracketType
{
    None,
    Close,
    Open
}
