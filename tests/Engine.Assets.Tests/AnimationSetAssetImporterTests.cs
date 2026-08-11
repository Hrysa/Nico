using System.Text.Json;
using Engine.Core;
using Engine.Graphics;
using Xunit;

namespace Engine.Assets.Tests;

public sealed class AnimationSetAssetImporterTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("nico-animation-set-import-").FullName;

    /// <summary>Publishes a generated source under the typed runtime content contract.</summary>
    [Fact]
    public void Import_ValidSource_PublishesLoadableMainArtifact()
    {
        var sourcePath = Path.Combine(_directory, "character.nanimset");
        var source = new AssetReference(AssetId.New(), "animation/Idle");
        using (var stream = File.Create(sourcePath))
            new AnimationSetResource([new AnimationSetEntry("Idle", source)]).Save(stream);
        var staging = Path.Combine(_directory, "staging");
        var metadata = new AssetMetadata(1, AssetId.New(), "animation-set",
            JsonDocument.Parse("{}").RootElement.Clone());
        var context = new AssetImportContext(sourcePath, metadata, "editor", staging,
            CancellationToken.None);

        var result = new AnimationSetAssetImporter().Import(context);

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal("main", artifact.Key);
        Assert.Equal("nico/animation-set", artifact.ContentType);
        using var published = File.OpenRead(Path.Combine(staging, artifact.RelativePath));
        Assert.Equal("Idle", Assert.Single(AnimationSetResource.Load(published).Entries).Alias);
    }

    /// <summary>Removes temporary importer artifacts.</summary>
    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
