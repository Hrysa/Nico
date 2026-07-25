using System.Numerics;
using Engine.Graphics;

namespace Editor;

public static class Triangle
{
    public static Vertex[] CreateVertices()
    {
        // Winding: counter-clockwise when viewed from outside (Vulkan Y-down convention)
        return
        [
            // Front face (Z+) - Red
            new(new(-0.5f, -0.5f,  0.5f), new(1.0f, 0.0f, 0.0f)),
            new(new( 0.5f, -0.5f,  0.5f), new(1.0f, 0.0f, 0.0f)),
            new(new( 0.5f,  0.5f,  0.5f), new(1.0f, 0.0f, 0.0f)),
            new(new( 0.5f,  0.5f,  0.5f), new(1.0f, 0.0f, 0.0f)),
            new(new(-0.5f,  0.5f,  0.5f), new(1.0f, 0.0f, 0.0f)),
            new(new(-0.5f, -0.5f,  0.5f), new(1.0f, 0.0f, 0.0f)),

            // Back face (Z-) - Green
            new(new( 0.5f, -0.5f, -0.5f), new(0.0f, 1.0f, 0.0f)),
            new(new(-0.5f, -0.5f, -0.5f), new(0.0f, 1.0f, 0.0f)),
            new(new(-0.5f,  0.5f, -0.5f), new(0.0f, 1.0f, 0.0f)),
            new(new(-0.5f,  0.5f, -0.5f), new(0.0f, 1.0f, 0.0f)),
            new(new( 0.5f,  0.5f, -0.5f), new(0.0f, 1.0f, 0.0f)),
            new(new( 0.5f, -0.5f, -0.5f), new(0.0f, 1.0f, 0.0f)),

            // Top face (Y+) - Blue
            new(new(-0.5f,  0.5f,  0.5f), new(0.0f, 0.0f, 1.0f)),
            new(new( 0.5f,  0.5f,  0.5f), new(0.0f, 0.0f, 1.0f)),
            new(new( 0.5f,  0.5f, -0.5f), new(0.0f, 0.0f, 1.0f)),
            new(new( 0.5f,  0.5f, -0.5f), new(0.0f, 0.0f, 1.0f)),
            new(new(-0.5f,  0.5f, -0.5f), new(0.0f, 0.0f, 1.0f)),
            new(new(-0.5f,  0.5f,  0.5f), new(0.0f, 0.0f, 1.0f)),

            // Bottom face (Y-) - Yellow
            new(new(-0.5f, -0.5f, -0.5f), new(1.0f, 1.0f, 0.0f)),
            new(new( 0.5f, -0.5f, -0.5f), new(1.0f, 1.0f, 0.0f)),
            new(new( 0.5f, -0.5f,  0.5f), new(1.0f, 1.0f, 0.0f)),
            new(new( 0.5f, -0.5f,  0.5f), new(1.0f, 1.0f, 0.0f)),
            new(new(-0.5f, -0.5f,  0.5f), new(1.0f, 1.0f, 0.0f)),
            new(new(-0.5f, -0.5f, -0.5f), new(1.0f, 1.0f, 0.0f)),

            // Right face (X+) - Cyan
            new(new( 0.5f, -0.5f,  0.5f), new(0.0f, 1.0f, 1.0f)),
            new(new( 0.5f, -0.5f, -0.5f), new(0.0f, 1.0f, 1.0f)),
            new(new( 0.5f,  0.5f, -0.5f), new(0.0f, 1.0f, 1.0f)),
            new(new( 0.5f,  0.5f, -0.5f), new(0.0f, 1.0f, 1.0f)),
            new(new( 0.5f,  0.5f,  0.5f), new(0.0f, 1.0f, 1.0f)),
            new(new( 0.5f, -0.5f,  0.5f), new(0.0f, 1.0f, 1.0f)),

            // Left face (X-) - Magenta
            new(new(-0.5f, -0.5f, -0.5f), new(1.0f, 0.0f, 1.0f)),
            new(new(-0.5f, -0.5f,  0.5f), new(1.0f, 0.0f, 1.0f)),
            new(new(-0.5f,  0.5f,  0.5f), new(1.0f, 0.0f, 1.0f)),
            new(new(-0.5f,  0.5f,  0.5f), new(1.0f, 0.0f, 1.0f)),
            new(new(-0.5f,  0.5f, -0.5f), new(1.0f, 0.0f, 1.0f)),
            new(new(-0.5f, -0.5f, -0.5f), new(1.0f, 0.0f, 1.0f)),
        ];
    }

    public static PushConstants CreatePushConstants()
    {
        var model = Matrix4x4.Identity;

        var view = Matrix4x4.CreateLookAt(
            new Vector3(2.0f, 2.0f, 2.0f),
            Vector3.Zero,
            Vector3.UnitY);

        // Vulkan clips Y from +1 (top) to -1 (bottom), unlike OpenGL.
        // Flip Y by negating the [1][1] element of the projection matrix.
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4.0f,
            1280.0f / 720.0f,
            0.1f,
            100.0f);

        // Flip Y for Vulkan
        projection.M22 = -projection.M22;

        return new PushConstants
        {
            Model = model,
            View = view,
            Projection = projection
        };
    }
}
