using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Generates the shared screen-space geometry used to draw and pick editor gizmos.
/// </summary>
internal static class GizmoLayout
{
    internal const float AxisPixels = 96f;
    internal const float RingPixels = 72f;
    internal const float VisibleLinePixels = 2f;
    internal const float HitLinePixels = 8f;
    internal const float ArrowLengthPixels = 12f;
    internal const int RingSegments = 64;

    private const float MinimumInteractiveAxisPixels = 4f;
    private static readonly Vector3[] Axes = [Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ];
    private static readonly Vector3[] Colors = [new(1f, 0f, 0f), new(0f, 1f, 0f), new(0f, 0f, 1f)];

    /// <summary>
    /// Creates a validated constant-size gizmo layout.
    /// </summary>
    /// <param name="target">Target world position.</param>
    /// <param name="view">View matrix.</param>
    /// <param name="projection">Projection matrix.</param>
    /// <param name="viewport">Scene viewport.</param>
    /// <returns>A valid shared layout, or <see cref="GizmoLayoutResult.Empty"/>.</returns>
    internal static GizmoLayoutResult Create(Vector3 target, Matrix4x4 view, Matrix4x4 projection, GizmoViewport viewport)
    {
        if (!GizmoProjection.TryWorldToScreen(target, view, projection, viewport, out var center)
            || !GizmoProjection.TryWorldUnitsPerPixel(target, view, projection, viewport, out var worldUnitsPerPixel))
            return GizmoLayoutResult.Empty;

        var handles = new List<GizmoHandleGeometry>(6);
        for (var axisIndex = 0; axisIndex < Axes.Length; axisIndex++)
            handles.Add(CreateRing(axisIndex, target, center, worldUnitsPerPixel, view, projection, viewport));
        for (var axisIndex = 0; axisIndex < Axes.Length; axisIndex++)
            handles.Add(CreateAxis(axisIndex, target, center, worldUnitsPerPixel, view, projection, viewport));

        return new GizmoLayoutResult
        {
            IsValid = true,
            Viewport = viewport,
            View = view,
            Projection = projection,
            TargetWorld = target,
            TargetScreen = center,
            WorldUnitsPerPixel = worldUnitsPerPixel,
            Handles = handles
        };
    }

    /// <summary>
    /// Creates one segmented rotation ring.
    /// </summary>
    /// <param name="axisIndex">World-axis index.</param>
    /// <param name="target">Target world position.</param>
    /// <param name="center">Target screen position.</param>
    /// <param name="worldUnitsPerPixel">World scale at the target.</param>
    /// <param name="view">View matrix.</param>
    /// <param name="projection">Projection matrix.</param>
    /// <param name="viewport">Scene viewport.</param>
    /// <returns>The rotation handle geometry.</returns>
    private static GizmoHandleGeometry CreateRing(int axisIndex, Vector3 target, Vector2 center, float worldUnitsPerPixel, Matrix4x4 view, Matrix4x4 projection, GizmoViewport viewport)
    {
        var axis = Axes[axisIndex];
        var (basisA, basisB) = CreateCircleBasis(axis);
        var radius = RingPixels * worldUnitsPerPixel;
        var segments = new List<GizmoSegment>(RingSegments);
        var extent = 0f;

        for (var segmentIndex = 0; segmentIndex < RingSegments; segmentIndex++)
        {
            var startAngle = MathF.Tau * segmentIndex / RingSegments;
            var endAngle = MathF.Tau * (segmentIndex + 1) / RingSegments;
            var startWorld = target + (basisA * MathF.Cos(startAngle) + basisB * MathF.Sin(startAngle)) * radius;
            var endWorld = target + (basisA * MathF.Cos(endAngle) + basisB * MathF.Sin(endAngle)) * radius;
            if (!GizmoProjection.TryWorldToScreen(startWorld, view, projection, viewport, out var start)
                || !GizmoProjection.TryWorldToScreen(endWorld, view, projection, viewport, out var end))
                continue;

            extent = MathF.Max(extent, MathF.Max(Vector2.Distance(center, start), Vector2.Distance(center, end)));
            if (GizmoProjection.ClipSegment(viewport, ref start, ref end))
                segments.Add(new GizmoSegment(start, end, VisibleLinePixels, HitLinePixels));
        }

        return new GizmoHandleGeometry(
            (GizmoHandleKind)((int)GizmoHandleKind.RotateX + axisIndex),
            0,
            Colors[axisIndex],
            segments.Count > 0,
            segments,
            [],
            extent);
    }

    /// <summary>
    /// Creates one translation line and arrowhead.
    /// </summary>
    /// <param name="axisIndex">World-axis index.</param>
    /// <param name="target">Target world position.</param>
    /// <param name="center">Target screen position.</param>
    /// <param name="worldUnitsPerPixel">World scale at the target.</param>
    /// <param name="view">View matrix.</param>
    /// <param name="projection">Projection matrix.</param>
    /// <param name="viewport">Scene viewport.</param>
    /// <returns>The translation handle geometry.</returns>
    private static GizmoHandleGeometry CreateAxis(int axisIndex, Vector3 target, Vector2 center, float worldUnitsPerPixel, Matrix4x4 view, Matrix4x4 projection, GizmoViewport viewport)
    {
        var endWorld = target + Axes[axisIndex] * AxisPixels * worldUnitsPerPixel;
        if (!GizmoProjection.TryWorldToScreen(endWorld, view, projection, viewport, out var projectedEnd))
            return EmptyAxis(axisIndex);

        var extent = Vector2.Distance(center, projectedEnd);
        var interactive = extent >= MinimumInteractiveAxisPixels;
        var segments = new List<GizmoSegment>(1);
        var triangles = new List<GizmoTriangle>(2);
        var clippedStart = center;
        var clippedEnd = projectedEnd;
        if (GizmoProjection.ClipSegment(viewport, ref clippedStart, ref clippedEnd))
            segments.Add(new GizmoSegment(clippedStart, clippedEnd, VisibleLinePixels, HitLinePixels));

        if (interactive)
        {
            var direction = Vector2.Normalize(projectedEnd - center);
            var normal = new Vector2(-direction.Y, direction.X);
            var baseCenter = projectedEnd - direction * ArrowLengthPixels;
            var arrow = new GizmoTriangle(projectedEnd, baseCenter + normal * ArrowLengthPixels * 0.45f, baseCenter - normal * ArrowLengthPixels * 0.45f);
            triangles.AddRange(GizmoProjection.ClipTriangle(viewport, arrow));
        }

        return new GizmoHandleGeometry(
            (GizmoHandleKind)((int)GizmoHandleKind.TranslateX + axisIndex),
            1,
            Colors[axisIndex],
            interactive && segments.Count > 0,
            segments,
            triangles,
            extent);
    }

    /// <summary>
    /// Creates an inert translation handle for an unprojectable axis.
    /// </summary>
    /// <param name="axisIndex">World-axis index.</param>
    /// <returns>An empty foreground handle.</returns>
    private static GizmoHandleGeometry EmptyAxis(int axisIndex)
    {
        return new GizmoHandleGeometry(
            (GizmoHandleKind)((int)GizmoHandleKind.TranslateX + axisIndex),
            1,
            Colors[axisIndex],
            false,
            [],
            [],
            0f);
    }

    /// <summary>
    /// Creates an orthonormal basis perpendicular to an axis.
    /// </summary>
    /// <param name="axis">Normalized axis.</param>
    /// <returns>Two perpendicular unit vectors.</returns>
    private static (Vector3 First, Vector3 Second) CreateCircleBasis(Vector3 axis)
    {
        var reference = MathF.Abs(Vector3.Dot(axis, Vector3.UnitY)) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        var first = Vector3.Normalize(Vector3.Cross(axis, reference));
        return (first, Vector3.Cross(axis, first));
    }
}
