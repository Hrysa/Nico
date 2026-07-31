using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Converts and composes rotations using the engine's row-vector Euler convention.
/// </summary>
internal static class GizmoTransformMath
{
    private const float SingularityThreshold = 0.99999f;

    /// <summary>
    /// Creates a rotation matrix in Rz * Ry * Rx order.
    /// </summary>
    /// <param name="euler">Euler angles in radians around X, Y, and Z.</param>
    /// <returns>The row-vector rotation matrix.</returns>
    internal static Matrix4x4 ToRotationMatrix(Vector3 euler)
    {
        return Matrix4x4.CreateRotationZ(euler.Z)
             * Matrix4x4.CreateRotationY(euler.Y)
             * Matrix4x4.CreateRotationX(euler.X);
    }

    /// <summary>
    /// Extracts canonical Euler angles from an Rz * Ry * Rx rotation matrix.
    /// </summary>
    /// <param name="rotation">A normalized row-vector rotation matrix.</param>
    /// <returns>Euler angles with Y in the range [-PI/2, PI/2].</returns>
    internal static Vector3 ToEuler(Matrix4x4 rotation)
    {
        var sinY = Math.Clamp(rotation.M31, -1f, 1f);
        var y = MathF.Asin(sinY);

        if (MathF.Abs(sinY) >= SingularityThreshold)
        {
            var xAtSingularity = MathF.Atan2(rotation.M23, rotation.M22);
            return new Vector3(xAtSingularity, y, 0f);
        }

        var x = MathF.Atan2(-rotation.M32, rotation.M33);
        var z = MathF.Atan2(-rotation.M21, rotation.M11);
        return new Vector3(x, y, z);
    }

    /// <summary>
    /// Applies an axis-angle delta around a world axis to an Euler orientation.
    /// </summary>
    /// <param name="originalEuler">Original Euler orientation.</param>
    /// <param name="worldAxis">World-space rotation axis.</param>
    /// <param name="radians">Signed angle delta in radians.</param>
    /// <returns>The canonical Euler representation of the rotated orientation.</returns>
    internal static Vector3 RotateWorld(Vector3 originalEuler, Vector3 worldAxis, float radians)
    {
        if (!IsFinite(originalEuler) || !IsFinite(worldAxis) || !float.IsFinite(radians))
            return originalEuler;

        var axisLengthSquared = worldAxis.LengthSquared();
        if (axisLengthSquared <= float.Epsilon)
            return originalEuler;

        var normalizedAxis = worldAxis / MathF.Sqrt(axisLengthSquared);
        var composed = ToRotationMatrix(originalEuler)
            * Matrix4x4.CreateFromAxisAngle(normalizedAxis, radians);
        return ToEuler(composed);
    }

    /// <summary>
    /// Determines whether every vector component is finite.
    /// </summary>
    /// <param name="value">Vector to validate.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
