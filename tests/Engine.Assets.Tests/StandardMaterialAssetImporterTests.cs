using System.Numerics;
using System.Text.Json;
using Engine.Core;
using Engine.Graphics;
using Xunit;

namespace Engine.Assets.Tests;

public sealed class StandardMaterialAssetImporterTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("nico-material-import-").FullName;

    /// <summary>Publishes a standalone material under the typed runtime contract.</summary>
    [Fact]
    public void Import_ValidSource_PublishesLoadableMainArtifact()
    {
        var sourcePath = Path.Combine(_directory, "ground.nmat");
        using (var stream = File.Create(sourcePath))
            StandardMaterialAssetCodec.Save(stream,
                new StandardMaterialAsset { BaseColor = new Vector4(0.3f, 0.4f, 0.5f, 1f) });
        var staging = Path.Combine(_directory, "staging");
        var metadata = new AssetMetadata(1, AssetId.New(), "standard-material",
            JsonDocument.Parse("{}").RootElement.Clone());
        var context = new AssetImportContext(sourcePath, metadata, "editor", staging,
            CancellationToken.None);

        var result = new StandardMaterialAssetImporter().Import(context);

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal("main", artifact.Key);
        Assert.Equal("nico/standard-material", artifact.ContentType);
        using var published = File.OpenRead(Path.Combine(staging, artifact.RelativePath));
        var material = StandardMaterialAssetCodec.Load(published);
        Assert.Equal(new Vector4(0.3f, 0.4f, 0.5f, 1f), material.BaseColor);
        Assert.Null(material.BaseColorTexture);
    }

    /// <summary>Publishes referenced textures as import dependencies for invalidation ordering.</summary>
    [Fact]
    public void Import_TexturedSource_ReportsTextureDependency()
    {
        var sourcePath = Path.Combine(_directory, "textured.nmat");
        var baseColorTexture = new AssetReference(AssetId.New(), "base");
        var normalTexture = new AssetReference(AssetId.New(), "normal");
        var metallicRoughnessTexture = new AssetReference(AssetId.New(), "metal-rough");
        using (var stream = File.Create(sourcePath))
            StandardMaterialAssetCodec.Save(stream, new StandardMaterialAsset
            {
                BaseColorTexture = baseColorTexture,
                NormalTexture = normalTexture,
                MetallicRoughnessTexture = metallicRoughnessTexture
            });
        var metadata = new AssetMetadata(1, AssetId.New(), "standard-material",
            JsonDocument.Parse("{}").RootElement.Clone());
        var context = new AssetImportContext(sourcePath, metadata, "editor",
            Path.Combine(_directory, "dependency-staging"), CancellationToken.None);

        var result = new StandardMaterialAssetImporter().Import(context);

        Assert.Equal(
            [baseColorTexture, normalTexture, metallicRoughnessTexture],
            result.Dependencies);
    }

    /// <summary>Removes temporary importer artifacts.</summary>
    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
