using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Renders a 3-axis position gizmo (RGB = XYZ) using thin quads.
/// Each axis is a colored rectangle originating from the local origin.
/// </summary>
public class AxisGizmo : Node3D
{
    private const float AxisLength = 2.0f;
    private const float LineWidth = 0.02f;

    /// <summary>Gets the combined mesh containing all three axis lines.</summary>
    public Mesh Mesh { get; }

    /// <summary>
    /// Creates a new AxisGizmo with X (red), Y (green), and Z (blue) axis lines.
    /// </summary>
    public AxisGizmo()
    {
        Name = "AxisGizmo";
        Mesh = new Mesh { Name = "AxisGizmoMesh", Vertices = GenerateAxisLines() };
    }

    private static Vertex[] GenerateAxisLines()
    {
        var hw = LineWidth / 2f;
        var len = AxisLength;
        var verts = new Vertex[18];

        // X axis — red, extends along +X, width along Y
        verts[0] = new(new Vector3(0, -hw, 0), new Vector3(1, 0, 0));
        verts[1] = new(new Vector3(len, -hw, 0), new Vector3(1, 0, 0));
        verts[2] = new(new Vector3(len, hw, 0), new Vector3(1, 0, 0));
        verts[3] = new(new Vector3(len, hw, 0), new Vector3(1, 0, 0));
        verts[4] = new(new Vector3(0, hw, 0), new Vector3(1, 0, 0));
        verts[5] = new(new Vector3(0, -hw, 0), new Vector3(1, 0, 0));

        // Y axis — green, extends along +Y, width along X
        verts[6] = new(new Vector3(-hw, 0, 0), new Vector3(0, 1, 0));
        verts[7] = new(new Vector3(-hw, len, 0), new Vector3(0, 1, 0));
        verts[8] = new(new Vector3(hw, len, 0), new Vector3(0, 1, 0));
        verts[9] = new(new Vector3(hw, len, 0), new Vector3(0, 1, 0));
        verts[10] = new(new Vector3(hw, 0, 0), new Vector3(0, 1, 0));
        verts[11] = new(new Vector3(-hw, 0, 0), new Vector3(0, 1, 0));

        // Z axis — blue, extends along +Z, width along Y
        verts[12] = new(new Vector3(0, -hw, 0), new Vector3(0, 0, 1));
        verts[13] = new(new Vector3(0, -hw, len), new Vector3(0, 0, 1));
        verts[14] = new(new Vector3(0, hw, len), new Vector3(0, 0, 1));
        verts[15] = new(new Vector3(0, hw, len), new Vector3(0, 0, 1));
        verts[16] = new(new Vector3(0, hw, 0), new Vector3(0, 0, 1));
        verts[17] = new(new Vector3(0, -hw, 0), new Vector3(0, 0, 1));

        return verts;
    }
}
