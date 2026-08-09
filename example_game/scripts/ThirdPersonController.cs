using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.Scripting;

namespace ExampleGame;

/// <summary>Moves a dynamic character relative to an independently orbiting third-person camera.</summary>
public sealed partial class ThirdPersonController : SceneScript
{
    private const float DegreesToRadians = MathF.PI / 180f;
    private const float MinimumPitch = -80f * DegreesToRadians;
    private const float MaximumPitch = -10f * DegreesToRadians;
    private RigidBodyComponent _body = null!;
    private PerspectiveCamera? _camera;
    private Node3D? _cameraRig;
    private float _cameraYaw;
    private float _cameraPitch = -45f * DegreesToRadians;
    private int _stableVerticalFrames;

    /// <summary>Gets or sets horizontal movement speed in world units per second.</summary>
    [Observe(Editor)]
    public partial float MoveSpeed { get; set; } = 4f;

    /// <summary>Gets or sets upward velocity applied by a grounded jump.</summary>
    [Observe(Editor)]
    public partial float JumpSpeed { get; set; } = 5f;

    /// <summary>Gets or sets the height above the character followed by the camera.</summary>
    [Observe(Editor)]
    public partial float CameraTargetHeight { get; set; } = 1.25f;

    /// <summary>Gets or sets the camera distance behind its orbit target.</summary>
    [Observe(Editor)]
    public partial float CameraDistance { get; set; } = 7f;

    /// <summary>Gets or sets camera orbit sensitivity in degrees per pointer pixel.</summary>
    [Observe(Editor)]
    public partial float CameraOrbitSensitivity { get; set; } = 0.18f;

    /// <inheritdoc />
    public override void OnReady()
    {
        _body = Owner.GetComponent<RigidBodyComponent>() ?? AddDefaultRigidBody();
        _body.MotionType = RigidBodyMotionType.Dynamic;
        _body.UseGravity = true;
        _body.LinearDamping = 0.1f;
        if (Owner.GetComponent<ColliderComponent>() is null)
            AddDefaultCollider();

        _camera = Scene.FindNode<PerspectiveCamera>("GameCamera");
        _cameraRig = _camera?.Parent as Node3D;
        if (_camera is not null)
        {
            var initialRotation = _camera.GetWorldRotation();
            _cameraYaw = initialRotation.Y;
            _cameraPitch = Math.Clamp(initialRotation.X, MinimumPitch, MaximumPitch);
        }
        UpdateCameraRig();
    }

    /// <inheritdoc />
    public override void OnUpdate(double deltaTime)
    {
        if (Owner is not Node3D)
            return;

        UpdateCameraOrbit();
        UpdateCameraRig();
        var movement = ReadMovement();
        var velocity = _body.LinearVelocity;
        velocity.X = movement.X * MoveSpeed;
        velocity.Z = movement.Z * MoveSpeed;
        UpdateGroundedState(velocity);
        if (Scene.Input.WasKeyPressed(InputKey.Space) && _stableVerticalFrames >= 2)
        {
            velocity.Y = JumpSpeed;
            _stableVerticalFrames = 0;
        }
        _body.LinearVelocity = velocity;

        if (movement.LengthSquared() > float.Epsilon)
            Owner.Rotation = Owner.Rotation with { Y = MathF.Atan2(movement.X, movement.Z) };
    }

    /// <inheritdoc />
    public override void OnLateUpdate(double deltaTime)
    {
        UpdateCameraRig();
    }

    /// <summary>Reads normalized camera-relative WASD movement.</summary>
    /// <returns>Horizontal world-space movement direction.</returns>
    private Vector3 ReadMovement()
    {
        var horizontal = 0f;
        var vertical = 0f;
        if (Scene.Input.IsKeyDown(InputKey.A))
            horizontal -= 1f;
        if (Scene.Input.IsKeyDown(InputKey.D))
            horizontal += 1f;
        if (Scene.Input.IsKeyDown(InputKey.W))
            vertical += 1f;
        if (Scene.Input.IsKeyDown(InputKey.S))
            vertical -= 1f;
        if (horizontal == 0f && vertical == 0f)
            return Vector3.Zero;

        var forward = _camera?.GetForwardVector() ?? -Vector3.UnitZ;
        forward.Y = 0f;
        if (forward.LengthSquared() <= float.Epsilon)
            forward = -Vector3.UnitZ;
        else
            forward = Vector3.Normalize(forward);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var movement = right * horizontal + forward * vertical;
        return movement.LengthSquared() > 1f ? Vector3.Normalize(movement) : movement;
    }

    /// <summary>Applies right-pointer drag to the independent camera orbit.</summary>
    private void UpdateCameraOrbit()
    {
        if (!Scene.Input.IsPointerButtonDown(InputPointerButton.Secondary))
            return;
        var delta = Scene.Input.PointerDelta;
        if (delta == Vector2.Zero || !float.IsFinite(CameraOrbitSensitivity))
            return;
        var sensitivity = CameraOrbitSensitivity * DegreesToRadians;
        _cameraYaw = MathF.IEEERemainder(_cameraYaw - delta.X * sensitivity, MathF.Tau);
        _cameraPitch = Math.Clamp(
            _cameraPitch - delta.Y * sensitivity,
            MinimumPitch,
            MaximumPitch);
    }

    /// <summary>Places the orbit pivot at the player and the camera behind that pivot.</summary>
    private void UpdateCameraRig()
    {
        if (_cameraRig is null || _camera is null || Owner is not Node3D owner3D)
            return;
        var target = owner3D.GetWorldPosition() + Vector3.UnitY * CameraTargetHeight;
        _cameraRig.SetWorldTransform(target, new Vector3(_cameraPitch, _cameraYaw, 0f));
        _camera.Position = new Vector3(0f, 0f, MathF.Max(0.1f, CameraDistance));
        _camera.Rotation = Vector3.Zero;
    }

    /// <summary>Tracks consecutive vertically settled frames without assuming a ground height.</summary>
    /// <param name="velocity">Current physics velocity.</param>
    private void UpdateGroundedState(Vector3 velocity)
    {
        if (MathF.Abs(velocity.Y) <= 0.05f)
            _stableVerticalFrames++;
        else
            _stableVerticalFrames = 0;
    }

    /// <summary>Adds the default dynamic body used when the scene has none.</summary>
    /// <returns>The attached body.</returns>
    private RigidBodyComponent AddDefaultRigidBody()
    {
        var body = new RigidBodyComponent
        {
            MotionType = RigidBodyMotionType.Dynamic,
            Mass = 1f,
            LinearDamping = 0.1f
        };
        Owner.AddComponent(body);
        return body;
    }

    /// <summary>Adds a foot-origin capsule matching the example character.</summary>
    private void AddDefaultCollider()
    {
        Owner.AddComponent(new ColliderComponent
        {
            Shape = ColliderShape.Capsule,
            Center = new Vector3(0f, 0.9f, 0f),
            Radius = 0.35f,
            Height = 1.8f
        });
    }
}
