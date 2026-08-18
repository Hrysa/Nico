using System.Numerics;

namespace ExampleGame.Server;

/// <summary>Fixed-tick authoritative movement state backed by shared terrain collision.</summary>
internal sealed class ServerCharacterMotor
{
    private const float Gravity = -9.81f;
    private const float MoveSpeed = 8f;
    private const float JumpSpeed = 5f;
    private const float GroundSnapDistance = 0.2f;
    private const float MaximumAcceleration = 30f;
    internal static readonly float CosMaximumSlope = MathF.Cos(MathF.PI / 4f);
    private Vector2 _movement;
    private Vector2 _lastWalkableHorizontalPosition;
    private bool _jumpRequested;

    /// <summary>Creates a motor at one authoritative foot position.</summary>
    /// <param name="spawnPosition">Initial world-space foot position.</param>
    internal ServerCharacterMotor(Vector3 spawnPosition)
    {
        Position = spawnPosition;
        _lastWalkableHorizontalPosition = new Vector2(spawnPosition.X, spawnPosition.Z);
        IsGrounded = true;
        GroundNormal = Vector3.UnitY;
    }

    /// <summary>Gets the authoritative world-space foot position.</summary>
    internal Vector3 Position { get; private set; }

    /// <summary>Gets the authoritative world-space velocity.</summary>
    internal Vector3 Velocity { get; private set; }

    /// <summary>Gets whether a walkable terrain contact supports the player.</summary>
    internal bool IsGrounded { get; private set; }

    /// <summary>Gets the latest walkable terrain support normal.</summary>
    internal Vector3 GroundNormal { get; private set; }

    /// <summary>Stores the latest normalized input intent for the next fixed tick.</summary>
    /// <param name="movement">World XZ movement intent.</param>
    /// <param name="jump">Whether to latch a jump request.</param>
    internal void SetInput(Vector2 movement, bool jump)
    {
        var lengthSquared = movement.LengthSquared();
        _movement = lengthSquared > 1f ? movement / MathF.Sqrt(lengthSquared) : movement;
        _jumpRequested |= jump;
    }

    /// <summary>Advances authoritative movement by exactly one fixed tick.</summary>
    /// <param name="terrain">Shared authored collision field.</param>
    /// <param name="deltaTime">Fixed simulation duration.</param>
    internal void Simulate(ServerTerrainCollision terrain, float deltaTime)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        if (!float.IsFinite(deltaTime) || deltaTime <= 0f)
            throw new ArgumentOutOfRangeException(nameof(deltaTime));

        var wasGrounded = IsGrounded;
        var target = _movement * MoveSpeed;
        var horizontal = new Vector2(Velocity.X, Velocity.Z);
        horizontal = MoveTowards(horizontal, target, MaximumAcceleration * deltaTime);
        var vertical = Velocity.Y;
        if (_jumpRequested && IsGrounded)
        {
            vertical = JumpSpeed;
            IsGrounded = false;
        }
        _jumpRequested = false;
        if (!IsGrounded)
            vertical += Gravity * deltaTime;

        var previous = Position;
        var next = previous + new Vector3(horizontal.X, vertical, horizontal.Y) * deltaTime;
        if (!terrain.TrySample(next, out var ground))
        {
            Position = next;
            Velocity = new Vector3(horizontal.X, vertical, horizontal.Y);
            IsGrounded = false;
            return;
        }

        var walkable = Vector3.Dot(ground.Normal, Vector3.UnitY) >= CosMaximumSlope;
        if (walkable)
            _lastWalkableHorizontalPosition = new Vector2(next.X, next.Z);
        if (!walkable && next.Y <= ground.Height + GroundSnapDistance)
        {
            next.X = _lastWalkableHorizontalPosition.X;
            next.Z = _lastWalkableHorizontalPosition.Y;
            horizontal = Vector2.Zero;
            if (terrain.TrySample(next, out var previousGround))
            {
                ground = previousGround;
                walkable = Vector3.Dot(ground.Normal, Vector3.UnitY) >= CosMaximumSlope;
            }
        }
        var descending = vertical <= 0f;
        var supported = walkable && (wasGrounded || descending) &&
            next.Y <= ground.Height + GroundSnapDistance;
        if (supported)
        {
            next.Y = ground.Height;
            vertical = 0f;
            GroundNormal = ground.Normal;
            _lastWalkableHorizontalPosition = new Vector2(next.X, next.Z);
        }
        Position = next;
        Velocity = new Vector3(horizontal.X, vertical, horizontal.Y);
        IsGrounded = supported;
    }

    /// <summary>Moves one velocity toward a target by a bounded magnitude.</summary>
    /// <param name="current">Current horizontal velocity.</param>
    /// <param name="target">Desired horizontal velocity.</param>
    /// <param name="maximumDelta">Maximum change this tick.</param>
    /// <returns>The bounded next velocity.</returns>
    private static Vector2 MoveTowards(Vector2 current, Vector2 target, float maximumDelta)
    {
        var delta = target - current;
        var lengthSquared = delta.LengthSquared();
        if (lengthSquared <= maximumDelta * maximumDelta)
            return target;
        return current + delta * (maximumDelta / MathF.Sqrt(lengthSquared));
    }
}
