using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// A unit cube mesh (36 vertices, 12 triangles) centered at the origin.
/// Each face has a distinct color.
/// </summary>
public class CubeMesh : Mesh
{
    /// <summary>
    /// Creates a new CubeMesh with the 36 vertices of a unit cube.
    /// </summary>
    public CubeMesh()
    {
        Name = "CubeMesh";
        Vertices =
        [
            // Front face (z = +0.5) — red
            new(new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(1, 0, 0)),
            new(new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(1, 0, 0)),
            new(new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(1, 0, 0)),
            new(new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(1, 0, 0)),
            new(new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(1, 0, 0)),
            new(new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(1, 0, 0)),
            // Back face (z = -0.5) — green
            new(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0, 1, 0)),
            new(new Vector3( 0.5f, -0.5f, -0.5f), new Vector3(0, 1, 0)),
            new(new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(0, 1, 0)),
            new(new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(0, 1, 0)),
            new(new Vector3(-0.5f,  0.5f, -0.5f), new Vector3(0, 1, 0)),
            new(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0, 1, 0)),
            // Top face (y = +0.5) — blue
            new(new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(0, 0, 1)),
            new(new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(0, 0, 1)),
            new(new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(0, 0, 1)),
            new(new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(0, 0, 1)),
            new(new Vector3(-0.5f,  0.5f, -0.5f), new Vector3(0, 0, 1)),
            new(new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(0, 0, 1)),
            // Bottom face (y = -0.5) — yellow
            new(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(1, 1, 0)),
            new(new Vector3( 0.5f, -0.5f, -0.5f), new Vector3(1, 1, 0)),
            new(new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(1, 1, 0)),
            new(new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(1, 1, 0)),
            new(new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(1, 1, 0)),
            new(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(1, 1, 0)),
            // Right face (x = +0.5) — magenta
            new(new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(1, 0, 1)),
            new(new Vector3( 0.5f, -0.5f, -0.5f), new Vector3(1, 0, 1)),
            new(new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(1, 0, 1)),
            new(new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(1, 0, 1)),
            new(new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(1, 0, 1)),
            new(new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(1, 0, 1)),
            // Left face (x = -0.5) — cyan
            new(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0, 1, 1)),
            new(new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(0, 1, 1)),
            new(new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(0, 1, 1)),
            new(new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(0, 1, 1)),
            new(new Vector3(-0.5f,  0.5f, -0.5f), new Vector3(0, 1, 1)),
            new(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0, 1, 1)),
        ];
    }
}
