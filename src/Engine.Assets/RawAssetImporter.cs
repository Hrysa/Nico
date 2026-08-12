namespace Engine.Assets;

/// <summary>Copies a source file unchanged into the imported artifact cache.</summary>
public sealed class RawAssetImporter : IAssetImporter
{
    private readonly string _id;
    private readonly string _contentType;

    /// <inheritdoc/>
    public string Id => _id;

    /// <inheritdoc/>
    public int Version => 1;

    /// <summary>Creates a configurable pass-through source importer.</summary>
    /// <param name="id">Stable importer identifier.</param>
    /// <param name="contentType">Published artifact content type.</param>
    public RawAssetImporter(
        string id = "raw",
        string contentType = "application/octet-stream")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        _id = id;
        _contentType = contentType;
    }

    /// <inheritdoc/>
    public AssetImportResult Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(context.SourcePath);
        var artifactPath = "content" + extension.ToLowerInvariant();
        using (var source = context.OpenSource())
        using (var destination = context.CreateArtifact(artifactPath))
            source.CopyTo(destination);
        context.CancellationToken.ThrowIfCancellationRequested();
        return new AssetImportResult(
            [new AssetArtifact("main", _contentType, artifactPath)],
            [],
            []);
    }
}
