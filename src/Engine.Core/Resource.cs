namespace Engine.Core;

/// <summary>
/// Base class for all loadable assets (meshes, textures, materials, etc.).
/// </summary>
public class Resource
{
    /// <summary>Gets or sets the resource name for debugging.</summary>
    public string Name { get; set; } = string.Empty;
}
