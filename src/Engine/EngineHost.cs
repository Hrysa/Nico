using Engine.Graphics;
using Engine.Assets;
using Engine.Core;
using Engine.Scripting;
using Engine.UI;
using System.Numerics;
using System.Globalization;

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
    private UIHost? _uiHost;
    private IUIViewportPolicy? _uiViewportPolicy;
    private WorldSpaceUIHost? _worldSpaceUI;
    private RuntimePauseMenu? _pauseMenu;
    private UIInputContextMode _prePauseInputContext = UIInputContextMode.GameplayOnly;
    private double _simulationTimeScale = 1d;
    private double _runningSimulationTimeScale = 1d;

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

    /// <summary>Mounts or replaces the retained screen-space game UI.</summary>
    /// <param name="root">Root HUD or menu element.</param>
    /// <param name="overlay">Optional host-local popup and drag overlay.</param>
    /// <param name="viewportPolicy">Optional reference-resolution and safe-area policy.</param>
    /// <param name="inputContext">Initial gameplay/UI input arbitration mode.</param>
    /// <param name="schedulingMode">Recurring UI/window update ownership policy.</param>
    public void SetUI(
        UIElement root,
        Canvas? overlay = null,
        IUIViewportPolicy? viewportPolicy = null,
        UIInputContextMode inputContext = UIInputContextMode.Shared,
        UIHostSchedulingMode schedulingMode = UIHostSchedulingMode.ExternallyManaged)
    {
        ArgumentNullException.ThrowIfNull(root);
        DetachPauseMenu();
        _worldSpaceUI = null;
        _uiHost?.Dispose();
        _uiViewportPolicy = viewportPolicy;
        if (_renderView.IsValid)
            ConfigurePresentation(_width, _height);
        _uiHost = new UIHost(
            _window, _window, _window, root, _width, _height, overlay, _window,
            viewportPolicy, inputContext, schedulingMode);
        _uiHost.SimulationTimeScale = _simulationTimeScale;
    }

    /// <summary>Attaches a camera-projected layer hosted inside the current UI root.</summary>
    /// <param name="worldSpaceUI">World-space layer to update from the loaded scene camera.</param>
    public void AttachWorldSpaceUI(WorldSpaceUIHost worldSpaceUI)
    {
        ArgumentNullException.ThrowIfNull(worldSpaceUI);
        if (_uiHost is null)
            throw new InvalidOperationException("SetUI must be called before attaching world-space UI.");
        if (!IsDescendantOf(worldSpaceUI, _uiHost.Root))
            throw new InvalidOperationException("The world-space layer must belong to the active UI root.");
        _worldSpaceUI = worldSpaceUI;
    }

    /// <summary>Gets whether gameplay should receive input under the active UI context.</summary>
    public bool AllowsGameplayInput => _uiHost?.AllowsGameplayInput ?? true;

    /// <summary>Changes gameplay/UI input arbitration, for example when opening a pause menu.</summary>
    /// <param name="inputContext">New input-sharing mode.</param>
    public void SetUIInputContext(UIInputContextMode inputContext)
    {
        if (_uiHost is null)
            throw new InvalidOperationException("SetUI must be called before changing its input context.");
        _uiHost.InputContext = inputContext;
    }

    /// <summary>Gets whether gameplay simulation is paused by the runtime UI.</summary>
    public bool IsPaused => _simulationTimeScale == 0d;

    /// <summary>Gets the current gameplay simulation time scale.</summary>
    public double SimulationTimeScale => _simulationTimeScale;

    /// <summary>Sets gameplay time scale while leaving the unscaled UI clock unaffected.</summary>
    /// <param name="timeScale">Finite non-negative gameplay scale.</param>
    public void SetSimulationTimeScale(double timeScale)
    {
        if (!double.IsFinite(timeScale) || timeScale < 0d)
            throw new ArgumentOutOfRangeException(nameof(timeScale));
        _simulationTimeScale = timeScale;
        if (timeScale > 0d)
            _runningSimulationTimeScale = timeScale;
        if (_uiHost is not null)
            _uiHost.SimulationTimeScale = timeScale;
    }

    /// <summary>Attaches a pause layer hosted inside the current UI root.</summary>
    /// <param name="pauseMenu">Retained pause menu to coordinate.</param>
    public void AttachPauseMenu(RuntimePauseMenu pauseMenu)
    {
        ArgumentNullException.ThrowIfNull(pauseMenu);
        if (_uiHost is null)
            throw new InvalidOperationException("SetUI must be called before attaching a pause menu.");
        if (!IsDescendantOf(pauseMenu, _uiHost.Root))
            throw new InvalidOperationException("The pause menu must belong to the active UI root.");
        DetachPauseMenu();
        _pauseMenu = pauseMenu;
        pauseMenu.ResumeRequested += ResumeFromPauseMenu;
        pauseMenu.QuitRequested += QuitFromPauseMenu;
        _uiHost.NavigationProcessed += HandlePauseNavigation;
        _uiHost.KeyProcessed += HandlePauseKey;
        pauseMenu.Close();
    }

    /// <summary>Pauses or resumes simulation and switches the associated UI input scope.</summary>
    /// <param name="paused">True to open modal pause UI; false to resume gameplay.</param>
    public void SetPaused(bool paused)
    {
        if (_uiHost is null)
            throw new InvalidOperationException("SetUI must be called before changing pause state.");
        if (paused == IsPaused)
            return;
        if (paused)
        {
            _prePauseInputContext = _uiHost.InputContext;
            SetSimulationTimeScale(0d);
            _uiHost.InputContext = UIInputContextMode.UIExclusive;
            _pauseMenu?.Open();
            if (_pauseMenu is not null)
                _uiHost.InputRouter.Focus(_pauseMenu.ResumeButton);
        }
        else
        {
            SetSimulationTimeScale(_runningSimulationTimeScale);
            _pauseMenu?.Close();
            _uiHost.InputContext = _prePauseInputContext;
            _uiHost.InputRouter.Focus(null);
        }
        _uiHost.Refresh();
    }

    /// <summary>Reapplies the active runtime UI policy after scale or safe-area changes.</summary>
    public void RefreshUILayout()
    {
        if (_uiHost is null)
            return;
        if (_renderView.IsValid)
            ConfigurePresentation(_width, _height);
        _uiHost.RefreshViewportPolicy();
    }

    /// <summary>Applies runtime UI culture and derives left-to-right or right-to-left flow.</summary>
    /// <param name="culture">Culture used by the active UI tree.</param>
    public void SetUICulture(CultureInfo culture)
    {
        if (_uiHost is null)
            throw new InvalidOperationException("SetUI must be called before changing UI culture.");
        _uiHost.SetCulture(culture);
    }

    /// <summary>Enables or suppresses non-essential runtime UI motion.</summary>
    /// <param name="reducedMotion">True to replace motion with stable visual state.</param>
    public void SetReducedMotion(bool reducedMotion)
    {
        if (_uiHost is null)
            throw new InvalidOperationException("SetUI must be called before changing UI motion.");
        _uiHost.SetMotionPreference(reducedMotion
            ? UIMotionPreference.Reduced
            : UIMotionPreference.Full);
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
        DetachPauseMenu();
        _uiHost?.Dispose();
        _uiHost = null;
        _worldSpaceUI = null;
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
        _scriptRuntime?.Update(delta * _simulationTimeScale);
        if (_camera is null)
            return;
        _renderQueue.Clear();
        _camera.UpdateViewport(_width, _height);
        if (_worldSpaceUI is not null && _uiHost is not null)
        {
            var logicalSize = _uiViewportPolicy?.Resolve(
                new Vector2(_width, _height), _window.RasterScale).LogicalSize
                ?? new Vector2(_width, _height);
            if (_worldSpaceUI.UpdateProjection(_camera, logicalSize))
                _uiHost.Refresh();
        }
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
        var logicalSize = _uiViewportPolicy?.Resolve(
            new Vector2(width, height), _window.RasterScale).LogicalSize ?? new Vector2(width, height);
        var logicalWidth = logicalSize.X;
        var logicalHeight = logicalSize.Y;
        _window.SetViewportQuadVertices(_renderView,
        [
            new(new Vector3(0, 0, 0), new Vector2(0, 0)),
            new(new Vector3(0, logicalHeight, 0), new Vector2(0, 1)),
            new(new Vector3(logicalWidth, logicalHeight, 0), new Vector2(1, 1)),
            new(new Vector3(logicalWidth, logicalHeight, 0), new Vector2(1, 1)),
            new(new Vector3(logicalWidth, 0, 0), new Vector2(1, 0)),
            new(new Vector3(0, 0, 0), new Vector2(0, 0))
        ]);
        _window.SetPushConstants(new PushConstants
        {
            Model = Matrix4x4.Identity,
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.CreateOrthographicOffCenter(
                0, logicalWidth, 0, logicalHeight, -1, 1)
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

    /// <summary>Resumes gameplay after the retained pause-menu action.</summary>
    private void ResumeFromPauseMenu() => SetPaused(false);

    /// <summary>Requests native application closure from the pause menu.</summary>
    private void QuitFromPauseMenu() => _window.Close();

    /// <summary>Handles controller menu/cancel actions not consumed by a focused control.</summary>
    /// <param name="navigationEvent">Controller navigation transition.</param>
    /// <param name="handled">Whether the UI router consumed the action.</param>
    private void HandlePauseNavigation(NavigationInputEvent navigationEvent, bool handled)
    {
        if (!navigationEvent.IsPressed || navigationEvent.IsRepeat)
            return;
        if (navigationEvent.Action == UINavigationAction.Menu)
            SetPaused(!IsPaused);
        else if (navigationEvent.Action == UINavigationAction.Cancel && IsPaused && !handled)
            SetPaused(false);
    }

    /// <summary>Toggles pause from the standard keyboard cancel gesture.</summary>
    /// <param name="keyEvent">Routed keyboard transition.</param>
    private void HandlePauseKey(KeyInputEvent keyEvent)
    {
        if (keyEvent.IsPressed && !keyEvent.IsRepeat && keyEvent.Key == InputKey.Escape)
            SetPaused(!IsPaused);
    }

    /// <summary>Disconnects pause-menu and navigation callbacks from the current UI host.</summary>
    private void DetachPauseMenu()
    {
        if (_pauseMenu is not null)
        {
            _pauseMenu.ResumeRequested -= ResumeFromPauseMenu;
            _pauseMenu.QuitRequested -= QuitFromPauseMenu;
        }
        if (_uiHost is not null)
        {
            _uiHost.NavigationProcessed -= HandlePauseNavigation;
            _uiHost.KeyProcessed -= HandlePauseKey;
        }
        _pauseMenu = null;
    }

    /// <summary>Checks retained ancestry without allocating an enumeration.</summary>
    /// <param name="element">Candidate descendant.</param>
    /// <param name="root">Required root.</param>
    /// <returns>True when the element is the root or belongs to its subtree.</returns>
    private static bool IsDescendantOf(UIElement element, UIElement root)
    {
        UIElement? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, root))
                return true;
            current = current.Parent as UIElement;
        }
        return false;
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
