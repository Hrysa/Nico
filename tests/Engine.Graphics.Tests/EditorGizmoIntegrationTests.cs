using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>
/// Verifies the selection and pointer call sequence used by the Editor.
/// </summary>
public class EditorGizmoIntegrationTests
{
    /// <summary>
    /// Verifies automatic display, consumed mouse-down, transform isolation, and mouse-up termination.
    /// </summary>
    [Fact]
    public void SelectedObject_ProducesAndCompletesTranslationGesture()
    {
        var (gizmo, layout) = EditorGizmoTests.CreateGizmo();
        var transform = new GizmoTransform(Vector3.Zero, new Vector3(0.2f, 0.3f, 0.4f));
        var pointer = EditorGizmoTests.HandlePoint(layout, GizmoHandleKind.TranslateX);
        var segment = layout.Handles.Single(handle => handle.Kind == GizmoHandleKind.TranslateX).Segments[0];
        var direction = Vector2.Normalize(segment.End - segment.Start);

        Assert.NotEmpty(gizmo.BuildOverlay());
        Assert.True(gizmo.UpdateHover(pointer));
        var consumed = gizmo.BeginDrag(pointer, transform);
        Assert.True(consumed);
        Assert.True(gizmo.TryUpdateDrag(pointer + direction * 20f, out var updated));
        Assert.NotEqual(transform.Position, updated.Position);
        Assert.Equal(transform.Rotation, updated.Rotation);

        gizmo.EndDrag();
        Assert.False(gizmo.IsDragging);
    }

    /// <summary>
    /// Verifies that losing selection cancels a gesture and hides the gizmo.
    /// </summary>
    [Fact]
    public void SelectionLoss_CancelsGestureAndHidesOverlay()
    {
        var (gizmo, layout) = EditorGizmoTests.CreateGizmo();
        var pointer = EditorGizmoTests.HandlePoint(layout, GizmoHandleKind.TranslateY);
        gizmo.UpdateHover(pointer);
        Assert.True(gizmo.BeginDrag(pointer, new GizmoTransform(Vector3.Zero, Vector3.Zero)));

        gizmo.CancelDrag();

        Assert.False(gizmo.TryUpdateDrag(pointer + Vector2.One, out _));
        Assert.Empty(gizmo.BuildOverlay());
    }
}
