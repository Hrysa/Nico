using System.Numerics;
using Engine.Scripting;

namespace ExampleGame;

/// <summary>
/// Moves its scene node smoothly from side to side around its authored position.
/// </summary>
public sealed class MoveMainObject : SceneScript
{
    private double _elapsedTime;
    private float _previousOffset;

    /// <inheritdoc />
    public override void OnReady()
    {
        _previousOffset = 0f;
    }

    /// <inheritdoc />
    public override void OnUpdate(double deltaTime)
    {
        _elapsedTime += deltaTime;
        var offset = MathF.Sin((float)_elapsedTime) * 2f;
        Owner.Position += Vector3.UnitX * (offset - _previousOffset);
        _previousOffset = offset;
    }
}
