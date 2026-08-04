using Engine.Core;

namespace Engine.Assets;

/// <summary>Identifies the current physical storage location of one imported artifact.</summary>
public abstract record AssetLocation;

/// <summary>Locates an imported artifact in a loose physical file.</summary>
/// <param name="Path">Absolute artifact file path.</param>
public sealed record LooseFileAssetLocation(string Path) : AssetLocation;

/// <summary>Locates an imported artifact through a mounted virtual path.</summary>
/// <param name="Path">Mounted virtual artifact path.</param>
public sealed record VirtualFileAssetLocation(string Path) : AssetLocation;

/// <summary>Locates an imported artifact through a package-owned logical entry.</summary>
/// <param name="Package">Stable package identifier.</param>
/// <param name="Entry">Package-local entry identifier.</param>
public sealed record PackageEntryAssetLocation(string Package, string Entry) : AssetLocation;

/// <summary>Describes one resolved imported artifact ready for runtime loading.</summary>
/// <param name="Location">Current physical artifact location.</param>
/// <param name="ContentType">Stable content type selecting a runtime loader.</param>
/// <param name="Generation">Stable artifact generation identifier.</param>
public sealed record ResolvedAsset(
    AssetLocation Location,
    string ContentType,
    string Generation);

/// <summary>Resolves persistent asset references to current artifact locations.</summary>
public interface IAssetResolver
{
    /// <summary>Resolves one persistent asset or sub-asset reference.</summary>
    /// <param name="reference">Persistent asset reference.</param>
    /// <returns>The current artifact location, content type, and generation.</returns>
    ResolvedAsset Resolve(AssetReference reference);
}

/// <summary>Opens readable package entry streams without exposing archive layout.</summary>
public interface IPackageReader
{
    /// <summary>Gets the stable package identifier.</summary>
    string Id { get; }

    /// <summary>Opens a logical package entry.</summary>
    /// <param name="entry">Package-local entry identifier.</param>
    /// <returns>A readable entry stream owned by the caller.</returns>
    Stream OpenRead(string entry);
}

/// <summary>Opens artifact streams independently of their loose, virtual, or package storage.</summary>
public interface IAssetStorage
{
    /// <summary>Opens one resolved artifact location.</summary>
    /// <param name="location">Resolved artifact location.</param>
    /// <returns>A readable artifact stream owned by the caller.</returns>
    Stream OpenRead(AssetLocation location);
}

/// <summary>Routes resolved artifact locations to loose files, VFS mounts, or package readers.</summary>
public sealed class AssetStorageRouter : IAssetStorage
{
    private readonly IVirtualFileSystem _virtualFileSystem;
    private readonly Dictionary<string, IPackageReader> _packages;

    /// <summary>Creates a storage router over one VFS and optional package readers.</summary>
    /// <param name="virtualFileSystem">Mounted virtual filesystem.</param>
    /// <param name="packages">Available package readers.</param>
    public AssetStorageRouter(
        IVirtualFileSystem virtualFileSystem,
        IEnumerable<IPackageReader>? packages = null)
    {
        ArgumentNullException.ThrowIfNull(virtualFileSystem);
        _virtualFileSystem = virtualFileSystem;
        _packages = (packages ?? []).ToDictionary(package => package.Id, StringComparer.Ordinal);
    }

    /// <inheritdoc/>
    public Stream OpenRead(AssetLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        return location switch
        {
            LooseFileAssetLocation loose => new FileStream(loose.Path, FileMode.Open,
                FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan),
            VirtualFileAssetLocation virtualFile => _virtualFileSystem.OpenRead(virtualFile.Path),
            PackageEntryAssetLocation package when _packages.TryGetValue(package.Package,
                out var reader) => reader.OpenRead(package.Entry),
            PackageEntryAssetLocation package => throw new KeyNotFoundException(
                $"Asset package '{package.Package}' is not mounted."),
            _ => throw new NotSupportedException(
                $"Asset location type '{location.GetType().Name}' is not supported.")
        };
    }
}
