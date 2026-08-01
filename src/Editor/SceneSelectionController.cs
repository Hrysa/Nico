using System.Numerics;
using Engine.Graphics;

namespace Editor;

/// <summary>
/// Owns scene selection and transform-gizmo pointer interaction.
/// </summary>
public sealed class SceneSelectionController
{
    private readonly IReadOnlyList<MeshInstance3D> _objects;
    private readonly PerspectiveCamera _camera;
    private readonly Func<GizmoViewport> _getViewport;
    private readonly EditorGizmo _gizmo = new();
    private bool _consumedPrimaryDown;

    /// <summary>Gets the currently selected scene object.</summary>
    public MeshInstance3D? SelectedObject { get; private set; }

    /// <summary>Occurs when the selected scene object changes.</summary>
    public event Action<MeshInstance3D?>? SelectionChanged;

    /// <summary>
    /// Creates a scene-selection controller.
    /// </summary>
    /// <param name="objects">Selectable scene objects.</param>
    /// <param name="camera">Scene viewport camera.</param>
    /// <param name="getViewport">Callback returning current viewport geometry.</param>
    public SceneSelectionController(
        IReadOnlyList<MeshInstance3D> objects,
        PerspectiveCamera camera,
        Func<GizmoViewport> getViewport)
    {
        _objects = objects;
        _camera = camera;
        _getViewport = getViewport;
    }

    /// <summary>Updates gizmo hover or an active drag.</summary>
    /// <param name="position">Pointer position.</param>
    public void MovePointer(Vector2 position)
    {
        if (SelectedObject is null)
            return;

        if (_gizmo.IsDragging && _gizmo.TryUpdateDrag(position, out var updated))
        {
            SelectedObject.Position = updated.Position;
            SelectedObject.Rotation = updated.Rotation;
        }
        else if (!_gizmo.IsDragging)
        {
            _gizmo.UpdateHover(position);
        }
    }

    /// <summary>Begins gizmo interaction or selects an object.</summary>
    /// <param name="position">Pointer position.</param>
    /// <param name="insideViewport">Whether the pointer is inside the scene viewport.</param>
    public void PrimaryDown(Vector2 position, bool insideViewport)
    {
        _consumedPrimaryDown = false;
        if (!insideViewport)
            return;

        if (SelectedObject is not null)
        {
            var transform = new GizmoTransform(SelectedObject.Position, SelectedObject.Rotation);
            if (_gizmo.BeginDrag(position, transform))
            {
                _consumedPrimaryDown = true;
                return;
            }
        }

        var viewport = _getViewport();
        var hit = MeshPicker.Pick(_objects, _camera,
            viewport.X, viewport.Y, viewport.Width, viewport.Height, position);
        if (ReferenceEquals(hit, SelectedObject))
            return;

        Select(hit);
    }

    /// <summary>Ends primary-button gizmo interaction.</summary>
    /// <returns>True when the preceding primary press was consumed by the gizmo.</returns>
    public bool PrimaryUp()
    {
        var consumed = _consumedPrimaryDown;
        _consumedPrimaryDown = false;
        if (_gizmo.IsDragging)
            _gizmo.EndDrag();
        return consumed;
    }

    /// <summary>Updates gizmo screen geometry for the current frame.</summary>
    /// <param name="pointerPosition">Current pointer position.</param>
    public void Update(Vector2 pointerPosition)
    {
        if (SelectedObject is null)
        {
            _gizmo.CancelDrag();
            return;
        }

        var viewport = _getViewport();
        _gizmo.UpdateLayout(
            new GizmoTransform(SelectedObject.Position, SelectedObject.Rotation),
            _camera.GetViewMatrix(), _camera.GetProjectionMatrix(), viewport);
        if (!_gizmo.IsDragging)
            _gizmo.UpdateHover(pointerPosition);
    }

    /// <summary>Builds the current selection overlay.</summary>
    /// <returns>Overlay geometry, or an empty array when nothing is selected.</returns>
    public Vertex[] BuildOverlay()
    {
        return SelectedObject is null ? [] : _gizmo.BuildOverlay();
    }

    /// <summary>Cancels active gizmo interaction.</summary>
    public void CancelInteraction()
    {
        _consumedPrimaryDown = false;
        _gizmo.CancelDrag();
    }

    /// <summary>Selects a scene object from an external editor surface.</summary>
    /// <param name="item">Object to select, or null to clear selection.</param>
    public void Select(MeshInstance3D? item)
    {
        if (ReferenceEquals(item, SelectedObject))
            return;
        _gizmo.CancelDrag();
        SelectedObject = item;
        SelectionChanged?.Invoke(item);
    }
}
