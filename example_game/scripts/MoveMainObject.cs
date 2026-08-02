using System.Numerics;
using Engine.Scripting;

namespace ExampleGame;

/// <summary>
/// Moves its scene node smoothly from side to side around its authored position.
/// </summary>
public sealed class MoveMainObject : SceneScript
{
    private Vector3 _origin;
    private double _elapsedTime;

    /// <inheritdoc />
    public override void OnReady()
    {
        _origin = Owner.Position;
    }

    /// <inheritdoc />
    public override void OnUpdate(double deltaTime)
    {
        _elapsedTime += deltaTime;
        var offset = MathF.Sin((float)_elapsedTime) * 2f;
        Owner.Position = _origin + Vector3.UnitX * offset;
    }
}
