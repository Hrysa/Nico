using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Renders a 3-axis rotation gizmo (RGB = XYZ) using circle rings.
/// Each circle lies in the plane perpendicular to its rotation axis.
/// </summary>
public class RotationGizmo : Node3D
{
    private const float Radius = 1.5f;
    private const float RingWidth = 0.04f;
    private const int Segments = 64;

    /// <summary>Gets the combined mesh containing all three rotation circles.</summary>
    public Mesh Mesh { get; }

    /// <summary>
    /// Creates a new RotationGizmo with X (red), Y (green), and Z (blue) rotation circles.
    /// </summary>
    public RotationGizmo()
    {
        Name = "RotationGizmo";
        Mesh = new Mesh { Name = "RotationGizmoMesh", Vertices = GenerateCircles() };
    }

    private static Vertex[] GenerateCircles()
    {
        var outerR = Radius;
        var innerR = Radius - RingWidth;
        var verts = new Vertex[Segments * 6 * 3]; // 6 verts per segment * 3 circles

        int offset = 0;
        offset = GenerateCircle(verts, offset, outerR, innerR, Vector3.UnitX, new Vector3(1, 0, 0)); // X = red, YZ plane
        offset = GenerateCircle(verts, offset, outerR, innerR, Vector3.UnitY, new Vector3(0, 1, 0)); // Y = green, XZ plane
        offset = GenerateCircle(verts, offset, outerR, innerR, Vector3.UnitZ, new Vector3(0, 0, 1)); // Z = blue, XY plane

        return verts;
    }

    /// <summary>
    /// Generates a circle ring mesh in the plane perpendicular to the given axis.
    /// </summary>
    private static int GenerateCircle(Vertex[] verts, int offset, float outerR, float innerR, Vector3 axis, Vector3 color)
    {
        // Two basis vectors perpendicular to the axis
        Vector3 u, v;
        if (MathF.Abs(axis.Y) < 0.99f)
        {
            u = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitY));
            v = Vector3.Cross(axis, u);
        }
        else
        {
            u = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitX));
            v = Vector3.Cross(axis, u);
        }

        float step = MathF.PI * 2f / Segments;

        for (int i = 0; i < Segments; i++)
        {
            float a0 = i * step;
            float a1 = (i + 1) * step;

            var cos0 = MathF.Cos(a0);
            var sin0 = MathF.Sin(a0);
            var cos1 = MathF.Cos(a1);
            var sin1 = MathF.Sin(a1);

            // Inner and outer points
            var i0 = u * (innerR * cos0) + v * (innerR * sin0);
            var o0 = u * (outerR * cos0) + v * (outerR * sin0);
            var i1 = u * (innerR * cos1) + v * (innerR * sin1);
            var o1 = u * (outerR * cos1) + v * (outerR * sin1);

            // Two triangles per segment
            verts[offset++] = new Vertex(i0, color);
            verts[offset++] = new Vertex(o0, color);
            verts[offset++] = new Vertex(o1, color);

            verts[offset++] = new Vertex(o1, color);
            verts[offset++] = new Vertex(i1, color);
            verts[offset++] = new Vertex(i0, color);
        }

        return offset;
    }
}
