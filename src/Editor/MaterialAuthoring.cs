using Engine.Core;

namespace Editor;

/// <summary>Creates project-owned standard-material assets.</summary>
public static class MaterialAuthoring
{
    /// <summary>Writes a default material source asset atomically.</summary>
    /// <param name="path">Destination project path.</param>
    public static void SaveDefault(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Save(path, new StandardMaterialAsset());
    }

    /// <summary>Writes one standard-material source asset atomically.</summary>
    /// <param name="path">Destination project path.</param>
    /// <param name="resource">Persistent material content to write.</param>
    public static void Save(string path, StandardMaterialAsset resource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(resource);
        AssetDocumentStorage.WriteAtomic(path,
            stream => StandardMaterialAssetCodec.Save(stream, resource));
    }
}
