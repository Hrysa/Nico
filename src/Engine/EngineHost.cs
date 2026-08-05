using Engine.Graphics;
using Engine.Assets;
using Engine.Core;
using Engine.Scripting;
using System.Numerics;

namespace Engine;

/// <summary>
/// Creates the default engine runtime using the bundled Silk.NET graphics backend.
/// </summary>
public static class EngineHost
{
    /// <summary>
    /// Creates and initializes the default game window.
    /// </summary>
    /// <param name="title">Window title.</param>
    /// <param name="width">Initial client width.</param>
    /// <param name="height">Initial client height.</param>
    /// <returns>An initialized engine application.</returns>
    public static EngineApplication CreateWindow(string title, int width, int height)
    {
        var window = new SilkWindow();
        window.Initialize(new WindowOptions { Title = title, Width = width, Height = height });
        return new EngineApplication(window, width, height);
    }
}

/// <summary>
/// Owns the runtime services needed to run a game application.
/// </summary>
public sealed class EngineApplication : IDisposable
{
    private readonly SilkWindow _window;
    private int _width;
    private int _height;
    private RenderViewHandle _renderView;
    private PerspectiveCamera? _camera;
    private readonly RenderQueue _renderQueue = new();
    private readonly List<(MeshInstance3D Instance, MeshHandle Mesh, TextureHandle Texture)>
        _renderables = [];
    private CompiledScriptCatalog? _scriptCatalog;
    private SceneScriptRuntime? _scriptRuntime;
    private RuntimeResourceManager? _runtimeResources;

    /// <summary>
    /// Creates an application around an initialized window.
    /// </summary>
    /// <param name="window">Initialized engine window.</param>
    internal EngineApplication(SilkWindow window, int width, int height)
    {
        _window = window;
        _width = width;
        _height = height;
    }

    /// <summary>Loads a loose project scene and its referenced imported models.</summary>
    /// <param name="projectRoot">Project root containing asset metadata and cache.</param>
    /// <param name="scenePath">Absolute or project-relative scene path.</param>
    public void LoadProjectScene(string projectRoot, string scenePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(scenePath);
        if (_renderView.IsValid)
            throw new InvalidOperationException("This application already has a loaded scene.");
        var root = Path.GetFullPath(projectRoot);
        var fullScenePath = Path.IsPathRooted(scenePath)
            ? Path.GetFullPath(scenePath) : Path.GetFullPath(Path.Combine(root, scenePath));
        var scene = SceneFileStore.Load(fullScenePath);
        var database = new AssetDatabase(root, SelectImporter);
        var registry = new AssetImporterRegistry();
        registry.Register(new GlbModelImporter());
        var pipeline = new AssetImportPipeline(database, registry);
        _runtimeResources = CreateRuntimeResourceManager(database, pipeline);

        _camera = scene.GameCamera;
        _renderView = _window.CreateRenderView(_width, _height);
        ConfigurePresentation(_width, _height);
        _window.SetViewportClearColor(_renderView, 0.05f, 0.05f, 0.12f);
        _window.SubmitUI(new UIDrawList());
        foreach (var instance in scene.MeshInstances)
            LoadAssetMesh(database, pipeline, instance);
        LoadScripts(root, database, scene.Root);
        _window.Update += RenderScene;
        _window.Resized += ResizeScene;
        _window.SetContinuousRendering(true);
    }

    /// <summary>
    /// Runs the application until its window closes.
    /// </summary>
    public void Run()
    {
        _window.Run();
    }

    /// <summary>
    /// Releases the application and its runtime services.
    /// </summary>
    public void Dispose()
    {
        _scriptRuntime?.Dispose();
        _scriptRuntime = null;
        _scriptCatalog?.Dispose();
        _scriptCatalog = null;
        _runtimeResources?.Dispose();
        _runtimeResources = null;
        foreach (var renderable in _renderables)
        {
            _window.DestroyMesh(renderable.Mesh);
            if (renderable.Texture.IsValid)
                _window.DestroyTexture(renderable.Texture);
        }
        _renderables.Clear();
        _window.Dispose();
    }

    /// <summary>Loads one imported model and creates renderer resources.</summary>
    /// <param name="database">Loose project asset database.</param>
    /// <param name="pipeline">Loose project import pipeline.</param>
    /// <param name="instance">Persistent imported model instance.</param>
    private void LoadAssetMesh(
        AssetDatabase database,
        AssetImportPipeline pipeline,
        MeshInstance3D instance)
    {
        var meshReference = instance.Mesh;
        AssetImportOutcome? outcome = null;
        StaticMeshResource mesh;
        if (BuiltInAssets.IsCubeMesh(meshReference))
        {
            mesh = BuiltInAssets.LoadMesh(meshReference);
        }
        else
        {
            var record = database.Find(meshReference.Asset)
                ?? throw new FileNotFoundException($"Mesh asset '{meshReference.Asset}' is missing.");
            outcome = pipeline.Import(record, "player");
            if (!outcome.Succeeded || outcome.ArtifactDirectory is null ||
                !outcome.Artifacts.Any(artifact => artifact.Key == meshReference.SubAsset &&
                    artifact.ContentType == "nico/static-mesh"))
                throw new InvalidDataException($"Mesh sub-asset '{meshReference}' is missing.");
            mesh = LoadRuntimeResource(meshReference, new StaticMeshResource([], [], []));
        }
        var defaultMaterial = MaterialProperties.Default;
        var material = new StandardMaterialResource
        {
            BaseColor = defaultMaterial.BaseColor,
            Metallic = defaultMaterial.Metallic,
            Roughness = defaultMaterial.Roughness,
            DoubleSided = defaultMaterial.DoubleSided
        };
        var textureSlot = -1;
        var materialSlot = mesh.Submeshes.Count > 0 ? mesh.Submeshes[0].MaterialSlot : -1;
        var defaultMaterialArtifact = outcome?.Artifacts.FirstOrDefault(artifact =>
            artifact.Key == $"material/{materialSlot}");
        if (instance.Materials.Count == 0 && defaultMaterialArtifact is not null)
            instance.Materials.Add(new AssetReference(meshReference.Asset, defaultMaterialArtifact.Key));
        var materialReference = instance.Materials.FirstOrDefault();
        var materialRecord = materialReference.Asset.Value == Guid.Empty
            ? null : database.Find(materialReference.Asset);
        var materialOutcome = materialRecord is null
            ? null
            : materialReference.Asset == meshReference.Asset
                ? outcome : pipeline.Import(materialRecord, "player");
        var materialArtifact = materialOutcome?.Artifacts.FirstOrDefault(artifact =>
            artifact.Key == materialReference.SubAsset &&
            artifact.ContentType == "nico/standard-material");
        if (materialArtifact is not null)
        {
            var decoded = LoadRuntimeResource(materialReference,
                new DecodedStandardMaterial(new StandardMaterialResource(), -1));
            material = CloneMaterial(decoded.Material);
            textureSlot = decoded.BaseColorTextureSlot;
        }
        var textureHandle = default(TextureHandle);
        var textureArtifact = materialOutcome?.Artifacts.FirstOrDefault(artifact =>
            artifact.Key == $"texture/{textureSlot}");
        if (textureArtifact is not null)
        {
            var texture = LoadRuntimeResource(
                new AssetReference(materialReference.Asset, textureArtifact.Key),
                new TextureResource(0, 0, [], TextureColorSpace.Linear));
            textureHandle = _window.CreateTexture(texture);
            material.BaseColorTexture = textureHandle;
        }
        if (instance.MaterialOverride is { } materialOverride)
        {
            material.BaseColor = materialOverride.BaseColor;
            material.Metallic = materialOverride.Metallic;
            material.Roughness = materialOverride.Roughness;
            material.DoubleSided = materialOverride.DoubleSided;
        }
        try
        {
            var meshHandle = _window.CreateStaticMesh(mesh, material);
            _renderables.Add((instance, meshHandle, textureHandle));
        }
        catch
        {
            if (textureHandle.IsValid)
                _window.DestroyTexture(textureHandle);
            throw;
        }
    }

    /// <summary>Creates the shared decoded-resource manager for a loose Player project.</summary>
    /// <param name="database">Project asset database.</param>
    /// <param name="pipeline">Player import pipeline.</param>
    /// <returns>The configured runtime resource manager.</returns>
    private static RuntimeResourceManager CreateRuntimeResourceManager(
        AssetDatabase database,
        AssetImportPipeline pipeline)
    {
        var manager = new RuntimeResourceManager(
            new PublishedArtifactResolver(database, pipeline, "player"),
            new AssetStorageRouter(new MountedVirtualFileSystem()),
            unusedCapacity: 128);
        manager.RegisterLoader(new DelegateRuntimeResourceLoader<StaticMeshResource>(
            "nico/static-mesh", (stream, _, _) => StaticMeshResource.Load(stream)));
        manager.RegisterLoader(new DelegateRuntimeResourceLoader<DecodedStandardMaterial>(
            "nico/standard-material", (stream, _, _) =>
            {
                var (material, textureSlot) = StandardMaterialResource.Load(stream);
                return new DecodedStandardMaterial(material, textureSlot);
            }));
        manager.RegisterLoader(new DelegateRuntimeResourceLoader<TextureResource>(
            "nico/texture2d", (stream, _, _) => TextureResource.Load(stream)));
        return manager;
    }

    /// <summary>Loads a decoded resource through the shared zero-reference LRU.</summary>
    /// <typeparam name="TResource">Decoded resource type.</typeparam>
    /// <param name="reference">Persistent artifact reference.</param>
    /// <param name="fallback">Value exposed only if initial loading fails.</param>
    /// <returns>The ready decoded resource.</returns>
    private TResource LoadRuntimeResource<TResource>(
        AssetReference reference,
        TResource fallback) where TResource : class
    {
        var manager = _runtimeResources
            ?? throw new InvalidOperationException("Runtime resources are not initialized.");
        var handle = manager.Acquire(reference, fallback);
        try
        {
            manager.WaitAsync(handle).GetAwaiter().GetResult();
            if (manager.GetState(handle) != ResourceLoadState.Ready)
            {
                throw new InvalidDataException($"Runtime resource '{reference}' failed to load.",
                    manager.GetError(handle));
            }
            return manager.Get(handle);
        }
        finally
        {
            manager.Release(handle);
        }
    }

    /// <summary>Creates renderer-local mutable values from a shared decoded material.</summary>
    /// <param name="source">Shared decoded material.</param>
    /// <returns>An independent material copy.</returns>
    private static StandardMaterialResource CloneMaterial(StandardMaterialResource source)
    {
        return new StandardMaterialResource
        {
            BaseColor = source.BaseColor,
            Metallic = source.Metallic,
            Roughness = source.Roughness,
            DoubleSided = source.DoubleSided
        };
    }

    /// <summary>Submits the loaded scene through its game camera.</summary>
    /// <param name="delta">Elapsed frame time.</param>
    private void RenderScene(double delta)
    {
        _scriptRuntime?.Update(delta);
        if (_camera is null)
            return;
        _renderQueue.Clear();
        _camera.UpdateViewport(_width, _height);
        foreach (var renderable in _renderables)
            _renderQueue.Add(renderable.Mesh,
                _camera.GetPushConstants(renderable.Instance.GetModelMatrix()));
        _window.Submit(_renderView, _renderQueue);
    }

    /// <summary>Resizes the Player render target and presentation quad.</summary>
    /// <param name="width">New client width.</param>
    /// <param name="height">New client height.</param>
    private void ResizeScene(int width, int height)
    {
        _width = Math.Max(width, 1);
        _height = Math.Max(height, 1);
        _window.ResizeRenderView(_renderView, _width, _height);
        ConfigurePresentation(_width, _height);
    }

    /// <summary>Configures a full-window viewport presentation quad.</summary>
    /// <param name="width">Client width.</param>
    /// <param name="height">Client height.</param>
    private void ConfigurePresentation(float width, float height)
    {
        _window.SetViewportQuadVertices(_renderView,
        [
            new(new Vector3(0, 0, 0), new Vector2(0, 0)),
            new(new Vector3(0, height, 0), new Vector2(0, 1)),
            new(new Vector3(width, height, 0), new Vector2(1, 1)),
            new(new Vector3(width, height, 0), new Vector2(1, 1)),
            new(new Vector3(width, 0, 0), new Vector2(1, 0)),
            new(new Vector3(0, 0, 0), new Vector2(0, 0))
        ]);
        _window.SetPushConstants(new PushConstants
        {
            Model = Matrix4x4.Identity,
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.CreateOrthographicOffCenter(0, width, 0, height, -1, 1)
        });
    }

    /// <summary>Selects importers needed by loose Player scene loading.</summary>
    /// <param name="path">Project-relative source path.</param>
    /// <returns>The importer identifier or null.</returns>
    private static string? SelectImporter(string path)
    {
        return Path.GetExtension(path).Equals(".glb", StringComparison.OrdinalIgnoreCase)
            ? "gltf-model"
            : Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                ? "csharp-script" : null;
    }

    /// <summary>Loads and starts scripts attached to the scene, when any are present.</summary>
    /// <param name="projectRoot">Absolute loose-project root.</param>
    /// <param name="database">Loose-project asset database.</param>
    /// <param name="sceneRoot">Loaded scene root.</param>
    private void LoadScripts(string projectRoot, AssetDatabase database, Node sceneRoot)
    {
        if (!Enumerate(sceneRoot).Any(node => node.ScriptId is not null))
            return;
        var scriptsDirectory = Path.Combine(projectRoot, "Scripts");
        var projects = Directory.Exists(scriptsDirectory)
            ? Directory.GetFiles(scriptsDirectory, "*.csproj", SearchOption.TopDirectoryOnly)
            : [];
        if (projects.Length != 1)
            throw new InvalidOperationException(
                $"Expected one script project in '{scriptsDirectory}', but found {projects.Length}.");
        var projectName = Path.GetFileNameWithoutExtension(projects[0]);
        var assemblyPath = Path.Combine(scriptsDirectory, "bin", "EditorPlay", $"{projectName}.dll");
        var catalogPath = CompiledScriptCatalog.GetCatalogPath(assemblyPath);
        _scriptCatalog = File.Exists(catalogPath)
            ? CompiledScriptCatalog.Load(assemblyPath)
            : CompiledScriptCatalog.RecoverDevelopmentCatalog(
                assemblyPath,
                GetScriptSources(database));
        _scriptRuntime = new SceneScriptRuntime();
        try
        {
            _scriptRuntime.Attach(sceneRoot, _scriptCatalog);
            _scriptRuntime.Start();
        }
        catch
        {
            _scriptRuntime.Dispose();
            _scriptRuntime = null;
            _scriptCatalog.Dispose();
            _scriptCatalog = null;
            throw;
        }
    }

    /// <summary>Finds indexed C# script assets for legacy loose-project catalog recovery.</summary>
    /// <param name="projectRoot">Absolute loose-project root.</param>
    /// <returns>Script identities paired with their conventional type names.</returns>
    private static IEnumerable<(AssetId Asset, string SourceName)> GetScriptSources(
        AssetDatabase database)
    {
        return database.Assets
            .Where(record => record.Importer == "csharp-script")
            .Select(record => (record.Id, Path.GetFileNameWithoutExtension(record.ProjectPath)))
            .ToArray();
    }

    /// <summary>Enumerates a node and all descendants.</summary>
    /// <param name="root">Subtree root.</param>
    /// <returns>The complete subtree.</returns>
    private static IEnumerable<Node> Enumerate(Node root)
    {
        yield return root;
        foreach (var child in root.Children)
        foreach (var descendant in Enumerate(child))
            yield return descendant;
    }
}
