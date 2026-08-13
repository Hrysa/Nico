using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Engine.Core;

namespace Engine.Assets;

/// <summary>Reports one cached or newly executed asset import.</summary>
/// <param name="Asset">Imported persistent asset identity.</param>
/// <param name="Fingerprint">Generation fingerprint.</param>
/// <param name="ArtifactDirectory">Published generation directory, or null after failure.</param>
/// <param name="CacheHit">Whether an existing generation was reused.</param>
/// <param name="Succeeded">Whether valid artifacts are available.</param>
/// <param name="Artifacts">Published artifact descriptions.</param>
/// <param name="Dependencies">Declared persistent dependencies.</param>
/// <param name="Diagnostics">Structured import diagnostics.</param>
/// <param name="Objects">Browsable objects discovered inside the source asset.</param>
public sealed record AssetImportOutcome(
    AssetId Asset,
    string Fingerprint,
    string? ArtifactDirectory,
    bool CacheHit,
    bool Succeeded,
    IReadOnlyList<AssetArtifact> Artifacts,
    IReadOnlyList<AssetReference> Dependencies,
    IReadOnlyList<AssetImportDiagnostic> Diagnostics,
    IReadOnlyList<AssetImportObject>? Objects = null);

/// <summary>Describes completed work in one bounded asset-import batch.</summary>
/// <param name="CompletedCount">Number of assets whose imports have completed.</param>
/// <param name="TotalCount">Total number of assets in the batch.</param>
/// <param name="Asset">Asset that most recently completed.</param>
/// <param name="Outcome">Published or failed import result for that asset.</param>
public sealed record AssetImportProgress(
    int CompletedCount,
    int TotalCount,
    AssetMetadataRecord Asset,
    AssetImportOutcome Outcome);

/// <summary>Resolves references through the latest published loose import generation.</summary>
public sealed class PublishedArtifactResolver : IAssetResolver
{
    private readonly AssetDatabase _database;
    private readonly AssetImportPipeline _pipeline;
    private readonly string _target;

    /// <summary>Creates a resolver for one import target.</summary>
    /// <param name="database">Asset identity database.</param>
    /// <param name="pipeline">Published artifact pipeline.</param>
    /// <param name="target">Stable import target.</param>
    public PublishedArtifactResolver(
        AssetDatabase database,
        AssetImportPipeline pipeline,
        string target)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        _database = database;
        _pipeline = pipeline;
        _target = target;
    }

    /// <inheritdoc/>
    public ResolvedAsset Resolve(AssetReference reference)
    {
        var record = _database.Find(reference.Asset)
            ?? throw new FileNotFoundException($"Asset '{reference.Asset}' is missing.");
        var outcome = _pipeline.TryGetLatestPublished(record, _target)
            ?? throw new FileNotFoundException($"Asset '{reference.Asset}' has no published import.");
        var requestedKey = reference.SubAsset ?? "main";
        var artifact = outcome.Artifacts.FirstOrDefault(candidate =>
            candidate.Key == requestedKey)
            ?? throw new FileNotFoundException($"Sub-asset '{reference}' is missing.");
        return new ResolvedAsset(
            new LooseFileAssetLocation(Path.Combine(outcome.ArtifactDirectory!, artifact.RelativePath)),
            artifact.ContentType,
            outcome.Fingerprint);
    }
}

/// <summary>Imports loose project assets on demand before resolving their published artifacts.</summary>
public sealed class ImportingArtifactResolver : IAssetResolver
{
    private readonly AssetDatabase _database;
    private readonly AssetImportPipeline _pipeline;
    private readonly string _target;

    /// <summary>Creates a resolver for a loose project whose sources remain available.</summary>
    /// <param name="database">Asset identity database.</param>
    /// <param name="pipeline">Artifact import pipeline.</param>
    /// <param name="target">Stable import target.</param>
    public ImportingArtifactResolver(
        AssetDatabase database,
        AssetImportPipeline pipeline,
        string target)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        _database = database;
        _pipeline = pipeline;
        _target = target;
    }

    /// <inheritdoc/>
    public ResolvedAsset Resolve(AssetReference reference)
    {
        var record = _database.Find(reference.Asset)
            ?? throw new FileNotFoundException($"Asset '{reference.Asset}' is missing.");
        var outcome = _pipeline.Import(record, _target);
        if (!outcome.Succeeded || outcome.ArtifactDirectory is null)
            throw new InvalidDataException($"Asset '{reference.Asset}' failed to import.");
        var requestedKey = reference.SubAsset ?? "main";
        var artifact = outcome.Artifacts.FirstOrDefault(candidate => candidate.Key == requestedKey)
            ?? throw new FileNotFoundException($"Sub-asset '{reference}' is missing.");
        return new ResolvedAsset(
            new LooseFileAssetLocation(Path.Combine(
                outcome.ArtifactDirectory, artifact.RelativePath)),
            artifact.ContentType,
            outcome.Fingerprint);
    }
}

/// <summary>Executes importers and atomically publishes fingerprinted artifact generations.</summary>
public sealed class AssetImportPipeline
{
    private const string ManifestFileName = "manifest.json";
    private readonly AssetDatabase _database;
    private readonly AssetImporterRegistry _registry;
    private readonly AssetDependencyGraph _dependencyGraph;
    private readonly string _artifactRoot;
    private readonly ConcurrentDictionary<AssetId, object> _assetLocks = new();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// <summary>Creates an import pipeline using the project's generated cache directory.</summary>
    /// <param name="database">Asset database resolving source records.</param>
    /// <param name="registry">Registered importer implementations.</param>
    /// <param name="dependencyGraph">Optional shared dependency graph.</param>
    public AssetImportPipeline(
        AssetDatabase database,
        AssetImporterRegistry registry,
        AssetDependencyGraph? dependencyGraph = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(registry);
        _database = database;
        _registry = registry;
        _dependencyGraph = dependencyGraph ?? new AssetDependencyGraph();
        _artifactRoot = Path.Combine(database.ProjectRoot, ".nico", "cache", "artifacts");
    }

    /// <summary>Imports or reuses the current generation of one asset.</summary>
    /// <param name="record">Current database record.</param>
    /// <param name="target">Stable build target identifier.</param>
    /// <returns>The cached, published, or failed import outcome.</returns>
    public AssetImportOutcome Import(
        AssetMetadataRecord record,
        string target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        cancellationToken.ThrowIfCancellationRequested();
        var assetLock = _assetLocks.GetOrAdd(record.Id, _ => new object());
        lock (assetLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ImportCore(record, target, cancellationToken);
        }
    }

    /// <summary>Finds the most recently published cached generation without reading source bytes.</summary>
    /// <param name="record">Current asset record.</param>
    /// <param name="target">Stable build target identifier.</param>
    /// <returns>The latest matching successful outcome, or null when none has been published.</returns>
    public AssetImportOutcome? TryGetLatestPublished(
        AssetMetadataRecord record,
        string target)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        var assetRoot = Path.Combine(_artifactRoot, record.Id.ToString());
        if (!Directory.Exists(assetRoot))
            return null;
        foreach (var generationPath in Directory.EnumerateDirectories(assetRoot)
                     .Where(path => !Path.GetFileName(path).StartsWith(".staging-",
                         StringComparison.Ordinal))
                     .OrderByDescending(Directory.GetLastWriteTimeUtc))
        {
            var manifestPath = Path.Combine(generationPath, ManifestFileName);
            if (!File.Exists(manifestPath))
                continue;
            using var stream = File.OpenRead(manifestPath);
            var manifest = JsonSerializer.Deserialize<AssetArtifactManifest>(stream, _jsonOptions);
            if (manifest is null || manifest.Asset != record.Id ||
                !string.Equals(manifest.Target, target, StringComparison.Ordinal))
                continue;
            return new AssetImportOutcome(manifest.Asset, manifest.Fingerprint, generationPath,
                true, true, manifest.Artifacts, manifest.Dependencies, manifest.Diagnostics,
                manifest.Objects);
        }
        return null;
    }

    /// <summary>Imports several assets with bounded concurrency and deterministic result order.</summary>
    /// <param name="records">Current asset records to import.</param>
    /// <param name="target">Stable build target identifier.</param>
    /// <param name="maximumConcurrency">Maximum simultaneous importer executions.</param>
    /// <param name="cancellationToken">Cancellation request for pending and active imports.</param>
    /// <param name="progress">Optional thread-safe receiver for completed-asset progress.</param>
    /// <returns>Import outcomes in the same order as the supplied records.</returns>
    public async Task<IReadOnlyList<AssetImportOutcome>> ImportAsync(
        IEnumerable<AssetMetadataRecord> records,
        string target,
        int maximumConcurrency,
        CancellationToken cancellationToken = default,
        IProgress<AssetImportProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        if (maximumConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumConcurrency));
        var ordered = records.ToArray();
        var outcomes = new AssetImportOutcome[ordered.Length];
        var completed = 0;
        using var gate = new SemaphoreSlim(maximumConcurrency, maximumConcurrency);
        var tasks = ordered.Select((record, index) => ImportOneAsync(record, index)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return outcomes;

        /// <summary>Imports one indexed record after entering the shared concurrency gate.</summary>
        async Task ImportOneAsync(AssetMetadataRecord record, int index)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                try
                {
                    outcomes[index] = await Task.Run(
                        () => Import(record, target, cancellationToken), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    outcomes[index] = new AssetImportOutcome(
                        record.Id, string.Empty, null, false, false, [], [],
                        [new AssetImportDiagnostic(
                            AssetDiagnosticSeverity.Error,
                            "BATCH_IMPORT_EXCEPTION",
                            exception.Message)],
                        []);
                }
                var completedCount = Interlocked.Increment(ref completed);
                progress?.Report(new AssetImportProgress(
                    completedCount, ordered.Length, record, outcomes[index]));
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>Executes or reuses one import while holding its per-asset publication lock.</summary>
    /// <param name="record">Current asset record.</param>
    /// <param name="target">Stable build target identifier.</param>
    /// <param name="cancellationToken">Cancellation request.</param>
    /// <returns>The import outcome.</returns>
    private AssetImportOutcome ImportCore(
        AssetMetadataRecord record,
        string target,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.Combine(_database.ProjectRoot,
            record.ProjectPath.Replace('/', Path.DirectorySeparatorChar));
        var metadata = AssetMetadataStore.Load(sourcePath);
        var importer = _registry.Resolve(metadata.Importer);
        var fingerprint = ComputeFingerprint(sourcePath, metadata, importer, target);
        var assetRoot = Path.Combine(_artifactRoot, metadata.Id.ToString());
        var generationPath = Path.Combine(assetRoot, fingerprint);
        var manifestPath = Path.Combine(generationPath, ManifestFileName);
        if (File.Exists(manifestPath))
        {
            var cached = ReadOutcome(manifestPath, generationPath, cacheHit: true);
            _dependencyGraph.Update(cached.Asset, cached.Dependencies);
            return cached;
        }

        Directory.CreateDirectory(assetRoot);
        var stagingPath = Path.Combine(assetRoot, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);
        try
        {
            AssetImportResult result;
            try
            {
                result = importer.Import(new AssetImportContext(
                    sourcePath, metadata, target, stagingPath, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                ValidateResult(result, stagingPath);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new AssetImportOutcome(metadata.Id, fingerprint, null, false, false,
                    [], [],
                    [new AssetImportDiagnostic(AssetDiagnosticSeverity.Error,
                        "IMPORT_EXCEPTION", exception.Message)], []);
            }

            if (result.Diagnostics.Any(diagnostic =>
                    diagnostic.Severity == AssetDiagnosticSeverity.Error))
            {
                return new AssetImportOutcome(metadata.Id, fingerprint, null, false, false,
                    [], result.Dependencies, result.Diagnostics, result.Objects);
            }

            var manifest = new AssetArtifactManifest(metadata.Id, fingerprint, importer.Id,
                importer.Version, target, result.Artifacts, result.Dependencies, result.Diagnostics,
                result.Objects);
            WriteManifest(Path.Combine(stagingPath, ManifestFileName), manifest);
            if (Directory.Exists(generationPath))
            {
                Directory.Delete(stagingPath, recursive: true);
                var concurrent = ReadOutcome(manifestPath, generationPath, cacheHit: true);
                _dependencyGraph.Update(concurrent.Asset, concurrent.Dependencies);
                return concurrent;
            }
            Directory.Move(stagingPath, generationPath);
            _dependencyGraph.Update(metadata.Id, result.Dependencies);
            return new AssetImportOutcome(metadata.Id, fingerprint, generationPath, false, true,
                result.Artifacts, result.Dependencies, result.Diagnostics, result.Objects);
        }
        finally
        {
            if (Directory.Exists(stagingPath))
                Directory.Delete(stagingPath, recursive: true);
        }
    }

    /// <summary>Computes a generation key from source bytes and import configuration.</summary>
    /// <param name="sourcePath">Absolute source path.</param>
    /// <param name="metadata">Authoritative metadata.</param>
    /// <param name="importer">Resolved importer implementation.</param>
    /// <param name="target">Build target identifier.</param>
    /// <returns>A lowercase SHA-256 generation fingerprint.</returns>
    private static string ComputeFingerprint(
        string sourcePath,
        AssetMetadata metadata,
        IAssetImporter importer,
        string target)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var stream = File.OpenRead(sourcePath))
        {
            var buffer = new byte[81920];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, count);
        }
        AppendText(hash, importer.Id);
        AppendText(hash, importer.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendText(hash, target);
        AppendText(hash, metadata.Settings.GetRawText());
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Appends one length-delimited UTF-8 value to a generation hash.</summary>
    /// <param name="hash">Incremental generation hash.</param>
    /// <param name="value">Text value to append.</param>
    private static void AppendText(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    /// <summary>Validates importer declarations and staged artifact containment.</summary>
    /// <param name="result">Importer result to validate.</param>
    /// <param name="stagingPath">Importer staging root.</param>
    private static void ValidateResult(AssetImportResult result, string stagingPath)
    {
        ArgumentNullException.ThrowIfNull(result);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in result.Artifacts)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(artifact.Key);
            ArgumentException.ThrowIfNullOrWhiteSpace(artifact.ContentType);
            ArgumentException.ThrowIfNullOrWhiteSpace(artifact.RelativePath);
            if (!keys.Add(artifact.Key))
                throw new InvalidDataException($"Duplicate artifact key '{artifact.Key}'.");
            var fullPath = Path.GetFullPath(Path.Combine(stagingPath,
                artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(stagingPath, fullPath);
            if (relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal) || !File.Exists(fullPath))
            {
                throw new InvalidDataException(
                    $"Importer did not create contained artifact '{artifact.RelativePath}'.");
            }
        }
        var objects = result.Objects;
        if (objects is null)
            return;
        var objectKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in objects)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Key);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.Kind);
            if (!objectKeys.Add(item.Key))
                throw new InvalidDataException($"Duplicate imported object key '{item.Key}'.");
        }
        foreach (var item in objects)
        {
            if (item.ParentKey is not null && !objectKeys.Contains(item.ParentKey))
            {
                throw new InvalidDataException(
                    $"Imported object '{item.Key}' references missing parent '{item.ParentKey}'.");
            }
            if (item.ArtifactKey is not null && !keys.Contains(item.ArtifactKey))
            {
                throw new InvalidDataException(
                    $"Imported object '{item.Key}' references missing artifact '{item.ArtifactKey}'.");
            }
        }
    }

    /// <summary>Writes and flushes one generation manifest.</summary>
    /// <param name="path">Manifest destination path.</param>
    /// <param name="manifest">Manifest to serialize.</param>
    private static void WriteManifest(string path, AssetArtifactManifest manifest)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough);
        JsonSerializer.Serialize(stream, manifest, _jsonOptions);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Reads a previously published generation manifest.</summary>
    /// <param name="manifestPath">Published manifest path.</param>
    /// <param name="generationPath">Published artifact directory.</param>
    /// <param name="cacheHit">Whether the generation was reused.</param>
    /// <returns>The reconstructed successful outcome.</returns>
    private static AssetImportOutcome ReadOutcome(
        string manifestPath,
        string generationPath,
        bool cacheHit)
    {
        using var stream = File.OpenRead(manifestPath);
        var manifest = JsonSerializer.Deserialize<AssetArtifactManifest>(stream, _jsonOptions)
            ?? throw new InvalidDataException($"Artifact manifest is empty: {manifestPath}");
        return new AssetImportOutcome(manifest.Asset, manifest.Fingerprint, generationPath,
            cacheHit, true, manifest.Artifacts, manifest.Dependencies, manifest.Diagnostics,
            manifest.Objects);
    }

    /// <summary>Stores one published artifact generation.</summary>
    /// <param name="Asset">Persistent source identity.</param>
    /// <param name="Fingerprint">Generation fingerprint.</param>
    /// <param name="Importer">Stable importer identifier.</param>
    /// <param name="ImporterVersion">Importer implementation version.</param>
    /// <param name="Target">Build target identifier.</param>
    /// <param name="Artifacts">Published artifact descriptions.</param>
    /// <param name="Dependencies">Declared persistent dependencies.</param>
    /// <param name="Diagnostics">Non-error import diagnostics.</param>
    /// <param name="Objects">Browsable objects discovered inside the source asset.</param>
    private sealed record AssetArtifactManifest(
        AssetId Asset,
        string Fingerprint,
        string Importer,
        int ImporterVersion,
        string Target,
        IReadOnlyList<AssetArtifact> Artifacts,
        IReadOnlyList<AssetReference> Dependencies,
        IReadOnlyList<AssetImportDiagnostic> Diagnostics,
        IReadOnlyList<AssetImportObject>? Objects = null);
}
