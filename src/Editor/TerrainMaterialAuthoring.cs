using Engine.Core;

namespace Editor;

/// <summary>Creates project-owned terrain layer and painted material assets.</summary>
public static class TerrainMaterialAuthoring
{
    /// <summary>Writes a default terrain layer atomically.</summary>
    /// <param name="path">Destination project path.</param>
    public static void SaveDefaultLayer(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        AssetDocumentStorage.WriteAtomic(path,
            stream => TerrainMaterialAssetCodec.SaveLayer(stream, new TerrainLayerAsset()));
    }

    /// <summary>Writes a default empty painted terrain material atomically.</summary>
    /// <param name="path">Destination project path.</param>
    public static void SaveDefaultMaterial(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        AssetDocumentStorage.WriteAtomic(path,
            stream => TerrainMaterialAssetCodec.SaveMaterial(stream, new TerrainMaterialAsset()));
    }
}
