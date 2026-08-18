using System.Text.Json;
using Editor;
using Xunit;

namespace Editor.Tests;

public sealed class EditorSceneSessionTests
{
    /// <summary>Verifies a project without persisted selection starts untitled.</summary>
    [Fact]
    public void Load_MissingSession_ReturnsNull()
    {
        var projectRoot = CreateTemporaryProject();
        try
        {
            var scenePath = EditorSceneSession.Load(projectRoot, out var error);

            Assert.Null(scenePath);
            Assert.Null(error);
        }
        finally
        {
            Directory.Delete(projectRoot, true);
        }
    }

    /// <summary>Verifies the selected scene is stored relatively and restored.</summary>
    [Fact]
    public void SaveAndLoad_ProjectScene_RoundTripsSelection()
    {
        var projectRoot = CreateTemporaryProject();
        var scenesDirectory = Path.Combine(projectRoot, "Scenes");
        var scenePath = Path.Combine(scenesDirectory, "Main.node");
        Directory.CreateDirectory(scenesDirectory);
        File.WriteAllText(scenePath, "{}");
        try
        {
            EditorSceneSession.Save(projectRoot, scenePath);

            var restoredPath = EditorSceneSession.Load(projectRoot, out var error);
            using var json = JsonDocument.Parse(
                File.ReadAllText(EditorSceneSession.GetStoragePath(projectRoot)));
            var persistedPath = json.RootElement.GetProperty("lastScene").GetString();

            Assert.Equal(scenePath, restoredPath);
            Assert.Null(error);
            Assert.Equal(Path.Combine("Scenes", "Main.node"), persistedPath);
        }
        finally
        {
            Directory.Delete(projectRoot, true);
        }
    }

    /// <summary>Verifies a removed last scene falls back to an untitled scene.</summary>
    [Fact]
    public void Load_RemovedScene_ReturnsNull()
    {
        var projectRoot = CreateTemporaryProject();
        var scenePath = Path.Combine(projectRoot, "Previous.node");
        File.WriteAllText(scenePath, "{}");
        try
        {
            EditorSceneSession.Save(projectRoot, scenePath);
            File.Delete(scenePath);

            var restoredPath = EditorSceneSession.Load(projectRoot, out var error);

            Assert.Null(restoredPath);
            Assert.Null(error);
        }
        finally
        {
            Directory.Delete(projectRoot, true);
        }
    }

    /// <summary>Verifies clearing the selected scene persists the untitled state.</summary>
    [Fact]
    public void Save_NullScene_ClearsSelection()
    {
        var projectRoot = CreateTemporaryProject();
        var scenePath = Path.Combine(projectRoot, "Previous.node");
        File.WriteAllText(scenePath, "{}");
        try
        {
            EditorSceneSession.Save(projectRoot, scenePath);
            EditorSceneSession.Save(projectRoot, null);

            var restoredPath = EditorSceneSession.Load(projectRoot, out var error);

            Assert.Null(restoredPath);
            Assert.Null(error);
        }
        finally
        {
            Directory.Delete(projectRoot, true);
        }
    }

    /// <summary>Verifies persisted paths cannot restore scenes outside the project.</summary>
    [Fact]
    public void Load_OutsideProjectScene_ReturnsNull()
    {
        var projectRoot = CreateTemporaryProject();
        var outsideScene = Path.Combine(Path.GetDirectoryName(projectRoot)!, "Outside.node");
        File.WriteAllText(outsideScene, "{}");
        try
        {
            var storagePath = EditorSceneSession.GetStoragePath(projectRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(storagePath)!);
            File.WriteAllText(storagePath, JsonSerializer.Serialize(new
            {
                lastScene = Path.GetRelativePath(projectRoot, outsideScene)
            }));

            var restoredPath = EditorSceneSession.Load(projectRoot, out var error);

            Assert.Null(restoredPath);
            Assert.Null(error);
        }
        finally
        {
            Directory.Delete(projectRoot, true);
            File.Delete(outsideScene);
        }
    }

    /// <summary>Creates an isolated game-project directory for one test.</summary>
    /// <returns>Absolute temporary project path.</returns>
    private static string CreateTemporaryProject()
    {
        var path = Path.Combine(Path.GetTempPath(), $"editor-scene-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
