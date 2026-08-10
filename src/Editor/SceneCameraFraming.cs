using System.Numerics;
using Engine.Core;
using Engine.Graphics;

namespace Editor;

/// <summary>Frames hierarchy nodes in the editor Scene camera.</summary>
public static class SceneCameraFraming
{
    /// <summary>Moves a camera to contain a node and its descendant mesh bounds.</summary>
    /// <param name="camera">Scene camera to reposition.</param>
    /// <param name="target">Hierarchy node to frame.</param>
    /// <returns>True when the target supplied a usable world-space focus point.</returns>
    public static bool TryFrame(PerspectiveCamera camera, Node3D target)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(target);

        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var foundBounds = AccumulateBounds(target, ref minimum, ref maximum);
        var center = foundBounds ? (minimum + maximum) * 0.5f : target.GetWorldPosition();
        return TryApply(camera, center, foundBounds, minimum, maximum);
    }

    /// <summary>Moves a camera to contain a set of mesh instances.</summary>
    /// <param name="camera">Scene camera to reposition.</param>
    /// <param name="meshes">Mesh instances whose combined bounds should be framed.</param>
    /// <returns>True when at least one mesh supplied finite world-space bounds.</returns>
    public static bool TryFrame(PerspectiveCamera camera, IReadOnlyList<MeshInstance3D> meshes)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(meshes);

        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        var found = false;
        for (var index = 0; index < meshes.Count; index++)
            found |= AccumulateMeshBounds(meshes[index], ref minimum, ref maximum);
        return found && TryApply(camera, (minimum + maximum) * 0.5f,
            hasBounds: true, minimum, maximum);
    }

    /// <summary>Applies a computed focus point and optional bounds to the camera.</summary>
    /// <param name="camera">Scene camera to reposition.</param>
    /// <param name="center">World-space focus point.</param>
    /// <param name="hasBounds">Whether minimum and maximum contain mesh bounds.</param>
    /// <param name="minimum">World-space bounds minimum.</param>
    /// <param name="maximum">World-space bounds maximum.</param>
    /// <returns>True when the focus point is finite.</returns>
    private static bool TryApply(
        PerspectiveCamera camera,
        Vector3 center,
        bool hasBounds,
        Vector3 minimum,
        Vector3 maximum)
    {
        if (!IsFinite(center))
            return false;

        var radius = hasBounds
            ? MathF.Max(Vector3.Distance(minimum, maximum) * 0.5f, 0.5f)
            : 0.5f;
        var halfVerticalFov = Math.Clamp(camera.Fov * 0.5f, 0.01f, MathF.PI * 0.49f);
        var halfHorizontalFov = MathF.Atan(MathF.Tan(halfVerticalFov) * MathF.Max(camera.Aspect, 0.01f));
        var limitingHalfFov = MathF.Min(halfVerticalFov, halfHorizontalFov);
        var distance = radius / MathF.Sin(limitingHalfFov) * 1.1f;
        var forward = camera.GetForwardVector();
        if (!IsFinite(forward) || forward.LengthSquared() <= float.Epsilon)
            forward = -Vector3.UnitZ;
        else
            forward = Vector3.Normalize(forward);

        camera.Position = center - forward * distance;
        camera.Near = MathF.Max(0.01f, MathF.Min(camera.Near, distance - radius));
        camera.Far = MathF.Max(camera.Far, distance + radius * 2f);
        camera.LookAt(center);
        return true;
    }

    /// <summary>Accumulates transformed mesh bounds from a hierarchy subtree.</summary>
    /// <param name="node">Current hierarchy node.</param>
    /// <param name="minimum">Accumulated world-space minimum.</param>
    /// <param name="maximum">Accumulated world-space maximum.</param>
    /// <returns>True when this subtree contains at least one finite mesh bound.</returns>
    private static bool AccumulateBounds(Node node, ref Vector3 minimum, ref Vector3 maximum)
    {
        var found = node is MeshInstance3D mesh &&
            AccumulateMeshBounds(mesh, ref minimum, ref maximum);

        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
            found |= AccumulateBounds(children[index], ref minimum, ref maximum);
        return found;
    }

    /// <summary>Accumulates the transformed corners of one mesh instance.</summary>
    /// <param name="mesh">Mesh instance to inspect.</param>
    /// <param name="minimum">Accumulated world-space minimum.</param>
    /// <param name="maximum">Accumulated world-space maximum.</param>
    /// <returns>True when the mesh contains at least one finite bound corner.</returns>
    private static bool AccumulateMeshBounds(
        MeshInstance3D mesh,
        ref Vector3 minimum,
        ref Vector3 maximum)
    {
        if (mesh.LocalBounds is not { } bounds)
            return false;
        var found = false;
        var model = mesh.GetModelMatrix();
        for (var cornerIndex = 0; cornerIndex < 8; cornerIndex++)
        {
            var corner = new Vector3(
                (cornerIndex & 1) == 0 ? bounds.Minimum.X : bounds.Maximum.X,
                (cornerIndex & 2) == 0 ? bounds.Minimum.Y : bounds.Maximum.Y,
                (cornerIndex & 4) == 0 ? bounds.Minimum.Z : bounds.Maximum.Z);
            var world = Vector3.Transform(corner, model);
            if (!IsFinite(world))
                continue;
            minimum = Vector3.Min(minimum, world);
            maximum = Vector3.Max(maximum, world);
            found = true;
        }
        return found;
    }

    /// <summary>Checks whether every vector component is finite.</summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when all components are finite.</returns>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
