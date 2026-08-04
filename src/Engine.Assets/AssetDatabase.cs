using Engine.Core;

namespace Engine.Assets;

/// <summary>Identifies how one persistent asset record changed during a database refresh.</summary>
public enum AssetChangeKind
{
    /// <summary>A new persistent asset was discovered.</summary>
    Added,

    /// <summary>A known persistent asset disappeared.</summary>
    Removed,

    /// <summary>A known persistent asset moved to another project path.</summary>
    Moved,

    /// <summary>A known persistent asset changed importer metadata.</summary>
    Changed
}

/// <summary>Describes one difference between two asset database snapshots.</summary>
/// <param name="Kind">Kind of database change.</param>
/// <param name="Previous">Previous asset record when one existed.</param>
/// <param name="Current">Current asset record when one exists.</param>
public sealed record AssetChange(
    AssetChangeKind Kind,
    AssetMetadataRecord? Previous,
    AssetMetadataRecord? Current);

/// <summary>Describes one recoverable asset deletion moved into project trash.</summary>
/// <param name="Asset">Removed persistent asset identity.</param>
/// <param name="OriginalPath">Previous normalized project-relative source path.</param>
/// <param name="TrashDirectory">Absolute recovery directory containing source and metadata.</param>
public sealed record AssetDeletion(
    AssetId Asset,
    string OriginalPath,
    string TrashDirectory);

/// <summary>Indexes persistent project assets by UUIDv7 identity and source path.</summary>
public sealed class AssetDatabase
{
    private readonly Func<string, string?> _selectImporter;
    private readonly StringComparer _pathComparer;
    private Dictionary<AssetId, AssetMetadataRecord> _byId = new();
    private Dictionary<string, AssetMetadataRecord> _byPath;
    private AssetMetadataDiagnostic[] _diagnostics = [];

    /// <summary>Gets the normalized absolute project root.</summary>
    public string ProjectRoot { get; }

    /// <summary>Gets the current asset records in deterministic path order.</summary>
    public IReadOnlyList<AssetMetadataRecord> Assets => _byPath.Values
        .OrderBy(record => record.ProjectPath, StringComparer.Ordinal).ToArray();

    /// <summary>Gets diagnostics produced by the most recent refresh.</summary>
    public IReadOnlyList<AssetMetadataDiagnostic> Diagnostics => _diagnostics;

    /// <summary>Occurs after a refresh changes one or more indexed asset records.</summary>
    public event Action<IReadOnlyList<AssetChange>>? Changed;

    /// <summary>Creates and performs the initial project asset scan.</summary>
    /// <param name="projectRoot">Project root containing source assets.</param>
    /// <param name="selectImporter">Returns an importer ID for supported files, otherwise null.</param>
    public AssetDatabase(string projectRoot, Func<string, string?> selectImporter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(selectImporter);
        ProjectRoot = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(ProjectRoot))
            throw new DirectoryNotFoundException($"Asset project root does not exist: {ProjectRoot}");
        _selectImporter = selectImporter;
        _pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        _byPath = new Dictionary<string, AssetMetadataRecord>(_pathComparer);
        Refresh();
    }

    /// <summary>Finds an asset by persistent identity.</summary>
    /// <param name="id">Persistent asset identity.</param>
    /// <returns>The current record, or null when no asset uses the ID.</returns>
    public AssetMetadataRecord? Find(AssetId id)
    {
        return _byId.GetValueOrDefault(id);
    }

    /// <summary>Finds an asset by absolute or project-relative source path.</summary>
    /// <param name="path">Source path beneath the project root.</param>
    /// <returns>The current record, or null when the path is not an indexed asset.</returns>
    public AssetMetadataRecord? FindByPath(string path)
    {
        return _byPath.GetValueOrDefault(NormalizeProjectPath(path));
    }

    /// <summary>Rescans metadata and publishes differences from the previous snapshot.</summary>
    /// <returns>The ordered changes published by this refresh.</returns>
    public IReadOnlyList<AssetChange> Refresh()
    {
        var scan = AssetMetadataScanner.Scan(ProjectRoot, _selectImporter);
        var nextById = scan.Assets.ToDictionary(record => record.Id);
        var nextByPath = scan.Assets.ToDictionary(
            record => record.ProjectPath, record => record, _pathComparer);
        var changes = Compare(_byId, nextById);
        _byId = nextById;
        _byPath = nextByPath;
        _diagnostics = scan.Diagnostics.ToArray();
        if (changes.Count > 0)
            Changed?.Invoke(changes);
        return changes;
    }

    /// <summary>Moves or renames an asset and its sidecar while preserving identity.</summary>
    /// <param name="id">Persistent asset identity to move.</param>
    /// <param name="destinationPath">New absolute or project-relative source path.</param>
    /// <returns>The refreshed asset record at its destination.</returns>
    public AssetMetadataRecord MoveAsset(AssetId id, string destinationPath)
    {
        var record = Require(id);
        var sourcePath = ResolveProjectPath(record.ProjectPath);
        var destination = ResolveProjectPath(destinationPath);
        EnsureCompatibleDestination(record, destination);
        if (_pathComparer.Equals(record.ProjectPath, NormalizeProjectPath(destination)))
            return record;
        EnsureDestinationAvailable(destination);
        var sourceSidecar = AssetMetadataStore.GetSidecarPath(sourcePath);
        var destinationSidecar = AssetMetadataStore.GetSidecarPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(sourcePath, destination);
        try
        {
            File.Move(sourceSidecar, destinationSidecar);
        }
        catch
        {
            File.Move(destination, sourcePath);
            throw;
        }
        Refresh();
        return Find(id) ?? throw new InvalidOperationException(
            "The moved asset was not present after metadata refresh.");
    }

    /// <summary>Copies an asset while assigning the copy a new persistent identity.</summary>
    /// <param name="id">Persistent source asset identity.</param>
    /// <param name="destinationPath">New absolute or project-relative copy path.</param>
    /// <returns>The refreshed record for the copied asset.</returns>
    public AssetMetadataRecord DuplicateAsset(AssetId id, string destinationPath)
    {
        var record = Require(id);
        var sourcePath = ResolveProjectPath(record.ProjectPath);
        var destination = ResolveProjectPath(destinationPath);
        EnsureCompatibleDestination(record, destination);
        EnsureDestinationAvailable(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(sourcePath, destination);
        try
        {
            var original = AssetMetadataStore.Load(sourcePath);
            var duplicate = original with { Id = AssetId.New() };
            AssetMetadataStore.Save(destination, duplicate);
        }
        catch
        {
            if (File.Exists(destination))
                File.Delete(destination);
            var destinationSidecar = AssetMetadataStore.GetSidecarPath(destination);
            if (File.Exists(destinationSidecar))
                File.Delete(destinationSidecar);
            throw;
        }
        Refresh();
        return FindByPath(destination) ?? throw new InvalidOperationException(
            "The duplicated asset was not present after metadata refresh.");
    }

    /// <summary>Moves an asset and sidecar into recoverable project-local trash.</summary>
    /// <param name="id">Persistent asset identity to remove from the database.</param>
    /// <returns>Recovery information for the trashed source and sidecar.</returns>
    public AssetDeletion DeleteAsset(AssetId id)
    {
        var record = Require(id);
        var sourcePath = ResolveProjectPath(record.ProjectPath);
        var sourceSidecar = AssetMetadataStore.GetSidecarPath(sourcePath);
        var trashDirectory = Path.Combine(ProjectRoot, ".nico", "trash", id.ToString(),
            DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ",
                System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(trashDirectory);
        var trashSource = Path.Combine(trashDirectory, Path.GetFileName(sourcePath));
        var trashSidecar = Path.Combine(trashDirectory, Path.GetFileName(sourceSidecar));
        File.Move(sourcePath, trashSource);
        try
        {
            File.Move(sourceSidecar, trashSidecar);
        }
        catch
        {
            File.Move(trashSource, sourcePath);
            throw;
        }
        Refresh();
        return new AssetDeletion(id, record.ProjectPath, trashDirectory);
    }

    /// <summary>Normalizes and validates an absolute or project-relative source path.</summary>
    /// <param name="path">Path to normalize beneath the project root.</param>
    /// <returns>A slash-normalized project-relative path.</returns>
    public string NormalizeProjectPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(Path.IsPathRooted(path)
            ? path : Path.Combine(ProjectRoot, path));
        var relative = Path.GetRelativePath(ProjectRoot, fullPath);
        if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new ArgumentException("Asset paths must remain beneath the project root.",
                nameof(path));
        }
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Returns a required current asset record.</summary>
    /// <param name="id">Persistent asset identity.</param>
    /// <returns>The current indexed record.</returns>
    private AssetMetadataRecord Require(AssetId id)
    {
        return Find(id) ?? throw new KeyNotFoundException($"Asset '{id}' was not found.");
    }

    /// <summary>Resolves a validated project path to its absolute physical location.</summary>
    /// <param name="path">Absolute or project-relative path.</param>
    /// <returns>The normalized absolute path beneath the project root.</returns>
    private string ResolveProjectPath(string path)
    {
        var relative = NormalizeProjectPath(path);
        return Path.GetFullPath(Path.Combine(ProjectRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>Validates that a destination retains the asset's importer classification.</summary>
    /// <param name="record">Asset being moved or copied.</param>
    /// <param name="destination">Absolute destination source path.</param>
    private void EnsureCompatibleDestination(AssetMetadataRecord record, string destination)
    {
        var importer = _selectImporter(destination);
        if (!string.Equals(importer, record.Importer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Destination must remain compatible with importer '{record.Importer}'.");
        }
    }

    /// <summary>Rejects occupied source or sidecar destinations.</summary>
    /// <param name="destination">Absolute destination source path.</param>
    private static void EnsureDestinationAvailable(string destination)
    {
        var sidecar = AssetMetadataStore.GetSidecarPath(destination);
        if (File.Exists(destination) || Directory.Exists(destination) ||
            File.Exists(sidecar) || Directory.Exists(sidecar))
        {
            throw new IOException($"Asset destination already exists: {destination}");
        }
    }

    /// <summary>Compares indexed snapshots by persistent identity.</summary>
    /// <param name="previous">Previous records indexed by ID.</param>
    /// <param name="current">Current records indexed by ID.</param>
    /// <returns>Deterministically ordered snapshot changes.</returns>
    private static IReadOnlyList<AssetChange> Compare(
        IReadOnlyDictionary<AssetId, AssetMetadataRecord> previous,
        IReadOnlyDictionary<AssetId, AssetMetadataRecord> current)
    {
        var changes = new List<AssetChange>();
        foreach (var oldRecord in previous.Values)
        {
            if (!current.TryGetValue(oldRecord.Id, out var newRecord))
                changes.Add(new AssetChange(AssetChangeKind.Removed, oldRecord, null));
            else if (!string.Equals(oldRecord.ProjectPath, newRecord.ProjectPath,
                         StringComparison.Ordinal))
                changes.Add(new AssetChange(AssetChangeKind.Moved, oldRecord, newRecord));
            else if (!string.Equals(oldRecord.Importer, newRecord.Importer,
                         StringComparison.Ordinal))
                changes.Add(new AssetChange(AssetChangeKind.Changed, oldRecord, newRecord));
        }
        foreach (var newRecord in current.Values)
        {
            if (!previous.ContainsKey(newRecord.Id))
                changes.Add(new AssetChange(AssetChangeKind.Added, null, newRecord));
        }
        return changes.OrderBy(change => change.Current?.ProjectPath ?? change.Previous!.ProjectPath,
            StringComparer.Ordinal).ToArray();
    }
}
