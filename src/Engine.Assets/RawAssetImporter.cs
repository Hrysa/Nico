namespace Engine.Assets;

/// <summary>Copies a source file unchanged into the imported artifact cache.</summary>
public sealed class RawAssetImporter : IAssetImporter
{
    /// <inheritdoc/>
    public string Id => "raw";

    /// <inheritdoc/>
    public int Version => 1;

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
            [new AssetArtifact("main", "application/octet-stream", artifactPath)],
            [],
            []);
    }
}
