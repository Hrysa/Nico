using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Resolves pointer input against shared gizmo geometry in explicit layer order.
/// </summary>
internal static class GizmoPicker
{
    private const float TieEpsilon = 0.0001f;

    /// <summary>
    /// Picks the foremost handle containing a screen point.
    /// </summary>
    /// <param name="layout">Shared handle layout.</param>
    /// <param name="pointer">Pointer position in screen pixels.</param>
    /// <returns>The picked handle, or <see cref="GizmoHandleKind.None"/>.</returns>
    internal static GizmoHandleKind Pick(GizmoLayoutResult layout, Vector2 pointer)
    {
        if (!layout.IsValid || !layout.Viewport.Contains(pointer))
            return GizmoHandleKind.None;

        foreach (var layer in layout.Handles.Select(handle => handle.Layer).Distinct().OrderDescending())
        {
            var bestKind = GizmoHandleKind.None;
            var bestDistance = float.PositiveInfinity;
            foreach (var handle in layout.Handles.Where(candidate => candidate.Layer == layer && candidate.Interactive))
            {
                if (!TryDistance(handle, pointer, out var distance))
                    continue;

                if (distance < bestDistance - TieEpsilon)
                {
                    bestDistance = distance;
                    bestKind = handle.Kind;
                }
            }

            if (bestKind != GizmoHandleKind.None)
                return bestKind;
        }

        return GizmoHandleKind.None;
    }

    /// <summary>
    /// Finds the closest hit distance inside one handle.
    /// </summary>
    /// <param name="handle">Handle geometry.</param>
    /// <param name="pointer">Pointer position.</param>
    /// <param name="distance">Closest accepted distance.</param>
    /// <returns>True when the pointer hits the handle.</returns>
    private static bool TryDistance(GizmoHandleGeometry handle, Vector2 pointer, out float distance)
    {
        distance = float.PositiveInfinity;
        foreach (var triangle in handle.Triangles)
        {
            if (Contains(triangle, pointer))
                distance = 0f;
        }

        foreach (var segment in handle.Segments)
        {
            var candidate = DistanceToSegment(pointer, segment.Start, segment.End);
            if (candidate <= segment.HitWidth * 0.5f)
                distance = MathF.Min(distance, candidate);
        }

        return float.IsFinite(distance);
    }

    /// <summary>
    /// Calculates distance from a point to a finite line segment.
    /// </summary>
    /// <param name="point">Point to measure.</param>
    /// <param name="start">Segment start.</param>
    /// <param name="end">Segment end.</param>
    /// <returns>The shortest distance in pixels.</returns>
    private static float DistanceToSegment(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared <= float.Epsilon)
            return Vector2.Distance(point, start);

        var amount = Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
        return Vector2.Distance(point, start + segment * amount);
    }

    /// <summary>
    /// Determines whether a point lies inside or on a triangle.
    /// </summary>
    /// <param name="triangle">Triangle to test.</param>
    /// <param name="point">Point to test.</param>
    /// <returns>True when the point is inside the triangle.</returns>
    private static bool Contains(GizmoTriangle triangle, Vector2 point)
    {
        var first = Cross(triangle.B - triangle.A, point - triangle.A);
        var second = Cross(triangle.C - triangle.B, point - triangle.B);
        var third = Cross(triangle.A - triangle.C, point - triangle.C);
        var hasNegative = first < -TieEpsilon || second < -TieEpsilon || third < -TieEpsilon;
        var hasPositive = first > TieEpsilon || second > TieEpsilon || third > TieEpsilon;
        return !(hasNegative && hasPositive);
    }

    /// <summary>
    /// Calculates the scalar 2D cross product.
    /// </summary>
    /// <param name="first">First vector.</param>
    /// <param name="second">Second vector.</param>
    /// <returns>The signed scalar cross product.</returns>
    private static float Cross(Vector2 first, Vector2 second)
    {
        return first.X * second.Y - first.Y * second.X;
    }
}
