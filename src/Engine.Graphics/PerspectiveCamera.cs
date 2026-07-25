using System.Numerics;
using Engine.Core;

namespace Engine.Graphics;

/// <summary>
/// A 3D perspective camera that generates View and Projection matrices
/// for perspective-correct rendering. Extends Node for scene-graph
/// integration (Position = eye, Rotation = euler angles).
/// </summary>
public class PerspectiveCamera : Node, ICamera
{
    private float _fov;
    private float _aspect;
    private float _near;
    private float _far;
    private Matrix4x4 _viewMatrix;
    private Matrix4x4 _projectionMatrix;
    private bool _viewDirty = true;
    private bool _projectionDirty = true;

    /// <summary>Gets or sets the vertical field of view in radians. Default: PI/4 (45 degrees).</summary>
    public float Fov
    {
        get => _fov;
        set { _fov = value; _projectionDirty = true; }
    }

    /// <summary>Gets or sets the viewport aspect ratio (width / height).</summary>
    public float Aspect
    {
        get => _aspect;
        set { _aspect = value; _projectionDirty = true; }
    }

    /// <summary>Gets or sets the near clip plane distance. Default: 0.1.</summary>
    public float Near
    {
        get => _near;
        set { _near = value; _projectionDirty = true; }
    }

    /// <summary>Gets or sets the far clip plane distance. Default: 1000.0.</summary>
    public float Far
    {
        get => _far;
        set { _far = value; _projectionDirty = true; }
    }

    /// <summary>
    /// Creates a new PerspectiveCamera with sensible defaults.
    /// Position is at (0, 2, 5) looking toward the origin.
    /// </summary>
    /// <param name="fov">Vertical field of view in radians.</param>
    /// <param name="aspect">Aspect ratio (width / height).</param>
    /// <param name="near">Near clip plane.</param>
    /// <param name="far">Far clip plane.</param>
    public PerspectiveCamera(float fov = MathF.PI / 4f, float aspect = 16f / 9f, float near = 0.1f, float far = 1000f)
    {
        _fov = fov;
        _aspect = aspect;
        _near = near;
        _far = far;
        Position = new Vector3(0, 2, 5);
        Name = "PerspectiveCamera";
    }

    /// <inheritdoc/>
    public Matrix4x4 GetViewMatrix()
    {
        if (_viewDirty)
        {
            _viewMatrix = ComputeViewMatrix();
            _viewDirty = false;
        }
        return _viewMatrix;
    }

    /// <inheritdoc/>
    public Matrix4x4 GetProjectionMatrix()
    {
        if (_projectionDirty)
        {
            // System.Numerics CreatePerspectiveFieldOfView uses OpenGL conventions
            // (Y-up, Z: -1→1). Vulkan needs Y-down, Z: 0→1.
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(_fov, _aspect, _near, _far);

            // Convert OpenGL projection to Vulkan convention:
            // 1. Flip Y for Vulkan's Y-down screen coordinates
            // 2. Remap Z from [-1,1] to [0,1] for Vulkan depth range
            proj.M22 = -proj.M22;                           // Y-flip
            proj.M33 = _far / (_far - _near);               // Z scale
            proj.M34 = _near * _far / (_far - _near);       // Z translation
            proj.M43 = 1.0f;                                // w = z_eye

            _projectionMatrix = proj;
            _projectionDirty = false;
        }
        return _projectionMatrix;
    }

    /// <inheritdoc/>
    public PushConstants GetPushConstants(Matrix4x4 model)
    {
        // Transpose matrices because GLSL mat4 is column-major while
        // System.Numerics Matrix4x4 is row-major. GLSL reads push constant
        // bytes as columns, effectively transposing the data. By pushing
        // the transpose, GLSL reconstructs the intended matrix.
        return new PushConstants
        {
            Model = Matrix4x4.Transpose(model),
            View = Matrix4x4.Transpose(GetViewMatrix()),
            Projection = Matrix4x4.Transpose(GetProjectionMatrix())
        };
    }

    /// <inheritdoc/>
    public void UpdateViewport(float width, float height)
    {
        if (height > 0)
            Aspect = width / height;
    }

    /// <summary>Moves the camera forward along its local -Z axis.</summary>
    /// <param name="distance">Distance to move in world units.</param>
    public void MoveForward(float distance)
    {
        Position += GetForwardVector() * distance;
        _viewDirty = true;
    }

    /// <summary>Moves the camera right along its local +X axis.</summary>
    /// <param name="distance">Distance to move in world units.</param>
    public void MoveRight(float distance)
    {
        Position += GetRightVector() * distance;
        _viewDirty = true;
    }

    /// <summary>Moves the camera up along its local +Y axis.</summary>
    /// <param name="distance">Distance to move in world units.</param>
    public void MoveUp(float distance)
    {
        Position += GetUpVector() * distance;
        _viewDirty = true;
    }

    /// <summary>
    /// Rotates the camera by the given yaw and pitch angles (in radians).
    /// Pitch is clamped to prevent gimbal lock at +/- 89 degrees.
    /// </summary>
    /// <param name="yaw">Yaw rotation (around Y axis, left/right).</param>
    /// <param name="pitch">Pitch rotation (around X axis, up/down).</param>
    public void Rotate(float yaw, float pitch)
    {
        Rotation = new Vector3(
            Math.Clamp(Rotation.X + pitch, -MathF.PI / 2f + 0.01f, MathF.PI / 2f - 0.01f),
            Rotation.Y + yaw,
            Rotation.Z);
        _viewDirty = true;
    }

    /// <summary>Gets the camera's forward direction vector (local -Z in world space).</summary>
    /// <returns>The normalized forward vector.</returns>
    public Vector3 GetForwardVector()
    {
        var yaw = Rotation.Y;
        var pitch = Rotation.X;
        return new Vector3(
            MathF.Sin(yaw) * MathF.Cos(pitch),
            MathF.Sin(pitch),
            -MathF.Cos(yaw) * MathF.Cos(pitch));
    }

    /// <summary>Gets the camera's right direction vector (local +X in world space).</summary>
    /// <returns>The normalized right vector.</returns>
    public Vector3 GetRightVector()
    {
        return Vector3.Normalize(Vector3.Cross(GetForwardVector(), Vector3.UnitY));
    }

    /// <summary>Gets the camera's up direction vector (local +Y in world space).</summary>
    /// <returns>The normalized up vector.</returns>
    public Vector3 GetUpVector()
    {
        return Vector3.Normalize(Vector3.Cross(GetRightVector(), GetForwardVector()));
    }

    private Matrix4x4 ComputeViewMatrix()
    {
        var eye = Position;
        var target = eye + GetForwardVector();
        var up = GetUpVector();

        return Matrix4x4.CreateLookAt(eye, target, up);
    }
}
