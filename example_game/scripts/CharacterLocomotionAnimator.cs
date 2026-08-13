using Engine.Core;
using Engine.Graphics;
using Engine.Scripting;

namespace ExampleGame;

/// <summary>Drives character locomotion presentation from horizontal physics velocity.</summary>
public sealed class CharacterLocomotionAnimator : SceneScript
{
    private RigidBodyComponent _body = null!;
    private AnimationController _animation = null!;
    private bool _isMoving;

    /// <inheritdoc />
    public override void OnReady()
    {
        _body = Owner.GetComponent<RigidBodyComponent>() ?? throw new InvalidOperationException(
            "Character locomotion animation requires a rigid body.");
        var animations = Scene.Assets.FindByPath<AnimationSetResource>(
            "models/Locomotion.nanimset");
        _animation = Scene.Animation.Bind(Owner, animations);
        _animation.TryPlay("Idle", out _, 0f);
    }

    /// <inheritdoc />
    public override void OnUpdate(double deltaTime)
    {
        var velocity = _body.LinearVelocity;
        var isMoving = velocity.X * velocity.X + velocity.Z * velocity.Z > float.Epsilon;
        if (_isMoving == isMoving)
            return;
        _isMoving = isMoving;
        _animation.TryPlay(isMoving ? "Run" : "Idle", out _,
            isMoving ? 0.15f : 0.2f);
    }
}
