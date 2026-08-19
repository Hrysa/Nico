using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.Scripting;
using ExampleGame.Networking;

namespace ExampleGame;

/// <summary>Presents one authoritative monster with locomotion, hit, and death animation.</summary>
public sealed class MonsterController : SceneScript
{
    private AnimationController _animation = null!;
    private AnimationState? _hitAnimation;
    private int _monsterIndex;
    private byte _health = 100;
    private bool _isReacting;
    private bool _isDead;
    private bool _isMoving;

    /// <inheritdoc />
    public override void OnReady()
    {
        _monsterIndex = Owner.Name switch
        {
            "Monster 1" => 0,
            "Monster 2" => 1,
            _ => throw new InvalidOperationException(
                "Monster presentation requires the name 'Monster 1' or 'Monster 2'.")
        };
        var animations = Scene.Assets.FindByPath<AnimationSetResource>(
            "models/Monster.nanimset");
        _animation = Scene.Animation.Bind(Owner, animations);
        _hitAnimation = _animation.GetOrCreate("Hit");
        _hitAnimation.Ended += OnHitEnded;
        _animation.TryPlay("Idle", out _, 0f);
    }

    /// <inheritdoc />
    public override void OnUpdate(double deltaTime)
    {
        if (Owner is not Node3D owner || !CombatPresentationState.HasSnapshot)
            return;
        var state = GetState(CombatPresentationState.Snapshot);
        var current = owner.GetWorldPosition();
        var blend = 1f - MathF.Exp(-14f * (float)Math.Max(0d, deltaTime));
        var position = Vector3.Lerp(current, state.Position, blend);
        owner.SetWorldTransform(position, new Vector3(0f, state.FacingYaw, 0f));

        if (state.Health == 0)
        {
            if (!_isDead)
            {
                _isDead = true;
                _isReacting = false;
                _animation.PlayFromStart("Death", 0.08f);
            }
            _health = 0;
            return;
        }
        if (state.Health < _health)
        {
            _health = state.Health;
            _isReacting = true;
            _animation.PlayFromStart("Hit", 0.05f);
            return;
        }
        _health = state.Health;
        if (_isReacting)
            return;
        var isMoving = Vector3.DistanceSquared(position, state.Position) > 0.0004f;
        if (_isMoving == isMoving)
            return;
        _isMoving = isMoving;
        _animation.TryPlay(isMoving ? "Run" : "Idle", out _, 0.15f);
    }

    /// <inheritdoc />
    public override void OnDestroy()
    {
        _hitAnimation?.Ended -= OnHitEnded;
    }

    /// <summary>Returns this authored monster's slot from a fixed authoritative snapshot.</summary>
    /// <param name="snapshot">Newest server snapshot.</param>
    /// <returns>Monster state matching the owning scene node.</returns>
    private MonsterSnapshotMessage GetState(ServerSnapshotMessage snapshot)
    {
        return _monsterIndex == 0 ? snapshot.Monster0 : snapshot.Monster1;
    }

    /// <summary>Returns a surviving monster to its current locomotion state.</summary>
    /// <param name="state">Completed hit animation.</param>
    private void OnHitEnded(AnimationState state)
    {
        _isReacting = false;
        if (_isDead)
            return;
        _animation.TryPlay(_isMoving ? "Run" : "Idle", out _, 0.12f);
    }
}
