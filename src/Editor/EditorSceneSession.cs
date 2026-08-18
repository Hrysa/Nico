using System.Text.Json;

namespace Editor;

/// <summary>Persists the last active scene for one Editor project.</summary>
public static class EditorSceneSession
{
    private const string SettingsDirectory = ".nico";
    private const string SessionFileName = "editor-session.json";

    /// <summary>Gets the project-scoped Editor scene-session persistence path.</summary>
    /// <param name="projectRoot">Absolute or relative game-project root.</param>
    /// <returns>Normalized session JSON path.</returns>
    public static string GetStoragePath(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return Path.Combine(Path.GetFullPath(projectRoot), SettingsDirectory, SessionFileName);
    }

    /// <summary>Restores the last active scene when it remains a valid project scene.</summary>
    /// <param name="projectRoot">Game-project root.</param>
    /// <param name="error">Restoration error when persisted data could not be read.</param>
    /// <returns>The normalized scene path, or null when the Editor should start untitled.</returns>
    public static string? Load(string projectRoot, out Exception? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        try
        {
            var storagePath = GetStoragePath(projectRoot);
            if (!File.Exists(storagePath))
            {
                error = null;
                return null;
            }

            var state = JsonSerializer.Deserialize<SceneSessionState>(
                File.ReadAllText(storagePath), SerializerOptions);
            if (string.IsNullOrWhiteSpace(state?.LastScene))
            {
                error = null;
                return null;
            }

            var root = Path.GetFullPath(projectRoot);
            var scenePath = Path.GetFullPath(Path.Combine(root, state.LastScene));
            if (!IsProjectScene(root, scenePath) || !File.Exists(scenePath))
            {
                error = null;
                return null;
            }

            error = null;
            return scenePath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            JsonException or NotSupportedException or ArgumentException)
        {
            error = exception;
            return null;
        }
    }

    /// <summary>Persists the active project scene or clears the previous selection.</summary>
    /// <param name="projectRoot">Game-project root.</param>
    /// <param name="scenePath">Active scene path, or null for an untitled scene.</param>
    public static void Save(string projectRoot, string? scenePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.GetFullPath(projectRoot);
        string? relativePath = null;
        if (scenePath is not null)
        {
            var fullScenePath = Path.GetFullPath(scenePath);
            if (!IsProjectScene(root, fullScenePath))
                throw new ArgumentException("Scene path must be a .node file inside the project.",
                    nameof(scenePath));
            relativePath = Path.GetRelativePath(root, fullScenePath);
        }

        var storagePath = GetStoragePath(root);
        var directory = Path.GetDirectoryName(storagePath)
            ?? throw new ArgumentException("Session path has no parent directory.",
                nameof(projectRoot));
        Directory.CreateDirectory(directory);
        var temporaryPath = storagePath + ".tmp";
        var state = new SceneSessionState { LastScene = relativePath };
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, SerializerOptions));
        File.Move(temporaryPath, storagePath, overwrite: true);
    }

    /// <summary>Checks whether a path identifies a node scene within the project root.</summary>
    /// <param name="projectRoot">Normalized project root.</param>
    /// <param name="scenePath">Normalized candidate scene path.</param>
    /// <returns>True when the candidate is a project-contained .node file.</returns>
    private static bool IsProjectScene(string projectRoot, string scenePath)
    {
        if (!scenePath.EndsWith(".node", StringComparison.OrdinalIgnoreCase))
            return false;
        var relativePath = Path.GetRelativePath(projectRoot, scenePath);
        return !Path.IsPathRooted(relativePath) && relativePath != ".." &&
            !relativePath.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) &&
            !relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar,
                StringComparison.Ordinal);
    }

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Serializable project-scoped scene selection.</summary>
    private sealed class SceneSessionState
    {
        /// <summary>Gets the project-relative active scene path.</summary>
        public string? LastScene { get; init; }
    }
}
