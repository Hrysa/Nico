using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Calculates one transform drag entirely from immutable mouse-down state.
/// </summary>
internal sealed class GizmoDragSession
{
    private const float Epsilon = 0.00001f;

    private readonly GizmoHandleKind _handle;
    private readonly GizmoTransform _original;
    private readonly GizmoLayoutResult _layout;
    private readonly Vector2 _startPointer;
    private readonly Vector3 _worldAxis;
    private readonly DragStrategy _strategy;
    private readonly Vector2 _screenDirection;
    private readonly float _worldUnitsPerScreenPixel;
    private readonly Vector3 _startRadial;

    /// <summary>
    /// Initializes a captured drag session.
    /// </summary>
    /// <param name="handle">Active handle.</param>
    /// <param name="original">Original transform.</param>
    /// <param name="layout">Captured layout and camera state.</param>
    /// <param name="startPointer">Mouse-down pointer.</param>
    /// <param name="worldAxis">Selected world axis.</param>
    /// <param name="strategy">Fixed drag calculation strategy.</param>
    /// <param name="screenDirection">Captured projected axis or tangent.</param>
    /// <param name="worldUnitsPerScreenPixel">Translation conversion ratio.</param>
    /// <param name="startRadial">Rotation-plane starting radial vector.</param>
    private GizmoDragSession(
        GizmoHandleKind handle,
        GizmoTransform original,
        GizmoLayoutResult layout,
        Vector2 startPointer,
        Vector3 worldAxis,
        DragStrategy strategy,
        Vector2 screenDirection,
        float worldUnitsPerScreenPixel,
        Vector3 startRadial)
    {
        _handle = handle;
        _original = original;
        _layout = layout;
        _startPointer = startPointer;
        _worldAxis = worldAxis;
        _strategy = strategy;
        _screenDirection = screenDirection;
        _worldUnitsPerScreenPixel = worldUnitsPerScreenPixel;
        _startRadial = startRadial;
    }

    /// <summary>
    /// Attempts to capture a stable drag session for a handle.
    /// </summary>
    /// <param name="handle">Handle to drag.</param>
    /// <param name="pointer">Mouse-down pointer.</param>
    /// <param name="original">Original target transform.</param>
    /// <param name="layout">Current validated layout.</param>
    /// <param name="session">Captured session when successful.</param>
    /// <returns>True when a stable calculation strategy is available.</returns>
    internal static bool TryStart(GizmoHandleKind handle, Vector2 pointer, GizmoTransform original, GizmoLayoutResult layout, out GizmoDragSession? session)
    {
        session = null;
        if (!layout.IsValid || !IsFinite(pointer) || !IsFinite(original.Position) || !IsFinite(original.Rotation))
            return false;

        var geometryIndex = -1;
        for (var index = 0; index < layout.Handles.Count; index++)
        {
            if (layout.Handles[index].Kind == handle)
            {
                geometryIndex = index;
                break;
            }
        }
        if (geometryIndex < 0 || !TryGetAxis(handle, out var worldAxis))
            return false;
        var geometry = layout.Handles[geometryIndex];
        if (!geometry.Interactive)
            return false;

        if (IsTranslation(handle))
        {
            if (geometry.Segments.Count == 0 || geometry.ScreenExtent <= Epsilon)
                return false;
            var projected = geometry.Segments[0].End - geometry.Segments[0].Start;
            if (projected.LengthSquared() <= Epsilon)
                return false;

            var worldUnitsPerScreenPixel = GizmoLayout.AxisPixels * layout.WorldUnitsPerPixel / geometry.ScreenExtent;
            if (!float.IsFinite(worldUnitsPerScreenPixel) || worldUnitsPerScreenPixel <= 0f)
                return false;

            session = new GizmoDragSession(handle, original, layout, pointer, worldAxis, DragStrategy.Translation,
                Vector2.Normalize(projected), worldUnitsPerScreenPixel, default);
            return true;
        }

        if (GizmoProjection.TryScreenToRay(pointer, layout.View, layout.Projection, layout.Viewport, out var rayOrigin, out var rayDirection)
            && TryIntersectRotationPlane(rayOrigin, rayDirection, layout.TargetWorld, worldAxis, out var radial))
        {
            session = new GizmoDragSession(handle, original, layout, pointer, worldAxis, DragStrategy.RotationPlane,
                default, 0f, radial);
            return true;
        }

        if (!TryFindTangent(geometry, pointer, out var tangent))
            return false;

        session = new GizmoDragSession(handle, original, layout, pointer, worldAxis, DragStrategy.RotationTangent,
            tangent, 0f, default);
        return true;
    }

    /// <summary>
    /// Calculates a transform update from the captured mouse-down state.
    /// </summary>
    /// <param name="pointer">Current pointer position.</param>
    /// <param name="transform">Calculated transform when successful.</param>
    /// <returns>True when a finite update is available.</returns>
    internal bool TryUpdate(Vector2 pointer, out GizmoTransform transform)
    {
        transform = _original;
        if (!IsFinite(pointer))
            return false;

        if (_strategy == DragStrategy.Translation)
        {
            var pixels = Vector2.Dot(pointer - _startPointer, _screenDirection);
            var distance = pixels * _worldUnitsPerScreenPixel;
            var position = _original.Position + _worldAxis * distance;
            if (!IsFinite(position))
                return false;
            transform = _original with { Position = position };
            return true;
        }

        float radians;
        if (_strategy == DragStrategy.RotationPlane)
        {
            if (!GizmoProjection.TryScreenToRay(pointer, _layout.View, _layout.Projection, _layout.Viewport, out var rayOrigin, out var rayDirection)
                || !TryIntersectRotationPlane(rayOrigin, rayDirection, _layout.TargetWorld, _worldAxis, out var currentRadial))
                return false;
            radians = MathF.Atan2(
                Vector3.Dot(_worldAxis, Vector3.Cross(_startRadial, currentRadial)),
                Vector3.Dot(_startRadial, currentRadial));
        }
        else
        {
            radians = Vector2.Dot(pointer - _startPointer, _screenDirection) / GizmoLayout.RingPixels;
        }

        if (!float.IsFinite(radians))
            return false;
        var rotation = GizmoTransformMath.RotateWorld(_original.Rotation, _worldAxis, radians);
        if (!IsFinite(rotation))
            return false;
        transform = _original with { Rotation = rotation };
        return true;
    }

    /// <summary>
    /// Intersects a world ray with a rotation plane and returns a normalized radial vector.
    /// </summary>
    /// <param name="rayOrigin">World ray origin.</param>
    /// <param name="rayDirection">Normalized world ray direction.</param>
    /// <param name="planeOrigin">Point on the plane.</param>
    /// <param name="planeNormal">Plane normal and rotation axis.</param>
    /// <param name="radial">Normalized in-plane vector.</param>
    /// <returns>True when the intersection is stable and in front of the ray.</returns>
    private static bool TryIntersectRotationPlane(Vector3 rayOrigin, Vector3 rayDirection, Vector3 planeOrigin, Vector3 planeNormal, out Vector3 radial)
    {
        radial = default;
        var denominator = Vector3.Dot(rayDirection, planeNormal);
        if (!float.IsFinite(denominator) || MathF.Abs(denominator) <= Epsilon)
            return false;

        var distance = Vector3.Dot(planeOrigin - rayOrigin, planeNormal) / denominator;
        if (!float.IsFinite(distance) || distance < 0f)
            return false;

        var vector = rayOrigin + rayDirection * distance - planeOrigin;
        var lengthSquared = vector.LengthSquared();
        if (!IsFinite(vector) || lengthSquared <= Epsilon)
            return false;
        radial = vector / MathF.Sqrt(lengthSquared);
        return true;
    }

    /// <summary>
    /// Finds the displayed ring segment closest to the pointer and returns its tangent.
    /// </summary>
    /// <param name="geometry">Rotation ring geometry.</param>
    /// <param name="pointer">Mouse-down pointer.</param>
    /// <param name="tangent">Normalized screen tangent.</param>
    /// <returns>True when a non-degenerate ring segment exists.</returns>
    private static bool TryFindTangent(GizmoHandleGeometry geometry, Vector2 pointer, out Vector2 tangent)
    {
        tangent = default;
        var bestDistance = float.PositiveInfinity;
        foreach (var segment in geometry.Segments)
        {
            var direction = segment.End - segment.Start;
            var lengthSquared = direction.LengthSquared();
            if (lengthSquared <= Epsilon)
                continue;

            var amount = Math.Clamp(Vector2.Dot(pointer - segment.Start, direction) / lengthSquared, 0f, 1f);
            var distance = Vector2.DistanceSquared(pointer, segment.Start + direction * amount);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                tangent = direction / MathF.Sqrt(lengthSquared);
            }
        }
        return IsFinite(tangent) && tangent.LengthSquared() > Epsilon;
    }

    /// <summary>
    /// Determines whether a handle performs translation.
    /// </summary>
    /// <param name="handle">Handle identity.</param>
    /// <returns>True for a translation handle.</returns>
    private static bool IsTranslation(GizmoHandleKind handle)
    {
        return handle is GizmoHandleKind.TranslateX or GizmoHandleKind.TranslateY or GizmoHandleKind.TranslateZ;
    }

    /// <summary>
    /// Resolves a semantic handle to its world axis.
    /// </summary>
    /// <param name="handle">Handle identity.</param>
    /// <param name="axis">Matching unit axis.</param>
    /// <returns>True for a supported transform handle.</returns>
    private static bool TryGetAxis(GizmoHandleKind handle, out Vector3 axis)
    {
        axis = handle switch
        {
            GizmoHandleKind.RotateX or GizmoHandleKind.TranslateX => Vector3.UnitX,
            GizmoHandleKind.RotateY or GizmoHandleKind.TranslateY => Vector3.UnitY,
            GizmoHandleKind.RotateZ or GizmoHandleKind.TranslateZ => Vector3.UnitZ,
            _ => default
        };
        return axis != Vector3.Zero;
    }

    /// <summary>
    /// Determines whether a screen vector contains only finite values.
    /// </summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector2 value) => float.IsFinite(value.X) && float.IsFinite(value.Y);

    /// <summary>
    /// Determines whether a world vector contains only finite values.
    /// </summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>
    /// Identifies the fixed calculation chosen at mouse-down.
    /// </summary>
    private enum DragStrategy
    {
        Translation,
        RotationPlane,
        RotationTangent
    }
}
