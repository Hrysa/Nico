using Engine.Core;
using Engine.Graphics;

namespace ExampleGame.Server;

/// <summary>Creates headless placeholders for presentation-only custom scene nodes.</summary>
internal sealed class ServerSceneNodeFactory : ISceneNodeFactory
{
    /// <summary>Gets the shared stateless headless factory.</summary>
    internal static ServerSceneNodeFactory Instance { get; } = new();

    /// <inheritdoc/>
    public bool TryCreate(string sceneTypeId, out Node? node)
    {
        if (sceneTypeId == "nico/hud-root")
        {
            node = new Node();
            return true;
        }
        node = null;
        return false;
    }
}
