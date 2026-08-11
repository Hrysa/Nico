using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

public sealed class CubeMeshTests
{
    /// <summary>Verifies every built-in cube triangle faces away from the cube center.</summary>
    [Fact]
    public void Constructor_AllTrianglesHaveOutwardWinding()
    {
        var vertices = new CubeMesh().Vertices;

        Assert.Equal(36, vertices.Length);
        for (var index = 0; index < vertices.Length; index += 3)
        {
            var first = vertices[index].Position;
            var second = vertices[index + 1].Position;
            var third = vertices[index + 2].Position;
            var normal = Vector3.Cross(second - first, third - first);
            var center = (first + second + third) / 3f;

            Assert.True(Vector3.Dot(normal, center) > 0f,
                $"Triangle {index / 3} is not outward-facing.");
        }
    }
}
