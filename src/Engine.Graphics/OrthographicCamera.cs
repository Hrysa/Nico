using System.Numerics;
using Engine.Core;

namespace Engine.Graphics;

/// <summary>
/// A 2D orthographic camera for editor UI rendering and 2D game views.
/// Currently a stub — all methods throw <see cref="NotImplementedException"/>.
/// </summary>
public class OrthographicCamera : Node, ICamera
{
    private float _size;
    private float _aspect;
    private float _near;
    private float _far;

    /// <summary>Gets or sets the orthographic view height (world units visible vertically). Default: 10.</summary>
    public float Size
    {
        get => _size;
        set { _size = value; /* TODO: mark projection dirty */ }
    }

    /// <summary>Gets or sets the viewport aspect ratio (width / height).</summary>
    public float Aspect
    {
        get => _aspect;
        set { _aspect = value; /* TODO: mark projection dirty */ }
    }

    /// <summary>Gets or sets the near clip plane. Default: -1.</summary>
    public float Near
    {
        get => _near;
        set { _near = value; /* TODO: mark projection dirty */ }
    }

    /// <summary>Gets or sets the far clip plane. Default: 1.</summary>
    public float Far
    {
        get => _far;
        set { _far = value; /* TODO: mark projection dirty */ }
    }

    /// <summary>
    /// Creates a new OrthographicCamera with sensible defaults.
    /// </summary>
    /// <param name="size">Vertical view size in world units.</param>
    /// <param name="aspect">Aspect ratio (width / height).</param>
    /// <param name="near">Near clip plane.</param>
    /// <param name="far">Far clip plane.</param>
    public OrthographicCamera(float size = 10f, float aspect = 16f / 9f, float near = -1f, float far = 1f)
    {
        _size = size;
        _aspect = aspect;
        _near = near;
        _far = far;
        Name = "OrthographicCamera";
    }

    /// <inheritdoc/>
    public Matrix4x4 GetViewMatrix()
    {
        // TODO: Implement orthographic view matrix
        // Should transform world coordinates so that (0,0) maps to the camera center
        // and Size controls the vertical extent
        throw new NotImplementedException("OrthographicCamera.GetViewMatrix");
    }

    /// <inheritdoc/>
    public Matrix4x4 GetProjectionMatrix()
    {
        // TODO: Implement orthographic projection matrix
        // Use Matrix4x4.CreateOrthographic(width, height, near, far)
        // where width = Size * Aspect, height = Size
        throw new NotImplementedException("OrthographicCamera.GetProjectionMatrix");
    }

    /// <inheritdoc/>
    public PushConstants GetPushConstants(Matrix4x4 model)
    {
        // TODO: Implement by composing model * GetViewMatrix() * GetProjectionMatrix()
        throw new NotImplementedException("OrthographicCamera.GetPushConstants");
    }

    /// <inheritdoc/>
    public void UpdateViewport(float width, float height)
    {
        // TODO: Update Aspect = width / height, mark projection dirty
        throw new NotImplementedException("OrthographicCamera.UpdateViewport");
    }

    /// <summary>
    /// TODO: Pan the camera by moving the view center in world space.
    /// </summary>
    /// <param name="deltaX">Horizontal pan amount.</param>
    /// <param name="deltaY">Vertical pan amount.</param>
    public void Pan(float deltaX, float deltaY)
    {
        // TODO: Move Position by (deltaX, -deltaY, 0), mark view dirty
        throw new NotImplementedException("OrthographicCamera.Pan");
    }

    /// <summary>
    /// TODO: Zoom the camera by scaling the view Size.
    /// </summary>
    /// <param name="delta">Zoom factor. Positive zooms in (smaller Size), negative zooms out.</param>
    public void Zoom(float delta)
    {
        // TODO: Scale Size by (1 - delta * zoomSpeed), clamp to min/max
        throw new NotImplementedException("OrthographicCamera.Zoom");
    }
}
