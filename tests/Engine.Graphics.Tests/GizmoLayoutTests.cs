using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>
/// Verifies constant-size, world-aligned gizmo layout generation.
/// </summary>
public class GizmoLayoutTests
{
    private static readonly GizmoViewport Viewport = new(100f, 50f, 800f, 600f);

    /// <summary>
    /// Verifies that translation geometry retains its requested pixel extent as camera distance changes.
    /// </summary>
    /// <param name="cameraZ">Camera distance from the world origin.</param>
    [Theory]
    [InlineData(5f)]
    [InlineData(10f)]
    public void Create_KeepsTargetPixelSizeAcrossCameraDistance(float cameraZ)
    {
        var result = GizmoLayout.Create(Vector3.Zero, CreateView(cameraZ), GizmoProjectionTests.CreateProjection(), Viewport);

        var x = result.Handles.Single(handle => handle.Kind == GizmoHandleKind.TranslateX);
        Assert.True(result.IsValid);
        Assert.InRange(x.ScreenExtent, 95f, 97f);
    }

    /// <summary>
    /// Verifies the complete six-handle layer order.
    /// </summary>
    [Fact]
    public void Create_OrdersRingsBehindTranslationHandles()
    {
        var result = GizmoLayout.Create(Vector3.Zero, CreateView(5f), GizmoProjectionTests.CreateProjection(), Viewport);

        Assert.Equal(
            [GizmoHandleKind.RotateX, GizmoHandleKind.RotateY, GizmoHandleKind.RotateZ, GizmoHandleKind.TranslateX, GizmoHandleKind.TranslateY, GizmoHandleKind.TranslateZ],
            result.Handles.Select(handle => handle.Kind));
        Assert.All(result.Handles.Take(3), handle => Assert.Equal(0, handle.Layer));
        Assert.All(result.Handles.Skip(3), handle => Assert.Equal(1, handle.Layer));
    }

    /// <summary>
    /// Verifies that translation axes follow projected world X and Y directions.
    /// </summary>
    [Fact]
    public void Create_TranslationAxesFollowWorldDirections()
    {
        var result = GizmoLayout.Create(Vector3.Zero, CreateView(5f), GizmoProjectionTests.CreateProjection(), Viewport);
        var x = result.Handles.Single(handle => handle.Kind == GizmoHandleKind.TranslateX).Segments[0];
        var y = result.Handles.Single(handle => handle.Kind == GizmoHandleKind.TranslateY).Segments[0];

        Assert.True(x.End.X > x.Start.X);
        Assert.InRange(x.End.Y - x.Start.Y, -0.001f, 0.001f);
        Assert.True(y.End.Y < y.Start.Y);
        Assert.InRange(y.End.X - y.Start.X, -0.001f, 0.001f);
    }

    /// <summary>
    /// Verifies that a target behind the camera yields an inert layout.
    /// </summary>
    [Fact]
    public void Create_RejectsTargetBehindCamera()
    {
        var result = GizmoLayout.Create(new Vector3(0f, 0f, 10f), CreateView(5f), GizmoProjectionTests.CreateProjection(), Viewport);

        Assert.False(result.IsValid);
        Assert.Empty(result.Handles);
    }

    /// <summary>
    /// Verifies that generated geometry is finite and clipped to the viewport.
    /// </summary>
    [Fact]
    public void Create_EmitsFiniteGeometryInsideViewport()
    {
        var result = GizmoLayout.Create(Vector3.Zero, CreateView(5f), GizmoProjectionTests.CreateProjection(), Viewport);

        var points = result.Handles.SelectMany(handle => handle.Segments.SelectMany(segment => new[] { segment.Start, segment.End }))
            .Concat(result.Handles.SelectMany(handle => handle.Triangles.SelectMany(triangle => new[] { triangle.A, triangle.B, triangle.C })));
        Assert.All(points, point =>
        {
            Assert.True(float.IsFinite(point.X) && float.IsFinite(point.Y));
            Assert.InRange(point.X, Viewport.X, Viewport.X + Viewport.Width);
            Assert.InRange(point.Y, Viewport.Y, Viewport.Y + Viewport.Height);
        });
    }

    /// <summary>
    /// Verifies that a camera-aligned translation axis cannot be picked.
    /// </summary>
    [Fact]
    public void Create_MarksCameraAlignedTranslationAxisNonInteractive()
    {
        var result = GizmoLayout.Create(Vector3.Zero, CreateView(5f), GizmoProjectionTests.CreateProjection(), Viewport);

        var z = result.Handles.Single(handle => handle.Kind == GizmoHandleKind.TranslateZ);
        Assert.False(z.Interactive);
    }

    /// <summary>
    /// Creates a view matrix looking from positive Z toward the origin.
    /// </summary>
    /// <param name="cameraZ">Positive camera Z position.</param>
    /// <returns>The view matrix.</returns>
    private static Matrix4x4 CreateView(float cameraZ)
    {
        return Matrix4x4.CreateLookAt(new Vector3(0f, 0f, cameraZ), Vector3.Zero, Vector3.UnitY);
    }
}
