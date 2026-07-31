using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Coordinates layout, picking, dragging, and overlay generation for one selected object.
/// </summary>
public sealed class EditorGizmo
{
    private GizmoLayoutResult _layout = GizmoLayoutResult.Empty;
    private GizmoDragSession? _dragSession;

    /// <summary>Gets the handle currently under the pointer or captured by a drag.</summary>
    public GizmoHandleKind HoveredHandle { get; private set; }

    /// <summary>Gets the handle captured by the current drag.</summary>
    public GizmoHandleKind ActiveHandle { get; private set; }

    /// <summary>Gets whether a transform drag is active.</summary>
    public bool IsDragging => _dragSession is not null;

    /// <summary>
    /// Rebuilds shared handle geometry for the current target and camera.
    /// </summary>
    /// <param name="target">Current target transform.</param>
    /// <param name="view">Scene view matrix.</param>
    /// <param name="projection">Scene projection matrix.</param>
    /// <param name="viewport">Scene viewport.</param>
    public void UpdateLayout(GizmoTransform target, Matrix4x4 view, Matrix4x4 projection, GizmoViewport viewport)
    {
        var updated = GizmoLayout.Create(target.Position, view, projection, viewport);
        if (!updated.IsValid)
        {
            CancelDrag();
            return;
        }

        _layout = updated;
    }

    /// <summary>
    /// Updates pointer hover unless a drag already owns the pointer.
    /// </summary>
    /// <param name="pointer">Pointer position in screen pixels.</param>
    /// <returns>True when a handle is hovered or actively dragged.</returns>
    public bool UpdateHover(Vector2 pointer)
    {
        if (IsDragging)
        {
            HoveredHandle = ActiveHandle;
            return true;
        }

        HoveredHandle = GizmoPicker.Pick(_layout, pointer);
        return HoveredHandle != GizmoHandleKind.None;
    }

    /// <summary>
    /// Begins dragging the currently hovered handle.
    /// </summary>
    /// <param name="pointer">Mouse-down pointer position.</param>
    /// <param name="target">Original target transform.</param>
    /// <returns>True when the pointer was consumed by a stable drag session.</returns>
    public bool BeginDrag(Vector2 pointer, GizmoTransform target)
    {
        if (IsDragging || HoveredHandle == GizmoHandleKind.None)
            return false;

        var picked = GizmoPicker.Pick(_layout, pointer);
        if (picked != HoveredHandle
            || !GizmoDragSession.TryStart(picked, pointer, target, _layout, out var session)
            || session is null)
            return false;

        _dragSession = session;
        ActiveHandle = picked;
        HoveredHandle = picked;
        return true;
    }

    /// <summary>
    /// Calculates a transform update for the active drag.
    /// </summary>
    /// <param name="pointer">Current pointer position.</param>
    /// <param name="transform">Calculated transform when successful.</param>
    /// <returns>True when a drag is active and produced a finite update.</returns>
    public bool TryUpdateDrag(Vector2 pointer, out GizmoTransform transform)
    {
        transform = default;
        return _dragSession is not null && _dragSession.TryUpdate(pointer, out transform);
    }

    /// <summary>
    /// Ends the active drag while keeping the selected object's layout visible.
    /// </summary>
    public void EndDrag()
    {
        _dragSession = null;
        ActiveHandle = GizmoHandleKind.None;
        HoveredHandle = GizmoHandleKind.None;
    }

    /// <summary>
    /// Cancels interaction and clears the selected object's layout.
    /// </summary>
    public void CancelDrag()
    {
        _dragSession = null;
        ActiveHandle = GizmoHandleKind.None;
        HoveredHandle = GizmoHandleKind.None;
        _layout = GizmoLayoutResult.Empty;
    }

    /// <summary>
    /// Builds the current layered screen-space overlay.
    /// </summary>
    /// <returns>Overlay vertices, or an empty array without a valid selection layout.</returns>
    public Vertex[] BuildOverlay()
    {
        return GizmoOverlayBuilder.Build(_layout, HoveredHandle, ActiveHandle);
    }
}
