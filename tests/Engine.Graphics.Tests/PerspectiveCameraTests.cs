using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>
/// Verifies perspective-camera orientation behavior.
/// </summary>
public class PerspectiveCameraTests
{
    /// <summary>
    /// Verifies that aiming at the origin aligns the forward vector from multiple positions.
    /// </summary>
    /// <param name="x">Camera X position.</param>
    /// <param name="y">Camera Y position.</param>
    /// <param name="z">Camera Z position.</param>
    [Theory]
    [InlineData(4f, 3f, 6f)]
    [InlineData(-3f, 2f, 8f)]
    public void LookAt_PointsForwardAtTarget(float x, float y, float z)
    {
        var camera = new PerspectiveCamera { Position = new Vector3(x, y, z) };

        camera.LookAt(Vector3.Zero);

        AssertVectorClose(Vector3.Normalize(-camera.Position), camera.GetForwardVector());
        Assert.Equal(0f, camera.Rotation.Z);
    }

    /// <summary>
    /// Verifies that aiming refreshes an already-cached view matrix.
    /// </summary>
    [Fact]
    public void LookAt_InvalidatesCachedViewMatrix()
    {
        var camera = new PerspectiveCamera { Position = new Vector3(0f, 0f, 6f) };
        var before = camera.GetViewMatrix();

        camera.LookAt(new Vector3(2f, 0f, 0f));

        Assert.NotEqual(before, camera.GetViewMatrix());
    }

    /// <summary>
    /// Verifies that aiming at the camera's own position preserves its orientation.
    /// </summary>
    [Fact]
    public void LookAt_SamePositionLeavesRotationUnchanged()
    {
        var camera = new PerspectiveCamera { Position = new Vector3(1f, 2f, 3f), Rotation = new Vector3(0.2f, 0.3f, 0.4f) };

        camera.LookAt(camera.Position);

        Assert.Equal(new Vector3(0.2f, 0.3f, 0.4f), camera.Rotation);
    }

    /// <summary>
    /// Verifies that non-finite targets preserve the current orientation.
    /// </summary>
    [Fact]
    public void LookAt_NonFiniteTargetLeavesRotationUnchanged()
    {
        var camera = new PerspectiveCamera { Rotation = new Vector3(0.2f, 0.3f, 0.4f) };

        camera.LookAt(new Vector3(float.NaN, 0f, 0f));

        Assert.Equal(new Vector3(0.2f, 0.3f, 0.4f), camera.Rotation);
    }

    /// <summary>
    /// Compares vectors within a numeric tolerance.
    /// </summary>
    /// <param name="expected">Expected vector.</param>
    /// <param name="actual">Actual vector.</param>
    private static void AssertVectorClose(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, 0.0001f);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, 0.0001f);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, 0.0001f);
    }
}
