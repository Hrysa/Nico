using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>
/// Verifies validated world and screen projection behavior.
/// </summary>
public class GizmoProjectionTests
{
    private static readonly GizmoViewport Viewport = new(100f, 50f, 800f, 600f);

    /// <summary>
    /// Verifies that the viewed world origin maps to the offset viewport center.
    /// </summary>
    [Fact]
    public void TryWorldToScreen_ProjectsOriginToViewportCenter()
    {
        var view = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 5f), Vector3.Zero, Vector3.UnitY);
        var projection = CreateProjection();

        var success = GizmoProjection.TryWorldToScreen(Vector3.Zero, view, projection, Viewport, out var screen);

        Assert.True(success);
        Assert.InRange(screen.X, 499.999f, 500.001f);
        Assert.InRange(screen.Y, 349.999f, 350.001f);
    }

    /// <summary>
    /// Verifies that a ray through viewport center points toward the viewed origin.
    /// </summary>
    [Fact]
    public void TryScreenToRay_CenterRayPointsDownNegativeZ()
    {
        var view = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 5f), Vector3.Zero, Vector3.UnitY);
        var projection = CreateProjection();

        var success = GizmoProjection.TryScreenToRay(new Vector2(500f, 350f), view, projection, Viewport, out var origin, out var direction);

        Assert.True(success);
        Assert.True(float.IsFinite(origin.X));
        Assert.InRange(direction.X, -0.0001f, 0.0001f);
        Assert.InRange(direction.Y, -0.0001f, 0.0001f);
        Assert.InRange(direction.Z, -1.0001f, -0.9999f);
    }

    /// <summary>
    /// Verifies that zero-sized viewports are rejected without non-finite output.
    /// </summary>
    [Fact]
    public void Projection_RejectsZeroSizedViewport()
    {
        var invalid = new GizmoViewport(0f, 0f, 0f, 600f);

        Assert.False(GizmoProjection.TryWorldToScreen(Vector3.Zero, Matrix4x4.Identity, Matrix4x4.Identity, invalid, out _));
        Assert.False(GizmoProjection.TryScreenToRay(Vector2.Zero, Matrix4x4.Identity, Matrix4x4.Identity, invalid, out _, out _));
    }

    /// <summary>
    /// Verifies that a singular view-projection matrix cannot produce a ray.
    /// </summary>
    [Fact]
    public void TryScreenToRay_RejectsNonInvertibleMatrix()
    {
        Assert.False(GizmoProjection.TryScreenToRay(new Vector2(500f, 350f), new Matrix4x4(), Matrix4x4.Identity, Viewport, out _, out _));
    }

    /// <summary>
    /// Creates the Vulkan-corrected perspective matrix used by projection tests.
    /// </summary>
    /// <returns>A 45-degree perspective projection.</returns>
    internal static Matrix4x4 CreateProjection()
    {
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 4f / 3f, 0.1f, 100f);
        projection.M22 = -projection.M22;
        return projection;
    }
}
