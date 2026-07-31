using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>
/// Verifies immutable world-space translation and rotation drag calculations.
/// </summary>
public class GizmoDragSessionTests
{
    private static readonly GizmoViewport Viewport = new(0f, 0f, 800f, 600f);

    /// <summary>
    /// Verifies translation along each foreshortened world axis.
    /// </summary>
    /// <param name="handle">Translation handle.</param>
    [Theory]
    [InlineData(GizmoHandleKind.TranslateX)]
    [InlineData(GizmoHandleKind.TranslateY)]
    [InlineData(GizmoHandleKind.TranslateZ)]
    public void Translation_UsesProjectedWorldAxisRatio(GizmoHandleKind handle)
    {
        var layout = CreateLayout(new Vector3(5f, 4f, 6f));
        var geometry = layout.Handles.Single(candidate => candidate.Kind == handle);
        var start = geometry.Segments[0].Start;
        var screenDirection = Vector2.Normalize(geometry.Segments[0].End - geometry.Segments[0].Start);
        var original = new GizmoTransform(Vector3.Zero, new Vector3(0.2f, -0.3f, 0.4f));
        var pixels = 24f;

        Assert.True(GizmoDragSession.TryStart(handle, start, original, layout, out var session));
        Assert.NotNull(session);
        Assert.True(session.TryUpdate(start + screenDirection * pixels, out var result));

        var expectedDistance = pixels * GizmoLayout.AxisPixels * layout.WorldUnitsPerPixel / geometry.ScreenExtent;
        var expectedAxis = AxisFor(handle);
        AssertVectorClose(original.Position + expectedAxis * expectedDistance, result.Position);
        Assert.Equal(original.Rotation, result.Rotation);
    }

    /// <summary>
    /// Verifies that updates are based on mouse-down state instead of accumulated results.
    /// </summary>
    [Fact]
    public void Translation_RepeatedPointerProducesIdenticalResult()
    {
        var layout = CreateLayout(new Vector3(5f, 4f, 6f));
        var geometry = layout.Handles.Single(candidate => candidate.Kind == GizmoHandleKind.TranslateX);
        var start = geometry.Segments[0].Start;
        var pointer = start + Vector2.Normalize(geometry.Segments[0].End - start) * 30f;
        Assert.True(GizmoDragSession.TryStart(GizmoHandleKind.TranslateX, start, new GizmoTransform(Vector3.One, Vector3.Zero), layout, out var session));

        Assert.True(session!.TryUpdate(pointer, out var first));
        Assert.True(session.TryUpdate(pointer, out var second));

        Assert.Equal(first, second);
        Assert.True(session.TryUpdate(start, out var restored));
        AssertVectorClose(Vector3.One, restored.Position);
    }

    /// <summary>
    /// Verifies quarter-turn plane rotation around all world axes.
    /// </summary>
    /// <param name="handle">Rotation handle.</param>
    [Theory]
    [InlineData(GizmoHandleKind.RotateX)]
    [InlineData(GizmoHandleKind.RotateY)]
    [InlineData(GizmoHandleKind.RotateZ)]
    public void Rotation_PlaneDragProducesPositiveQuarterTurn(GizmoHandleKind handle)
    {
        var layout = CreateLayout(new Vector3(5f, 4f, 6f));
        var geometry = layout.Handles.Single(candidate => candidate.Kind == handle);
        var start = geometry.Segments[0].Start;
        var quarter = geometry.Segments[16].Start;
        Assert.True(GizmoDragSession.TryStart(handle, start, new GizmoTransform(Vector3.Zero, Vector3.Zero), layout, out var session));

        Assert.True(session!.TryUpdate(quarter, out var result));

        var expected = Matrix4x4.CreateFromAxisAngle(AxisFor(handle), MathF.PI / 2f);
        AssertMatrixClose(expected, GizmoTransformMath.ToRotationMatrix(result.Rotation), 0.002f);
        Assert.Equal(Vector3.Zero, result.Position);
    }

    /// <summary>
    /// Verifies world-axis composition for an already-rotated target.
    /// </summary>
    [Fact]
    public void Rotation_ComposesAfterOriginalOrientation()
    {
        var layout = CreateLayout(new Vector3(5f, 4f, 6f));
        var geometry = layout.Handles.Single(candidate => candidate.Kind == GizmoHandleKind.RotateZ);
        var original = new GizmoTransform(new Vector3(2f, 3f, 4f), new Vector3(0.3f, -0.2f, 0.4f));
        Assert.True(GizmoDragSession.TryStart(GizmoHandleKind.RotateZ, geometry.Segments[0].Start, original, layout, out var session));

        Assert.True(session!.TryUpdate(geometry.Segments[16].Start, out var result));

        var expected = GizmoTransformMath.ToRotationMatrix(original.Rotation)
            * Matrix4x4.CreateRotationZ(MathF.PI / 2f);
        AssertMatrixClose(expected, GizmoTransformMath.ToRotationMatrix(result.Rotation), 0.002f);
        Assert.Equal(original.Position, result.Position);
    }

    /// <summary>
    /// Verifies that an edge-on rotation uses one captured tangent strategy for the full session.
    /// </summary>
    [Fact]
    public void Rotation_EdgeOnFallbackUsesCapturedTangent()
    {
        var layout = CreateLayout(new Vector3(0f, 0f, 5f));
        var geometry = layout.Handles.Single(candidate => candidate.Kind == GizmoHandleKind.RotateY);
        var segment = geometry.Segments[0];
        var tangent = Vector2.Normalize(segment.End - segment.Start);
        Assert.True(GizmoDragSession.TryStart(GizmoHandleKind.RotateY, segment.Start, new GizmoTransform(Vector3.Zero, Vector3.Zero), layout, out var session));

        Assert.True(session!.TryUpdate(segment.Start + tangent * (MathF.PI * GizmoLayout.RingPixels / 2f), out var result));

        AssertMatrixClose(Matrix4x4.CreateRotationY(MathF.PI / 2f), GizmoTransformMath.ToRotationMatrix(result.Rotation), 0.002f);
    }

    /// <summary>
    /// Verifies that invalid pointer data cannot corrupt a transform.
    /// </summary>
    [Fact]
    public void TryUpdate_RejectsNonFinitePointer()
    {
        var layout = CreateLayout(new Vector3(5f, 4f, 6f));
        var geometry = layout.Handles.Single(candidate => candidate.Kind == GizmoHandleKind.TranslateX);
        Assert.True(GizmoDragSession.TryStart(GizmoHandleKind.TranslateX, geometry.Segments[0].Start, new GizmoTransform(Vector3.Zero, Vector3.Zero), layout, out var session));

        Assert.False(session!.TryUpdate(new Vector2(float.NaN, 10f), out _));
    }

    /// <summary>
    /// Creates a valid layout looking at the world origin.
    /// </summary>
    /// <param name="camera">Camera world position.</param>
    /// <returns>The generated layout.</returns>
    private static GizmoLayoutResult CreateLayout(Vector3 camera)
    {
        var view = Matrix4x4.CreateLookAt(camera, Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 4f / 3f, 0.1f, 100f);
        projection.M22 = -projection.M22;
        return GizmoLayout.Create(Vector3.Zero, view, projection, Viewport);
    }

    /// <summary>
    /// Resolves the world axis represented by a handle.
    /// </summary>
    /// <param name="handle">Handle identity.</param>
    /// <returns>The matching world unit axis.</returns>
    private static Vector3 AxisFor(GizmoHandleKind handle)
    {
        return handle switch
        {
            GizmoHandleKind.TranslateX or GizmoHandleKind.RotateX => Vector3.UnitX,
            GizmoHandleKind.TranslateY or GizmoHandleKind.RotateY => Vector3.UnitY,
            GizmoHandleKind.TranslateZ or GizmoHandleKind.RotateZ => Vector3.UnitZ,
            _ => throw new ArgumentOutOfRangeException(nameof(handle))
        };
    }

    /// <summary>
    /// Compares vectors within a numeric tolerance.
    /// </summary>
    /// <param name="expected">Expected vector.</param>
    /// <param name="actual">Actual vector.</param>
    /// <param name="tolerance">Maximum absolute component difference.</param>
    private static void AssertVectorClose(Vector3 expected, Vector3 actual, float tolerance = 0.0001f)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, tolerance);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, tolerance);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, tolerance);
    }

    /// <summary>
    /// Compares matrices within a numeric tolerance.
    /// </summary>
    /// <param name="expected">Expected matrix.</param>
    /// <param name="actual">Actual matrix.</param>
    /// <param name="tolerance">Maximum absolute element difference.</param>
    private static void AssertMatrixClose(Matrix4x4 expected, Matrix4x4 actual, float tolerance)
    {
        var expectedValues = new[] { expected.M11, expected.M12, expected.M13, expected.M21, expected.M22, expected.M23, expected.M31, expected.M32, expected.M33 };
        var actualValues = new[] { actual.M11, actual.M12, actual.M13, actual.M21, actual.M22, actual.M23, actual.M31, actual.M32, actual.M33 };
        for (var index = 0; index < expectedValues.Length; index++)
            Assert.InRange(MathF.Abs(expectedValues[index] - actualValues[index]), 0f, tolerance);
    }
}
