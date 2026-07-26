using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Renders a 3-axis rotation gizmo (RGB = XYZ) using circle rings.
/// Each circle lies in the plane perpendicular to its rotation axis.
/// Supports hover highlighting via <see cref="SetHighlight"/>.
/// </summary>
public class RotationGizmo : Node3D
{
    private const float Radius = 1.5f;
    private const float RingWidth = 0.08f;
    private const int Segments = 64;

    private int _highlightedAxis = -1;

    /// <summary>Gets the combined mesh containing all three rotation circles.</summary>
    public Mesh Mesh { get; }

    /// <summary>
    /// Creates a new RotationGizmo with X (red), Y (green), and Z (blue) rotation circles.
    /// </summary>
    public RotationGizmo()
    {
        Name = "RotationGizmo";
        Mesh = new Mesh { Name = "RotationGizmoMesh", Vertices = GenerateCircles(-1) };
    }

    /// <summary>
    /// Sets the highlighted axis index (0=X, 1=Y, 2=Z) or -1 for none.
    /// Regenerates the mesh with brighter colors for the highlighted circle.
    /// </summary>
    /// <param name="axis">The axis to highlight, or -1 to clear.</param>
    public void SetHighlight(int axis)
    {
        if (axis == _highlightedAxis) return;
        _highlightedAxis = axis;
        Mesh.Vertices = GenerateCircles(axis);
    }

    private static Vertex[] GenerateCircles(int highlightAxis)
    {
        var outerR = Radius;
        var innerR = Radius - RingWidth;
        var verts = new Vertex[Segments * 6 * 3];

        var dimRed = new Vector3(1, 0, 0);
        var dimGreen = new Vector3(0, 1, 0);
        var dimBlue = new Vector3(0, 0, 1);
        var bright = new Vector3(1, 1, 0.5f);

        int offset = 0;
        offset = GenerateCircle(verts, offset, outerR, innerR, Vector3.UnitX, highlightAxis == 0 ? bright : dimRed);
        offset = GenerateCircle(verts, offset, outerR, innerR, Vector3.UnitY, highlightAxis == 1 ? bright : dimGreen);
        offset = GenerateCircle(verts, offset, outerR, innerR, Vector3.UnitZ, highlightAxis == 2 ? bright : dimBlue);

        return verts;
    }

    private static int GenerateCircle(Vertex[] verts, int offset, float outerR, float innerR, Vector3 axis, Vector3 color)
    {
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

            var i0 = u * (innerR * cos0) + v * (innerR * sin0);
            var o0 = u * (outerR * cos0) + v * (outerR * sin0);
            var i1 = u * (innerR * cos1) + v * (innerR * sin1);
            var o1 = u * (outerR * cos1) + v * (outerR * sin1);

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
