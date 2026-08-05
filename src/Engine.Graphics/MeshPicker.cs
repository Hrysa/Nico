using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Performs CPU picking against transformed mesh bounds.
/// </summary>
public static class MeshPicker
{
    /// <summary>
    /// Finds the closest mesh instance intersected by a screen-space ray.
    /// </summary>
    /// <param name="instances">Candidate mesh instances.</param>
    /// <param name="camera">Viewport camera.</param>
    /// <param name="viewportX">Viewport left edge.</param>
    /// <param name="viewportY">Viewport top edge.</param>
    /// <param name="viewportWidth">Viewport width.</param>
    /// <param name="viewportHeight">Viewport height.</param>
    /// <param name="screenPosition">Pointer position in window pixels.</param>
    /// <returns>The closest intersected instance, or null.</returns>
    public static MeshInstance3D? Pick(
        IEnumerable<MeshInstance3D> instances,
        ICamera camera,
        float viewportX,
        float viewportY,
        float viewportWidth,
        float viewportHeight,
        Vector2 screenPosition)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(camera);
        if (!TryCreateRay(camera, viewportX, viewportY, viewportWidth, viewportHeight,
                screenPosition, out var rayOrigin, out var rayDirection))
            return null;

        MeshInstance3D? closest = null;
        var closestDistance = float.PositiveInfinity;
        foreach (var instance in instances)
        {
            if (!TryIntersect(instance, rayOrigin, rayDirection, out var distance)
                || distance >= closestDistance)
                continue;

            closest = instance;
            closestDistance = distance;
        }

        return closest;
    }

    /// <summary>Creates a world-space ray through a viewport pixel.</summary>
    /// <param name="camera">Viewport camera.</param>
    /// <param name="viewportX">Viewport left edge.</param>
    /// <param name="viewportY">Viewport top edge.</param>
    /// <param name="viewportWidth">Viewport width.</param>
    /// <param name="viewportHeight">Viewport height.</param>
    /// <param name="screenPosition">Pointer position.</param>
    /// <param name="origin">Created ray origin.</param>
    /// <param name="direction">Created normalized ray direction.</param>
    /// <returns>True when a finite ray was created.</returns>
    private static bool TryCreateRay(
        ICamera camera,
        float viewportX,
        float viewportY,
        float viewportWidth,
        float viewportHeight,
        Vector2 screenPosition,
        out Vector3 origin,
        out Vector3 direction)
    {
        origin = default;
        direction = default;
        if (viewportWidth <= 0f || viewportHeight <= 0f
            || !Matrix4x4.Invert(camera.GetViewMatrix() * camera.GetProjectionMatrix(), out var inverse))
            return false;

        var x = ((screenPosition.X - viewportX) / viewportWidth) * 2f - 1f;
        var y = ((screenPosition.Y - viewportY) / viewportHeight) * 2f - 1f;
        var near = Vector4.Transform(new Vector4(x, y, 0f, 1f), inverse);
        var far = Vector4.Transform(new Vector4(x, y, 1f, 1f), inverse);
        if (MathF.Abs(near.W) <= float.Epsilon || MathF.Abs(far.W) <= float.Epsilon)
            return false;

        origin = new Vector3(near.X, near.Y, near.Z) / near.W;
        var farPoint = new Vector3(far.X, far.Y, far.Z) / far.W;
        var delta = farPoint - origin;
        if (!IsFinite(origin) || !IsFinite(delta) || delta.LengthSquared() <= float.Epsilon)
            return false;

        direction = Vector3.Normalize(delta);
        return true;
    }

    /// <summary>Intersects a world-space ray with one transformed mesh bound.</summary>
    /// <param name="instance">Mesh instance.</param>
    /// <param name="rayOrigin">World-space ray origin.</param>
    /// <param name="rayDirection">World-space ray direction.</param>
    /// <param name="distance">World-space distance to the hit.</param>
    /// <returns>True when the ray intersects the mesh bounds.</returns>
    private static bool TryIntersect(
        MeshInstance3D instance,
        Vector3 rayOrigin,
        Vector3 rayDirection,
        out float distance)
    {
        distance = 0f;
        if (!TryGetBounds(instance, out var minimum, out var maximum)
            || !Matrix4x4.Invert(instance.GetModelMatrix(), out var inverseModel))
            return false;

        var localOrigin = Vector3.Transform(rayOrigin, inverseModel);
        var localDirection = Vector3.TransformNormal(rayDirection, inverseModel);
        if (!TryIntersectBounds(localOrigin, localDirection, minimum, maximum, out var localDistance))
            return false;

        var localHit = localOrigin + localDirection * localDistance;
        var worldHit = Vector3.Transform(localHit, instance.GetModelMatrix());
        distance = Vector3.Distance(rayOrigin, worldHit);
        return float.IsFinite(distance);
    }

    /// <summary>Gets decoded imported bounds or computes procedural mesh bounds.</summary>
    /// <param name="instance">Candidate mesh instance.</param>
    /// <param name="minimum">Resolved minimum corner.</param>
    /// <param name="maximum">Resolved maximum corner.</param>
    /// <returns>True when usable local bounds exist.</returns>
    private static bool TryGetBounds(
        MeshInstance3D instance,
        out Vector3 minimum,
        out Vector3 maximum)
    {
        if (instance.LocalBounds is { } imported)
        {
            minimum = imported.Minimum;
            maximum = imported.Maximum;
            return true;
        }
        minimum = default;
        maximum = default;
        return false;
    }

    /// <summary>Intersects a ray with an axis-aligned bounding box.</summary>
    /// <param name="origin">Ray origin.</param>
    /// <param name="direction">Ray direction.</param>
    /// <param name="minimum">Minimum bound.</param>
    /// <param name="maximum">Maximum bound.</param>
    /// <param name="distance">Distance along the ray.</param>
    /// <returns>True when the ray intersects the box in front of its origin.</returns>
    private static bool TryIntersectBounds(
        Vector3 origin,
        Vector3 direction,
        Vector3 minimum,
        Vector3 maximum,
        out float distance)
    {
        var near = 0f;
        var far = float.PositiveInfinity;
        for (var axis = 0; axis < 3; axis++)
        {
            var ray = direction[axis];
            if (MathF.Abs(ray) <= 0.000001f)
            {
                if (origin[axis] < minimum[axis] || origin[axis] > maximum[axis])
                {
                    distance = 0f;
                    return false;
                }
                continue;
            }

            var first = (minimum[axis] - origin[axis]) / ray;
            var second = (maximum[axis] - origin[axis]) / ray;
            if (first > second)
                (first, second) = (second, first);
            near = MathF.Max(near, first);
            far = MathF.Min(far, second);
            if (near > far)
            {
                distance = 0f;
                return false;
            }
        }

        distance = near;
        return far >= 0f;
    }

    /// <summary>Checks whether all vector components are finite.</summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
