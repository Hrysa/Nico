using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Colored world-origin axes rendered as thin rectangular prisms.
/// </summary>
public sealed class OriginAxesMesh : Mesh
{
    /// <summary>
    /// Creates red X, green Y, and blue Z world-origin axes.
    /// </summary>
    /// <param name="extent">Axis distance from the origin in world units.</param>
    /// <param name="thickness">Axis thickness in world units.</param>
    public OriginAxesMesh(float extent = 10f, float thickness = 0.025f)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(extent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thickness);

        Name = "OriginAxesMesh";
        var vertices = new List<Vertex>(108);
        AppendBox(vertices, new Vector3(-extent, 0.004f, 0f), new Vector3(extent, 0.004f, 0f), thickness, Color.Red);
        AppendBox(vertices, Vector3.Zero, new Vector3(0f, extent, 0f), thickness, Color.Green);
        AppendBox(vertices, new Vector3(0f, 0.004f, -extent), new Vector3(0f, 0.004f, extent), thickness, Color.Blue);
        Vertices = vertices.ToArray();
    }

    /// <summary>
    /// Appends an axis-aligned rectangular prism between two points.
    /// </summary>
    /// <param name="vertices">Destination vertex collection.</param>
    /// <param name="start">Prism start.</param>
    /// <param name="end">Prism end.</param>
    /// <param name="thickness">Prism thickness.</param>
    /// <param name="color">Prism color.</param>
    private static void AppendBox(List<Vertex> vertices, Vector3 start, Vector3 end, float thickness, Vector3 color)
    {
        var minimum = Vector3.Min(start, end) - new Vector3(thickness * 0.5f);
        var maximum = Vector3.Max(start, end) + new Vector3(thickness * 0.5f);
        var corners = new[]
        {
            new Vector3(minimum.X, minimum.Y, minimum.Z),
            new Vector3(maximum.X, minimum.Y, minimum.Z),
            new Vector3(maximum.X, maximum.Y, minimum.Z),
            new Vector3(minimum.X, maximum.Y, minimum.Z),
            new Vector3(minimum.X, minimum.Y, maximum.Z),
            new Vector3(maximum.X, minimum.Y, maximum.Z),
            new Vector3(maximum.X, maximum.Y, maximum.Z),
            new Vector3(minimum.X, maximum.Y, maximum.Z)
        };

        AppendFace(vertices, corners, 0, 1, 2, 3, color);
        AppendFace(vertices, corners, 5, 4, 7, 6, color);
        AppendFace(vertices, corners, 4, 0, 3, 7, color);
        AppendFace(vertices, corners, 1, 5, 6, 2, color);
        AppendFace(vertices, corners, 3, 2, 6, 7, color);
        AppendFace(vertices, corners, 4, 5, 1, 0, color);
    }

    /// <summary>
    /// Appends one quad face as two triangles.
    /// </summary>
    /// <param name="vertices">Destination vertex collection.</param>
    /// <param name="corners">Prism corners.</param>
    /// <param name="a">First corner index.</param>
    /// <param name="b">Second corner index.</param>
    /// <param name="c">Third corner index.</param>
    /// <param name="d">Fourth corner index.</param>
    /// <param name="color">Face color.</param>
    private static void AppendFace(List<Vertex> vertices, Vector3[] corners, int a, int b, int c, int d, Vector3 color)
    {
        vertices.Add(new Vertex(corners[a], color));
        vertices.Add(new Vertex(corners[b], color));
        vertices.Add(new Vertex(corners[c], color));
        vertices.Add(new Vertex(corners[c], color));
        vertices.Add(new Vertex(corners[d], color));
        vertices.Add(new Vertex(corners[a], color));
    }
}
