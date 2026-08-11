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
    /// <param name="destination">Reusable destination array, grown only when capacity is insufficient.</param>
    /// <returns>Number of combined screen-space triangle vertices written.</returns>
    internal static int Build(ScenePreviewList previews, Matrix4x4 view,
        Matrix4x4 projection, GizmoViewport viewport, Vertex[] gizmo,
        ref Vertex[] destination)
    {
        var lines = previews.Lines;
        EnsureCapacity(ref destination, checked(lines.Count * 6 + gizmo.Length));
        var vertexCount = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.DepthMode != ScenePreviewDepthMode.AlwaysVisible)
                continue;
            if (!TryProject(line.Start, view, projection, viewport, out var start) ||
                !TryProject(line.End, view, projection, viewport, out var end))
                continue;
            AddScreenLine(destination, ref vertexCount, start, end, line.Color);
        }
        gizmo.AsSpan().CopyTo(destination.AsSpan(vertexCount));
        return vertexCount + gizmo.Length;
    }

    /// <summary>Grows a retained vertex array geometrically when the current capacity is insufficient.</summary>
    /// <param name="destination">Reusable array to validate or replace.</param>
    /// <param name="requiredCapacity">Minimum vertex capacity.</param>
    private static void EnsureCapacity(ref Vertex[] destination, int requiredCapacity)
    {
        if (destination.Length >= requiredCapacity)
            return;
        var capacity = Math.Max(16, destination.Length);
        while (capacity < requiredCapacity)
            capacity = checked(capacity * 2);
        Array.Resize(ref destination, capacity);
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
    /// <param name="vertices">Triangle destination.</param><param name="vertexCount">Next writable vertex index.</param><param name="start">Line start.</param>
    /// <param name="end">Line end.</param><param name="color">RGBA color.</param>
    private static void AddScreenLine(Vertex[] vertices, ref int vertexCount,
        Vector2 start, Vector2 end, Vector4 color)
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
        vertices[vertexCount++] = new Vertex(new Vector3(a, 0f), color);
        vertices[vertexCount++] = new Vertex(new Vector3(b, 0f), color);
        vertices[vertexCount++] = new Vertex(new Vector3(c, 0f), color);
        vertices[vertexCount++] = new Vertex(new Vector3(a, 0f), color);
        vertices[vertexCount++] = new Vertex(new Vector3(c, 0f), color);
        vertices[vertexCount++] = new Vertex(new Vector3(d, 0f), color);
    }
}
