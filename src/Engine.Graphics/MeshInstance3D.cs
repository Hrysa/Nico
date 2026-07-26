namespace Engine.Graphics;

/// <summary>
/// A 3D node that renders a Mesh. Combines a Node3D transform with a Mesh resource.
/// </summary>
public class MeshInstance3D : Node3D
{
    /// <summary>Gets or sets the mesh to render.</summary>
    public Mesh? Mesh { get; set; }

    /// <summary>
    /// Creates a new MeshInstance3D with the specified mesh.
    /// </summary>
    /// <param name="mesh">The mesh resource to render.</param>
    public MeshInstance3D(Mesh? mesh = null)
    {
        Mesh = mesh;
        Name = "MeshInstance3D";
    }
}
