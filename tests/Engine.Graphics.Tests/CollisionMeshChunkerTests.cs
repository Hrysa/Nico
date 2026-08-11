using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

public sealed class CollisionMeshChunkerTests
{
    /// <summary>Partitions distant triangles into deterministic bounded generation units.</summary>
    [Fact]
    public void Chunk_DistantTriangles_ProducesSeparateOrderedMeshes()
    {
        var source = new StaticMeshResource(
        [
            VertexAt(-6f, 0f), VertexAt(-5f, 0f), VertexAt(-6f, 1f),
            VertexAt(6f, 0f), VertexAt(7f, 0f), VertexAt(6f, 1f)
        ], [0, 1, 2, 3, 4, 5], [new Submesh(0, 6, 0)]);

        var chunks = CollisionMeshChunker.Chunk(source, 5f);

        Assert.Equal(2, chunks.Length);
        Assert.Equal((-2, 0, 0), chunks[0].Coordinate);
        Assert.Equal((1, 0, 0), chunks[1].Coordinate);
        Assert.All(chunks, chunk => Assert.Equal(3, chunk.Mesh.Indices.Length));
    }

    /// <summary>Creates one minimally populated model vertex.</summary>
    /// <param name="x">X coordinate.</param><param name="z">Z coordinate.</param>
    /// <returns>Model vertex.</returns>
    private static ModelVertex VertexAt(float x, float z) =>
        new(new Vector3(x, 0f, z), Vector3.UnitY, Vector2.Zero, Vector4.UnitX);
}
