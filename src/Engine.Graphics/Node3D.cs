using System.Numerics;
using Engine.Core;

namespace Engine.Graphics;

/// <summary>
/// 3D scene node with a world-space model matrix computed from
/// Position, Rotation (euler), and Scale.
/// </summary>
public class Node3D : Node
{
    /// <summary>
    /// Computes the model matrix from the node's Position, Rotation, and Scale.
/// Order: Scale → RotateZ → RotateY → RotateX → Translate.
    /// </summary>
    /// <returns>The world-space model matrix.</returns>
    public Matrix4x4 GetModelMatrix()
    {
        return Matrix4x4.CreateScale(Scale)
             * GizmoTransformMath.ToRotationMatrix(Rotation)
             * Matrix4x4.CreateTranslation(Position);
    }
}
