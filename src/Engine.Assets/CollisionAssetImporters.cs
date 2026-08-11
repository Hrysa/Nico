namespace Engine.Assets;

/// <summary>Publishes a generated collision-mesh source with its runtime content type.</summary>
public sealed class CollisionMeshAssetImporter : IAssetImporter
{
    /// <inheritdoc/>
    public string Id => "collision-mesh";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public AssetImportResult Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Copy(context, "collision.nmesh", "nico/static-mesh");
    }

    /// <summary>Copies one source to a typed artifact.</summary>
    /// <param name="context">Import context.</param><param name="relativePath">Artifact path.</param>
    /// <param name="contentType">Runtime loader content type.</param><returns>Import result.</returns>
    internal static AssetImportResult Copy(AssetImportContext context, string relativePath,
        string contentType)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        using (var source = context.OpenSource())
        using (var destination = context.CreateArtifact(relativePath))
            source.CopyTo(destination);
        return new AssetImportResult(
            [new AssetArtifact("main", contentType, relativePath)], [], []);
    }
}

/// <summary>Publishes a generated terrain source with its runtime content type.</summary>
public sealed class TerrainAssetImporter : IAssetImporter
{
    /// <inheritdoc/>
    public string Id => "terrain";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public AssetImportResult Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return CollisionMeshAssetImporter.Copy(context, "terrain.nterrain", "nico/terrain");
    }
}

/// <summary>Publishes a project-authored animation-set source with its runtime content type.</summary>
public sealed class AnimationSetAssetImporter : IAssetImporter
{
    /// <inheritdoc/>
    public string Id => "animation-set";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public AssetImportResult Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return CollisionMeshAssetImporter.Copy(
            context, "animation-set.nanimset", "nico/animation-set");
    }
}
