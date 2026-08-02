using System.Numerics;
namespace Engine.Graphics;

/// <summary>
/// A 3D perspective camera that generates View and Projection matrices
/// for perspective-correct rendering. Extends Node for scene-graph
/// integration (Position = eye, Rotation = euler angles).
/// </summary>
public class PerspectiveCamera : Node3D, ICamera
{
    private float _fov;
    private float _aspect;
    private float _near;
    private float _far;
    private Matrix4x4 _viewMatrix;
    private Matrix4x4 _cachedWorldTransform;
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
        var worldTransform = GetModelMatrix();
        if (_viewDirty || worldTransform != _cachedWorldTransform)
        {
            _viewMatrix = ComputeViewMatrix(worldTransform);
            _cachedWorldTransform = worldTransform;
            _viewDirty = false;
        }
        return _viewMatrix;
    }

    /// <inheritdoc/>
    public Matrix4x4 GetProjectionMatrix()
    {
        if (_projectionDirty)
        {
            // System.Numerics CreatePerspectiveFieldOfView produces a row-major
            // matrix. The Slang shaders preserve the existing column-major
            // SPIR-V storage contract, which reads the raw bytes as the correct
            // column-vector equivalent. Only the Vulkan Y-flip is needed.
            var proj = Matrix4x4.CreatePerspectiveFieldOfView(_fov, _aspect, _near, _far);
            proj.M22 = -proj.M22;  // Y-flip for Vulkan

            _projectionMatrix = proj;
            _projectionDirty = false;
        }
        return _projectionMatrix;
    }

    /// <inheritdoc/>
    public PushConstants GetPushConstants(Matrix4x4 model)
    {
        // System.Numerics Matrix4x4 is row-major. When pushed as raw bytes
        // via vkCmdPushConstants, the shader's column-major SPIR-V storage reads
        // the transpose, which is the correct column-vector equivalent.
        // No explicit transpose needed (consistent with game viewport usage).
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
        if (height > 0)
            Aspect = width / height;
    }

    /// <summary>Moves the camera forward along its local -Z axis.</summary>
    /// <param name="distance">Distance to move in world units.</param>
    public void MoveForward(float distance)
    {
        MoveInWorldDirection(GetForwardVector(), distance);
        _viewDirty = true;
    }

    /// <summary>Moves the camera right along its local +X axis.</summary>
    /// <param name="distance">Distance to move in world units.</param>
    public void MoveRight(float distance)
    {
        MoveInWorldDirection(GetRightVector(), distance);
        _viewDirty = true;
    }

    /// <summary>Moves the camera up along its local +Y axis.</summary>
    /// <param name="distance">Distance to move in world units.</param>
    public void MoveUp(float distance)
    {
        MoveInWorldDirection(GetUpVector(), distance);
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

    /// <summary>
    /// Aims the camera at a world-space target while keeping roll at zero.
    /// </summary>
    /// <param name="target">World-space point to face.</param>
    public void LookAt(Vector3 target)
    {
        var direction = target - Position;
        var lengthSquared = direction.LengthSquared();
        if (!IsFinite(direction) || !float.IsFinite(lengthSquared) || lengthSquared <= float.Epsilon)
            return;

        direction /= MathF.Sqrt(lengthSquared);
        Rotation = new Vector3(
            MathF.Asin(Math.Clamp(direction.Y, -1f, 1f)),
            MathF.Atan2(direction.X, -direction.Z),
            0f);
        _viewDirty = true;
    }

    /// <summary>Gets the camera's forward direction vector (local -Z in world space).</summary>
    /// <returns>The normalized forward vector.</returns>
    public Vector3 GetForwardVector()
    {
        return TransformDirectionByParent(GetLocalForwardVector());
    }

    /// <summary>Gets the camera's local forward direction before parent transforms.</summary>
    /// <returns>The normalized local forward vector.</returns>
    private Vector3 GetLocalForwardVector()
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
        return TransformDirectionByParent(GetLocalRightVector());
    }

    /// <summary>Gets the camera's up direction vector (local +Y in world space).</summary>
    /// <returns>The normalized up vector.</returns>
    public Vector3 GetUpVector()
    {
        return TransformDirectionByParent(GetLocalUpVector());
    }

    /// <summary>Gets the camera's local right direction before parent transforms.</summary>
    /// <returns>The normalized local right vector.</returns>
    private Vector3 GetLocalRightVector()
    {
        return Vector3.Normalize(Vector3.Cross(GetLocalForwardVector(), Vector3.UnitY));
    }

    /// <summary>Gets the camera's local up direction before parent transforms.</summary>
    /// <returns>The normalized local up vector.</returns>
    private Vector3 GetLocalUpVector()
    {
        return Vector3.Normalize(Vector3.Cross(GetLocalRightVector(), GetLocalForwardVector()));
    }

    /// <summary>Computes a view matrix using the camera's established forward/up convention.</summary>
    /// <param name="worldTransform">Camera transform including its scene parents.</param>
    /// <returns>The inverse world camera transform.</returns>
    private Matrix4x4 ComputeViewMatrix(Matrix4x4 worldTransform)
    {
        var eye = Vector3.Transform(Vector3.Zero, worldTransform);
        var forward = GetForwardVector();
        var up = GetUpVector();
        return Matrix4x4.CreateLookAt(eye, eye + forward, up);
    }

    /// <summary>Applies ancestor rotation to one local camera direction.</summary>
    /// <param name="direction">Direction expressed relative to this camera's parent.</param>
    /// <returns>The normalized world-space direction.</returns>
    private Vector3 TransformDirectionByParent(Vector3 direction)
    {
        if (Parent is not Node3D parent
            || !Matrix4x4.Decompose(parent.GetModelMatrix(), out _, out var rotation, out _))
            return Vector3.Normalize(direction);
        return Vector3.Normalize(Vector3.Transform(direction, rotation));
    }

    /// <summary>Moves in world space while storing a parent-relative position.</summary>
    /// <param name="worldDirection">Normalized world-space movement direction.</param>
    /// <param name="distance">Signed movement distance.</param>
    private void MoveInWorldDirection(Vector3 worldDirection, float distance)
    {
        var localDirection = worldDirection;
        if (Parent is Node3D parent
            && Matrix4x4.Decompose(parent.GetModelMatrix(), out _, out var rotation, out _))
            localDirection = Vector3.Transform(worldDirection, Quaternion.Inverse(rotation));
        Position += Vector3.Normalize(localDirection) * distance;
    }

    /// <inheritdoc/>
    protected override void OnTransformChanged()
    {
        _viewDirty = true;
    }

    /// <summary>
    /// Determines whether every vector component is finite.
    /// </summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }
}
