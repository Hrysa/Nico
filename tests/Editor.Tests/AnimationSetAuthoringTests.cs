using Editor;
using Engine.Core;
using Engine.Graphics;
using Xunit;

namespace Editor.Tests;

public sealed class AnimationSetAuthoringTests
{
    /// <summary>Writes a source artifact accepted by the shared runtime decoder.</summary>
    [Fact]
    public void Save_ValidEntries_WritesLoadableSource()
    {
        var directory = Directory.CreateTempSubdirectory("nico-animation-set-");
        try
        {
            var path = Path.Combine(directory.FullName, "character.nanimset");
            var source = new AssetReference(AssetId.New(), "animation/Run");

            AnimationSetAuthoring.Save(path,
                [new AnimationSetEntry("Run", source, "Sprint")]);

            Assert.StartsWith("{", File.ReadAllText(path).TrimStart(), StringComparison.Ordinal);
            using var stream = File.OpenRead(path);
            var loaded = AnimationSetResource.Load(stream);
            var entry = Assert.Single(loaded.Entries);
            Assert.Equal("Run", entry.Alias);
            Assert.Equal(source, entry.Source);
            Assert.Null(entry.Clip);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
