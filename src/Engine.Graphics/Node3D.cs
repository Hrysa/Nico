using System.Numerics;
using Engine.Core;

namespace Engine.Graphics;

/// <summary>
/// 3D scene node with a world-space model matrix computed from
/// Position, quaternion Orientation, and Scale.
/// </summary>
public class Node3D : Node
{
    /// <summary>
    /// Computes the model matrix from the node's Position, Orientation, and Scale.
    /// Order: Scale → Rotate → Translate.
    /// </summary>
    /// <returns>The world-space model matrix.</returns>
    public Matrix4x4 GetModelMatrix()
    {
        var local = Matrix4x4.CreateScale(Scale)
             * Matrix4x4.CreateFromQuaternion(Orientation)
             * Matrix4x4.CreateTranslation(Position);

        return Parent is Node3D parent ? local * parent.GetModelMatrix() : local;
    }

    /// <summary>Gets this node's world-space position.</summary>
    /// <returns>The transformed origin in world space.</returns>
    public Vector3 GetWorldPosition()
    {
        return Vector3.Transform(Vector3.Zero, GetModelMatrix());
    }

    /// <summary>Gets this node's world-space Euler rotation.</summary>
    /// <returns>World rotation in the engine's Euler convention.</returns>
    public Vector3 GetWorldRotation()
    {
        if (!Matrix4x4.Decompose(GetModelMatrix(), out _, out var rotation, out _))
            return Rotation;
        return GizmoTransformMath.ToEuler(Matrix4x4.CreateFromQuaternion(rotation));
    }

    /// <summary>
    /// Sets world-space position and rotation while preserving the parent-relative representation.
    /// </summary>
    /// <param name="worldPosition">Desired world-space position.</param>
    /// <param name="worldRotation">Desired world-space Euler rotation.</param>
    public void SetWorldTransform(Vector3 worldPosition, Vector3 worldRotation)
    {
        if (Parent is not Node3D parent)
        {
            Position = worldPosition;
            Rotation = worldRotation;
            return;
        }

        var parentModel = parent.GetModelMatrix();
        if (Matrix4x4.Invert(parentModel, out var inverseParent))
            Position = Vector3.Transform(worldPosition, inverseParent);

        if (!Matrix4x4.Decompose(parentModel, out _, out var parentRotation, out _))
            return;
        var parentRotationMatrix = Matrix4x4.CreateFromQuaternion(parentRotation);
        if (!Matrix4x4.Invert(parentRotationMatrix, out var inverseParentRotation))
            return;
        var localRotation = GizmoTransformMath.ToRotationMatrix(worldRotation)
            * inverseParentRotation;
        if (Matrix4x4.Decompose(localRotation, out _, out var localOrientation, out _))
            Orientation = localOrientation;
    }
}
