using System.Numerics;
using Engine.Graphics;

namespace Editor;

/// <summary>Projects world-space preview lines into the editor's clipped overlay pass.</summary>
internal static class ScenePreviewOverlayBuilder
{
    private const float HalfThickness = 0.75f;

    /// <summary>Builds screen-space triangles for diagnostic lines and an existing gizmo overlay.</summary>
    /// <param name="previews">World-space preview primitives.</param>
    /// <param name="view">Scene camera view matrix.</param>
    /// <param name="projection">Scene camera projection matrix.</param>
    /// <param name="viewport">Scene viewport bounds.</param>
    /// <param name="gizmo">Existing transform-gizmo vertices.</param>
    /// <returns>Combined screen-space triangle vertices.</returns>
    internal static Vertex[] Build(ScenePreviewList previews, Matrix4x4 view,
        Matrix4x4 projection, GizmoViewport viewport, Vertex[] gizmo)
    {
        var lines = previews.Lines;
        var vertices = new List<Vertex>(lines.Count * 6 + gizmo.Length);
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.DepthMode != ScenePreviewDepthMode.AlwaysVisible)
                continue;
            if (!TryProject(line.Start, view, projection, viewport, out var start) ||
                !TryProject(line.End, view, projection, viewport, out var end))
                continue;
            AddScreenLine(vertices, start, end, line.Color);
        }
        for (var index = 0; index < gizmo.Length; index++)
            vertices.Add(gizmo[index]);
        return vertices.ToArray();
    }

    /// <summary>Projects a visible world point to logical editor coordinates.</summary>
    /// <param name="world">World position.</param><param name="view">View matrix.</param>
    /// <param name="projection">Projection matrix.</param><param name="viewport">Viewport bounds.</param>
    /// <param name="screen">Projected point.</param><returns>True when in front of the camera.</returns>
    internal static bool TryProject(Vector3 world, Matrix4x4 view, Matrix4x4 projection,
        GizmoViewport viewport, out Vector2 screen)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), view * projection);
        if (!float.IsFinite(clip.W) || clip.W <= 0.0001f)
        {
            screen = default;
            return false;
        }
        var inverseW = 1f / clip.W;
        var x = clip.X * inverseW;
        var y = clip.Y * inverseW;
        screen = new Vector2(
            viewport.X + (x + 1f) * viewport.Width * .5f,
            viewport.Y + (y + 1f) * viewport.Height * .5f);
        return float.IsFinite(screen.X) && float.IsFinite(screen.Y);
    }

    /// <summary>Adds a fixed-width screen-space line as two triangles.</summary>
    /// <param name="vertices">Triangle destination.</param><param name="start">Line start.</param>
    /// <param name="end">Line end.</param><param name="color">RGBA color.</param>
    private static void AddScreenLine(List<Vertex> vertices, Vector2 start, Vector2 end, Vector4 color)
    {
        var direction = end - start;
        var lengthSquared = direction.LengthSquared();
        if (lengthSquared <= 0.0001f)
            return;
        var normal = new Vector2(-direction.Y, direction.X) *
            (HalfThickness / MathF.Sqrt(lengthSquared));
        var a = start - normal;
        var b = start + normal;
        var c = end + normal;
        var d = end - normal;
        vertices.Add(new Vertex(new Vector3(a, 0f), color));
        vertices.Add(new Vertex(new Vector3(b, 0f), color));
        vertices.Add(new Vertex(new Vector3(c, 0f), color));
        vertices.Add(new Vertex(new Vector3(a, 0f), color));
        vertices.Add(new Vertex(new Vector3(c, 0f), color));
        vertices.Add(new Vertex(new Vector3(d, 0f), color));
    }
}
