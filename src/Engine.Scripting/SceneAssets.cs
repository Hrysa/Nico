using Engine.Core;

namespace Engine.Scripting;

/// <summary>Identifies one imported artifact with a compile-time resource type.</summary>
/// <typeparam name="TResource">Runtime resource expected from the artifact.</typeparam>
public readonly record struct Asset<TResource>
{
    /// <summary>Gets the persistent untyped artifact reference used by engine services.</summary>
    public AssetReference Reference { get; }

    /// <summary>Creates a typed wrapper around one persistent artifact reference.</summary>
    /// <param name="reference">Persistent artifact reference.</param>
    public Asset(AssetReference reference) => Reference = reference;
}

/// <summary>Resolves project assets for game scripts without exposing the asset database.</summary>
public interface ISceneAssetService
{
    /// <summary>Finds a typed imported artifact by project-relative source path.</summary>
    /// <typeparam name="TResource">Expected runtime resource type.</typeparam>
    /// <param name="projectPath">Project-relative source path.</param>
    /// <param name="subAsset">Imported artifact key, defaulting to the primary artifact.</param>
    /// <returns>Typed persistent artifact reference.</returns>
    Asset<TResource> FindByPath<TResource>(string projectPath, string subAsset = "main");
}

/// <summary>Maps project-relative paths to persistent asset identities.</summary>
public sealed class SceneAssetRegistry : ISceneAssetService
{
    private readonly Func<string, AssetId?> _resolveAsset;

    /// <summary>Creates a script asset registry over a host-owned path resolver.</summary>
    /// <param name="resolveAsset">Resolves a normalized project path to persistent identity.</param>
    public SceneAssetRegistry(Func<string, AssetId?> resolveAsset)
    {
        ArgumentNullException.ThrowIfNull(resolveAsset);
        _resolveAsset = resolveAsset;
    }

    /// <inheritdoc/>
    public Asset<TResource> FindByPath<TResource>(string projectPath, string subAsset = "main")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(subAsset);
        var normalized = projectPath.Replace('\\', '/');
        var id = _resolveAsset(normalized) ?? throw new FileNotFoundException(
            $"Project asset '{normalized}' is missing.");
        return new Asset<TResource>(new AssetReference(id, subAsset));
    }
}

/// <summary>Rejects asset lookup when a scene has no active project.</summary>
internal sealed class EmptySceneAssetService : ISceneAssetService
{
    /// <summary>Gets the shared detached-scene service.</summary>
    internal static EmptySceneAssetService Instance { get; } = new();

    /// <inheritdoc/>
    public Asset<TResource> FindByPath<TResource>(string projectPath, string subAsset = "main")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(subAsset);
        throw new InvalidOperationException("The active scene has no project asset resolver.");
    }
}
