using System.Numerics;

namespace Engine.Graphics;

/// <summary>Builds compact indexed geometry for engine-owned 3D primitives.</summary>
public static class BuiltInPrimitiveMeshBuilder
{
    private const int RadialSegments = 24;
    private const int HemisphereSegments = 8;

    /// <summary>Builds a one-unit square on the XZ plane with an upward normal.</summary>
    /// <returns>Indexed plane geometry.</returns>
    public static StaticMeshResource BuildPlane()
    {
        var color = Vector4.One;
        var vertices = new[]
        {
            new ModelVertex(new Vector3(-0.5f, 0f, -0.5f), Vector3.UnitY,
                new Vector2(0f, 0f), color),
            new ModelVertex(new Vector3(-0.5f, 0f, 0.5f), Vector3.UnitY,
                new Vector2(0f, 1f), color),
            new ModelVertex(new Vector3(0.5f, 0f, 0.5f), Vector3.UnitY,
                new Vector2(1f, 1f), color),
            new ModelVertex(new Vector3(0.5f, 0f, -0.5f), Vector3.UnitY,
                new Vector2(1f, 0f), color)
        };
        return Create(vertices, [0, 1, 2, 0, 2, 3]);
    }

    /// <summary>Builds a unit-diameter UV sphere centered at the origin.</summary>
    /// <returns>Indexed smooth sphere geometry.</returns>
    public static StaticMeshResource BuildSphere()
    {
        var profile = new ProfilePoint[HemisphereSegments * 2 + 1];
        for (var index = 0; index < profile.Length; index++)
        {
            var angle = -MathF.PI / 2f + MathF.PI * index / (profile.Length - 1);
            var radialNormal = MathF.Cos(angle);
            var verticalNormal = MathF.Sin(angle);
            profile[index] = new ProfilePoint(
                radialNormal * 0.5f,
                verticalNormal * 0.5f,
                radialNormal,
                verticalNormal,
                index / (float)(profile.Length - 1));
        }
        return BuildRevolved(profile);
    }

    /// <summary>Builds a radius-0.5 cylinder with one-unit height along the Y axis.</summary>
    /// <returns>Indexed smooth-sided cylinder geometry with flat caps.</returns>
    public static StaticMeshResource BuildCylinder()
    {
        var vertices = new List<ModelVertex>((RadialSegments + 1) * 4 + 2);
        var indices = new List<uint>(RadialSegments * 12);
        AddCylinderSide(vertices, indices);
        AddCylinderCap(vertices, indices, 0.5f, Vector3.UnitY, top: true);
        AddCylinderCap(vertices, indices, -0.5f, -Vector3.UnitY, top: false);
        return Create(vertices.ToArray(), indices.ToArray());
    }

    /// <summary>Builds a radius-0.5 capsule with two-unit total height along the Y axis.</summary>
    /// <returns>Indexed smooth capsule geometry.</returns>
    public static StaticMeshResource BuildCapsule()
    {
        var profile = new List<ProfilePoint>(HemisphereSegments * 2 + 2);
        for (var index = 0; index <= HemisphereSegments; index++)
        {
            var angle = -MathF.PI / 2f + MathF.PI / 2f * index / HemisphereSegments;
            var radialNormal = MathF.Cos(angle);
            var verticalNormal = MathF.Sin(angle);
            profile.Add(new ProfilePoint(
                radialNormal * 0.5f,
                -0.5f + verticalNormal * 0.5f,
                radialNormal,
                verticalNormal,
                index / (float)(HemisphereSegments * 2 + 1)));
        }
        profile.Add(new ProfilePoint(0.5f, 0.5f, 1f, 0f,
            (HemisphereSegments + 1f) / (HemisphereSegments * 2 + 1f)));
        for (var index = 1; index <= HemisphereSegments; index++)
        {
            var angle = MathF.PI / 2f * index / HemisphereSegments;
            var radialNormal = MathF.Cos(angle);
            var verticalNormal = MathF.Sin(angle);
            profile.Add(new ProfilePoint(
                radialNormal * 0.5f,
                0.5f + verticalNormal * 0.5f,
                radialNormal,
                verticalNormal,
                (HemisphereSegments + 1f + index) /
                (HemisphereSegments * 2f + 1f)));
        }
        return BuildRevolved(profile.ToArray());
    }

    /// <summary>Builds a smooth surface by revolving an ascending Y-axis profile.</summary>
    /// <param name="profile">Bottom-to-top surface profile.</param>
    /// <returns>Indexed revolved geometry.</returns>
    private static StaticMeshResource BuildRevolved(ReadOnlySpan<ProfilePoint> profile)
    {
        var ringSize = RadialSegments + 1;
        var vertices = new ModelVertex[profile.Length * ringSize];
        var indices = new List<uint>((profile.Length - 1) * RadialSegments * 6);
        for (var ring = 0; ring < profile.Length; ring++)
        {
            var point = profile[ring];
            for (var segment = 0; segment <= RadialSegments; segment++)
            {
                var u = segment / (float)RadialSegments;
                var angle = MathF.Tau * u;
                var cosine = MathF.Cos(angle);
                var sine = MathF.Sin(angle);
                var normal = Vector3.Normalize(new Vector3(
                    point.NormalRadius * cosine, point.NormalY, point.NormalRadius * sine));
                vertices[ring * ringSize + segment] = new ModelVertex(
                    new Vector3(point.Radius * cosine, point.Y, point.Radius * sine),
                    normal,
                    new Vector2(u, point.V),
                    Vector4.One);
            }
        }
        for (var ring = 0; ring < profile.Length - 1; ring++)
        {
            var lower = profile[ring];
            var upper = profile[ring + 1];
            for (var segment = 0; segment < RadialSegments; segment++)
            {
                var lowerCurrent = checked((uint)(ring * ringSize + segment));
                var lowerNext = lowerCurrent + 1;
                var upperCurrent = lowerCurrent + checked((uint)ringSize);
                var upperNext = upperCurrent + 1;
                if (lower.Radius <= float.Epsilon)
                {
                    AddTriangle(indices, lowerCurrent, upperCurrent, upperNext);
                }
                else if (upper.Radius <= float.Epsilon)
                {
                    AddTriangle(indices, lowerCurrent, upperCurrent, lowerNext);
                }
                else
                {
                    AddTriangle(indices, lowerCurrent, upperCurrent, lowerNext);
                    AddTriangle(indices, lowerNext, upperCurrent, upperNext);
                }
            }
        }
        return Create(vertices, indices.ToArray());
    }

    /// <summary>Adds the smooth wall of the built-in cylinder.</summary>
    /// <param name="vertices">Destination vertices.</param>
    /// <param name="indices">Destination indices.</param>
    private static void AddCylinderSide(List<ModelVertex> vertices, List<uint> indices)
    {
        var firstVertex = vertices.Count;
        for (var segment = 0; segment <= RadialSegments; segment++)
        {
            var u = segment / (float)RadialSegments;
            var angle = MathF.Tau * u;
            var normal = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            vertices.Add(new ModelVertex(normal * 0.5f + new Vector3(0f, -0.5f, 0f),
                normal, new Vector2(u, 0f), Vector4.One));
            vertices.Add(new ModelVertex(normal * 0.5f + new Vector3(0f, 0.5f, 0f),
                normal, new Vector2(u, 1f), Vector4.One));
        }
        for (var segment = 0; segment < RadialSegments; segment++)
        {
            var lower = checked((uint)(firstVertex + segment * 2));
            var upper = lower + 1;
            var nextLower = lower + 2;
            var nextUpper = lower + 3;
            AddTriangle(indices, lower, upper, nextLower);
            AddTriangle(indices, nextLower, upper, nextUpper);
        }
    }

    /// <summary>Adds one flat cylinder cap with independent normals and UVs.</summary>
    /// <param name="vertices">Destination vertices.</param>
    /// <param name="indices">Destination indices.</param>
    /// <param name="y">Cap height.</param>
    /// <param name="normal">Cap normal.</param>
    /// <param name="top">Whether winding should face upward.</param>
    private static void AddCylinderCap(
        List<ModelVertex> vertices,
        List<uint> indices,
        float y,
        Vector3 normal,
        bool top)
    {
        var center = checked((uint)vertices.Count);
        vertices.Add(new ModelVertex(new Vector3(0f, y, 0f), normal,
            new Vector2(0.5f, 0.5f), Vector4.One));
        var ring = checked((uint)vertices.Count);
        for (var segment = 0; segment <= RadialSegments; segment++)
        {
            var angle = MathF.Tau * segment / RadialSegments;
            var x = MathF.Cos(angle) * 0.5f;
            var z = MathF.Sin(angle) * 0.5f;
            vertices.Add(new ModelVertex(new Vector3(x, y, z), normal,
                new Vector2(x + 0.5f, z + 0.5f), Vector4.One));
        }
        for (var segment = 0; segment < RadialSegments; segment++)
        {
            var current = ring + checked((uint)segment);
            var next = current + 1;
            if (top)
                AddTriangle(indices, center, next, current);
            else
                AddTriangle(indices, center, current, next);
        }
    }

    /// <summary>Adds one indexed triangle.</summary>
    /// <param name="indices">Destination indices.</param>
    /// <param name="a">First vertex index.</param>
    /// <param name="b">Second vertex index.</param>
    /// <param name="c">Third vertex index.</param>
    private static void AddTriangle(List<uint> indices, uint a, uint b, uint c)
    {
        indices.Add(a);
        indices.Add(b);
        indices.Add(c);
    }

    /// <summary>Creates one validated single-submesh resource.</summary>
    /// <param name="vertices">Primitive vertices.</param>
    /// <param name="indices">Triangle indices.</param>
    /// <returns>Validated static mesh.</returns>
    private static StaticMeshResource Create(ModelVertex[] vertices, uint[] indices) =>
        new(vertices, indices, [new Submesh(0, checked((uint)indices.Length), 0)]);

    /// <summary>Describes one ring in a surface-of-revolution profile.</summary>
    /// <param name="Radius">Ring radius.</param>
    /// <param name="Y">Ring height.</param>
    /// <param name="NormalRadius">Radial component of the smooth normal.</param>
    /// <param name="NormalY">Vertical component of the smooth normal.</param>
    /// <param name="V">Vertical texture coordinate.</param>
    private readonly record struct ProfilePoint(
        float Radius,
        float Y,
        float NormalRadius,
        float NormalY,
        float V);
}
