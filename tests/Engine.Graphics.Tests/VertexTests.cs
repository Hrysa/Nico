using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>Exercises allocation-free vertex equality used by retained uploads.</summary>
public sealed class VertexTests
{
    /// <summary>Verifies colored vertex comparisons do not box or allocate.</summary>
    [Fact]
    public void Vertex_Equals_RepeatedComparisonDoesNotAllocate()
    {
        var left = new Vertex(Vector3.UnitX, Vector3.One);
        var right = new Vertex(Vector3.UnitX, Vector3.One);
        Assert.True(left.Equals(right));
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();

        var equal = true;
        for (var index = 0; index < 10_000; index++)
            equal &= left.Equals(right);

        var allocationEnd = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(equal);
        Assert.Equal(allocationStart, allocationEnd);
    }

    /// <summary>Verifies textured vertex comparisons do not box or allocate.</summary>
    [Fact]
    public void VertexT_Equals_RepeatedComparisonDoesNotAllocate()
    {
        var left = new VertexT(Vector3.UnitX, Vector2.One);
        var right = new VertexT(Vector3.UnitX, Vector2.One);
        Assert.True(left.Equals(right));
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();

        var equal = true;
        for (var index = 0; index < 10_000; index++)
            equal &= left.Equals(right);

        var allocationEnd = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(equal);
        Assert.Equal(allocationStart, allocationEnd);
    }
}
