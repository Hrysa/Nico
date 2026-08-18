using Engine.Assets;
using Engine.Core;
using Engine.Graphics;
using Engine.Physics;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace ExampleGame.Server;

/// <summary>Owns the example game's headless authoritative BEPU simulation.</summary>
internal sealed class AuthoritativePhysicsServer : IDisposable
{
    private readonly ILogger<AuthoritativePhysicsServer> _logger;
    private readonly AssetDatabase _assetDatabase;
    private readonly AssetImportPipeline _assetPipeline;
    private readonly LoadedScene _scene;
    private readonly PhysicsWorld _physicsWorld;
    private readonly ServerTerrainCollision _terrainCollision;
    private readonly string[] _terrainSourcePaths;
    private readonly TerrainSourceStamp[] _terrainSourceStamps;
    private readonly int _terrainReloadPollTicks;
    private readonly UdpGameServer _network;
    private long _nextTerrainReloadPoll;
    private bool _disposed;

    /// <summary>Creates and attaches a BEPU world to one authored scene.</summary>
    /// <param name="scenePath">Path to the authored example scene.</param>
    /// <param name="tickRate">Authoritative simulation frequency.</param>
    /// <param name="port">UDP listen port, or zero for an ephemeral port.</param>
    /// <param name="networkSnapshotRate">UDP snapshots sent per second.</param>
    /// <param name="clientTimeout">Silent-client timeout.</param>
    /// <param name="logger">Server logger.</param>
    internal AuthoritativePhysicsServer(
        string scenePath,
        int tickRate,
        int port,
        int networkSnapshotRate,
        TimeSpan clientTimeout,
        ILogger<AuthoritativePhysicsServer> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tickRate);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        var sceneDirectory = Path.GetDirectoryName(Path.GetFullPath(scenePath))
            ?? throw new InvalidOperationException("The scene path has no parent directory.");
        var projectRoot = Directory.GetParent(sceneDirectory)?.FullName
            ?? throw new InvalidOperationException("The scene path is not inside a game project.");
        _assetDatabase = new AssetDatabase(projectRoot, SelectImporter);
        var registry = new AssetImporterRegistry();
        registry.Register(new GlbModelImporter());
        registry.Register(new TerrainAssetImporter());
        _assetPipeline = new AssetImportPipeline(_assetDatabase, registry);
        _scene = SceneFileStore.Load(scenePath, ServerSceneNodeFactory.Instance);
        _terrainSourcePaths = FindTerrainSourcePaths(_scene.Root);
        _terrainSourceStamps = new TerrainSourceStamp[_terrainSourcePaths.Length];
        for (var index = 0; index < _terrainSourcePaths.Length; index++)
            _terrainSourceStamps[index] = ReadTerrainSourceStamp(_terrainSourcePaths[index]);
        _terrainReloadPollTicks = Math.Max(1, tickRate / 4);
        _terrainCollision = new ServerTerrainCollision(_scene.Root, ResolveTerrain);
        _physicsWorld = new PhysicsWorld(ResolveCollisionMesh, ResolveTerrain)
        {
            FixedTimeStep = 1d / tickRate,
            EnableInterpolation = false
        };
        _physicsWorld.Attach(_scene.Root);
        _network = new UdpGameServer(
            port, tickRate, networkSnapshotRate, clientTimeout, _terrainCollision,
            FindMonsterSpawnPositions(_scene.Root), logger);
        TickRate = tickRate;
        _logger.LogInformation(
            "Loaded authoritative scene {ScenePath} with {BodyCount} BEPU bodies and " +
            "{TerrainCount} shared terrain surfaces at {TickRate} Hz; UDP port {Port}",
            scenePath, _physicsWorld.BodyCount, _terrainCollision.SurfaceCount, tickRate,
            _network.Port);
    }

    /// <summary>Selects importers needed by authoritative server resources.</summary>
    /// <param name="path">Source asset path.</param>
    /// <returns>The GLB importer ID, or null for unsupported files.</returns>
    private static string? SelectImporter(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".glb" => "gltf-model",
            ".nterrain" => "terrain",
            _ => null
        };
    }

    /// <summary>Imports and decodes one static collision-mesh artifact.</summary>
    /// <param name="reference">Persistent mesh artifact reference.</param>
    /// <returns>The decoded triangle mesh.</returns>
    private StaticMeshResource ResolveCollisionMesh(AssetReference reference)
    {
        var record = _assetDatabase.Find(reference.Asset)
            ?? throw new FileNotFoundException($"Collision asset '{reference.Asset}' is missing.");
        var outcome = _assetPipeline.Import(record, "server");
        var artifact = outcome.Artifacts.FirstOrDefault(candidate =>
            candidate.Key == reference.SubAsset && candidate.ContentType == "nico/static-mesh")
            ?? throw new FileNotFoundException($"Collision mesh '{reference}' is missing.");
        var artifactDirectory = outcome.ArtifactDirectory
            ?? throw new InvalidDataException($"Collision asset '{reference.Asset}' failed to import.");
        using var stream = File.OpenRead(Path.Combine(artifactDirectory, artifact.RelativePath));
        return StaticMeshResource.Load(stream);
    }

    /// <summary>Imports and decodes one authored terrain artifact.</summary>
    /// <param name="reference">Persistent terrain artifact reference.</param>
    /// <returns>The decoded shared terrain height grid.</returns>
    private TerrainResource ResolveTerrain(AssetReference reference)
    {
        var record = _assetDatabase.Find(reference.Asset)
            ?? throw new FileNotFoundException($"Terrain asset '{reference.Asset}' is missing.");
        var outcome = _assetPipeline.Import(record, "server");
        var artifact = outcome.Artifacts.FirstOrDefault(candidate =>
            candidate.Key == reference.SubAsset && candidate.ContentType == "nico/terrain")
            ?? throw new FileNotFoundException($"Terrain output '{reference}' is missing.");
        var artifactDirectory = outcome.ArtifactDirectory
            ?? throw new InvalidDataException($"Terrain asset '{reference.Asset}' failed to import.");
        using var stream = File.OpenRead(Path.Combine(artifactDirectory, artifact.RelativePath));
        return TerrainResource.Load(stream);
    }

    /// <summary>Finds the two authored monster nodes in deterministic name order.</summary>
    /// <param name="root">Authoritative scene root.</param>
    /// <returns>Exactly two world-space spawn positions.</returns>
    private static Vector3[] FindMonsterSpawnPositions(Node root)
    {
        var monsters = new List<Node3D>(2);
        AddMonsterNodes(root, monsters);
        monsters.Sort(static (left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.Ordinal));
        if (monsters.Count != 2)
            throw new InvalidDataException(
                "The authoritative example scene requires exactly two named Monster nodes.");
        return [monsters[0].GetWorldPosition(), monsters[1].GetWorldPosition()];
    }

    /// <summary>Recursively collects nodes whose names begin with the monster prefix.</summary>
    /// <param name="node">Current scene node.</param>
    /// <param name="monsters">Destination monster list.</param>
    private static void AddMonsterNodes(Node node, List<Node3D> monsters)
    {
        if (node is Node3D node3D && node.Name.StartsWith("Monster ",
                StringComparison.Ordinal))
        {
            monsters.Add(node3D);
        }
        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
            AddMonsterNodes(children[index], monsters);
    }

    /// <summary>Gets the configured authoritative simulation frequency.</summary>
    internal int TickRate { get; }

    /// <summary>Gets the actual bound authoritative UDP port.</summary>
    internal int Port => _network.Port;

    /// <summary>Gets the number of authenticated UDP clients.</summary>
    internal int ClientCount => _network.ClientCount;

    /// <summary>Gets the number of completed authoritative ticks.</summary>
    internal long Tick { get; private set; }

    /// <summary>Advances gameplay physics by exactly one authoritative tick.</summary>
    internal void Step()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ReloadTerrainIfChanged();
        _network.Step(Tick + 1, (float)_physicsWorld.FixedTimeStep);
        _physicsWorld.Update(_physicsWorld.FixedTimeStep);
        Tick++;
    }

    /// <summary>Reloads sampled and native collision when an authored terrain source changes.</summary>
    private void ReloadTerrainIfChanged()
    {
        if (Tick < _nextTerrainReloadPoll)
            return;
        _nextTerrainReloadPoll = Tick + _terrainReloadPollTicks;
        var changed = false;
        for (var index = 0; index < _terrainSourcePaths.Length; index++)
        {
            if (ReadTerrainSourceStamp(_terrainSourcePaths[index]) !=
                _terrainSourceStamps[index])
            {
                changed = true;
                break;
            }
        }
        if (!changed)
            return;
        try
        {
            _terrainCollision.Reload(_scene.Root, ResolveTerrain);
            _physicsWorld.Attach(_scene.Root);
            for (var index = 0; index < _terrainSourcePaths.Length; index++)
                _terrainSourceStamps[index] = ReadTerrainSourceStamp(_terrainSourcePaths[index]);
            _logger.LogInformation(
                "Reloaded authoritative terrain collision after a source edit");
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            _logger.LogWarning(exception,
                "Could not reload edited terrain collision; the server will retry");
        }
    }

    /// <summary>Finds the physical sources of all enabled terrain colliders in a scene.</summary>
    /// <param name="root">Scene root to inspect.</param>
    /// <returns>Distinct absolute terrain source paths.</returns>
    private string[] FindTerrainSourcePaths(Node root)
    {
        var paths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        AddTerrainSourcePaths(root, paths);
        return paths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Recursively collects terrain source paths without iterator allocation.</summary>
    /// <param name="node">Current scene node.</param>
    /// <param name="paths">Distinct destination path set.</param>
    private void AddTerrainSourcePaths(Node node, HashSet<string> paths)
    {
        var components = node.Components;
        for (var index = 0; index < components.Count; index++)
        {
            if (components[index] is not TerrainColliderComponent
                {
                    Enabled: true,
                    TerrainData: { } reference
                })
            {
                continue;
            }
            var record = _assetDatabase.Find(reference.Asset)
                ?? throw new FileNotFoundException(
                    $"Terrain asset '{reference.Asset}' is missing.");
            paths.Add(Path.GetFullPath(Path.Combine(
                _assetDatabase.ProjectRoot,
                record.ProjectPath.Replace('/', Path.DirectorySeparatorChar))));
        }
        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
            AddTerrainSourcePaths(children[index], paths);
    }

    /// <summary>Reads a cheap change signature for one physical terrain source.</summary>
    /// <param name="path">Absolute source path.</param>
    /// <returns>Current length and last-write timestamp.</returns>
    private static TerrainSourceStamp ReadTerrainSourceStamp(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Terrain source is missing.", path);
        return new TerrainSourceStamp(info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>Logs the latest authoritative transform and velocity of every dynamic body.</summary>
    internal void LogSnapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LogNodeSnapshot(_scene.Root);
    }

    /// <summary>Logs dynamic bodies recursively without allocating an iterator.</summary>
    /// <param name="node">Current scene node.</param>
    private void LogNodeSnapshot(Node node)
    {
        if (node is Node3D node3D)
        {
            var components = node.Components;
            for (var index = 0; index < components.Count; index++)
            {
                if (components[index] is not RigidBodyComponent
                    {
                        Enabled: true,
                        MotionType: RigidBodyMotionType.Dynamic
                    } body)
                    continue;
                _logger.LogInformation(
                    "Tick {Tick}: {Name} position=({X:F3}, {Y:F3}, {Z:F3}) velocity=({VX:F3}, {VY:F3}, {VZ:F3})",
                    Tick,
                    string.IsNullOrWhiteSpace(node.Name) ? node.GetType().Name : node.Name,
                    node3D.Position.X, node3D.Position.Y, node3D.Position.Z,
                    body.LinearVelocity.X, body.LinearVelocity.Y, body.LinearVelocity.Z);
                break;
            }
        }
        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
            LogNodeSnapshot(children[index]);
    }

    /// <summary>Releases the BEPU simulation and loaded scene ownership.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _network.Dispose();
        _physicsWorld.Dispose();
    }

    /// <summary>Identifies one observed version of a physical terrain source.</summary>
    /// <param name="Length">Source byte length.</param>
    /// <param name="LastWriteTimeUtc">Source last-write timestamp.</param>
    private readonly record struct TerrainSourceStamp(long Length, DateTime LastWriteTimeUtc);
}
