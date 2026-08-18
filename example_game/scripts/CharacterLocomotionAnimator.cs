using Engine.Core;
using Engine.Graphics;
using Engine.Scripting;

namespace ExampleGame;

/// <summary>Drives character locomotion presentation from horizontal physics velocity.</summary>
public sealed class CharacterLocomotionAnimator : SceneScript
{
    private RigidBodyComponent _body = null!;
    private AnimationController _animation = null!;
    private AnimationState? _jumpAnimation;
    private AnimationState? _attackAnimation;
    private uint _attackSequence;
    private bool _isMoving;
    private bool _isJumping;
    private bool _isAttacking;

    /// <inheritdoc />
    public override void OnReady()
    {
        _body = Owner.GetComponent<RigidBodyComponent>() ?? throw new InvalidOperationException(
            "Character locomotion animation requires a rigid body.");
        var animations = Scene.Assets.FindByPath<AnimationSetResource>(
            "models/Locomotion.nanimset");
        _animation = Scene.Animation.Bind(Owner, animations);
        _jumpAnimation = _animation.GetOrCreate("Jump");
        _jumpAnimation.Ended += OnJumpEnded;
        _attackAnimation = _animation.GetOrCreate("Attack");
        _attackAnimation.Ended += OnAttackEnded;
        _animation.TryPlay("Idle", out _, 0f);
    }

    /// <inheritdoc />
    public override void OnUpdate(double deltaTime)
    {
        if (CombatPresentationState.HasSnapshot &&
            CombatPresentationState.Snapshot.AttackSequence != _attackSequence)
        {
            _attackSequence = CombatPresentationState.Snapshot.AttackSequence;
            _isAttacking = true;
            _isJumping = false;
            _animation.PlayFromStart("Attack", 0.06f);
            return;
        }
        if (_isAttacking)
            return;
        if (!_isJumping && Scene.Input.WasKeyPressed(InputKey.Space))
        {
            _isJumping = true;
            _animation.PlayFromStart("Jump", 0.08f);
            return;
        }
        if (_isJumping)
            return;
        PlayLocomotion();
    }

    /// <inheritdoc />
    public override void OnDestroy()
    {
        if (_jumpAnimation is not null)
            _jumpAnimation.Ended -= OnJumpEnded;
        if (_attackAnimation is not null)
            _attackAnimation.Ended -= OnAttackEnded;
    }

    /// <summary>Returns to current locomotion after the one-shot jump finishes.</summary>
    /// <param name="state">Completed jump state.</param>
    private void OnJumpEnded(AnimationState state)
    {
        _isJumping = false;
        PlayLocomotion(force: true);
    }

    /// <summary>Returns to current locomotion after the authoritative attack finishes.</summary>
    /// <param name="state">Completed attack state.</param>
    private void OnAttackEnded(AnimationState state)
    {
        _isAttacking = false;
        PlayLocomotion(force: true);
    }

    /// <summary>Plays the locomotion clip selected from horizontal velocity.</summary>
    /// <param name="force">Whether to replay the selected state after an action.</param>
    private void PlayLocomotion(bool force = false)
    {
        var velocity = _body.LinearVelocity;
        var isMoving = velocity.X * velocity.X + velocity.Z * velocity.Z > float.Epsilon;
        if (!force && _isMoving == isMoving)
            return;
        _isMoving = isMoving;
        _animation.TryPlay(isMoving ? "Run" : "Idle", out _,
            isMoving ? 0.15f : 0.2f);
    }
}
