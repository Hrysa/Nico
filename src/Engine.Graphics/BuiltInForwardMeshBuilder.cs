using System.Numerics;

namespace Engine.Graphics;

/// <summary>Prepares immutable model geometry for the first built-in forward path.</summary>
public static class BuiltInForwardMeshBuilder
{
    /// <summary>Converts one non-indexed triangle mesh into static forward geometry.</summary>
    /// <param name="mesh">Source triangle-list mesh.</param>
    /// <returns>Indexed static geometry with generated flat normals.</returns>
    public static StaticMeshResource BuildStaticMesh(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (mesh.Vertices.Length % 3 != 0)
            throw new ArgumentException("A forward mesh must contain complete triangles.", nameof(mesh));
        var vertices = new ModelVertex[mesh.Vertices.Length];
        var indices = new uint[mesh.Vertices.Length];
        for (var index = 0; index < mesh.Vertices.Length; index += 3)
        {
            var a = mesh.Vertices[index].Position;
            var b = mesh.Vertices[index + 1].Position;
            var c = mesh.Vertices[index + 2].Position;
            var cross = Vector3.Cross(b - a, c - a);
            var normal = cross.LengthSquared() > float.Epsilon
                ? Vector3.Normalize(cross) : Vector3.UnitY;
            for (var vertexIndex = index; vertexIndex < index + 3; vertexIndex++)
            {
                vertices[vertexIndex] = new ModelVertex(mesh.Vertices[vertexIndex].Position,
                    normal, Vector2.Zero, new Vector4(1f, 0f, 0f, 1f));
                indices[vertexIndex] = checked((uint)vertexIndex);
            }
        }
        return new StaticMeshResource(vertices, indices,
            [new Submesh(0, checked((uint)indices.Length), 0)]);
    }

    /// <summary>Builds one compact vertex per source vertex for native indexed rendering.</summary>
    /// <param name="mesh">Indexed source geometry.</param>
    /// <param name="material">Standard material values.</param>
    /// <returns>Compact shaded vertices preserving source index addressing.</returns>
    public static ForwardModelVertex[] BuildIndexedVertices(
        StaticMeshResource mesh,
        ResolvedStandardMaterial material)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);
        var vertices = new ForwardModelVertex[mesh.Vertices.Length];
        for (var index = 0; index < vertices.Length; index++)
        {
            var source = mesh.Vertices[index];
            vertices[index] = new ForwardModelVertex(source.Position, source.Normal,
                source.TexCoord, source.Tangent, source.Color * material.BaseColor);
        }
        return vertices;
    }

    /// <summary>Builds packed geometry for the GPU skinning path.</summary>
    /// <param name="mesh">Skinned source geometry.</param>
    /// <param name="material">Standard material values.</param>
    /// <returns>Packed vertices preserving source index addressing.</returns>
    public static SkinnedForwardModelVertex[] BuildSkinnedVertices(
        SkinnedMeshResource mesh,
        ResolvedStandardMaterial material)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);
        var vertices = new SkinnedForwardModelVertex[mesh.Mesh.Vertices.Length];
        for (var index = 0; index < vertices.Length; index++)
        {
            var source = mesh.Mesh.Vertices[index];
            var influence = mesh.Influences[index];
            vertices[index] = new SkinnedForwardModelVertex(
                source.Position,
                source.Normal,
                source.TexCoord,
                source.Tangent,
                source.Color * material.BaseColor,
                new UIntVector4(influence.Joint0, influence.Joint1,
                    influence.Joint2, influence.Joint3),
                influence.Weights);
        }
        return vertices;
    }
}
