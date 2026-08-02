using Engine.Core;

namespace Engine.Scripting;

/// <summary>
/// Base class for game code attached to a scene node.
/// </summary>
public abstract class SceneScript
{
    /// <summary>Gets the node that owns this script instance.</summary>
    public Node Owner { get; internal set; } = null!;

    /// <summary>Gets services for querying and changing the active scene.</summary>
    public SceneContext Scene { get; internal set; } = null!;

    /// <summary>Runs once after this script and all other scene scripts are attached.</summary>
    public virtual void OnReady()
    {
    }

    /// <summary>Runs once for each game update.</summary>
    /// <param name="deltaTime">Elapsed time in seconds since the previous update.</param>
    public virtual void OnUpdate(double deltaTime)
    {
    }

    /// <summary>Runs before this script is detached from its scene.</summary>
    public virtual void OnDestroy()
    {
    }
}
