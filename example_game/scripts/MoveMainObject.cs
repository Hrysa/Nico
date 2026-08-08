using System.Numerics;
using Engine.Core;
using Engine.Scripting;

namespace ExampleGame;

/// <summary>
/// Moves its scene node smoothly from side to side around its authored position.
/// </summary>
public  sealed partial class MoveMainObject : SceneScript
{
    private double _elapsedTime;
    private float _previousOffset;
    [Observe(Editor)] public partial long Speed { set; get; } = 1;
    /// <inheritdoc />
    public override void OnReady()
    {
        _previousOffset = 0f;
        Owner.AddComponent(new RigidBodyComponent
        {
            MotionType = RigidBodyMotionType.Dynamic,
            Mass = 1f
        });

        Owner.AddComponent(new ColliderComponent
        {
            Shape = ColliderShape.Box,
            Size = Vector3.One
        });
    }

    /// <inheritdoc />
    public override void OnUpdate(double deltaTime)
    {
        // _elapsedTime += deltaTime;
        // var offset = MathF.Sin((float)_elapsedTime) * 2f * Speed;
        // Owner.Position += Vector3.UnitX * (offset - _previousOffset);
        // _previousOffset = offset;
    }
}
