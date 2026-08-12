using System.Text.Json;
using Engine.Core;
using Xunit;

namespace Engine.Assets.Tests;

/// <summary>Exercises standalone image publication into runtime texture artifacts.</summary>
public sealed class ImageTextureAssetImporterTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("nico-image-texture-").FullName;

    /// <summary>Decodes a minimal PNG into one loadable RGBA texture artifact.</summary>
    [Fact]
    public void Import_Png_PublishesMainTexture()
    {
        var sourcePath = Path.Combine(_directory, "pixel.png");
        File.WriteAllBytes(sourcePath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAF/gL+Av7xWQAAAABJRU5ErkJggg=="));
        var staging = Path.Combine(_directory, "staging");
        var metadata = new AssetMetadata(1, AssetId.New(), "image-texture",
            JsonDocument.Parse("{}").RootElement.Clone());
        var context = new AssetImportContext(sourcePath, metadata, "editor", staging,
            CancellationToken.None);

        var result = new ImageTextureAssetImporter().Import(context);

        var artifact = Assert.Single(result.Artifacts);
        Assert.Equal("main", artifact.Key);
        Assert.Equal("nico/texture2d", artifact.ContentType);
        using var stream = File.OpenRead(Path.Combine(staging, artifact.RelativePath));
        using var reader = new BinaryReader(stream);
        Assert.Equal("NTEX0001", System.Text.Encoding.ASCII.GetString(reader.ReadBytes(8)));
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32());
        Assert.Equal(1u, reader.ReadUInt32());
    }

    /// <summary>Deletes generated test files.</summary>
    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
