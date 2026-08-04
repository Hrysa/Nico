using System.Text.Json;
using Engine.Core;

namespace Engine.Assets;

/// <summary>Reads and atomically writes versioned JSON asset sidecars.</summary>
public static class AssetMetadataStore
{
    /// <summary>Gets the metadata schema version written by this engine.</summary>
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>Creates metadata with a new UUIDv7 identity and empty importer settings.</summary>
    /// <param name="importer">Stable importer identifier.</param>
    /// <returns>New validated asset metadata.</returns>
    public static AssetMetadata Create(string importer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(importer);
        using var document = JsonDocument.Parse("{}");
        return new AssetMetadata(CurrentVersion, AssetId.New(), importer.Trim(),
            document.RootElement.Clone());
    }

    /// <summary>Returns the sidecar path associated with a source asset path.</summary>
    /// <param name="sourcePath">Source asset path.</param>
    /// <returns>The source path followed by the <c>.meta</c> suffix.</returns>
    public static string GetSidecarPath(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return Path.GetFullPath(sourcePath) + ".meta";
    }

    /// <summary>Loads and validates metadata belonging to a source asset.</summary>
    /// <param name="sourcePath">Source asset path whose sidecar is loaded.</param>
    /// <returns>The validated metadata document.</returns>
    public static AssetMetadata Load(string sourcePath)
    {
        var sidecarPath = GetSidecarPath(sourcePath);
        try
        {
            using var stream = File.OpenRead(sidecarPath);
            var metadata = JsonSerializer.Deserialize<AssetMetadata>(stream, _jsonOptions)
                ?? throw new InvalidDataException($"Asset metadata is empty: {sidecarPath}");
            Validate(metadata, sidecarPath);
            return metadata with { Settings = metadata.Settings.Clone() };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Asset metadata is invalid JSON: {sidecarPath}", exception);
        }
    }

    /// <summary>Atomically writes validated metadata beside a source asset.</summary>
    /// <param name="sourcePath">Source asset path receiving the sidecar.</param>
    /// <param name="metadata">Metadata to validate and persist.</param>
    public static void Save(string sourcePath, AssetMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var sidecarPath = GetSidecarPath(sourcePath);
        Validate(metadata, sidecarPath);
        var directory = Path.GetDirectoryName(sidecarPath)
            ?? throw new InvalidOperationException("The metadata path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = sidecarPath + $".tmp-{Guid.NewGuid():N}";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, metadata, _jsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, sidecarPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    /// <summary>Validates the authoritative fields of one metadata document.</summary>
    /// <param name="metadata">Metadata document to validate.</param>
    /// <param name="sidecarPath">Sidecar path used in diagnostics.</param>
    private static void Validate(AssetMetadata metadata, string sidecarPath)
    {
        if (metadata.Version != CurrentVersion)
            throw new InvalidDataException(
                $"Unsupported asset metadata version {metadata.Version} in {sidecarPath}; " +
                $"expected {CurrentVersion}.");
        if (metadata.Id.Value == Guid.Empty)
            throw new InvalidDataException($"Asset metadata has an empty ID: {sidecarPath}");
        if (string.IsNullOrWhiteSpace(metadata.Importer))
            throw new InvalidDataException($"Asset metadata has no importer: {sidecarPath}");
        if (metadata.Settings.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                $"Asset metadata settings must be a JSON object: {sidecarPath}");
    }
}
