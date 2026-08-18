using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.Scripting;
using ExampleGame.Networking;

namespace ExampleGame;

/// <summary>Sends player intent to the authoritative server and presents returned character state.</summary>
public sealed partial class ThirdPersonController : SceneScript
{
    private const float DegreesToRadians = MathF.PI / 180f;
    private const float MinimumPitch = -80f * DegreesToRadians;
    private const float MaximumPitch = 80f * DegreesToRadians;
    private RigidBodyComponent _body = null!;
    private UdpGameClient? _networkClient;
    private Task<UdpGameClient>? _connectionTask;
    private CancellationTokenSource? _connectionCancellation;
    private PerspectiveCamera? _camera;
    private float _cameraYaw;
    private float _cameraPitch = -45f * DegreesToRadians;
    private bool _cameraOrbitActive;
    private uint _inputSequence;
    private double _inputAccumulator;
    private bool _jumpRequested;
    private bool _attackRequested;
    private bool _attackButtonDown;
    private Vector3 _authoritativePosition;
    private float _requestedYaw;
    private float _characterPitch;
    private float _characterRoll;

    /// <summary>Gets or sets the authoritative server host.</summary>
    [Observe(Editor)]
    public partial string ServerHost { get; set; } = "127.0.0.1";

    /// <summary>Gets or sets the authoritative server UDP port.</summary>
    [Observe(Editor)]
    public partial int ServerPort { get; set; } = 7777;

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
    public override bool IsStartupComplete => _networkClient is not null;

    /// <inheritdoc />
    public override void OnReady()
    {
        CombatPresentationState.Clear();
        if (Owner is not Node3D owner3D)
            throw new InvalidOperationException("Network character control requires a Node3D owner.");
        var serverHost = ServerHost;
        if (string.IsNullOrWhiteSpace(serverHost))
            throw new InvalidOperationException("An authoritative server host is required.");
        _body = Owner.GetComponent<RigidBodyComponent>() ?? AddDefaultRigidBody();
        _body.MotionType = RigidBodyMotionType.Kinematic;
        _body.UseGravity = false;
        _body.LinearVelocity = Vector3.Zero;
        if (Owner.GetComponent<ColliderComponent>() is null)
            AddDefaultCollider();
        _authoritativePosition = owner3D.GetWorldPosition();
        var authoredRotation = owner3D.GetWorldRotation();
        _requestedYaw = authoredRotation.Y;
        _characterPitch = authoredRotation.X;
        _characterRoll = authoredRotation.Z;
        owner3D.SetWorldTransform(_authoritativePosition,
            GetCharacterRotation());
        _camera = Scene.FindNode<PerspectiveCamera>("GameCamera");
        if (_camera is not null)
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
        _connectionCancellation = new CancellationTokenSource();
        var cancellationToken = _connectionCancellation.Token;
        _connectionTask = Task.Run(
            () => UdpGameClient.Connect(
                serverHost, ServerPort, TimeSpan.FromSeconds(2), cancellationToken),
            cancellationToken);
    }

    /// <inheritdoc />
    public override void OnStartupUpdate()
    {
        if (_connectionTask is not { IsCompleted: true } completedTask)
            return;
        _connectionTask = null;
        _connectionCancellation?.Dispose();
        _connectionCancellation = null;
        _networkClient = completedTask.GetAwaiter().GetResult();
        if (Owner is not Node3D owner3D)
            return;
        _authoritativePosition = _networkClient.SpawnPosition;
        owner3D.SetWorldTransform(_authoritativePosition, GetCharacterRotation());
        UpdateCameraRig();
    }

    /// <inheritdoc />
    public override void OnUpdate(double deltaTime)
    {
        if (Owner is not Node3D owner3D || _networkClient is null)
            return;

        UpdateCameraOrbit();
        var movement = ReadMovement();
        _jumpRequested |= Scene.Input.WasKeyPressed(InputKey.Space);
        var attackButtonDown = Scene.Input.IsPointerButtonDown(InputPointerButton.Primary);
        _attackRequested |= attackButtonDown && !_attackButtonDown;
        _attackButtonDown = attackButtonDown;
        if (movement.LengthSquared() > float.Epsilon)
            _requestedYaw = MathF.Atan2(movement.X, movement.Z);
        _inputAccumulator += Math.Max(0d, deltaTime);
        var inputInterval = 1d / _networkClient.TickRate;
        if (_inputAccumulator >= inputInterval)
        {
            _inputAccumulator %= inputInterval;
            _networkClient.SendInput(
                ++_inputSequence,
                new Vector2(movement.X, movement.Z),
                _requestedYaw,
                _jumpRequested,
                _attackRequested);
            _jumpRequested = false;
            _attackRequested = false;
        }
        if (_networkClient.TryReceiveLatestSnapshot(out var snapshot))
        {
            _authoritativePosition = snapshot.Position;
            _body.LinearVelocity = snapshot.Velocity;
            CombatPresentationState.Publish(snapshot);
            if (snapshot.AcknowledgedInput == _inputSequence)
                _requestedYaw = snapshot.FacingYaw;
        }
        if (_networkClient.HasTimedOut(TimeSpan.FromSeconds(3)))
            throw new TimeoutException("Lost connection to the authoritative game server.");

        var blend = 1f - MathF.Exp(-18f * (float)Math.Max(0d, deltaTime));
        var position = Vector3.Lerp(owner3D.GetWorldPosition(), _authoritativePosition, blend);
        owner3D.SetWorldTransform(position, GetCharacterRotation());
        UpdateCameraRig();
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
        _connectionCancellation?.Cancel();
        if (_connectionTask is { } pendingConnection)
        {
            try
            {
                pendingConnection.GetAwaiter().GetResult().Dispose();
            }
            catch (Exception exception) when (exception is OperationCanceledException or
                TimeoutException or System.Net.Sockets.SocketException)
            {
            }
        }
        _connectionTask = null;
        _connectionCancellation?.Dispose();
        _connectionCancellation = null;
        _networkClient?.Dispose();
        _networkClient = null;
        CombatPresentationState.Clear();
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

    /// <summary>Combines authored non-yaw orientation with predicted server-facing intent.</summary>
    /// <returns>Stable world-space character rotation.</returns>
    private Vector3 GetCharacterRotation()
    {
        return new Vector3(_characterPitch, _requestedYaw, _characterRoll);
    }

    /// <summary>Adds the default presentation body used when the scene has none.</summary>
    /// <returns>The attached body.</returns>
    private RigidBodyComponent AddDefaultRigidBody()
    {
        var body = new RigidBodyComponent
        {
            MotionType = RigidBodyMotionType.Kinematic,
            Mass = 1f,
            UseGravity = false
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
