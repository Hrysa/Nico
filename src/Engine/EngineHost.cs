using Engine.Graphics;
using Engine.Assets;
using Engine.Core;
using Engine.Physics;
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
        window.Initialize(new WindowOptions
        {
            Title = title,
            Width = width,
            Height = height,
            TargetFrameRate = 120d,
            PresentationMode = PresentationModePreference.Immediate,
            MsaaSamples = 1
        });
        return new EngineApplication(window, width, height);
    }
}

/// <summary>
/// Owns the runtime services needed to run a game application.
/// </summary>
public sealed class EngineApplication : IDisposable, ISceneRenderingService
{
    private readonly SilkWindow _window;
    private int _width;
    private int _height;
    private RenderViewHandle _renderView;
    private PerspectiveCamera? _camera;
    private Node3D? _sceneRoot;
    private readonly RenderQueue _renderQueue = new();
    private RenderPipeline _renderPipeline = BasicForwardRenderPipeline.Instance;
    private readonly List<RuntimeRenderable> _renderables = [];
    private CompiledScriptCatalog? _scriptCatalog;
    private SceneScriptRuntime? _scriptRuntime;
    private PhysicsWorld? _physicsWorld;
    private RuntimeResourceManager? _runtimeResources;
    private SceneAnimationRegistry? _animationService;
    private UIHost? _uiHost;
    private IUIViewportPolicy? _uiViewportPolicy;
    private WorldSpaceUIHost? _worldSpaceUI;
    private HudRoot? _sceneHud;
    private RuntimePauseMenu? _pauseMenu;
    private UIInputContextMode _prePauseInputContext = UIInputContextMode.GameplayOnly;
    private double _simulationTimeScale = 1d;
    private double _runningSimulationTimeScale = 1d;

    /// <summary>Gets or sets the pass composition used to render the game scene.</summary>
    public RenderPipeline RenderPipeline
    {
        get => _renderPipeline;
        set => _renderPipeline = value ?? throw new ArgumentNullException(nameof(value));
    }

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
        var scene = SceneFileStore.Load(fullScenePath, HudSceneNodeFactory.Instance);
        var database = new AssetDatabase(root, SelectImporter);
        var registry = new AssetImporterRegistry();
        registry.Register(new GlbModelImporter());
        registry.Register(new CollisionMeshAssetImporter());
        registry.Register(new TerrainAssetImporter());
        registry.Register(new AnimationSetAssetImporter());
        registry.Register(new StandardMaterialAssetImporter());
        registry.Register(new TerrainLayerAssetImporter());
        registry.Register(new TerrainMaterialAssetImporter());
        registry.Register(new ImageTextureAssetImporter());
        var pipeline = new AssetImportPipeline(database, registry);
        _runtimeResources = CreateRuntimeResourceManager(database, pipeline);

        _camera = scene.GameCamera;
        _sceneRoot = scene.Root;
        _renderView = _window.CreateRenderView(_width, _height);
        ConfigurePresentation(_width, _height);
        _window.SetViewportClearColor(_renderView, 0.05f, 0.05f, 0.12f);
        MountSceneHud(scene.Root);
        if (_sceneHud is null)
            _window.SubmitUI(new UIDrawList());
        _animationService = new SceneAnimationRegistry((_, animationSet, controller) =>
            BindAnimationSet(database, pipeline, animationSet, controller));
        foreach (var instance in scene.MeshInstances)
            LoadAssetMesh(database, pipeline, instance);
        LoadScripts(root, database, scene.Root);
        _physicsWorld = new PhysicsWorld(
            ResolveCollisionMeshResource, ResolveTerrainResource);
        _physicsWorld.EnableInterpolation = true;
        _physicsWorld.Attach(scene.Root);
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

    /// <summary>Gets whether the loaded scene supplies its own authored HUD root.</summary>
    public bool HasSceneHud => _sceneHud is not null;

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
        DetachSceneHud();
        _scriptRuntime?.Dispose();
        _scriptRuntime = null;
        _animationService?.Dispose();
        _animationService = null;
        _physicsWorld?.Dispose();
        _physicsWorld = null;
        _scriptCatalog?.Dispose();
        _scriptCatalog = null;
        _runtimeResources?.Dispose();
        _runtimeResources = null;
        foreach (var renderable in _renderables)
        {
            if (renderable.Palette.IsValid)
                _window.DestroySkinPalette(renderable.Palette);
            _window.DestroyMesh(renderable.Mesh);
            DestroyMaterialTextures(renderable.BaseColorTexture, renderable.NormalTexture,
                renderable.MetallicRoughnessTexture);
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
        SkinnedMeshResource? skin = null;
        TerrainResource? terrainResource = null;
        if (BuiltInAssets.IsBuiltInMesh(meshReference))
        {
            mesh = BuiltInAssets.LoadMesh(meshReference);
        }
        else
        {
            var record = database.Find(meshReference.Asset)
                ?? throw new FileNotFoundException($"Mesh asset '{meshReference.Asset}' is missing.");
            outcome = pipeline.Import(record, "player");
            var meshArtifact = outcome.Artifacts.FirstOrDefault(artifact =>
                artifact.Key == meshReference.SubAsset &&
                artifact.ContentType is "nico/static-mesh" or "nico/skinned-mesh" or
                    "nico/terrain");
            if (!outcome.Succeeded || outcome.ArtifactDirectory is null || meshArtifact is null)
                throw new InvalidDataException($"Mesh sub-asset '{meshReference}' is missing.");
            if (meshArtifact.ContentType == "nico/terrain")
            {
                var collider = instance.GetComponent<TerrainColliderComponent>() ??
                    throw new InvalidDataException(
                        $"Terrain mesh '{meshReference}' requires a terrain collider.");
                terrainResource = LoadRuntimeResource(meshReference,
                    new TerrainResource(2, 2, [0f, 0f, 0f, 0f]));
                instance.LocalBounds = TerrainMeshBuilder.GetBounds(
                    terrainResource, collider.HorizontalSize, collider.HeightScale, collider.Center);
                mesh = TerrainMeshBuilder.BuildStaticMesh(
                    terrainResource, collider.HorizontalSize, collider.HeightScale, collider.Center);
            }
            else if (meshArtifact.ContentType == "nico/skinned-mesh")
            {
                skin = LoadRuntimeResource(meshReference,
                    new SkinnedMeshResource(new StaticMeshResource([], [], []), [],
                        new SkeletonResource([]), []));
                mesh = skin.Mesh;
            }
            else
            {
                mesh = LoadRuntimeResource(meshReference, new StaticMeshResource([], [], []));
            }
        }
        var material = new StandardMaterialAsset();
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
            artifact.Key == materialReference.SubAsset);
        TextureResource? baseColorResource = null;
        TextureResource? normalResource = null;
        TextureResource? metallicRoughnessResource = null;
        if (materialArtifact?.ContentType == "nico/standard-material")
        {
            material = CloneMaterial(LoadRuntimeResource(
                materialReference, new StandardMaterialAsset()));
            baseColorResource = LoadOptionalTexture(material.BaseColorTexture);
            normalResource = LoadOptionalTexture(material.NormalTexture);
            metallicRoughnessResource = LoadOptionalTexture(
                material.MetallicRoughnessTexture);
        }
        else if (materialArtifact?.ContentType == "nico/terrain-material" &&
            terrainResource is not null)
        {
            var terrainMaterial = LoadRuntimeResource(materialReference,
                new TerrainMaterialAsset(terrainResource.Width, terrainResource.Depth));
            var terrainLayers = new List<ResolvedTerrainLayer>(terrainMaterial.Layers.Count);
            for (var index = 0; index < terrainMaterial.Layers.Count; index++)
            {
                var layer = LoadRuntimeResource(terrainMaterial.Layers[index],
                    new TerrainLayerAsset());
                terrainLayers.Add(new ResolvedTerrainLayer(layer,
                    LoadOptionalTexture(layer.BaseColorTexture),
                    LoadOptionalTexture(layer.NormalTexture),
                    LoadOptionalTexture(layer.MetallicRoughnessTexture)));
            }
            var composed = TerrainMaterialComposer.Compose(terrainMaterial, terrainLayers);
            material = composed.Material;
            baseColorResource = composed.BaseColorTexture;
            normalResource = composed.NormalTexture;
            metallicRoughnessResource = composed.MetallicRoughnessTexture;
            var collider = instance.GetComponent<TerrainColliderComponent>()!;
            mesh = TerrainMeshBuilder.BuildStaticMesh(terrainResource,
                collider.HorizontalSize, collider.HeightScale, collider.Center,
                tintByHeight: false);
        }
        var baseColorHandle = CreateMaterialTexture(baseColorResource, TextureColorSpace.Srgb);
        var normalHandle = CreateMaterialTexture(normalResource, TextureColorSpace.Linear);
        var metallicRoughnessHandle = CreateMaterialTexture(
            metallicRoughnessResource, TextureColorSpace.Linear);
        try
        {
            var resolvedMaterial = ResolvedStandardMaterial.Resolve(
                material, baseColorHandle, normalHandle, metallicRoughnessHandle);
            if (skin is null)
            {
                var meshHandle = _window.CreateStaticMesh(mesh, resolvedMaterial);
                _renderables.Add(new RuntimeRenderable(
                    instance, meshHandle, baseColorHandle, normalHandle,
                    metallicRoughnessHandle, default, null,
                    resolvedMaterial.SurfaceType));
            }
            else
            {
                var controller = new AnimationController(skin);
                var handles = _window.CreateSkinnedMesh(skin, resolvedMaterial);
                _window.UpdateSkinPalette(handles.Palette, controller.Pose.SkinMatrices);
                _animationService!.Register(instance, controller);
                _renderables.Add(new RuntimeRenderable(instance, handles.Mesh,
                    baseColorHandle, normalHandle, metallicRoughnessHandle,
                    handles.Palette, controller, resolvedMaterial.SurfaceType)
                { UploadedPoseRevision = controller.PoseRevision });
            }
        }
        catch
        {
            DestroyMaterialTextures(baseColorHandle, normalHandle, metallicRoughnessHandle);
            throw;
        }
    }

    /// <summary>Loads and creates one material texture with slot-correct color interpretation.</summary>
    /// <param name="reference">Optional persistent texture reference.</param>
    /// <param name="colorSpace">Required sample interpretation.</param>
    /// <returns>The renderer-owned texture handle, or an invalid handle when omitted.</returns>
    private TextureHandle CreateMaterialTexture(
        TextureResource? texture,
        TextureColorSpace colorSpace)
    {
        if (texture is null)
            return default;
        return _window.CreateTexture(texture with { ColorSpace = colorSpace });
    }

    /// <summary>Loads one optional decoded texture resource.</summary>
    /// <param name="reference">Optional persistent texture reference.</param>
    /// <returns>Decoded texture, or null when omitted.</returns>
    private TextureResource? LoadOptionalTexture(AssetReference? reference)
    {
        return reference is { } textureReference
            ? LoadRuntimeResource(textureReference,
                new TextureResource(0, 0, [], TextureColorSpace.Linear))
            : null;
    }

    /// <summary>Destroys all renderer-owned textures associated with one material.</summary>
    /// <param name="baseColorTexture">Optional base-color texture handle.</param>
    /// <param name="normalTexture">Optional normal-map texture handle.</param>
    /// <param name="metallicRoughnessTexture">Optional metallic-roughness texture handle.</param>
    private void DestroyMaterialTextures(
        TextureHandle baseColorTexture,
        TextureHandle normalTexture,
        TextureHandle metallicRoughnessTexture)
    {
        if (baseColorTexture.IsValid)
            _window.DestroyTexture(baseColorTexture);
        if (normalTexture.IsValid)
            _window.DestroyTexture(normalTexture);
        if (metallicRoughnessTexture.IsValid)
            _window.DestroyTexture(metallicRoughnessTexture);
    }

    /// <summary>Resolves and registers a script-selected animation set.</summary>
    /// <param name="database">Project asset database.</param>
    /// <param name="pipeline">Player import pipeline.</param>
    /// <param name="setReference">Resolved animation-set artifact reference.</param>
    /// <param name="controller">Target runtime controller.</param>
    private void BindAnimationSet(
        AssetDatabase database,
        AssetImportPipeline pipeline,
        AssetReference setReference,
        AnimationController controller)
    {
        var setRecord = database.Find(setReference.Asset) ?? throw new FileNotFoundException(
            $"Animation set asset '{setReference.Asset}' is missing.");
        var setOutcome = pipeline.Import(setRecord, "player");
        if (!setOutcome.Succeeded || !setOutcome.Artifacts.Any(candidate =>
                candidate.Key == setReference.SubAsset &&
                candidate.ContentType == "nico/animation-set"))
        {
            throw new InvalidDataException(
                $"Animation set sub-asset '{setReference}' is missing.");
        }
        var set = LoadRuntimeResource(setReference, new AnimationSetResource([]));
        var clips = set.BindTo(controller.Skeleton,
            source => ResolveAnimationSource(database, pipeline, source),
            controller.Resource.MeshNodeTransform);
        controller.RegisterClips(clips);
    }

    /// <summary>Loads one standalone or skinned animation artifact for set binding.</summary>
    /// <param name="database">Project asset database.</param>
    /// <param name="pipeline">Player import pipeline.</param>
    /// <param name="source">Explicit source artifact.</param>
    /// <returns>The decoded source skeleton and clips.</returns>
    private SkeletalAnimationResource ResolveAnimationSource(AssetDatabase database,
        AssetImportPipeline pipeline, AssetReference source)
    {
        var sourceRecord = database.Find(source.Asset) ?? throw new FileNotFoundException(
            $"Animation source asset '{source.Asset}' is missing.");
        var outcome = pipeline.Import(sourceRecord, "player");
        var artifact = outcome.Artifacts.FirstOrDefault(candidate =>
            candidate.Key == source.SubAsset &&
            candidate.ContentType is "nico/skeletal-animation" or "nico/skinned-mesh");
        if (!outcome.Succeeded || artifact is null)
            throw new InvalidDataException(
                $"Animation source sub-asset '{source}' is missing.");
        if (artifact.ContentType == "nico/skeletal-animation")
        {
            return LoadRuntimeResource(source,
                new SkeletalAnimationResource(new SkeletonResource([]), []));
        }
        var skin = LoadRuntimeResource(source,
            new SkinnedMeshResource(new StaticMeshResource([], [], []), [],
                new SkeletonResource([]), []));
        return new SkeletalAnimationResource(skin.Skeleton, skin.Animations.ToArray(),
            skin.MeshNodeTransform);
    }

    /// <summary>Mounts the single authored HUD found in a loaded scene.</summary>
    /// <param name="root">Loaded synthetic scene root.</param>
    private void MountSceneHud(Node root)
    {
        DetachSceneHud();
        HudRoot? found = null;
        foreach (var node in Enumerate(root))
        {
            if (node is not HudRoot hud)
                continue;
            if (found is not null)
                throw new InvalidDataException("A scene can contain only one HUD root.");
            found = hud;
        }
        if (found is null)
            return;
        _sceneHud = found;
        _sceneHud.ContentChanged += HandleSceneHudContentChanged;
        SetUI(_sceneHud.Content, inputContext: UIInputContextMode.Shared);
    }

    /// <summary>Rehosts a runtime-replaced authored HUD tree.</summary>
    /// <param name="previous">Previously hosted tree.</param>
    /// <param name="current">Replacement tree.</param>
    private void HandleSceneHudContentChanged(UIElement previous, UIElement current)
    {
        SetUI(current, inputContext: UIInputContextMode.Shared);
    }

    /// <summary>Disconnects the authored HUD without disposing its scene node.</summary>
    private void DetachSceneHud()
    {
        if (_sceneHud is not null)
            _sceneHud.ContentChanged -= HandleSceneHudContentChanged;
        _sceneHud = null;
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
            new ImportingArtifactResolver(database, pipeline, "player"),
            new AssetStorageRouter(new MountedVirtualFileSystem()),
            unusedCapacity: 128);
        manager.RegisterLoader(new DelegateRuntimeResourceLoader<StaticMeshResource>(
            "nico/static-mesh", (stream, _, _) => StaticMeshResource.Load(stream)));
        manager.RegisterLoader(new DelegateRuntimeResourceLoader<TerrainResource>(
            "nico/terrain", (stream, _, _) => TerrainResource.Load(stream)));
        manager.RegisterLoader(new DelegateRuntimeResourceLoader<SkinnedMeshResource>(
            "nico/skinned-mesh", (stream, _, _) => SkinnedMeshResource.Load(stream)));
        manager.RegisterLoader(new DelegateRuntimeResourceLoader<SkeletalAnimationResource>(
            "nico/skeletal-animation", (stream, _, _) => SkeletalAnimationResource.Load(stream)));
        manager.RegisterLoader(new DelegateRuntimeResourceLoader<AnimationSetResource>(
            "nico/animation-set", (stream, _, _) => AnimationSetResource.Load(stream)));
        manager.RegisterLoader(new DelegateRuntimeResourceLoader<StandardMaterialAsset>(
            "nico/standard-material", (stream, _, _) => StandardMaterialAssetCodec.Load(stream)));
        manager.RegisterLoader(new DelegateRuntimeResourceLoader<TerrainLayerAsset>(
            "nico/terrain-layer", (stream, _, _) => TerrainMaterialAssetCodec.LoadLayer(stream)));
        manager.RegisterLoader(new DelegateRuntimeResourceLoader<TerrainMaterialAsset>(
            "nico/terrain-material", (stream, _, _) =>
                TerrainMaterialAssetCodec.LoadMaterial(stream)));
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

    /// <summary>Loads one imported static mesh for runtime collision geometry.</summary>
    /// <param name="reference">Imported static-mesh artifact reference.</param>
    /// <returns>The decoded collision mesh.</returns>
    private StaticMeshResource ResolveCollisionMeshResource(AssetReference reference)
    {
        return LoadRuntimeResource(reference, new StaticMeshResource([], [], []));
    }

    /// <summary>Loads one explicit terrain grid for runtime collision.</summary>
    /// <param name="reference">Published terrain artifact reference.</param>
    /// <returns>The decoded terrain grid.</returns>
    private TerrainResource ResolveTerrainResource(AssetReference reference)
    {
        return LoadRuntimeResource(reference,
            new TerrainResource(2, 2, [0f, 0f, 0f, 0f]));
    }

    /// <summary>Creates renderer-local mutable values from a shared decoded material.</summary>
    /// <param name="source">Shared decoded material.</param>
    /// <returns>An independent material copy.</returns>
    private static StandardMaterialAsset CloneMaterial(StandardMaterialAsset source)
    {
        return source.Clone();
    }

    /// <summary>Submits the loaded scene through its game camera.</summary>
    /// <param name="delta">Elapsed frame time.</param>
    private void RenderScene(double delta)
    {
        var simulationDelta = delta * _simulationTimeScale;
        var startupComplete = _scriptRuntime?.IsStartupComplete ?? true;
        if (!startupComplete)
        {
            _scriptRuntime!.UpdateStartup();
            startupComplete = _scriptRuntime.IsStartupComplete;
        }
        if (startupComplete)
        {
            _scriptRuntime?.Update(simulationDelta);
            _physicsWorld?.Update(simulationDelta);
            _scriptRuntime?.LateUpdate(simulationDelta);
        }
        UpdateAnimations(simulationDelta);
        if (_camera is null)
            return;
        _renderQueue.Clear();
        if (_sceneRoot is not null)
            _renderQueue.ResolveLighting(_sceneRoot);
        _camera.UpdateViewport(_width, _height);
        _renderQueue.Camera = RenderCameraData.Create(
            _camera.GetViewMatrix(), _camera.GetProjectionMatrix());
        if (_worldSpaceUI is not null && _uiHost is not null)
        {
            var logicalSize = _uiViewportPolicy?.Resolve(
                new Vector2(_width, _height), _window.RasterScale).LogicalSize
                ?? new Vector2(_width, _height);
            if (_worldSpaceUI.UpdateProjection(_camera, logicalSize))
                _uiHost.Refresh();
        }
        foreach (var renderable in _renderables)
        {
            if (renderable.Animation is not null && renderable.Palette.IsValid)
            {
                _renderQueue.AddSkinned(renderable.Mesh, renderable.Palette,
                    _camera.GetPushConstants(renderable.Animation.Resource.ComposeModelTransform(
                        renderable.Instance.GetModelMatrix())),
                    castsShadows: renderable.SurfaceType != RenderSurfaceType.Transparent,
                    surfaceType: renderable.SurfaceType);
            }
            else
            {
                _renderQueue.Add(renderable.Mesh,
                    _camera.GetPushConstants(renderable.Instance.GetModelMatrix()),
                    castsShadows: renderable.SurfaceType != RenderSurfaceType.Transparent,
                    surfaceType: renderable.SurfaceType);
            }
        }
        RenderPipeline.Render(_window, _renderView, _renderQueue);
    }

    /// <summary>Advances active controllers and uploads only changed skin palettes.</summary>
    /// <param name="deltaTime">Elapsed scaled simulation time.</param>
    private void UpdateAnimations(double deltaTime)
    {
        for (var index = 0; index < _renderables.Count; index++)
        {
            var renderable = _renderables[index];
            if (renderable.Animation is not { } controller || !renderable.Palette.IsValid)
                continue;
            controller.Advance(deltaTime);
        }
        for (var index = 0; index < _renderables.Count; index++)
        {
            var renderable = _renderables[index];
            if (renderable.Animation is not { } controller || !renderable.Palette.IsValid)
                continue;
            controller.DispatchEvents();
            if (renderable.UploadedPoseRevision == controller.PoseRevision)
                continue;
            _window.UpdateSkinPalette(renderable.Palette, controller.Pose.SkinMatrices);
            renderable.UploadedPoseRevision = controller.PoseRevision;
        }
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

    /// <summary>Groups one runtime scene instance with renderer resources and playback state.</summary>
    /// <param name="Instance">Scene mesh instance.</param>
    /// <param name="Mesh">Static or skinned mesh handle.</param>
    /// <param name="BaseColorTexture">Optional base-color texture handle.</param>
    /// <param name="NormalTexture">Optional normal-map texture handle.</param>
    /// <param name="MetallicRoughnessTexture">Optional metallic-roughness texture handle.</param>
    /// <param name="Palette">Optional joint palette.</param>
    /// <param name="Animation">Optional animation controller.</param>
    private sealed class RuntimeRenderable
    {
        /// <summary>Gets the scene mesh instance.</summary>
        internal MeshInstance3D Instance { get; }

        /// <summary>Gets the static or skinned mesh handle.</summary>
        internal MeshHandle Mesh { get; }

        /// <summary>Gets the optional base-color texture handle.</summary>
        internal TextureHandle BaseColorTexture { get; }

        /// <summary>Gets the optional normal-map texture handle.</summary>
        internal TextureHandle NormalTexture { get; }

        /// <summary>Gets the optional metallic-roughness texture handle.</summary>
        internal TextureHandle MetallicRoughnessTexture { get; }

        /// <summary>Gets the optional joint-palette handle.</summary>
        internal SkinPaletteHandle Palette { get; }

        /// <summary>Gets the optional runtime animation controller.</summary>
        internal AnimationController? Animation { get; }

        /// <summary>Gets the SRP queue class owned by the resolved material.</summary>
        internal RenderSurfaceType SurfaceType { get; }

        /// <summary>Gets or sets the pose revision most recently uploaded to the palette.</summary>
        internal ulong UploadedPoseRevision { get; set; }

        /// <summary>Creates retained resources for one scene mesh instance.</summary>
        /// <param name="instance">Scene mesh instance.</param>
        /// <param name="mesh">Static or skinned mesh handle.</param>
        /// <param name="baseColorTexture">Optional base-color texture handle.</param>
        /// <param name="normalTexture">Optional normal-map texture handle.</param>
        /// <param name="metallicRoughnessTexture">Optional metallic-roughness texture handle.</param>
        /// <param name="palette">Optional joint-palette handle.</param>
        /// <param name="animation">Optional runtime animation controller.</param>
        /// <param name="surfaceType">SRP queue class owned by the resolved material.</param>
        internal RuntimeRenderable(MeshInstance3D instance, MeshHandle mesh,
            TextureHandle baseColorTexture, TextureHandle normalTexture,
            TextureHandle metallicRoughnessTexture, SkinPaletteHandle palette,
            AnimationController? animation, RenderSurfaceType surfaceType)
        {
            Instance = instance;
            Mesh = mesh;
            BaseColorTexture = baseColorTexture;
            NormalTexture = normalTexture;
            MetallicRoughnessTexture = metallicRoughnessTexture;
            Palette = palette;
            Animation = animation;
            SurfaceType = surfaceType;
        }
    }

    /// <summary>Selects importers needed by loose Player scene loading.</summary>
    /// <param name="path">Project-relative source path.</param>
    /// <returns>The importer identifier or null.</returns>
    private static string? SelectImporter(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".glb" => "gltf-model",
            ".cs" => "csharp-script",
            ".ncollision" => "collision-mesh",
            ".nterrain" => "terrain",
            ".nanimset" => "animation-set",
            ".nmat" => "standard-material",
            ".ntlayer" => "terrain-layer",
            ".ntmat" => "terrain-material",
            ".png" or ".jpg" or ".jpeg" => "image-texture",
            _ => null
        };
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
            var assets = new SceneAssetRegistry(path => database.FindByPath(path)?.Id);
            _scriptRuntime.Attach(sceneRoot, _scriptCatalog, _window, _animationService, this,
                assets);
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
