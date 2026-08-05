namespace Editor;

/// <summary>Selects initial asset importers for source formats recognized by the Editor.</summary>
public static class EditorAssetImporters
{
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
            _ => null
        };
    }
}
