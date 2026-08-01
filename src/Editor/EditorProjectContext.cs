namespace Editor;

/// <summary>
/// Identifies the game project currently opened by the editor.
/// </summary>
public sealed class EditorProjectContext
{
    /// <summary>Gets the normalized absolute path to the game project root.</summary>
    public string RootPath { get; }

    /// <summary>Gets the path of the project's primary scene file.</summary>
    public string ScenePath => Path.Combine(RootPath, "scene.json");

    /// <summary>
    /// Finds scene files that can be opened from this game project.
    /// </summary>
    /// <returns>Absolute scene paths ordered by project-relative path.</returns>
    public IReadOnlyList<string> FindSceneFiles()
    {
        var paths = Directory.EnumerateFiles(RootPath, "*.scene.json", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .ToList();
        if (File.Exists(ScenePath) && !paths.Contains(ScenePath, StringComparer.Ordinal))
            paths.Add(ScenePath);
        paths.Sort((left, right) => string.Compare(
            Path.GetRelativePath(RootPath, left),
            Path.GetRelativePath(RootPath, right),
            StringComparison.OrdinalIgnoreCase));
        return paths;
    }

    /// <summary>
    /// Opens an editor project context rooted at an existing directory.
    /// </summary>
    /// <param name="rootPath">Game project root path, absolute or relative to the current directory.</param>
    /// <returns>A context containing the normalized absolute project root.</returns>
    /// <exception cref="ArgumentException">Thrown when the path is empty.</exception>
    /// <exception cref="DirectoryNotFoundException">Thrown when the directory does not exist.</exception>
    public static EditorProjectContext Open(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        var normalizedPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(normalizedPath))
            throw new DirectoryNotFoundException($"Game project root does not exist: {normalizedPath}");

        return new EditorProjectContext(normalizedPath);
    }

    /// <summary>
    /// Creates a project context with a validated root path.
    /// </summary>
    /// <param name="rootPath">Normalized absolute game project root.</param>
    private EditorProjectContext(string rootPath)
    {
        RootPath = rootPath;
    }
}
