using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Provides validated projection and viewport clipping operations for editor gizmos.
/// </summary>
internal static class GizmoProjection
{
    private const float Epsilon = 0.000001f;

    /// <summary>
    /// Projects a world point to screen pixels.
    /// </summary>
    /// <param name="world">World-space point.</param>
    /// <param name="view">View matrix.</param>
    /// <param name="projection">Projection matrix.</param>
    /// <param name="viewport">Screen viewport.</param>
    /// <param name="screen">Projected screen point when successful.</param>
    /// <returns>True when the point is finite and in front of the camera.</returns>
    internal static bool TryWorldToScreen(Vector3 world, Matrix4x4 view, Matrix4x4 projection, GizmoViewport viewport, out Vector2 screen)
    {
        screen = default;
        if (!viewport.IsValid || !IsFinite(world) || !IsFinite(view) || !IsFinite(projection))
            return false;

        var clip = Vector4.Transform(new Vector4(world, 1f), view * projection);
        if (!IsFinite(clip) || clip.W <= Epsilon)
            return false;

        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;
        screen = new Vector2(
            viewport.X + (ndcX + 1f) * 0.5f * viewport.Width,
            viewport.Y + (ndcY + 1f) * 0.5f * viewport.Height);
        return IsFinite(screen);
    }

    /// <summary>
    /// Unprojects a screen point to a world-space ray.
    /// </summary>
    /// <param name="screen">Screen point in pixels.</param>
    /// <param name="view">View matrix.</param>
    /// <param name="projection">Projection matrix.</param>
    /// <param name="viewport">Screen viewport.</param>
    /// <param name="origin">Ray origin on the near plane.</param>
    /// <param name="direction">Normalized ray direction.</param>
    /// <returns>True when unprojection produces a finite ray.</returns>
    internal static bool TryScreenToRay(Vector2 screen, Matrix4x4 view, Matrix4x4 projection, GizmoViewport viewport, out Vector3 origin, out Vector3 direction)
    {
        origin = default;
        direction = default;
        if (!viewport.IsValid || !IsFinite(screen) || !Matrix4x4.Invert(view * projection, out var inverse))
            return false;

        var ndcX = ((screen.X - viewport.X) / viewport.Width) * 2f - 1f;
        var ndcY = ((screen.Y - viewport.Y) / viewport.Height) * 2f - 1f;
        var near = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), inverse);
        var far = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), inverse);
        if (!IsFinite(near) || !IsFinite(far) || MathF.Abs(near.W) <= Epsilon || MathF.Abs(far.W) <= Epsilon)
            return false;

        near /= near.W;
        far /= far.W;
        origin = new Vector3(near.X, near.Y, near.Z);
        var offset = new Vector3(far.X, far.Y, far.Z) - origin;
        var lengthSquared = offset.LengthSquared();
        if (!IsFinite(origin) || !IsFinite(offset) || lengthSquared <= Epsilon)
            return false;

        direction = offset / MathF.Sqrt(lengthSquared);
        return IsFinite(direction);
    }

    /// <summary>
    /// Calculates world distance represented by one vertical pixel at a target depth.
    /// </summary>
    /// <param name="target">Target world position.</param>
    /// <param name="view">View matrix.</param>
    /// <param name="projection">Projection matrix.</param>
    /// <param name="viewport">Screen viewport.</param>
    /// <param name="worldUnitsPerPixel">Calculated scale when successful.</param>
    /// <returns>True for a finite target in front of a perspective camera.</returns>
    internal static bool TryWorldUnitsPerPixel(Vector3 target, Matrix4x4 view, Matrix4x4 projection, GizmoViewport viewport, out float worldUnitsPerPixel)
    {
        worldUnitsPerPixel = 0f;
        if (!TryWorldToScreen(target, view, projection, viewport, out _))
            return false;

        var viewPoint = Vector4.Transform(new Vector4(target, 1f), view);
        var depth = -viewPoint.Z;
        var verticalScale = MathF.Abs(projection.M22);
        if (!float.IsFinite(depth) || depth <= Epsilon || !float.IsFinite(verticalScale) || verticalScale <= Epsilon)
            return false;

        worldUnitsPerPixel = 2f * depth / (verticalScale * viewport.Height);
        return float.IsFinite(worldUnitsPerPixel) && worldUnitsPerPixel > 0f;
    }

    /// <summary>
    /// Clips a line segment to a viewport using Liang-Barsky clipping.
    /// </summary>
    /// <param name="viewport">Clip rectangle.</param>
    /// <param name="start">Segment start, replaced with the clipped point.</param>
    /// <param name="end">Segment end, replaced with the clipped point.</param>
    /// <returns>True when any segment portion remains.</returns>
    internal static bool ClipSegment(GizmoViewport viewport, ref Vector2 start, ref Vector2 end)
    {
        if (!viewport.IsValid || !IsFinite(start) || !IsFinite(end))
            return false;

        var delta = end - start;
        var minimum = 0f;
        var maximum = 1f;
        if (!ClipTest(-delta.X, start.X - viewport.X, ref minimum, ref maximum)
            || !ClipTest(delta.X, viewport.X + viewport.Width - start.X, ref minimum, ref maximum)
            || !ClipTest(-delta.Y, start.Y - viewport.Y, ref minimum, ref maximum)
            || !ClipTest(delta.Y, viewport.Y + viewport.Height - start.Y, ref minimum, ref maximum))
            return false;

        var originalStart = start;
        start = originalStart + delta * minimum;
        end = originalStart + delta * maximum;
        return IsFinite(start) && IsFinite(end);
    }

    /// <summary>
    /// Clips a triangle to the viewport and triangulates the remaining polygon.
    /// </summary>
    /// <param name="viewport">Clip rectangle.</param>
    /// <param name="triangle">Triangle to clip.</param>
    /// <returns>Zero or more clipped triangles.</returns>
    internal static IReadOnlyList<GizmoTriangle> ClipTriangle(GizmoViewport viewport, GizmoTriangle triangle)
    {
        if (!viewport.IsValid || !IsFinite(triangle.A) || !IsFinite(triangle.B) || !IsFinite(triangle.C))
            return [];

        List<Vector2> polygon = [triangle.A, triangle.B, triangle.C];
        polygon = ClipPolygon(polygon, point => point.X >= viewport.X, (a, b) => IntersectVertical(a, b, viewport.X));
        polygon = ClipPolygon(polygon, point => point.X <= viewport.X + viewport.Width, (a, b) => IntersectVertical(a, b, viewport.X + viewport.Width));
        polygon = ClipPolygon(polygon, point => point.Y >= viewport.Y, (a, b) => IntersectHorizontal(a, b, viewport.Y));
        polygon = ClipPolygon(polygon, point => point.Y <= viewport.Y + viewport.Height, (a, b) => IntersectHorizontal(a, b, viewport.Y + viewport.Height));

        var result = new List<GizmoTriangle>();
        for (var index = 1; index < polygon.Count - 1; index++)
            result.Add(new GizmoTriangle(polygon[0], polygon[index], polygon[index + 1]));
        return result;
    }

    /// <summary>
    /// Updates a Liang-Barsky parameter range for one rectangle boundary.
    /// </summary>
    /// <param name="denominator">Boundary denominator.</param>
    /// <param name="numerator">Boundary numerator.</param>
    /// <param name="minimum">Current minimum segment parameter.</param>
    /// <param name="maximum">Current maximum segment parameter.</param>
    /// <returns>True when the segment remains potentially visible.</returns>
    private static bool ClipTest(float denominator, float numerator, ref float minimum, ref float maximum)
    {
        if (MathF.Abs(denominator) <= Epsilon)
            return numerator >= 0f;

        var ratio = numerator / denominator;
        if (denominator < 0f)
        {
            if (ratio > maximum)
                return false;
            minimum = MathF.Max(minimum, ratio);
        }
        else
        {
            if (ratio < minimum)
                return false;
            maximum = MathF.Min(maximum, ratio);
        }
        return true;
    }

    /// <summary>
    /// Clips a polygon against one boundary.
    /// </summary>
    /// <param name="input">Input polygon.</param>
    /// <param name="inside">Boundary inclusion predicate.</param>
    /// <param name="intersection">Boundary intersection function.</param>
    /// <returns>The clipped polygon.</returns>
    private static List<Vector2> ClipPolygon(List<Vector2> input, Func<Vector2, bool> inside, Func<Vector2, Vector2, Vector2> intersection)
    {
        var output = new List<Vector2>();
        if (input.Count == 0)
            return output;

        var previous = input[^1];
        var previousInside = inside(previous);
        foreach (var current in input)
        {
            var currentInside = inside(current);
            if (currentInside != previousInside)
                output.Add(intersection(previous, current));
            if (currentInside)
                output.Add(current);
            previous = current;
            previousInside = currentInside;
        }
        return output;
    }

    /// <summary>
    /// Intersects a segment with a vertical screen line.
    /// </summary>
    /// <param name="start">Segment start.</param>
    /// <param name="end">Segment end.</param>
    /// <param name="x">Vertical line coordinate.</param>
    /// <returns>The intersection point.</returns>
    private static Vector2 IntersectVertical(Vector2 start, Vector2 end, float x)
    {
        var delta = end - start;
        var amount = MathF.Abs(delta.X) <= Epsilon ? 0f : (x - start.X) / delta.X;
        return new Vector2(x, start.Y + delta.Y * amount);
    }

    /// <summary>
    /// Intersects a segment with a horizontal screen line.
    /// </summary>
    /// <param name="start">Segment start.</param>
    /// <param name="end">Segment end.</param>
    /// <param name="y">Horizontal line coordinate.</param>
    /// <returns>The intersection point.</returns>
    private static Vector2 IntersectHorizontal(Vector2 start, Vector2 end, float y)
    {
        var delta = end - start;
        var amount = MathF.Abs(delta.Y) <= Epsilon ? 0f : (y - start.Y) / delta.Y;
        return new Vector2(start.X + delta.X * amount, y);
    }

    /// <summary>
    /// Determines whether a vector contains only finite values.
    /// </summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    /// <summary>
    /// Determines whether a vector contains only finite values.
    /// </summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>
    /// Determines whether a vector contains only finite values.
    /// </summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) && float.IsFinite(value.W);

    /// <summary>
    /// Determines whether a matrix contains only finite values.
    /// </summary>
    /// <param name="value">Matrix to inspect.</param>
    /// <returns>True when every element is finite.</returns>
    private static bool IsFinite(Matrix4x4 value)
    {
        return IsFinite(new Vector4(value.M11, value.M12, value.M13, value.M14))
            && IsFinite(new Vector4(value.M21, value.M22, value.M23, value.M24))
            && IsFinite(new Vector4(value.M31, value.M32, value.M33, value.M34))
            && IsFinite(new Vector4(value.M41, value.M42, value.M43, value.M44));
    }
}
