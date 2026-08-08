using System.Numerics;
using Engine.Core;
using Xunit;

namespace Engine.Graphics.Tests;

public class BuiltInForwardMeshBuilderTests
{
    /// <summary>Gets expected bounds for every engine-owned primitive.</summary>
    public static TheoryData<AssetReference, Vector3, Vector3> BuiltInPrimitiveBounds => new()
    {
        { BuiltInAssets.PlaneMesh, new Vector3(-0.5f, 0f, -0.5f),
            new Vector3(0.5f, 0f, 0.5f) },
        { BuiltInAssets.SphereMesh, new Vector3(-0.5f), new Vector3(0.5f) },
        { BuiltInAssets.CylinderMesh, new Vector3(-0.5f), new Vector3(0.5f) },
        { BuiltInAssets.CapsuleMesh, new Vector3(-0.5f, -1f, -0.5f),
            new Vector3(0.5f, 1f, 0.5f) }
    };

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

    /// <summary>Verifies each new primitive has finite geometry, outward winding, and stable bounds.</summary>
    /// <param name="reference">Built-in mesh reference.</param>
    /// <param name="expectedMinimum">Expected minimum bound.</param>
    /// <param name="expectedMaximum">Expected maximum bound.</param>
    [Theory]
    [MemberData(nameof(BuiltInPrimitiveBounds))]
    public void LoadMesh_NewPrimitive_ProducesValidOutwardGeometry(
        AssetReference reference,
        Vector3 expectedMinimum,
        Vector3 expectedMaximum)
    {
        var mesh = BuiltInAssets.LoadMesh(reference);

        Assert.True(BuiltInAssets.IsBuiltInMesh(reference));
        Assert.NotEmpty(mesh.Vertices);
        Assert.NotEmpty(mesh.Indices);
        Assert.Equal(0, mesh.Indices.Length % 3);
        Assert.Equal(expectedMinimum, mesh.BoundsMinimum);
        Assert.Equal(expectedMaximum, mesh.BoundsMaximum);
        Assert.All(mesh.Vertices, vertex =>
        {
            Assert.True(float.IsFinite(vertex.Position.X));
            Assert.InRange(vertex.Normal.Length(), 0.999f, 1.001f);
        });
        for (var index = 0; index < mesh.Indices.Length; index += 3)
        {
            var a = mesh.Vertices[mesh.Indices[index]];
            var b = mesh.Vertices[mesh.Indices[index + 1]];
            var c = mesh.Vertices[mesh.Indices[index + 2]];
            var face = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            Assert.True(face.LengthSquared() > 0.0000001f);
            Assert.True(Vector3.Dot(face, a.Normal + b.Normal + c.Normal) > 0f);
        }
    }

    /// <summary>Verifies built-in primitive references are stable and mutually distinct.</summary>
    [Fact]
    public void BuiltInMeshReferences_AreDistinctAndRegistered()
    {
        AssetReference[] references =
        [
            BuiltInAssets.CubeMesh,
            BuiltInAssets.PlaneMesh,
            BuiltInAssets.SphereMesh,
            BuiltInAssets.CapsuleMesh,
            BuiltInAssets.CylinderMesh
        ];

        Assert.Equal(references.Length, references.Distinct().Count());
        Assert.All(references, reference => Assert.True(BuiltInAssets.IsBuiltInMesh(reference)));
    }
}
