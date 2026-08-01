using System.Numerics;
namespace Engine.Graphics;

/// <summary>
/// A 2D orthographic camera for editor UI rendering and 2D game views.
/// Supports position-based panning and size-based zooming.
/// </summary>
public class OrthographicCamera : Node3D, ICamera
{
    private float _size;
    private float _aspect;
    private float _near;
    private float _far;

    /// <summary>Gets or sets the orthographic view height (world units visible vertically). Default: 10.</summary>
    public float Size
    {
        get => _size;
        set => _size = value;
    }

    /// <summary>Gets or sets the viewport aspect ratio (width / height).</summary>
    public float Aspect
    {
        get => _aspect;
        set => _aspect = value;
    }

    /// <summary>Gets or sets the near clip plane. Default: -1.</summary>
    public float Near
    {
        get => _near;
        set => _near = value;
    }

    /// <summary>Gets or sets the far clip plane. Default: 1.</summary>
    public float Far
    {
        get => _far;
        set => _far = value;
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
        return Matrix4x4.CreateTranslation(-Position);
    }

    /// <inheritdoc/>
    public Matrix4x4 GetProjectionMatrix()
    {
        var projection = Matrix4x4.CreateOrthographic(_size * _aspect, _size, _near, _far);
        projection.M22 = -projection.M22;
        return projection;
    }

    /// <inheritdoc/>
    public PushConstants GetPushConstants(Matrix4x4 model)
    {
        return new PushConstants
        {
            Model = model,
            View = GetViewMatrix(),
            Projection = GetProjectionMatrix()
        };
    }

    /// <inheritdoc/>
    public void UpdateViewport(float width, float height)
    {
        if (height > 0f)
            Aspect = width / height;
    }

    /// <summary>
    /// Pans the camera by moving the view center in world space.
    /// </summary>
    /// <param name="deltaX">Horizontal pan amount.</param>
    /// <param name="deltaY">Vertical pan amount.</param>
    public void Pan(float deltaX, float deltaY)
    {
        Position += new Vector3(deltaX, -deltaY, 0f);
    }

    /// <summary>
    /// Zooms the camera by scaling the visible vertical size.
    /// </summary>
    /// <param name="delta">Zoom factor. Positive zooms in (smaller Size), negative zooms out.</param>
    public void Zoom(float delta)
    {
        const float ZoomSpeed = 0.1f;
        Size = Math.Clamp(Size * (1f - delta * ZoomSpeed), 0.01f, 100000f);
    }
}
