using Engine.Core;

namespace Engine.Graphics;

/// <summary>Identifies a scene node whose concrete type is supplied by a higher-level engine module.</summary>
public interface ICustomSceneNode
{
    /// <summary>Gets the stable scene-file type identifier.</summary>
    string SceneTypeId { get; }
}

/// <summary>Creates higher-level node types while loading a renderer-independent scene file.</summary>
public interface ISceneNodeFactory
{
    /// <summary>Attempts to create one detached node for a stable scene-file type identifier.</summary>
    /// <param name="sceneTypeId">Stable custom node type identifier.</param>
    /// <param name="node">Created detached node when recognized.</param>
    /// <returns>True when the type identifier was recognized.</returns>
    bool TryCreate(string sceneTypeId, out Node? node);
}
