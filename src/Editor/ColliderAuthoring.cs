using System.Numerics;
using Engine.Core;
using Engine.Graphics;

namespace Editor;

/// <summary>Identifies concrete collider types exposed by editor authoring commands.</summary>
public enum ColliderAuthoringKind
{
    Box,
    Sphere,
    Capsule,
    Cylinder,
    Plane,
    Mesh,
    Terrain
}

/// <summary>Creates concrete colliders and performs one-time render-bounds fitting.</summary>
public static class ColliderAuthoring
{
    /// <summary>Creates and fits one concrete collider for a scene node.</summary>
    /// <param name="kind">Requested collider type.</param>
    /// <param name="node">Authored component owner.</param>
    /// <returns>A detached collider ready to attach.</returns>
    public static ColliderComponent Create(ColliderAuthoringKind kind, Node3D node)
    {
        ArgumentNullException.ThrowIfNull(node);
        TryGetCombinedLocalBounds(node, out var minimum, out var maximum);
        var size = maximum - minimum;
        var center = (minimum + maximum) * 0.5f;
        var radius = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) * 0.5f;
        ColliderComponent collider = kind switch
        {
            ColliderAuthoringKind.Sphere => new SphereColliderComponent
                { Radius = MathF.Max(radius, 0.001f) },
            ColliderAuthoringKind.Capsule => new CapsuleColliderComponent
            {
                Radius = MathF.Max(MathF.Max(size.X, size.Z) * 0.5f, 0.001f),
                Height = MathF.Max(size.Y, 0.001f)
            },
            ColliderAuthoringKind.Cylinder => new CylinderColliderComponent
            {
                Radius = MathF.Max(MathF.Max(size.X, size.Z) * 0.5f, 0.001f),
                Height = MathF.Max(size.Y, 0.001f)
            },
            ColliderAuthoringKind.Plane => new PlaneColliderComponent
            {
                Size = new Vector2(MathF.Max(size.X, 0.001f), MathF.Max(size.Z, 0.001f))
            },
            ColliderAuthoringKind.Mesh => new MeshColliderComponent
            {
                Mesh = (node as MeshInstance3D)?.Mesh
            },
            ColliderAuthoringKind.Terrain => new TerrainColliderComponent
            {
                HorizontalSize = new Vector2(
                    MathF.Max(size.X, 0.001f), MathF.Max(size.Z, 0.001f)),
                HeightScale = MathF.Max(size.Y, 0.001f)
            },
            _ => new BoxColliderComponent
            {
                Size = new Vector3(
                    MathF.Max(size.X, 0.001f),
                    MathF.Max(size.Y, 0.001f),
                    MathF.Max(size.Z, 0.001f))
            }
        };
        collider.Center = center;
        return collider;
    }

    /// <summary>Computes descendant render bounds in the selected node's local space.</summary>
    /// <param name="node">Subtree root and target coordinate system.</param>
    /// <param name="minimum">Combined minimum, or negative half-unit fallback.</param>
    /// <param name="maximum">Combined maximum, or positive half-unit fallback.</param>
    /// <returns>True when at least one mesh supplied explicit bounds.</returns>
    public static bool TryGetCombinedLocalBounds(
        Node3D node,
        out Vector3 minimum,
        out Vector3 maximum)
    {
        ArgumentNullException.ThrowIfNull(node);
        minimum = new Vector3(float.PositiveInfinity);
        maximum = new Vector3(float.NegativeInfinity);
        if (!Matrix4x4.Invert(node.GetModelMatrix(), out var inverseTarget))
        {
            minimum = new Vector3(-0.5f);
            maximum = new Vector3(0.5f);
            return false;
        }
        var found = Accumulate(node, inverseTarget, ref minimum, ref maximum);
        if (found)
            return true;
        minimum = new Vector3(-0.5f);
        maximum = new Vector3(0.5f);
        return false;
    }

    /// <summary>Accumulates finite transformed mesh-bound corners recursively.</summary>
    /// <param name="node">Current subtree node.</param>
    /// <param name="inverseTarget">World-to-target transform.</param>
    /// <param name="minimum">Accumulated local minimum.</param>
    /// <param name="maximum">Accumulated local maximum.</param>
    /// <returns>True when this subtree supplied explicit mesh bounds.</returns>
    private static bool Accumulate(
        Node node,
        Matrix4x4 inverseTarget,
        ref Vector3 minimum,
        ref Vector3 maximum)
    {
        var found = false;
        if (node is MeshInstance3D { LocalBounds: { } bounds } mesh)
        {
            var toTarget = mesh.GetModelMatrix() * inverseTarget;
            for (var cornerIndex = 0; cornerIndex < 8; cornerIndex++)
            {
                var corner = new Vector3(
                    (cornerIndex & 1) == 0 ? bounds.Minimum.X : bounds.Maximum.X,
                    (cornerIndex & 2) == 0 ? bounds.Minimum.Y : bounds.Maximum.Y,
                    (cornerIndex & 4) == 0 ? bounds.Minimum.Z : bounds.Maximum.Z);
                var transformed = Vector3.Transform(corner, toTarget);
                if (!float.IsFinite(transformed.X) || !float.IsFinite(transformed.Y) ||
                    !float.IsFinite(transformed.Z))
                    continue;
                minimum = Vector3.Min(minimum, transformed);
                maximum = Vector3.Max(maximum, transformed);
                found = true;
            }
        }
        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
            found |= Accumulate(children[index], inverseTarget, ref minimum, ref maximum);
        return found;
    }
}
