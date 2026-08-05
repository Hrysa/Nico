using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>Exercises allocation-free color equality used by glyph cache keys.</summary>
public sealed class ColorTests
{
    /// <summary>Verifies repeated strongly typed equality and hashing do not allocate.</summary>
    [Fact]
    public void EqualityAndHashing_RepeatedCallsDoNotAllocate()
    {
        var left = new Color(0.25f, 0.5f, 0.75f);
        var right = new Color(0.25f, 0.5f, 0.75f);
        Assert.True(left.Equals(right));
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();

        var equal = true;
        var hash = 0;
        for (var index = 0; index < 10_000; index++)
        {
            equal &= left.Equals(right);
            hash ^= left.GetHashCode();
        }

        var allocationEnd = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(equal);
        Assert.Equal(0, hash);
        Assert.Equal(allocationStart, allocationEnd);
    }
}
