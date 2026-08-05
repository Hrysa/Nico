using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>
/// Verifies the public gizmo facade's interaction state.
/// </summary>
public class EditorGizmoTests
{
    private static readonly GizmoViewport Viewport = new(0f, 0f, 800f, 600f);

    /// <summary>
    /// Verifies that hover and mouse-down resolve the same shared handle.
    /// </summary>
    [Fact]
    public void BeginDrag_UsesExactlyTheHoveredHandle()
    {
        var (gizmo, layout) = CreateGizmo();
        var pointer = HandlePoint(layout, GizmoHandleKind.TranslateX);

        Assert.True(gizmo.UpdateHover(pointer));
        Assert.Equal(GizmoHandleKind.TranslateX, gizmo.HoveredHandle);
        Assert.True(gizmo.BeginDrag(pointer, new GizmoTransform(Vector3.Zero, Vector3.Zero)));
        Assert.Equal(gizmo.HoveredHandle, gizmo.ActiveHandle);
    }

    /// <summary>
    /// Verifies that pointer movement cannot steal an active drag.
    /// </summary>
    [Fact]
    public void UpdateHover_PreservesActiveHandleOutsideGeometry()
    {
        var (gizmo, layout) = CreateGizmo();
        var pointer = HandlePoint(layout, GizmoHandleKind.TranslateX);
        gizmo.UpdateHover(pointer);
        Assert.True(gizmo.BeginDrag(pointer, new GizmoTransform(Vector3.Zero, Vector3.Zero)));

        Assert.True(gizmo.UpdateHover(new Vector2(-100f, -100f)));
        Assert.Equal(GizmoHandleKind.TranslateX, gizmo.ActiveHandle);
        Assert.Equal(GizmoHandleKind.TranslateX, gizmo.HoveredHandle);
    }

    /// <summary>
    /// Verifies that mouse-up ends dragging without discarding the visible layout.
    /// </summary>
    [Fact]
    public void EndDrag_ClearsActiveStateAndKeepsOverlay()
    {
        var (gizmo, layout) = CreateGizmo();
        var pointer = HandlePoint(layout, GizmoHandleKind.TranslateX);
        gizmo.UpdateHover(pointer);
        gizmo.BeginDrag(pointer, new GizmoTransform(Vector3.Zero, Vector3.Zero));

        gizmo.EndDrag();

        Assert.False(gizmo.IsDragging);
        Assert.Equal(GizmoHandleKind.None, gizmo.ActiveHandle);
        Assert.NotEmpty(gizmo.BuildOverlay());
    }

    /// <summary>
    /// Verifies that selection cancellation clears interaction and display state.
    /// </summary>
    [Fact]
    public void CancelDrag_ClearsSessionHoverAndOverlay()
    {
        var (gizmo, layout) = CreateGizmo();
        var pointer = HandlePoint(layout, GizmoHandleKind.TranslateX);
        gizmo.UpdateHover(pointer);
        gizmo.BeginDrag(pointer, new GizmoTransform(Vector3.Zero, Vector3.Zero));

        gizmo.CancelDrag();

        Assert.False(gizmo.IsDragging);
        Assert.Equal(GizmoHandleKind.None, gizmo.HoveredHandle);
        Assert.Equal(GizmoHandleKind.None, gizmo.ActiveHandle);
        Assert.Empty(gizmo.BuildOverlay());
    }

    /// <summary>
    /// Verifies that invalid camera data cancels an existing interaction.
    /// </summary>
    [Fact]
    public void UpdateLayout_InvalidCameraCancelsSession()
    {
        var (gizmo, layout) = CreateGizmo();
        var pointer = HandlePoint(layout, GizmoHandleKind.TranslateX);
        gizmo.UpdateHover(pointer);
        gizmo.BeginDrag(pointer, new GizmoTransform(Vector3.Zero, Vector3.Zero));

        gizmo.UpdateLayout(new GizmoTransform(Vector3.Zero, Vector3.Zero), new Matrix4x4(), Matrix4x4.Identity, Viewport);

        Assert.False(gizmo.IsDragging);
        Assert.Empty(gizmo.BuildOverlay());
    }

    /// <summary>Reuses retained overlay geometry while layout and interaction stay unchanged.</summary>
    [Fact]
    public void BuildOverlay_UnchangedSelection_ReusesVertexArray()
    {
        var (gizmo, _) = CreateGizmo();

        var first = gizmo.BuildOverlay();
        var second = gizmo.BuildOverlay();

        Assert.Same(first, second);
    }

    /// <summary>
    /// Creates a facade and the equivalent layout used to locate handles.
    /// </summary>
    /// <returns>The initialized facade and reference layout.</returns>
    internal static (EditorGizmo Gizmo, GizmoLayoutResult Layout) CreateGizmo()
    {
        var view = Matrix4x4.CreateLookAt(new Vector3(5f, 4f, 6f), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 4f / 3f, 0.1f, 100f);
        projection.M22 = -projection.M22;
        var transform = new GizmoTransform(Vector3.Zero, Vector3.Zero);
        var layout = GizmoLayout.Create(transform.Position, view, projection, Viewport);
        var gizmo = new EditorGizmo();
        gizmo.UpdateLayout(transform, view, projection, Viewport);
        return (gizmo, layout);
    }

    /// <summary>
    /// Finds a stable point on a handle's first segment away from the shared origin.
    /// </summary>
    /// <param name="layout">Reference layout.</param>
    /// <param name="kind">Desired handle.</param>
    /// <returns>A point on the handle.</returns>
    internal static Vector2 HandlePoint(GizmoLayoutResult layout, GizmoHandleKind kind)
    {
        var segment = layout.Handles.Single(handle => handle.Kind == kind).Segments[0];
        return Vector2.Lerp(segment.Start, segment.End, 0.65f);
    }
}
