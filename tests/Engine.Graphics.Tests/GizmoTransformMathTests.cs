using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>
/// Verifies the engine's Euler-angle and world-rotation convention.
/// </summary>
public class GizmoTransformMathTests
{
    /// <summary>
    /// Verifies that model matrices include all three named Euler rotations in the documented order.
    /// </summary>
    [Fact]
    public void GetModelMatrix_IncludesZRotationInDocumentedOrder()
    {
        var node = new Node3D
        {
            Position = new Vector3(3f, -2f, 5f),
            Rotation = new Vector3(0.3f, -0.2f, 0.4f),
            Scale = new Vector3(2f, 3f, 4f)
        };
        var expected = Matrix4x4.CreateScale(node.Scale)
            * Matrix4x4.CreateRotationZ(node.Rotation.Z)
            * Matrix4x4.CreateRotationY(node.Rotation.Y)
            * Matrix4x4.CreateRotationX(node.Rotation.X)
            * Matrix4x4.CreateTranslation(node.Position);

        AssertMatrixClose(expected, node.GetModelMatrix());
    }

    /// <summary>
    /// Verifies that nonsingular Euler angles survive matrix conversion.
    /// </summary>
    [Theory]
    [InlineData(0.3f, -0.2f, 0.4f)]
    [InlineData(-1.1f, 0.7f, -0.6f)]
    [InlineData(2.2f, -1.0f, 1.4f)]
    public void EulerConversion_RoundTripsNonsingularOrientations(float x, float y, float z)
    {
        var original = new Vector3(x, y, z);
        var matrix = GizmoTransformMath.ToRotationMatrix(original);
        var roundTrip = GizmoTransformMath.ToEuler(matrix);

        Assert.InRange(roundTrip.Y, -MathF.PI / 2f, MathF.PI / 2f);
        AssertMatrixClose(matrix, GizmoTransformMath.ToRotationMatrix(roundTrip));
    }

    /// <summary>
    /// Verifies the canonical singularity representation.
    /// </summary>
    [Fact]
    public void EulerConversion_UsesZeroZAtPositiveSingularity()
    {
        var matrix = GizmoTransformMath.ToRotationMatrix(new Vector3(0.35f, MathF.PI / 2f, -0.6f));

        var euler = GizmoTransformMath.ToEuler(matrix);

        Assert.Equal(0f, euler.Z, 5);
        AssertMatrixClose(matrix, GizmoTransformMath.ToRotationMatrix(euler), 0.0005f);
    }

    /// <summary>
    /// Verifies that a world-axis delta is post-multiplied under the row-vector convention.
    /// </summary>
    [Fact]
    public void RotateWorld_PostMultipliesWorldAxisDelta()
    {
        var original = new Vector3(0.3f, -0.2f, 0.4f);
        var actual = GizmoTransformMath.ToRotationMatrix(
            GizmoTransformMath.RotateWorld(original, Vector3.UnitY, 0.25f));
        var expected = GizmoTransformMath.ToRotationMatrix(original)
            * Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, 0.25f);

        AssertMatrixClose(expected, actual);
    }

    /// <summary>
    /// Compares all matrix elements within a numeric tolerance.
    /// </summary>
    /// <param name="expected">Expected matrix.</param>
    /// <param name="actual">Actual matrix.</param>
    /// <param name="tolerance">Maximum absolute element difference.</param>
    private static void AssertMatrixClose(Matrix4x4 expected, Matrix4x4 actual, float tolerance = 0.0001f)
    {
        var expectedValues = new[] { expected.M11, expected.M12, expected.M13, expected.M14, expected.M21, expected.M22, expected.M23, expected.M24, expected.M31, expected.M32, expected.M33, expected.M34, expected.M41, expected.M42, expected.M43, expected.M44 };
        var actualValues = new[] { actual.M11, actual.M12, actual.M13, actual.M14, actual.M21, actual.M22, actual.M23, actual.M24, actual.M31, actual.M32, actual.M33, actual.M34, actual.M41, actual.M42, actual.M43, actual.M44 };

        for (var index = 0; index < expectedValues.Length; index++)
            Assert.InRange(MathF.Abs(expectedValues[index] - actualValues[index]), 0f, tolerance);
    }
}
