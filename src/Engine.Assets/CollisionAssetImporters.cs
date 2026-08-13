using Engine.Core;

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

/// <summary>Publishes a project-authored standard-material source.</summary>
public sealed class StandardMaterialAssetImporter : IAssetImporter
{
    /// <inheritdoc/>
    public string Id => "standard-material";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public AssetImportResult Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        StandardMaterialAsset material;
        using (var source = context.OpenSource())
            material = StandardMaterialAssetCodec.Load(source);
        using (var source = context.OpenSource())
        using (var destination = context.CreateArtifact("material.nmat"))
            source.CopyTo(destination);
        var dependencies = new List<AssetReference>(3);
        if (material.BaseColorTexture is { } baseColorTexture)
            dependencies.Add(baseColorTexture);
        if (material.NormalTexture is { } normalTexture)
            dependencies.Add(normalTexture);
        if (material.MetallicRoughnessTexture is { } metallicRoughnessTexture)
            dependencies.Add(metallicRoughnessTexture);
        return new AssetImportResult(
            [new AssetArtifact("main", "nico/standard-material", "material.nmat")],
            dependencies, []);
    }
}

/// <summary>Publishes a project-authored terrain layer and its texture dependencies.</summary>
public sealed class TerrainLayerAssetImporter : IAssetImporter
{
    /// <inheritdoc/>
    public string Id => "terrain-layer";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public AssetImportResult Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        TerrainLayerAsset layer;
        using (var source = context.OpenSource())
            layer = TerrainMaterialAssetCodec.LoadLayer(source);
        using (var source = context.OpenSource())
        using (var destination = context.CreateArtifact("terrain-layer.ntlayer"))
            source.CopyTo(destination);
        var dependencies = new List<AssetReference>(3);
        if (layer.BaseColorTexture is { } baseColorTexture)
            dependencies.Add(baseColorTexture);
        if (layer.NormalTexture is { } normalTexture)
            dependencies.Add(normalTexture);
        if (layer.MetallicRoughnessTexture is { } metallicRoughnessTexture)
            dependencies.Add(metallicRoughnessTexture);
        return new AssetImportResult(
            [new AssetArtifact("main", "nico/terrain-layer", "terrain-layer.ntlayer")],
            dependencies, []);
    }
}

/// <summary>Publishes a project-authored painted terrain material.</summary>
public sealed class TerrainMaterialAssetImporter : IAssetImporter
{
    /// <inheritdoc/>
    public string Id => "terrain-material";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public AssetImportResult Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        TerrainMaterialAsset material;
        using (var source = context.OpenSource())
            material = TerrainMaterialAssetCodec.LoadMaterial(source);
        using (var source = context.OpenSource())
        using (var destination = context.CreateArtifact("terrain-material.ntmat"))
            source.CopyTo(destination);
        return new AssetImportResult(
            [new AssetArtifact("main", "nico/terrain-material", "terrain-material.ntmat")],
            material.Layers.ToArray(), []);
    }
}
