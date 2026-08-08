using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.Scripting;

namespace ExampleGame;

/// <summary>Moves a dynamic character relative to a following third-person camera.</summary>
public sealed partial class ThirdPersonController : SceneScript
{
    private RigidBodyComponent _body = null!;
    private PerspectiveCamera? _camera;
    private Node3D? _cameraRig;

    /// <summary>Gets or sets horizontal movement speed in world units per second.</summary>
    [Observe(Editor)]
    public partial float MoveSpeed { get; set; } = 4f;

    /// <summary>Gets or sets upward velocity applied by a grounded jump.</summary>
    [Observe(Editor)]
    public partial float JumpSpeed { get; set; } = 5f;

    /// <summary>Gets or sets the height above the character followed by the camera.</summary>
    [Observe(Editor)]
    public partial float CameraTargetHeight { get; set; } = 1.25f;

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
    }

    /// <inheritdoc />
    public override void OnUpdate(double deltaTime)
    {
        if (Owner is not Node3D owner3D)
            return;

        var movement = ReadMovement();
        var velocity = _body.LinearVelocity;
        velocity.X = movement.X * MoveSpeed;
        velocity.Z = movement.Z * MoveSpeed;
        if (Scene.Input.WasKeyPressed(InputKey.Space) && IsGrounded(owner3D, velocity))
            velocity.Y = JumpSpeed;
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

    /// <summary>Keeps the child camera rig level while inheriting the player's position.</summary>
    private void UpdateCameraRig()
    {
        if (_cameraRig is null)
            return;
        _cameraRig.Position = new Vector3(0f, CameraTargetHeight, 0f);
        _cameraRig.Rotation = new Vector3(0f, -Owner.Rotation.Y, 0f);
    }

    /// <summary>Checks the initial flat-ground condition supported by the current solver.</summary>
    /// <param name="owner">Controlled world-space node.</param>
    /// <param name="velocity">Current physics velocity.</param>
    /// <returns>True when the character is resting at the example scene's ground height.</returns>
    private static bool IsGrounded(Node3D owner, Vector3 velocity) =>
        owner.GetWorldPosition().Y <= 0.03f && velocity.Y <= 0.05f;

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
