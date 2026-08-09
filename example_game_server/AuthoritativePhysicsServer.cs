using Engine.Assets;
using Engine.Core;
using Engine.Graphics;
using Engine.Physics;
using Microsoft.Extensions.Logging;

namespace ExampleGame.Server;

/// <summary>Owns the example game's headless authoritative BEPU simulation.</summary>
internal sealed class AuthoritativePhysicsServer : IDisposable
{
    private readonly ILogger<AuthoritativePhysicsServer> _logger;
    private readonly AssetDatabase _assetDatabase;
    private readonly AssetImportPipeline _assetPipeline;
    private readonly LoadedScene _scene;
    private readonly PhysicsWorld _physicsWorld;
    private bool _disposed;

    /// <summary>Creates and attaches a BEPU world to one authored scene.</summary>
    /// <param name="scenePath">Path to the authored example scene.</param>
    /// <param name="tickRate">Authoritative simulation frequency.</param>
    /// <param name="logger">Server logger.</param>
    internal AuthoritativePhysicsServer(
        string scenePath,
        int tickRate,
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
        _assetPipeline = new AssetImportPipeline(_assetDatabase, registry);
        _scene = SceneFileStore.Load(scenePath);
        _physicsWorld = new PhysicsWorld(ResolveCollisionMesh)
        {
            FixedTimeStep = 1d / tickRate,
            EnableInterpolation = false
        };
        _physicsWorld.Attach(_scene.Root);
        TickRate = tickRate;
        _logger.LogInformation(
            "Loaded authoritative scene {ScenePath} with {BodyCount} BEPU bodies at {TickRate} Hz",
            scenePath, _physicsWorld.BodyCount, tickRate);
    }

    /// <summary>Selects importers needed by authoritative server resources.</summary>
    /// <param name="path">Source asset path.</param>
    /// <returns>The GLB importer ID, or null for unsupported files.</returns>
    private static string? SelectImporter(string path)
    {
        return Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase)
            ? "gltf-model" : null;
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

    /// <summary>Gets the configured authoritative simulation frequency.</summary>
    internal int TickRate { get; }

    /// <summary>Gets the number of completed authoritative ticks.</summary>
    internal long Tick { get; private set; }

    /// <summary>Advances gameplay physics by exactly one authoritative tick.</summary>
    internal void Step()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _physicsWorld.Update(_physicsWorld.FixedTimeStep);
        Tick++;
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
        _physicsWorld.Dispose();
    }
}
