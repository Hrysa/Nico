using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Renders a 3-axis position gizmo (RGB = XYZ) using thin quads.
/// Each axis is a colored rectangle originating from the local origin.
/// Supports hover highlighting via <see cref="SetHighlight"/>.
/// </summary>
public class AxisGizmo : Node3D
{
    private const float AxisLength = 2.0f;
    private const float LineWidth = 0.2f;

    private int _highlightedAxis = -1;

    /// <summary>Gets the combined mesh containing all three axis lines.</summary>
    public Mesh Mesh { get; }

    /// <summary>
    /// Creates a new AxisGizmo with X (red), Y (green), and Z (blue) axis lines.
    /// </summary>
    public AxisGizmo()
    {
        Name = "AxisGizmo";
        Mesh = new Mesh { Name = "AxisGizmoMesh", Vertices = GenerateAxisLines(-1) };
    }

    /// <summary>
    /// Sets the highlighted axis index (0=X, 1=Y, 2=Z) or -1 for none.
    /// Regenerates the mesh with brighter colors for the highlighted axis.
    /// </summary>
    /// <param name="axis">The axis to highlight, or -1 to clear.</param>
    public void SetHighlight(int axis)
    {
        if (axis == _highlightedAxis) return;
        _highlightedAxis = axis;
        Mesh.Vertices = GenerateAxisLines(axis);
    }

    private static Vertex[] GenerateAxisLines(int highlightAxis)
    {
        var hw = LineWidth / 2f;
        var len = AxisLength;
        var verts = new Vertex[18];

        var dimRed = new Vector3(1, 0, 0);
        var dimGreen = new Vector3(0, 1, 0);
        var dimBlue = new Vector3(0, 0, 1);
        var bright = new Vector3(1, 1, 0.5f);

        var red = highlightAxis == 0 ? bright : dimRed;
        var green = highlightAxis == 1 ? bright : dimGreen;
        var blue = highlightAxis == 2 ? bright : dimBlue;

        // X axis — red, extends along +X, width along Y
        verts[0] = new(new Vector3(0, -hw, 0), red);
        verts[1] = new(new Vector3(len, -hw, 0), red);
        verts[2] = new(new Vector3(len, hw, 0), red);
        verts[3] = new(new Vector3(len, hw, 0), red);
        verts[4] = new(new Vector3(0, hw, 0), red);
        verts[5] = new(new Vector3(0, -hw, 0), red);

        // Y axis — green, extends along +Y, width along X
        verts[6] = new(new Vector3(-hw, 0, 0), green);
        verts[7] = new(new Vector3(-hw, len, 0), green);
        verts[8] = new(new Vector3(hw, len, 0), green);
        verts[9] = new(new Vector3(hw, len, 0), green);
        verts[10] = new(new Vector3(hw, 0, 0), green);
        verts[11] = new(new Vector3(-hw, 0, 0), green);

        // Z axis — blue, extends along +Z, width along Y
        verts[12] = new(new Vector3(0, -hw, 0), blue);
        verts[13] = new(new Vector3(0, -hw, len), blue);
        verts[14] = new(new Vector3(0, hw, len), blue);
        verts[15] = new(new Vector3(0, hw, len), blue);
        verts[16] = new(new Vector3(0, hw, 0), blue);
        verts[17] = new(new Vector3(0, -hw, 0), blue);

        return verts;
    }
}
