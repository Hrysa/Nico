using System.Text.Json;
using Engine.Assets;
using Engine.Core;
using Xunit;

namespace Engine.Assets.Tests;

public class AssetMetadataStoreTests
{
    /// <summary>Verifies engine-created metadata uses UUIDv7 and empty object settings.</summary>
    [Fact]
    public void Create_ReturnsCurrentValidatedMetadata()
    {
        var metadata = AssetMetadataStore.Create("texture");

        Assert.Equal(AssetMetadataStore.CurrentVersion, metadata.Version);
        Assert.Equal(7, metadata.Id.Value.Version);
        Assert.Equal("texture", metadata.Importer);
        Assert.Equal(JsonValueKind.Object, metadata.Settings.ValueKind);
    }

    /// <summary>Verifies a sidecar round trip preserves identity and importer settings.</summary>
    [Fact]
    public void SaveLoad_RoundTripsMetadata()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Player.png");
        File.WriteAllBytes(sourcePath, [1, 2, 3]);
        using var settingsDocument = JsonDocument.Parse(
            """{"colorSpace":"sRGB","generateMipmaps":true}""");
        var metadata = new AssetMetadata(AssetMetadataStore.CurrentVersion, AssetId.New(),
            "texture", settingsDocument.RootElement.Clone());

        AssetMetadataStore.Save(sourcePath, metadata);
        var restored = AssetMetadataStore.Load(sourcePath);

        Assert.Equal(metadata.Id, restored.Id);
        Assert.Equal("texture", restored.Importer);
        Assert.Equal("sRGB", restored.Settings.GetProperty("colorSpace").GetString());
        Assert.True(restored.Settings.GetProperty("generateMipmaps").GetBoolean());
        Assert.EndsWith("Player.png.meta", AssetMetadataStore.GetSidecarPath(sourcePath));
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp-*"));
    }

    /// <summary>Verifies replacing a sidecar preserves its path while publishing new metadata.</summary>
    [Fact]
    public void Save_ExistingSidecar_ReplacesAtomically()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Rotate.cs");
        var first = AssetMetadataStore.Create("raw");
        var second = AssetMetadataStore.Create("csharp-script");

        AssetMetadataStore.Save(sourcePath, first);
        AssetMetadataStore.Save(sourcePath, second);

        Assert.Equal(second.Id, AssetMetadataStore.Load(sourcePath).Id);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp-*"));
    }

    /// <summary>Verifies unsupported metadata versions fail with a structured data error.</summary>
    [Fact]
    public void Load_UnsupportedVersion_ThrowsInvalidDataException()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Future.asset");
        File.WriteAllText(sourcePath + ".meta", $$"""
            {
              "version": 99,
              "id": "{{AssetId.New()}}",
              "importer": "raw",
              "settings": {}
            }
            """);

        var exception = Assert.Throws<InvalidDataException>(
            () => AssetMetadataStore.Load(sourcePath));

        Assert.Contains("Unsupported asset metadata version 99", exception.Message);
    }

    /// <summary>Verifies malformed JSON is reported as invalid metadata rather than leaking parser details.</summary>
    [Fact]
    public void Load_MalformedJson_ThrowsInvalidDataException()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Broken.asset");
        File.WriteAllText(sourcePath + ".meta", "{not-json");

        Assert.Throws<InvalidDataException>(() => AssetMetadataStore.Load(sourcePath));
    }

    /// <summary>Verifies project scans create sidecars once and preserve their identities.</summary>
    [Fact]
    public void Scanner_MissingSidecars_CreatesAndPreservesIdentity()
    {
        using var temporary = new TemporaryDirectory();
        var texturePath = Path.Combine(temporary.Path, "Assets", "Player.png");
        Directory.CreateDirectory(Path.GetDirectoryName(texturePath)!);
        File.WriteAllBytes(texturePath, [1, 2, 3]);

        var first = AssetMetadataScanner.Scan(temporary.Path,
            path => Path.GetExtension(path) == ".png" ? "texture" : null);
        var second = AssetMetadataScanner.Scan(temporary.Path,
            path => Path.GetExtension(path) == ".png" ? "texture" : null);

        var asset = Assert.Single(first.Assets);
        Assert.Equal("Assets/Player.png", asset.ProjectPath);
        Assert.Equal("texture", asset.Importer);
        Assert.Equal(asset.Id, Assert.Single(second.Assets).Id);
        Assert.True(File.Exists(texturePath + ".meta"));
    }

    /// <summary>Verifies duplicate sidecar identities are repaired without changing the first asset.</summary>
    [Fact]
    public void Scanner_DuplicateIdentity_ReassignsLaterAsset()
    {
        using var temporary = new TemporaryDirectory();
        var firstPath = Path.Combine(temporary.Path, "A.png");
        var secondPath = Path.Combine(temporary.Path, "B.png");
        File.WriteAllBytes(firstPath, [1]);
        File.WriteAllBytes(secondPath, [2]);
        var shared = AssetMetadataStore.Create("texture");
        AssetMetadataStore.Save(firstPath, shared);
        AssetMetadataStore.Save(secondPath, shared);

        var result = AssetMetadataScanner.Scan(temporary.Path,
            path => Path.GetExtension(path) == ".png" ? "texture" : null);

        Assert.Equal(2, result.Assets.Count);
        Assert.Equal(shared.Id, AssetMetadataStore.Load(firstPath).Id);
        Assert.NotEqual(shared.Id, AssetMetadataStore.Load(secondPath).Id);
        Assert.Contains(result.Diagnostics,
            diagnostic => diagnostic.Path == "B.png" && diagnostic.Message.Contains("Duplicate"));
    }

    /// <summary>Verifies orphaned metadata is reported and generated folders are ignored.</summary>
    [Fact]
    public void Scanner_OrphanAndGeneratedContent_ReportsOnlyProjectOrphan()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "Missing.png.meta"), "{}");
        var generatedPath = Path.Combine(temporary.Path, ".nico", "Generated.png");
        Directory.CreateDirectory(Path.GetDirectoryName(generatedPath)!);
        File.WriteAllBytes(generatedPath, [1]);

        var result = AssetMetadataScanner.Scan(temporary.Path,
            path => Path.GetExtension(path) == ".png" ? "texture" : null);

        Assert.Empty(result.Assets);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("Missing.png.meta", diagnostic.Path);
        Assert.Contains("no source asset", diagnostic.Message);
    }

    /// <summary>Verifies database lookup resolves the same record by ID and source path.</summary>
    [Fact]
    public void Database_InitialScan_IndexesByIdentityAndPath()
    {
        using var temporary = new TemporaryDirectory();
        var scriptPath = Path.Combine(temporary.Path, "Scripts", "Move.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
        File.WriteAllText(scriptPath, "public sealed class Move { }");

        var database = new AssetDatabase(temporary.Path,
            path => Path.GetExtension(path) == ".cs" ? "csharp-script" : null);
        var record = Assert.Single(database.Assets);

        Assert.Same(record, database.Find(record.Id));
        Assert.Same(record, database.FindByPath("Scripts/Move.cs"));
        Assert.Same(record, database.FindByPath(scriptPath));
        Assert.Throws<ArgumentException>(() => database.FindByPath("../Outside.cs"));
    }

    /// <summary>Verifies moving an asset and sidecar publishes one move with stable identity.</summary>
    [Fact]
    public void Database_RefreshMovedAsset_PublishesStableMove()
    {
        using var temporary = new TemporaryDirectory();
        var oldPath = Path.Combine(temporary.Path, "Old.cs");
        var newPath = Path.Combine(temporary.Path, "Gameplay", "New.cs");
        File.WriteAllText(oldPath, "public sealed class Move { }");
        var database = new AssetDatabase(temporary.Path,
            path => Path.GetExtension(path) == ".cs" ? "csharp-script" : null);
        var original = Assert.Single(database.Assets);
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.Move(oldPath, newPath);
        File.Move(oldPath + ".meta", newPath + ".meta");
        IReadOnlyList<AssetChange>? published = null;
        database.Changed += changes => published = changes;

        var changes = database.Refresh();

        var change = Assert.Single(changes);
        Assert.Equal(AssetChangeKind.Moved, change.Kind);
        Assert.Equal(original.Id, change.Current!.Id);
        Assert.Equal("Gameplay/New.cs", change.Current.ProjectPath);
        Assert.Same(changes, published);
    }

    /// <summary>Verifies database moves preserve identity and keep source and sidecar together.</summary>
    [Fact]
    public void Database_MoveAsset_PreservesIdentityAndPublishesMove()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Old.cs");
        File.WriteAllText(sourcePath, "class Old { }");
        var database = CreateScriptDatabase(temporary.Path);
        var original = Assert.Single(database.Assets);
        IReadOnlyList<AssetChange>? published = null;
        database.Changed += changes => published = changes;

        var moved = database.MoveAsset(original.Id, "Scripts/New.cs");

        Assert.Equal(original.Id, moved.Id);
        Assert.Equal("Scripts/New.cs", moved.ProjectPath);
        Assert.False(File.Exists(sourcePath));
        Assert.False(File.Exists(sourcePath + ".meta"));
        Assert.True(File.Exists(Path.Combine(temporary.Path, "Scripts", "New.cs")));
        Assert.True(File.Exists(Path.Combine(temporary.Path, "Scripts", "New.cs.meta")));
        Assert.Equal(AssetChangeKind.Moved, Assert.Single(published!).Kind);
    }

    /// <summary>Verifies duplicating an asset copies content but assigns a distinct identity.</summary>
    [Fact]
    public void Database_DuplicateAsset_AssignsNewIdentity()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Move.cs");
        File.WriteAllText(sourcePath, "class Move { }");
        var database = CreateScriptDatabase(temporary.Path);
        var original = Assert.Single(database.Assets);

        var duplicate = database.DuplicateAsset(original.Id, "MoveCopy.cs");

        Assert.NotEqual(original.Id, duplicate.Id);
        Assert.Equal("class Move { }", File.ReadAllText(
            Path.Combine(temporary.Path, "MoveCopy.cs")));
        Assert.Equal(duplicate.Id, AssetMetadataStore.Load(
            Path.Combine(temporary.Path, "MoveCopy.cs")).Id);
        Assert.Equal(2, database.Assets.Count);
    }

    /// <summary>Verifies asset deletion is recoverable and publishes one removal.</summary>
    [Fact]
    public void Database_DeleteAsset_MovesSourceAndSidecarToTrash()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Move.cs");
        File.WriteAllText(sourcePath, "class Move { }");
        var database = CreateScriptDatabase(temporary.Path);
        var original = Assert.Single(database.Assets);
        IReadOnlyList<AssetChange>? published = null;
        database.Changed += changes => published = changes;

        var deletion = database.DeleteAsset(original.Id);

        Assert.Empty(database.Assets);
        Assert.False(File.Exists(sourcePath));
        Assert.False(File.Exists(sourcePath + ".meta"));
        Assert.True(File.Exists(Path.Combine(deletion.TrashDirectory, "Move.cs")));
        Assert.True(File.Exists(Path.Combine(deletion.TrashDirectory, "Move.cs.meta")));
        Assert.Equal(AssetChangeKind.Removed, Assert.Single(published!).Kind);
    }

    /// <summary>Verifies rejected destinations do not modify the source asset.</summary>
    [Fact]
    public void Database_MoveAsset_IncompatibleOrOccupiedDestination_LeavesSourceUntouched()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Move.cs");
        File.WriteAllText(sourcePath, "class Move { }");
        File.WriteAllText(Path.Combine(temporary.Path, "Occupied.cs"), "occupied");
        var database = CreateScriptDatabase(temporary.Path);
        var original = database.FindByPath("Move.cs")!;

        Assert.Throws<InvalidOperationException>(
            () => database.MoveAsset(original.Id, "Move.txt"));
        Assert.Throws<IOException>(
            () => database.MoveAsset(original.Id, "Occupied.cs"));

        Assert.True(File.Exists(sourcePath));
        Assert.True(File.Exists(sourcePath + ".meta"));
        Assert.Equal(original.Id, database.FindByPath("Move.cs")!.Id);
    }

    /// <summary>Verifies watcher bursts coalesce while generated-directory changes are ignored.</summary>
    [Fact]
    public void DatabaseWatcher_Debounce_CoalescesRelevantPathsOnly()
    {
        using var temporary = new TemporaryDirectory();
        using var watcher = new AssetDatabaseWatcher(temporary.Path,
            TimeSpan.FromMilliseconds(20), startNativeWatcher: false);
        using var published = new ManualResetEventSlim();
        var count = 0;
        watcher.RefreshRequested += () =>
        {
            Interlocked.Increment(ref count);
            published.Set();
        };

        watcher.SchedulePath(Path.Combine(temporary.Path, "Scripts", "A.cs"));
        watcher.SchedulePath(Path.Combine(temporary.Path, "Scripts", "A.cs.meta"));
        watcher.SchedulePath(Path.Combine(temporary.Path, ".nico", "cache", "artifact.bin"));

        Assert.True(published.Wait(TimeSpan.FromSeconds(2)));
        Thread.Sleep(60);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    /// <summary>Verifies watcher filtering rejects external and generated paths.</summary>
    [Fact]
    public void DatabaseWatcher_Relevance_RestrictsAuthoritativeProjectContent()
    {
        using var temporary = new TemporaryDirectory();
        using var watcher = new AssetDatabaseWatcher(temporary.Path,
            TimeSpan.Zero, startNativeWatcher: false);

        Assert.True(watcher.IsRelevant(Path.Combine(temporary.Path, "Scenes", "Main.node")));
        Assert.True(watcher.IsRelevant(Path.Combine(temporary.Path, "Scenes", "Main.node.meta")));
        Assert.False(watcher.IsRelevant(Path.Combine(temporary.Path, ".nico", "cache.bin")));
        Assert.False(watcher.IsRelevant(Path.Combine(temporary.Path, "obj", "generated.cs")));
        Assert.False(watcher.IsRelevant(Path.Combine(temporary.Path, "..", "outside.cs")));
    }

    /// <summary>Verifies successful scans publish a binary index with validation stamps.</summary>
    [Fact]
    public void Scanner_ValidAssets_PublishesBinaryStartupIndex()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Move.cs");
        File.WriteAllText(sourcePath, "class Move { }");

        var scan = AssetMetadataScanner.Scan(temporary.Path,
            path => Path.GetExtension(path) == ".cs" ? "csharp-script" : null);
        var cached = AssetIndexCache.Load(temporary.Path);

        var asset = Assert.Single(scan.Assets);
        var entry = Assert.Single(cached);
        Assert.Equal(asset.Id, entry.Id);
        Assert.Equal("Move.cs", entry.ProjectPath);
        Assert.Equal(new FileInfo(sourcePath).Length, entry.SourceLength);
        Assert.True(File.Exists(AssetIndexCache.GetPath(temporary.Path)));
    }

    /// <summary>Verifies changed sidecars bypass cached records and reload authoritative JSON.</summary>
    [Fact]
    public void Scanner_ChangedMetadata_InvalidatesBinaryEntry()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Move.cs");
        File.WriteAllText(sourcePath, "class Move { }");
        AssetMetadataScanner.Scan(temporary.Path,
            path => Path.GetExtension(path) == ".cs" ? "csharp-script" : null);
        var sidecarPath = sourcePath + ".meta";
        File.WriteAllText(sidecarPath, "{broken-json-and-a-different-length");
        File.SetLastWriteTimeUtc(sidecarPath, DateTime.UtcNow.AddSeconds(2));

        var scan = AssetMetadataScanner.Scan(temporary.Path,
            path => Path.GetExtension(path) == ".cs" ? "csharp-script" : null);

        Assert.Empty(scan.Assets);
        Assert.Contains(scan.Diagnostics,
            diagnostic => diagnostic.Path == "Move.cs" && diagnostic.Message.Contains("invalid JSON"));
        Assert.Empty(AssetIndexCache.Load(temporary.Path));
    }

    /// <summary>Verifies a corrupted binary index falls back to sidecars and repairs itself.</summary>
    [Fact]
    public void Scanner_CorruptedBinaryIndex_FallsBackAndRepublishes()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Move.cs");
        File.WriteAllText(sourcePath, "class Move { }");
        var first = AssetMetadataScanner.Scan(temporary.Path,
            path => Path.GetExtension(path) == ".cs" ? "csharp-script" : null);
        File.WriteAllBytes(AssetIndexCache.GetPath(temporary.Path), [1, 2, 3]);

        var second = AssetMetadataScanner.Scan(temporary.Path,
            path => Path.GetExtension(path) == ".cs" ? "csharp-script" : null);

        Assert.Equal(Assert.Single(first.Assets).Id, Assert.Single(second.Assets).Id);
        Assert.Single(AssetIndexCache.Load(temporary.Path));
    }

    /// <summary>Verifies reverse dependency traversal invalidates direct and indirect consumers.</summary>
    [Fact]
    public void DependencyGraph_TransitiveDependents_TracksReplacementAndCycles()
    {
        var texture = AssetId.New();
        var material = AssetId.New();
        var scene = AssetId.New();
        var graph = new AssetDependencyGraph();
        graph.Update(material, [new AssetReference(texture)]);
        graph.Update(scene, [new AssetReference(material)]);

        var invalidated = graph.GetTransitiveDependents(texture);

        Assert.Contains(material, invalidated);
        Assert.Contains(scene, invalidated);
        graph.Update(material, []);
        Assert.Empty(graph.GetDependents(texture));
        graph.Update(material, [new AssetReference(scene)]);
        graph.Update(scene, [new AssetReference(material)]);
        var cycle = Assert.Single(graph.FindCycles());
        Assert.Contains(material, cycle);
        Assert.Contains(scene, cycle);
    }

    /// <summary>Verifies batch imports never exceed their configured concurrency bound.</summary>
    [Fact]
    public async Task ImportPipeline_Batch_UsesBoundedConcurrency()
    {
        using var temporary = new TemporaryDirectory();
        for (var index = 0; index < 6; index++)
            File.WriteAllText(Path.Combine(temporary.Path, $"Asset{index}.parallel"), index.ToString());
        var database = new AssetDatabase(temporary.Path,
            path => Path.GetExtension(path) == ".parallel" ? "parallel" : null);
        var importer = new ParallelImporter();
        var registry = new AssetImporterRegistry();
        registry.Register(importer);
        var pipeline = new AssetImportPipeline(database, registry);

        var outcomes = await pipeline.ImportAsync(database.Assets, "test-x64", 2);

        Assert.Equal(6, outcomes.Count);
        Assert.All(outcomes, outcome => Assert.True(outcome.Succeeded));
        Assert.Equal(6, importer.ExecutionCount);
        Assert.InRange(importer.MaximumConcurrency, 1, 2);
    }

    /// <summary>Verifies simultaneous requests for one asset execute its importer only once.</summary>
    [Fact]
    public async Task ImportPipeline_SameAssetConcurrent_SerializesPublication()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "Asset.parallel"), "content");
        var database = new AssetDatabase(temporary.Path,
            path => Path.GetExtension(path) == ".parallel" ? "parallel" : null);
        var importer = new ParallelImporter();
        var registry = new AssetImporterRegistry();
        registry.Register(importer);
        var pipeline = new AssetImportPipeline(database, registry);
        var record = Assert.Single(database.Assets);

        var outcomes = await pipeline.ImportAsync([record, record, record], "test-x64", 3);

        Assert.Equal(1, importer.ExecutionCount);
        Assert.Single(outcomes, outcome => !outcome.CacheHit);
        Assert.Equal(2, outcomes.Count(outcome => outcome.CacheHit));
    }

    /// <summary>Verifies cancellation prevents queued batch importer execution.</summary>
    [Fact]
    public async Task ImportPipeline_CanceledBatch_ThrowsOperationCanceledException()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(temporary.Path, "Asset.parallel"), "content");
        var database = new AssetDatabase(temporary.Path,
            path => Path.GetExtension(path) == ".parallel" ? "parallel" : null);
        var registry = new AssetImporterRegistry();
        registry.Register(new ParallelImporter());
        var pipeline = new AssetImportPipeline(database, registry);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            pipeline.ImportAsync(database.Assets, "test-x64", 1, cancellation.Token));
    }

    /// <summary>Verifies raw imports publish once and reuse an identical fingerprint.</summary>
    [Fact]
    public void ImportPipeline_UnchangedRawAsset_ReusesPublishedGeneration()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Data.bin");
        File.WriteAllBytes(sourcePath, [1, 2, 3, 4]);
        var database = new AssetDatabase(temporary.Path,
            path => Path.GetExtension(path) == ".bin" ? "raw" : null);
        var registry = new AssetImporterRegistry();
        registry.Register(new RawAssetImporter());
        var pipeline = new AssetImportPipeline(database, registry);
        var record = Assert.Single(database.Assets);

        var first = pipeline.Import(record, "test-x64");
        var second = pipeline.Import(record, "test-x64");

        Assert.True(first.Succeeded);
        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.ArtifactDirectory, second.ArtifactDirectory);
        var artifact = Assert.Single(first.Artifacts);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(
            Path.Combine(first.ArtifactDirectory!, artifact.RelativePath)));
    }

    /// <summary>Reads published artifact metadata without executing the importer again.</summary>
    [Fact]
    public void ImportPipeline_LatestPublished_ReturnsCachedManifestForTarget()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Data.bin");
        File.WriteAllBytes(sourcePath, [1, 2, 3]);
        var database = new AssetDatabase(temporary.Path,
            path => Path.GetExtension(path) == ".bin" ? "raw" : null);
        var registry = new AssetImporterRegistry();
        registry.Register(new RawAssetImporter());
        var pipeline = new AssetImportPipeline(database, registry);
        var record = Assert.Single(database.Assets);
        var imported = pipeline.Import(record, "editor");

        var published = pipeline.TryGetLatestPublished(record, "editor");

        Assert.NotNull(published);
        Assert.True(published.CacheHit);
        Assert.Equal(imported.Fingerprint, published.Fingerprint);
        Assert.Equal(imported.Artifacts, published.Artifacts);
        Assert.Null(pipeline.TryGetLatestPublished(record, "player"));
    }

    /// <summary>Verifies source changes publish a new generation without deleting the old one.</summary>
    [Fact]
    public void ImportPipeline_ChangedSource_PreservesPreviousGeneration()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Data.bin");
        File.WriteAllBytes(sourcePath, [1]);
        var database = new AssetDatabase(temporary.Path,
            path => Path.GetExtension(path) == ".bin" ? "raw" : null);
        var registry = new AssetImporterRegistry();
        registry.Register(new RawAssetImporter());
        var pipeline = new AssetImportPipeline(database, registry);
        var record = Assert.Single(database.Assets);
        var first = pipeline.Import(record, "test-x64");

        File.WriteAllBytes(sourcePath, [2]);
        var second = pipeline.Import(record, "test-x64");

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.NotEqual(first.ArtifactDirectory, second.ArtifactDirectory);
        Assert.True(Directory.Exists(first.ArtifactDirectory));
        Assert.True(Directory.Exists(second.ArtifactDirectory));
    }

    /// <summary>Verifies a failed generation leaves the last successful artifact available.</summary>
    [Fact]
    public void ImportPipeline_ImporterFailure_PreservesSuccessfulGeneration()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Data.fail");
        File.WriteAllBytes(sourcePath, [1]);
        var database = new AssetDatabase(temporary.Path,
            path => Path.GetExtension(path) == ".fail" ? "controlled" : null);
        var importer = new ControlledImporter();
        var registry = new AssetImporterRegistry();
        registry.Register(importer);
        var pipeline = new AssetImportPipeline(database, registry);
        var record = Assert.Single(database.Assets);
        var successful = pipeline.Import(record, "test-x64");
        importer.Fail = true;
        File.WriteAllBytes(sourcePath, [2]);

        var failed = pipeline.Import(record, "test-x64");

        Assert.True(successful.Succeeded);
        Assert.Equal("Root", Assert.Single(successful.Objects!).Name);
        Assert.False(failed.Succeeded);
        Assert.Null(failed.ArtifactDirectory);
        Assert.True(Directory.Exists(successful.ArtifactDirectory));
        Assert.Contains(failed.Diagnostics,
            diagnostic => diagnostic.Code == "IMPORT_EXCEPTION");
        Assert.Empty(Directory.EnumerateDirectories(
            Path.GetDirectoryName(successful.ArtifactDirectory)!, ".staging-*"));
        Assert.Equal(successful.Objects,
            pipeline.TryGetLatestPublished(record, "test-x64")!.Objects);
    }

    /// <summary>Verifies artifact writers cannot escape their isolated staging directory.</summary>
    [Fact]
    public void ImportPipeline_EscapingArtifactPath_ReturnsFailure()
    {
        using var temporary = new TemporaryDirectory();
        var sourcePath = Path.Combine(temporary.Path, "Data.escape");
        File.WriteAllBytes(sourcePath, [1]);
        var database = new AssetDatabase(temporary.Path,
            path => Path.GetExtension(path) == ".escape" ? "escape" : null);
        var registry = new AssetImporterRegistry();
        registry.Register(new EscapingImporter());
        var pipeline = new AssetImportPipeline(database, registry);

        var outcome = pipeline.Import(Assert.Single(database.Assets), "test-x64");

        Assert.False(outcome.Succeeded);
        Assert.Contains(outcome.Diagnostics,
            diagnostic => diagnostic.Code == "IMPORT_EXCEPTION");
    }

    /// <summary>Owns an isolated test directory and removes it after use.</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        /// <summary>Gets the absolute isolated directory path.</summary>
        public string Path { get; }

        /// <summary>Creates an isolated directory.</summary>
        public TemporaryDirectory()
        {
            Path = Directory.CreateTempSubdirectory("nico-asset-metadata-").FullName;
        }

        /// <summary>Removes the isolated directory and its contents.</summary>
        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }

    /// <summary>Creates a database that recognizes C# script assets.</summary>
    /// <param name="root">Temporary project root.</param>
    /// <returns>The initialized script asset database.</returns>
    private static AssetDatabase CreateScriptDatabase(string root)
    {
        return new AssetDatabase(root,
            path => Path.GetExtension(path) == ".cs" ? "csharp-script" : null);
    }

    /// <summary>Importer used to verify publication survives later failures.</summary>
    private sealed class ControlledImporter : IAssetImporter
    {
        /// <summary>Gets or sets whether the next import throws.</summary>
        public bool Fail { get; set; }

        /// <inheritdoc/>
        public string Id => "controlled";

        /// <inheritdoc/>
        public int Version => 1;

        /// <inheritdoc/>
        public AssetImportResult Import(AssetImportContext context)
        {
            if (Fail)
                throw new InvalidOperationException("Controlled failure.");
            using (var artifact = context.CreateArtifact("content.bin"))
                artifact.WriteByte(42);
            return new AssetImportResult(
                [new AssetArtifact("main", "test/content", "content.bin")], [], [],
                [new AssetImportObject("node/0", "Root", "node")]);
        }
    }

    /// <summary>Importer used to verify staging-directory containment.</summary>
    private sealed class EscapingImporter : IAssetImporter
    {
        /// <inheritdoc/>
        public string Id => "escape";

        /// <inheritdoc/>
        public int Version => 1;

        /// <inheritdoc/>
        public AssetImportResult Import(AssetImportContext context)
        {
            using var artifact = context.CreateArtifact("../escaped.bin");
            return new AssetImportResult([], [], []);
        }
    }

    /// <summary>Tracks active executions while producing a small test artifact.</summary>
    private sealed class ParallelImporter : IAssetImporter
    {
        private int _active;
        private int _maximumConcurrency;
        private int _executionCount;

        /// <summary>Gets the highest number of simultaneous importer executions.</summary>
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        /// <summary>Gets the total number of importer executions.</summary>
        public int ExecutionCount => Volatile.Read(ref _executionCount);

        /// <inheritdoc/>
        public string Id => "parallel";

        /// <inheritdoc/>
        public int Version => 1;

        /// <inheritdoc/>
        public AssetImportResult Import(AssetImportContext context)
        {
            Interlocked.Increment(ref _executionCount);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                context.CancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(40));
                context.CancellationToken.ThrowIfCancellationRequested();
                using var artifact = context.CreateArtifact("content.bin");
                artifact.WriteByte(1);
                return new AssetImportResult(
                    [new AssetArtifact("main", "test/content", "content.bin")], [], []);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        /// <summary>Atomically records a new observed concurrency maximum.</summary>
        /// <param name="candidate">Current simultaneous execution count.</param>
        private void UpdateMaximum(int candidate)
        {
            var current = Volatile.Read(ref _maximumConcurrency);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(
                    ref _maximumConcurrency, candidate, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }
}
