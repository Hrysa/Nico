using Engine.Core;

namespace Engine.Assets;

/// <summary>Identifies one source asset discovered during metadata scanning.</summary>
/// <param name="Id">Persistent asset identity.</param>
/// <param name="ProjectPath">Normalized project-relative source path.</param>
/// <param name="Importer">Stable importer identifier.</param>
public sealed record AssetMetadataRecord(AssetId Id, string ProjectPath, string Importer);

/// <summary>Describes one recoverable metadata scan problem.</summary>
/// <param name="Path">Normalized project-relative source or sidecar path.</param>
/// <param name="Message">Actionable diagnostic message.</param>
public sealed record AssetMetadataDiagnostic(string Path, string Message);

/// <summary>Contains the complete result of one project metadata scan.</summary>
/// <param name="Assets">Assets with valid metadata.</param>
/// <param name="Diagnostics">Recoverable scan problems.</param>
public sealed record AssetMetadataScanResult(
    IReadOnlyList<AssetMetadataRecord> Assets,
    IReadOnlyList<AssetMetadataDiagnostic> Diagnostics);

/// <summary>Creates and validates metadata sidecars for supported project assets.</summary>
public static class AssetMetadataScanner
{
    private static readonly HashSet<string> _excludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".nico", "bin", "obj"
    };

    /// <summary>Scans a project and creates missing metadata for supported source files.</summary>
    /// <param name="projectRoot">Absolute or relative project root.</param>
    /// <param name="selectImporter">Returns an importer ID for supported files, otherwise null.</param>
    /// <returns>Valid asset records and recoverable diagnostics in deterministic path order.</returns>
    public static AssetMetadataScanResult Scan(
        string projectRoot,
        Func<string, string?> selectImporter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(selectImporter);
        var root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Asset project root does not exist: {root}");

        var diagnostics = new List<AssetMetadataDiagnostic>();
        var assets = new List<AssetMetadataRecord>();
        var indexEntries = new List<AssetIndexEntry>();
        var ids = new Dictionary<AssetId, string>();
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var cachedByPath = AssetIndexCache.Load(root)
            .ToDictionary(entry => entry.ProjectPath, entry => entry, pathComparer);
        var files = EnumerateProjectFiles(root).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var sourcePaths = new HashSet<string>(files.Where(path => !IsSidecar(path)),
            pathComparer);

        foreach (var sidecarPath in files.Where(IsSidecar))
        {
            var sourcePath = sidecarPath[..^".meta".Length];
            if (!sourcePaths.Contains(sourcePath))
            {
                diagnostics.Add(new AssetMetadataDiagnostic(NormalizeRelative(root, sidecarPath),
                    "Metadata sidecar has no source asset."));
            }
        }

        foreach (var sourcePath in sourcePaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            var importer = selectImporter(sourcePath);
            if (string.IsNullOrWhiteSpace(importer))
                continue;
            var relativePath = NormalizeRelative(root, sourcePath);
            var sidecarPath = AssetMetadataStore.GetSidecarPath(sourcePath);
            AssetMetadata? metadata = null;
            AssetId id;
            string resolvedImporter;
            try
            {
                if (File.Exists(sidecarPath) && cachedByPath.TryGetValue(relativePath, out var cached)
                    && string.Equals(cached.Importer, importer, StringComparison.Ordinal)
                    && Matches(cached, sourcePath, sidecarPath))
                {
                    id = cached.Id;
                    resolvedImporter = cached.Importer;
                }
                else if (File.Exists(sidecarPath))
                {
                    metadata = AssetMetadataStore.Load(sourcePath);
                    id = metadata.Id;
                    resolvedImporter = metadata.Importer;
                }
                else
                {
                    metadata = AssetMetadataStore.Create(importer);
                    AssetMetadataStore.Save(sourcePath, metadata);
                    id = metadata.Id;
                    resolvedImporter = metadata.Importer;
                }
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException
                or UnauthorizedAccessException)
            {
                diagnostics.Add(new AssetMetadataDiagnostic(NormalizeRelative(root, sourcePath),
                    exception.Message));
                continue;
            }

            if (ids.TryGetValue(id, out var originalPath))
            {
                metadata ??= AssetMetadataStore.Load(sourcePath);
                metadata = metadata with { Id = AssetId.New() };
                AssetMetadataStore.Save(sourcePath, metadata);
                id = metadata.Id;
                resolvedImporter = metadata.Importer;
                diagnostics.Add(new AssetMetadataDiagnostic(relativePath,
                    $"Duplicate asset ID also used by '{originalPath}' was replaced."));
            }
            ids.Add(id, relativePath);
            assets.Add(new AssetMetadataRecord(id, relativePath, resolvedImporter));
            indexEntries.Add(CreateIndexEntry(id, relativePath, resolvedImporter,
                sourcePath, sidecarPath));
        }

        AssetIndexCache.Save(root, indexEntries);
        return new AssetMetadataScanResult(assets, diagnostics);
    }

    /// <summary>Returns whether source and metadata filesystem stamps match a cached entry.</summary>
    /// <param name="entry">Cached asset index entry.</param>
    /// <param name="sourcePath">Absolute source path.</param>
    /// <param name="sidecarPath">Absolute metadata path.</param>
    /// <returns>True when both files are unchanged by length and UTC write timestamp.</returns>
    private static bool Matches(AssetIndexEntry entry, string sourcePath, string sidecarPath)
    {
        var source = new FileInfo(sourcePath);
        var metadata = new FileInfo(sidecarPath);
        return source.Length == entry.SourceLength &&
               source.LastWriteTimeUtc.Ticks == entry.SourceWriteTicks &&
               metadata.Length == entry.MetadataLength &&
               metadata.LastWriteTimeUtc.Ticks == entry.MetadataWriteTicks;
    }

    /// <summary>Creates one binary cache entry from validated current files.</summary>
    /// <param name="id">Persistent asset identity.</param>
    /// <param name="projectPath">Normalized project-relative path.</param>
    /// <param name="importer">Stable importer identifier.</param>
    /// <param name="sourcePath">Absolute source path.</param>
    /// <param name="sidecarPath">Absolute metadata path.</param>
    /// <returns>The current cache entry.</returns>
    private static AssetIndexEntry CreateIndexEntry(
        AssetId id,
        string projectPath,
        string importer,
        string sourcePath,
        string sidecarPath)
    {
        var source = new FileInfo(sourcePath);
        var metadata = new FileInfo(sidecarPath);
        return new AssetIndexEntry(id, projectPath, importer,
            source.Length, source.LastWriteTimeUtc.Ticks,
            metadata.Length, metadata.LastWriteTimeUtc.Ticks);
    }

    /// <summary>Enumerates project files while skipping generated and version-control directories.</summary>
    /// <param name="root">Normalized project root.</param>
    /// <returns>Project file paths.</returns>
    private static IEnumerable<string> EnumerateProjectFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                if (!_excludedDirectoryNames.Contains(Path.GetFileName(childDirectory)))
                    pending.Push(childDirectory);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
                yield return Path.GetFullPath(file);
        }
    }

    /// <summary>Returns whether a path identifies an engine metadata sidecar.</summary>
    /// <param name="path">File path to classify.</param>
    /// <returns>True when the file has the <c>.meta</c> suffix.</returns>
    private static bool IsSidecar(string path)
    {
        return path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates a slash-normalized path relative to the project root.</summary>
    /// <param name="root">Normalized project root.</param>
    /// <param name="path">Project-contained path.</param>
    /// <returns>Normalized project-relative path.</returns>
    private static string NormalizeRelative(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    }
}
