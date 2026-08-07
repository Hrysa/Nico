namespace Engine.Graphics.Bidi;

/// <summary>Resolves one UTF-16 line into logical ranges ordered for visual shaping.</summary>
internal sealed class BidiResolver
{
    private readonly BidiAlgorithm _algorithm = new();

    /// <summary>Resolves visual runs for one line.</summary>
    /// <param name="text">UTF-16 line.</param>
    /// <param name="direction">Requested paragraph direction.</param>
    /// <returns>Logical UTF-16 ranges in visual order.</returns>
    internal BidiResolvedLine Resolve(ReadOnlySpan<char> text, TextFlowDirection direction)
    {
        if (text.IsEmpty)
            return new BidiResolvedLine([], direction == TextFlowDirection.RightToLeft ? 1 : 0);
        var data = new BidiData(text, direction switch
        {
            TextFlowDirection.LeftToRight => 0,
            TextFlowDirection.RightToLeft => 1,
            _ => 2
        });
        _algorithm.Process(data);
        var levels = _algorithm.ResolvedLevels;
        var runs = new List<BidiResolvedRun>();
        var start = 0;
        while (start < levels.Length)
        {
            var level = levels[start];
            var end = start + 1;
            while (end < levels.Length && levels[end] == level)
                end++;
            var utf16Start = data.ScalarUtf16Starts[start];
            var utf16End = end == data.Length ? text.Length : data.ScalarUtf16Starts[end];
            runs.Add(new BidiResolvedRun(utf16Start, utf16End - utf16Start, level));
            start = end;
        }
        ReorderRuns(runs);
        return new BidiResolvedLine(runs.ToArray(), _algorithm.ResolvedParagraphEmbeddingLevel);
    }

    /// <summary>Applies UAX #9 rule L2 to level-homogeneous runs.</summary>
    /// <param name="runs">Logical runs to reorder in place.</param>
    private static void ReorderRuns(List<BidiResolvedRun> runs)
    {
        var maximum = 0;
        var minimumOdd = int.MaxValue;
        for (var index = 0; index < runs.Count; index++)
        {
            var level = runs[index].Level;
            maximum = Math.Max(maximum, level);
            if ((level & 1) != 0)
                minimumOdd = Math.Min(minimumOdd, level);
        }
        if (minimumOdd == int.MaxValue)
            return;
        for (var level = maximum; level >= minimumOdd; level--)
        {
            var index = 0;
            while (index < runs.Count)
            {
                while (index < runs.Count && runs[index].Level < level)
                    index++;
                var first = index;
                while (index < runs.Count && runs[index].Level >= level)
                    index++;
                if (index > first)
                    runs.Reverse(first, index - first);
            }
        }
    }
}

/// <summary>Stores resolved bidi runs and paragraph level.</summary>
/// <param name="Runs">Logical UTF-16 ranges in visual order.</param>
/// <param name="ParagraphLevel">Resolved paragraph embedding level.</param>
internal readonly record struct BidiResolvedLine(BidiResolvedRun[] Runs, int ParagraphLevel);

/// <summary>Stores one logical text range with a resolved embedding level.</summary>
/// <param name="Utf16Start">Source UTF-16 start.</param>
/// <param name="Utf16Length">Source UTF-16 length.</param>
/// <param name="Level">Resolved embedding level.</param>
internal readonly record struct BidiResolvedRun(int Utf16Start, int Utf16Length, int Level)
{
    /// <summary>Gets whether the shaping direction is right-to-left.</summary>
    internal bool IsRightToLeft => (Level & 1) != 0;
}
