using Engine.Assets;

namespace Editor;

/// <summary>Selects initial asset importers for source formats recognized by the Editor.</summary>
public static class EditorAssetImporters
{
    /// <summary>Registers every importer ID that can be returned by <see cref="Select"/>.</summary>
    /// <param name="registry">Registry receiving the complete Editor importer set.</param>
    public static void RegisterAll(AssetImporterRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.Register(new RawAssetImporter("scene", "nico/scene-source"));
        registry.Register(new RawAssetImporter("csharp-script", "text/x-csharp"));
        registry.Register(new GlbModelImporter());
        registry.Register(new CollisionMeshAssetImporter());
        registry.Register(new TerrainAssetImporter());
        registry.Register(new AnimationSetAssetImporter());
        registry.Register(new StandardMaterialAssetImporter());
        registry.Register(new TerrainLayerAssetImporter());
        registry.Register(new TerrainMaterialAssetImporter());
        registry.Register(new ImageTextureAssetImporter());
    }

    /// <summary>Returns the importer ID for a recognized source asset.</summary>
    /// <param name="path">Absolute or project-relative source path.</param>
    /// <returns>The stable importer ID, or null for an unsupported file.</returns>
    public static string? Select(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.EndsWith(".scene.node", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".node", StringComparison.OrdinalIgnoreCase))
        {
            return "scene";
        }
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" => "csharp-script",
            ".glb" => "gltf-model",
            ".ncollision" => "collision-mesh",
            ".nterrain" => "terrain",
            ".nanimset" => "animation-set",
            ".nmat" => "standard-material",
            ".ntlayer" => "terrain-layer",
            ".ntmat" => "terrain-material",
            ".png" or ".jpg" or ".jpeg" => "image-texture",
            _ => null
        };
    }
}
