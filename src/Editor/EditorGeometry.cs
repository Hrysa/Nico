using System.Numerics;
using Engine.Graphics;

namespace Editor;

public static class EditorGeometry
{
    public static Vertex[] CreateVertices(float width, float height)
    {
        var vertices = new List<Vertex>();

        AddQuad(vertices, 0, 0, width, height, Color.EditorBackground);

        AddQuad(vertices, 0, 0, width, 30, Color.EditorMenuBar);

        AddQuad(vertices, 0, height - 24, width, height, Color.EditorStatusBar);

        AddQuad(vertices, 0, 30, 220, height - 24, Color.EditorPanel);

        AddQuad(vertices, 220, 30, 222, height - 24, Color.EditorSeparator);

        AddQuad(vertices, width - 260, 30, width, height - 24, Color.EditorPanel);

        AddQuad(vertices, width - 262, 30, width - 260, height - 24, Color.EditorSeparator);

        AddQuad(vertices, 222, 30, width - 262, height - 24, Color.EditorViewport);

        AddQuad(vertices, 222, 30, width - 262, 32, Color.EditorViewportBorder);
        AddQuad(vertices, 222, height - 26, width - 262, height - 24, Color.EditorViewportBorder);
        AddQuad(vertices, 222, 30, 224, height - 24, Color.EditorViewportBorder);
        AddQuad(vertices, width - 264, 30, width - 262, height - 24, Color.EditorViewportBorder);

        AddQuad(vertices, 0, 30, 220, 52, Color.EditorPanelHeader);
        AddQuad(vertices, width - 260, 30, width, 52, Color.EditorPanelHeader);

        return vertices.ToArray();
    }

    private static void AddQuad(List<Vertex> vertices, float x0, float y0, float x1, float y1, Color color)
    {
        vertices.Add(new Vertex(new Vector3(x0, y0, 0), color));
        vertices.Add(new Vertex(new Vector3(x1, y0, 0), color));
        vertices.Add(new Vertex(new Vector3(x1, y1, 0), color));

        vertices.Add(new Vertex(new Vector3(x1, y1, 0), color));
        vertices.Add(new Vertex(new Vector3(x0, y1, 0), color));
        vertices.Add(new Vertex(new Vector3(x0, y0, 0), color));
    }

    public static PushConstants CreatePushConstants(float width, float height)
    {
        var model = Matrix4x4.Identity;
        var view = Matrix4x4.Identity;

        var projection = Matrix4x4.CreateOrthographicOffCenter(
            0, width,   // left, right
            height, 0,  // bottom, top (Vulkan Y-down: 0 at top, height at bottom)
            -1, 1);     // near, far

        return new PushConstants
        {
            Model = model,
            View = view,
            Projection = projection
        };
    }
}
