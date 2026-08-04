namespace Engine.Assets;

/// <summary>Stores uniquely identified asset importer implementations.</summary>
public sealed class AssetImporterRegistry
{
    private readonly Dictionary<string, IAssetImporter> _importers =
        new(StringComparer.Ordinal);

    /// <summary>Registers one importer by its stable identifier.</summary>
    /// <param name="importer">Importer implementation to register.</param>
    public void Register(IAssetImporter importer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentException.ThrowIfNullOrWhiteSpace(importer.Id);
        if (importer.Version <= 0)
            throw new ArgumentOutOfRangeException(nameof(importer),
                "Importer versions must be positive.");
        if (!_importers.TryAdd(importer.Id, importer))
            throw new InvalidOperationException($"Asset importer '{importer.Id}' is already registered.");
    }

    /// <summary>Resolves a required importer by stable identifier.</summary>
    /// <param name="id">Importer identifier from asset metadata.</param>
    /// <returns>The registered importer implementation.</returns>
    public IAssetImporter Resolve(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return _importers.TryGetValue(id, out var importer)
            ? importer
            : throw new KeyNotFoundException($"Asset importer '{id}' is not registered.");
    }
}
