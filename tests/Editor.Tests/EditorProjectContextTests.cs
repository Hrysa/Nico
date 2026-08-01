using Editor;
using Xunit;

namespace Editor.Tests;

public class EditorProjectContextTests
{
    /// <summary>Verifies relative project roots are exposed as normalized absolute paths.</summary>
    [Fact]
    public void Open_ExistingDirectory_NormalizesRootPath()
    {
        var context = EditorProjectContext.Open(".");

        Assert.Equal(Path.GetFullPath("."), context.RootPath);
    }

    /// <summary>Verifies a missing game project root is rejected before editor initialization.</summary>
    [Fact]
    public void Open_MissingDirectory_ThrowsDirectoryNotFoundException()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing-game-project-{Guid.NewGuid():N}");

        Assert.Throws<DirectoryNotFoundException>(() => EditorProjectContext.Open(missingPath));
    }

    /// <summary>Verifies scene discovery includes the primary scene and nested named scenes.</summary>
    [Fact]
    public void FindSceneFiles_ReturnsProjectScenesInRelativePathOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"editor-project-{Guid.NewGuid():N}");
        var levelsDirectory = Path.Combine(directory, "levels");
        Directory.CreateDirectory(levelsDirectory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "scene.json"), "{}");
            File.WriteAllText(Path.Combine(levelsDirectory, "second.scene.json"), "{}");
            File.WriteAllText(Path.Combine(directory, "settings.json"), "{}");
            var context = EditorProjectContext.Open(directory);

            var scenes = context.FindSceneFiles();

            Assert.Equal(2, scenes.Count);
            Assert.Equal(Path.Combine(levelsDirectory, "second.scene.json"), scenes[0]);
            Assert.Equal(Path.Combine(directory, "scene.json"), scenes[1]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
