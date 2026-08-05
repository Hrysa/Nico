using System.Numerics;
using Xunit;

namespace Engine.Graphics.Tests;

public class BuiltInForwardMeshBuilderTests
{
    /// <summary>Preserves source vertex addressing for the native indexed backend path.</summary>
    [Fact]
    public void BuildIndexedVertices_IndexedMesh_DoesNotExpandIndices()
    {
        var source = new[]
        {
            new ModelVertex(Vector3.Zero, Vector3.UnitY, Vector2.Zero, Vector4.UnitX),
            new ModelVertex(Vector3.UnitX, Vector3.UnitY, Vector2.UnitX, Vector4.UnitX),
            new ModelVertex(Vector3.UnitY, Vector3.UnitY, Vector2.UnitY, Vector4.UnitX)
        };
        var mesh = new StaticMeshResource(source, [2, 0, 1, 2, 1, 0],
            [new Submesh(0, 6, 0)]);

        var vertices = BuiltInForwardMeshBuilder.BuildIndexedVertices(mesh,
            new StandardMaterialResource());

        Assert.Equal(3, vertices.Length);
        Assert.Equal(source.Select(vertex => vertex.Position),
            vertices.Select(vertex => vertex.Position));
        Assert.All(vertices, vertex => Assert.Equal(Vector4.One, vertex.BaseColor));
    }

    /// <summary>Allows texture-backed materials while preserving their vertex color factor.</summary>
    [Fact]
    public void BuildIndexedVertices_TexturedMaterial_PreservesMaterialFactor()
    {
        var mesh = new StaticMeshResource(
            [
                new ModelVertex(Vector3.Zero, Vector3.UnitY, Vector2.Zero, Vector4.One),
                new ModelVertex(Vector3.UnitX, Vector3.UnitY, Vector2.UnitX, Vector4.One),
                new ModelVertex(Vector3.UnitZ, Vector3.UnitY, Vector2.UnitY, Vector4.One)
            ],
            [0, 1, 2], [new Submesh(0, 3, 0)]);
        var factor = new Vector4(0.25f, 0.5f, 0.75f, 1f);
        var material = new StandardMaterialResource
        {
            BaseColor = factor,
            BaseColorTexture = new TextureHandle(1)
        };

        var vertices = BuiltInForwardMeshBuilder.BuildIndexedVertices(mesh, material);

        Assert.All(vertices, vertex => Assert.Equal(factor, vertex.BaseColor));
    }

    /// <summary>Converts procedural triangle meshes into material-capable forward geometry.</summary>
    [Fact]
    public void BuildStaticMesh_Cube_ProducesIndexedForwardTriangles()
    {
        var source = new CubeMesh();

        var mesh = BuiltInForwardMeshBuilder.BuildStaticMesh(source);

        Assert.Equal(source.Vertices.Length, mesh.Vertices.Length);
        Assert.Equal(source.Vertices.Length, mesh.Indices.Length);
        Assert.All(mesh.Vertices, vertex => Assert.InRange(vertex.Normal.Length(), 0.999f, 1.001f));
        Assert.Equal(0, Assert.Single(mesh.Submeshes).MaterialSlot);
    }
}
