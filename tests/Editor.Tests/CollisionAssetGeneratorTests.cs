using System.Numerics;
using Editor;
using Engine.Graphics;
using Xunit;

namespace Editor.Tests;

public sealed class CollisionAssetGeneratorTests
{
    /// <summary>Writes deterministic loadable project collision chunks.</summary>
    [Fact]
    public void GenerateChunkFiles_TwoCells_WritesLoadableSources()
    {
        var directory = Directory.CreateTempSubdirectory("nico-collision-generation-");
        try
        {
            var source = new StaticMeshResource(
            [
                Vertex(-6f, 0f), Vertex(-5f, 0f), Vertex(-6f, 1f),
                Vertex(6f, 0f), Vertex(7f, 0f), Vertex(6f, 1f)
            ], [0, 1, 2, 3, 4, 5], [new Submesh(0, 6, 0)]);

            var paths = CollisionAssetGenerator.GenerateChunkFiles(
                source, directory.FullName, "map", 5f);

            Assert.Equal(2, paths.Length);
            Assert.EndsWith("map.-2_0_0.ncollision", paths[0], StringComparison.Ordinal);
            Assert.All(paths, path =>
            {
                using var stream = File.OpenRead(path);
                Assert.Equal(3, StaticMeshResource.Load(stream).Indices.Length);
            });
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Creates one collision source vertex.</summary>
    private static ModelVertex Vertex(float x, float z) =>
        new(new Vector3(x, 0f, z), Vector3.UnitY, Vector2.Zero, Vector4.UnitX);
}
