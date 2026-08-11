using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.Scripting;

namespace ExampleGame;

/// <summary>Moves a dynamic character relative to an independently orbiting third-person camera.</summary>
public sealed partial class ThirdPersonController : SceneScript
{
    private static readonly AnimationSet LocomotionAnimations = new(new AssetReference(
        new AssetId(Guid.Parse("019ff038-6e1e-7a7d-bd1d-01a67bb65285")), "main"));
    private const float DegreesToRadians = MathF.PI / 180f;
    private const float MinimumPitch = -80f * DegreesToRadians;
    private const float MaximumPitch = 80f * DegreesToRadians;
    private RigidBodyComponent _body = null!;
    private PerspectiveCamera? _camera;
    private AnimationController _animation = null!;
    private bool _isRunning;
    private float _cameraYaw;
    private float _cameraPitch = -45f * DegreesToRadians;
    private bool _cameraOrbitActive;
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
        _animation = Scene.Animation.Bind(Owner, LocomotionAnimations);
        _animation.TryPlay("Idle", out _, 0f);

        _camera = Scene.FindNode<PerspectiveCamera>("GameCamera");
        if (_camera is not null && Owner is Node3D owner3D)
        {
            var target = owner3D.GetWorldPosition() + Vector3.UnitY * CameraTargetHeight;
            var direction = target - _camera.GetWorldPosition();
            if (direction.LengthSquared() > float.Epsilon)
            {
                direction = Vector3.Normalize(direction);
                _cameraYaw = MathF.Atan2(direction.X, -direction.Z);
                _cameraPitch = Math.Clamp(MathF.Asin(direction.Y),
                    MinimumPitch, MaximumPitch);
            }
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

        var isMoving = movement.LengthSquared() > float.Epsilon;
        if (isMoving)
            Owner.Rotation = Owner.Rotation with { Y = MathF.Atan2(movement.X, movement.Z) };
        UpdateLocomotionAnimation(isMoving);
    }

    /// <inheritdoc />
    public override void OnLateUpdate(double deltaTime)
    {
        UpdateCameraRig();
    }

    /// <inheritdoc />
    public override void OnDestroy()
    {
        if (_cameraOrbitActive)
            Scene.Input.SetPointerCaptured(false);
        _cameraOrbitActive = false;
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

    /// <summary>Cross-fades between stable locomotion aliases when movement changes.</summary>
    /// <param name="isMoving">Whether horizontal movement input is active.</param>
    private void UpdateLocomotionAnimation(bool isMoving)
    {
        if (_isRunning == isMoving)
            return;
        _isRunning = isMoving;
        _animation.TryPlay(isMoving ? "Run" : "Idle", out _,
            isMoving ? 0.15f : 0.2f);
    }

    /// <summary>Applies right-pointer drag to the independent camera orbit.</summary>
    private void UpdateCameraOrbit()
    {
        var secondaryDown = Scene.Input.IsPointerButtonDown(InputPointerButton.Secondary);
        if (secondaryDown != _cameraOrbitActive)
        {
            _cameraOrbitActive = secondaryDown;
            Scene.Input.SetPointerCaptured(secondaryDown);
        }
        if (!secondaryDown)
            return;
        var delta = Scene.Input.PointerDelta;
        if (delta == Vector2.Zero || !float.IsFinite(CameraOrbitSensitivity))
            return;
        var sensitivity = CameraOrbitSensitivity * DegreesToRadians;
        _cameraYaw = MathF.IEEERemainder(_cameraYaw + delta.X * sensitivity, MathF.Tau);
        _cameraPitch = Math.Clamp(
            _cameraPitch - delta.Y * sensitivity,
            MinimumPitch,
            MaximumPitch);
    }

    /// <summary>Places only the camera on a spherical world-space orbit around the player.</summary>
    private void UpdateCameraRig()
    {
        if (_camera is null || Owner is not Node3D owner3D)
            return;
        var target = owner3D.GetWorldPosition() + Vector3.UnitY * CameraTargetHeight;
        var horizontal = MathF.Cos(_cameraPitch);
        var forward = new Vector3(
            MathF.Sin(_cameraYaw) * horizontal,
            MathF.Sin(_cameraPitch),
            -MathF.Cos(_cameraYaw) * horizontal);
        var distance = MathF.Max(0.1f, CameraDistance);
        var cameraPosition = target - forward * distance;
        _camera.Position = cameraPosition;
        _camera.Rotation = new Vector3(_cameraPitch, _cameraYaw, 0f);
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
        Owner.AddComponent(new CapsuleColliderComponent
        {
            Center = new Vector3(0f, 0.9f, 0f),
            Radius = 0.35f,
            Height = 1.8f
        });
    }
}
