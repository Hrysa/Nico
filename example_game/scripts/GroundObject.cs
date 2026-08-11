using System.Numerics;
using Engine.Core;
using Engine.Scripting;

namespace ExampleGame;

/// <summary>
/// Moves its scene node smoothly from side to side around its authored position.
/// </summary>
public  sealed partial class GroundObject : SceneScript
{
    /// <inheritdoc />
    public override void OnReady()
    {
        Owner.AddComponent(new PlaneColliderComponent { Size = Vector2.One });
    }
}
