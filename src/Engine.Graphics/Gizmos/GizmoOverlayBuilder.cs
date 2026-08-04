using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Tessellates shared gizmo geometry into the existing screen-space overlay format.
/// </summary>
internal static class GizmoOverlayBuilder
{
    private static readonly Vector3 HighlightColor = new(1f, 1f, 0.5f);

    /// <summary>
    /// Builds overlay triangles in background-to-foreground order.
    /// </summary>
    /// <param name="layout">Shared gizmo layout.</param>
    /// <param name="hovered">Currently hovered handle.</param>
    /// <param name="active">Currently dragged handle.</param>
    /// <returns>Finite screen-space overlay vertices.</returns>
    internal static Vertex[] Build(GizmoLayoutResult layout, GizmoHandleKind hovered, GizmoHandleKind active)
    {
        if (!layout.IsValid)
            return [];

        var vertices = new List<Vertex>();
        foreach (var handle in layout.Handles.OrderBy(candidate => candidate.Layer))
            AppendHandle(vertices, handle, handle.Color, layout.Viewport);

        var highlighted = active != GizmoHandleKind.None ? active : hovered;
        if (highlighted != GizmoHandleKind.None)
        {
            foreach (var handle in layout.Handles)
            {
                if (handle.Kind != highlighted)
                    continue;
                AppendHandle(vertices, handle, HighlightColor, layout.Viewport);
                break;
            }
        }

        return vertices.ToArray();
    }

    /// <summary>
    /// Appends all tessellated geometry for one handle.
    /// </summary>
    /// <param name="vertices">Destination vertex collection.</param>
    /// <param name="handle">Handle geometry.</param>
    /// <param name="color">Output color.</param>
    /// <param name="viewport">Clip viewport.</param>
    private static void AppendHandle(List<Vertex> vertices, GizmoHandleGeometry handle, Vector3 color, GizmoViewport viewport)
    {
        foreach (var segment in handle.Segments)
            AppendSegment(vertices, segment, color, viewport);
        foreach (var triangle in handle.Triangles)
            AppendTriangle(vertices, triangle, color, viewport);
    }

    /// <summary>
    /// Tessellates one screen segment into two consistently wound triangles.
    /// </summary>
    /// <param name="vertices">Destination vertex collection.</param>
    /// <param name="segment">Segment to tessellate.</param>
    /// <param name="color">Output color.</param>
    /// <param name="viewport">Clip viewport.</param>
    private static void AppendSegment(List<Vertex> vertices, GizmoSegment segment, Vector3 color, GizmoViewport viewport)
    {
        var direction = segment.End - segment.Start;
        var length = direction.Length();
        if (!float.IsFinite(length) || length <= float.Epsilon)
            return;

        var normal = new Vector2(-direction.Y, direction.X) / length * segment.VisibleWidth * 0.5f;
        var first = Clamp(segment.Start - normal, viewport);
        var second = Clamp(segment.Start + normal, viewport);
        var third = Clamp(segment.End + normal, viewport);
        var fourth = Clamp(segment.End - normal, viewport);
        AppendVertex(vertices, first, color);
        AppendVertex(vertices, second, color);
        AppendVertex(vertices, third, color);
        AppendVertex(vertices, first, color);
        AppendVertex(vertices, third, color);
        AppendVertex(vertices, fourth, color);
    }

    /// <summary>
    /// Appends one clipped triangle.
    /// </summary>
    /// <param name="vertices">Destination vertex collection.</param>
    /// <param name="triangle">Triangle to append.</param>
    /// <param name="color">Output color.</param>
    /// <param name="viewport">Clip viewport.</param>
    private static void AppendTriangle(List<Vertex> vertices, GizmoTriangle triangle, Vector3 color, GizmoViewport viewport)
    {
        AppendVertex(vertices, Clamp(triangle.A, viewport), color);
        AppendVertex(vertices, Clamp(triangle.B, viewport), color);
        AppendVertex(vertices, Clamp(triangle.C, viewport), color);
    }

    /// <summary>
    /// Appends a finite screen vertex.
    /// </summary>
    /// <param name="vertices">Destination vertex collection.</param>
    /// <param name="position">Screen position.</param>
    /// <param name="color">Vertex color.</param>
    private static void AppendVertex(List<Vertex> vertices, Vector2 position, Vector3 color)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)
            || !float.IsFinite(color.X) || !float.IsFinite(color.Y) || !float.IsFinite(color.Z))
            return;
        vertices.Add(new Vertex(new Vector3(position, 0f), color));
    }

    /// <summary>
    /// Clamps a tessellated point to the Scene viewport.
    /// </summary>
    /// <param name="point">Screen point.</param>
    /// <param name="viewport">Scene viewport.</param>
    /// <returns>The contained screen point.</returns>
    private static Vector2 Clamp(Vector2 point, GizmoViewport viewport)
    {
        return new Vector2(
            Math.Clamp(point.X, viewport.X, viewport.X + viewport.Width),
            Math.Clamp(point.Y, viewport.Y, viewport.Y + viewport.Height));
    }
}
