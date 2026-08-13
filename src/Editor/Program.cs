using System.Numerics;
using System.Collections.Concurrent;
using Editor;
using Engine.Assets;
using Engine.Core;
using Engine.Graphics;
using Engine.Physics;
using Engine.Scripting;
using Engine.UI;
using Microsoft.Extensions.Logging;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: Editor <game-project-root>");
    return 2;
}

EditorProjectContext project;
try
{
    project = EditorProjectContext.Open(args[0]);
}
catch (Exception exception) when (exception is ArgumentException or DirectoryNotFoundException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

var loggerFactory = LoggerFactory.Create(b =>
{
    b.AddConsole();
    b.SetMinimumLevel(LogLevel.Trace);
});

Debug.SetLoggerFactory(loggerFactory);

var logger = loggerFactory.CreateLogger<Program>();
logger.LogInformation("Starting Editor for game project {ProjectRoot}...", project.RootPath);

var assetDatabase = new AssetDatabase(project.RootPath, EditorAssetImporters.Select);
var assetImporterRegistry = new AssetImporterRegistry();
EditorAssetImporters.RegisterAll(assetImporterRegistry);
var assetImportPipeline = new AssetImportPipeline(assetDatabase, assetImporterRegistry);
using var runtimeResources = new RuntimeResourceManager(
    new PublishedArtifactResolver(assetDatabase, assetImportPipeline, "editor"),
    new AssetStorageRouter(new MountedVirtualFileSystem()),
    unusedCapacity: 128);
runtimeResources.RegisterLoader(new DelegateRuntimeResourceLoader<StaticMeshResource>(
    "nico/static-mesh", (stream, _, _) => StaticMeshResource.Load(stream)));
runtimeResources.RegisterLoader(new DelegateRuntimeResourceLoader<TerrainResource>(
    "nico/terrain", (stream, _, _) => TerrainResource.Load(stream)));
runtimeResources.RegisterLoader(new DelegateRuntimeResourceLoader<SkinnedMeshResource>(
    "nico/skinned-mesh", (stream, _, _) => SkinnedMeshResource.Load(stream)));
runtimeResources.RegisterLoader(new DelegateRuntimeResourceLoader<SkeletalAnimationResource>(
    "nico/skeletal-animation", (stream, _, _) => SkeletalAnimationResource.Load(stream)));
runtimeResources.RegisterLoader(new DelegateRuntimeResourceLoader<AnimationSetResource>(
    "nico/animation-set", (stream, _, _) => AnimationSetResource.Load(stream)));
runtimeResources.RegisterLoader(new DelegateRuntimeResourceLoader<StandardMaterialAsset>(
    "nico/standard-material", (stream, _, _) => StandardMaterialAssetCodec.Load(stream)));
runtimeResources.RegisterLoader(new DelegateRuntimeResourceLoader<TextureResource>(
    "nico/texture2d", (stream, _, _) => TextureResource.Load(stream)));
logger.LogInformation("Indexed {AssetCount} project assets with {DiagnosticCount} diagnostics",
    assetDatabase.Assets.Count, assetDatabase.Diagnostics.Count);
foreach (var diagnostic in assetDatabase.Diagnostics)
    logger.LogWarning("Asset metadata {AssetPath}: {Message}", diagnostic.Path, diagnostic.Message);

ScriptingWorkspace scriptingWorkspace;
try
{
    scriptingWorkspace = ProjectSolutionScaffolder.Ensure(
        project.RootPath,
        typeof(Node).Assembly.Location);
    logger.LogInformation("Scripting solution ready at {SolutionPath}", scriptingWorkspace.SolutionPath);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    logger.LogError(exception, "Could not create the scripting solution in {ProjectRoot}", project.RootPath);
    return 2;
}

using var window = new SilkWindow(loggerFactory);
using var scriptCompiler = new GameScriptCompiler(scriptingWorkspace, assetDatabase);
var width = 1280;
var height = 720;
var options = new WindowOptions
{
    Title = $"{Path.GetFileName(project.RootPath)} - Game Engine Editor",
    Width = width,
    Height = height,
    CustomTitleBar = true,
    IsEventDriven = true,
    TargetFrameRate = 120d,
    PresentationMode = PresentationModePreference.Immediate,
    MsaaSamples = 1
};

logger.LogInformation("Initializing window...");
window.Initialize(options);
using var secondaryWindows = new SilkWindowGroup(window, loggerFactory);
using var assetWatcher = new AssetDatabaseWatcher(project.RootPath);
var assetRefreshPending = 0;
assetWatcher.RefreshRequested += () =>
{
    Interlocked.Exchange(ref assetRefreshPending, 1);
    window.RequestFrame();
};

logger.LogInformation("Setting up editor UI...");
var editorView = EditorUI.BuildView(width, height);
editorView.Root.BackgroundColor = editorView.TitleBar.BackgroundColor;
var uiRoot = editorView.Root;
var overlay = editorView.Overlay;
window.SetUiClearColor(editorView.TitleBar.BackgroundColor);
var dockWorkspace = EditorDockWorkspace.Load(project.RootPath, out var dockRestoreError);
if (dockRestoreError is not null)
    logger.LogWarning(dockRestoreError, "Could not restore the Editor dock workspace; using defaults");
var dockWindowFactory = new EditorDockFloatingWindowFactory(secondaryWindows);
var animationSetEditor = new AnimationSetEditor { Width = 0f, Height = 0f };
var dockRegistry = EditorDockWorkspace.CreateRegistry(
    editorView, allowViewportFloating: true);
dockRegistry.Register(EditorDockWorkspace.AnimationSetId, "Animation Set",
    () => animationSetEditor);
using var dockSession = new DockSession(
    dockWorkspace,
    dockRegistry,
    dockWindowFactory,
    initializeFloatingWindows: false,
    mainCoordinates: window);
EditorDockWorkspace.Mount(editorView, dockSession);
using var mainUIHost = new UIHost(
    window, window, window, uiRoot, width, height,
    overlay: overlay,
    textLayout: window,
    schedulingMode: UIHostSchedulingMode.ExternallyManaged);

// ── Scene viewport: PerspectiveCamera for 3D scene ────────────
var sceneViewport = editorView.SceneViewport;
var sceneViewportId = window.CreateRenderView(sceneViewport.Width, sceneViewport.Height);
sceneViewport.RenderView = sceneViewportId;
var sceneViewportPresentation = new ViewportPresentationTracker(sceneViewport);
sceneViewportPresentation.Synchronize(window);
window.SetViewportClearColor(sceneViewportId, 0.0f, 0.0f, 0.0f);

var sceneCamera = new PerspectiveCamera(
    fov: MathF.PI / 4f,
    aspect: sceneViewport.Width / sceneViewport.Height,
    near: 0.1f,
    far: 1000f);
sceneCamera.Position = new Vector3(4f, 3f, 6f);
sceneCamera.LookAt(Vector3.Zero);
sceneCamera.Name = "SceneCamera";
sceneViewport.Camera = sceneCamera;

Node3D sceneRoot;
List<MeshInstance3D> sceneObjects;
PerspectiveCamera gameCamera;
var discoveredScenes = project.FindSceneFiles();
string? activeScenePath = File.Exists(project.ScenePath)
    ? project.ScenePath : discoveredScenes.FirstOrDefault();
editorView.ProjectLabel.Text = activeScenePath is null
    ? "Untitled.node" : Path.GetFileName(activeScenePath);
if (activeScenePath is not null)
{
    try
    {
        var loadedScene = SceneFileStore.Load(activeScenePath, HudSceneNodeFactory.Instance);
        sceneRoot = loadedScene.Root;
        sceneObjects = loadedScene.MeshInstances;
        gameCamera = loadedScene.GameCamera;
        logger.LogInformation("Loaded scene {ScenePath}", activeScenePath);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
        or System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
    {
        logger.LogCritical(exception, "Could not load scene {ScenePath}", activeScenePath);
        return 3;
    }
}
else
{
var cube = new MeshInstance3D { Name = "SceneCube" };
    sceneObjects = [cube];
    sceneRoot = new Node3D { Name = "Scene" };
    gameCamera = new PerspectiveCamera(
        fov: MathF.PI / 4f,
        aspect: editorView.GameViewport.Width / editorView.GameViewport.Height,
        near: 0.1f,
        far: 1000f)
    {
        Name = "GameCamera",
        Position = new Vector3(4f, 3f, 6f)
    };
    gameCamera.LookAt(Vector3.Zero);
    sceneRoot.AddChild(cube);
    sceneRoot.AddChild(gameCamera);
}
var hierarchyTree = editorView.HierarchyTree;
var inspector = editorView.Inspector;
inspector.ResolveScriptName = id => assetDatabase.Find(id)?.ProjectPath;
inspector.ResolveMaterialName = ResolveMaterialDisplayName;
inspector.ResolveAssetReferenceName = ResolveAssetReferenceDisplayName;
hierarchyTree.SetRoots(sceneRoot.Children);

GameScriptHost? scriptHost = null;
PhysicsWorld? physicsWorld = null;
Func<AssetReference, TerrainResource?>? liveTerrainResolver = null;
GameScriptHost? scriptSchemaHost = null;
LoadedScene? playScene = null;
LoadedScene? pendingPlayScene = null;
Task<GameScriptHost>? playBuildTask = null;
CancellationTokenSource? playBuildCancellation = null;
Task<GameScriptHost>? scriptSchemaBuildTask = null;
CancellationTokenSource? scriptSchemaBuildCancellation = null;
CompilationProgressDialog? compilationProgressDialog = null;
AssetImportProgressDialog? assetImportProgressDialog = null;
Task<IReadOnlyList<AssetImportOutcome>>? assetImportTask = null;
CancellationTokenSource? assetImportCancellation = null;
var assetImportProgress = new ConcurrentQueue<AssetImportProgress>();
var queuedAssetImports = new Dictionary<AssetId, AssetMetadataRecord>();
AssetMetadataRecord[] activeAssetImports = [];
var assetBindings = new Queue<(MeshInstance3D Instance, AssetImportOutcome? MeshOutcome)>();
var queuedReloadAllAssetsAfterImport = false;
var activeReloadAllAssetsAfterImport = false;
Node3D? editSelectionBeforePlay = null;
var isPlaying = false;
var isShuttingDown = false;
inspector.ResolveScriptType = id =>
{
    var catalog = scriptHost?.Catalog ?? scriptSchemaHost?.Catalog;
    if (catalog?.TryResolve(id, out var type) == true)
        return type;
    return null;
};
inspector.ResolveScriptInstance = component =>
    scriptHost?.TryGetScript(component, out var script) == true ? script : null;
StartScriptSchemaBuild();

// ── Game viewport: scene rendered through its GameCamera ─────
var gameViewport = editorView.GameViewport;
gameViewport.ClipToBounds = true;
var gameViewportId = window.CreateRenderView(gameViewport.Width, gameViewport.Height);
gameViewport.RenderView = gameViewportId;
gameViewport.Camera = gameCamera;
var gameViewportPresentation = new ViewportPresentationTracker(gameViewport);
gameViewportPresentation.Synchronize(window);
window.SetViewportClearColor(gameViewportId, 0.05f, 0.05f, 0.12f);
HudRoot? presentedHud = null;
UIElement? presentedHudContent = null;

Vector2 lastMousePos = Vector2.Zero;
var selection = new SceneSelectionController(sceneObjects, sceneCamera, GetSceneGizmoViewport);
var synchronizingSelection = false;
AttachHierarchy(hierarchyTree);
selection.SelectionChanged += item =>
{
    if (synchronizingSelection)
        return;
    synchronizingSelection = true;
    hierarchyTree.Select(item);
    synchronizingSelection = false;
};
var flyInputWindow = window;
var flyCamera = new FlyCameraController(
    sceneCamera, captured => flyInputWindow.SetMouseCaptured(captured), selection.CancelInteraction);
using var sceneInputContext = new SceneViewportInputContext(sceneViewport, flyCamera);
using var viewportRenderer = new EditorViewportRenderer(
    window, sceneViewportId, gameViewportId, sceneCamera, gameCamera, sceneObjects, sceneRoot,
    selection, ResolveCollisionMeshResource, ResolveTerrainResource,
    ResolveAnimationSetClips);
var gameRenderingService = new EditorGameRenderingService(viewportRenderer);
selection.PreviewPicker = viewportRenderer.PickPreview;
var renderScheduler = new EditorRenderScheduler();
DetachedToolWindow? detachedSceneWindow = null;
DetachedToolWindow? detachedGameWindow = null;
EditorViewportRenderer? detachedSceneRenderer = null;
EditorViewportRenderer? detachedGameRenderer = null;
foreach (var previewItem in editorView.ScenePreviewItems)
{
    var category = previewItem.Key;
    var item = previewItem.Value;
    item.Click += () =>
    {
        viewportRenderer.SetPreviewCategoryVisible(category, item.IsChecked);
        detachedSceneRenderer?.SetPreviewCategoryVisible(category, item.IsChecked);
        renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
        flyInputWindow.RequestFrame();
    };
}

/// <summary>Aligns main-window viewport textures with their latest retained layout bounds.</summary>
void SynchronizeMainViewportPresentations()
{
    if (detachedSceneWindow is null)
    {
        sceneViewportPresentation.Synchronize(window, out var sceneTargetResized);
        if (sceneTargetResized)
            renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
    }
    viewportRenderer.SynchronizeSceneVisibility(
        detachedSceneWindow is null && sceneViewportPresentation.IsPresented);
    if (detachedGameWindow is null)
    {
        gameViewportPresentation.Synchronize(window, out var gameTargetResized);
        if (gameTargetResized)
            renderScheduler.Invalidate(RenderInvalidation.GameViewport);
    }
}

mainUIHost.LayoutUpdated += SynchronizeMainViewportPresentations;
dockSession.MainHost.SplitResizeCompleted += ResizeViewportTargets;
TerrainBrushController terrainBrush = null!;
dockWindowFactory.RegisterLifecycle(
    EditorDockWorkspace.SceneId, OpenFloatingSceneViewport, CloseFloatingSceneViewport);
dockWindowFactory.RegisterLifecycle(
    EditorDockWorkspace.GameId, OpenFloatingGameViewport, CloseFloatingGameViewport);

var uiEventRouter = mainUIHost.InputRouter;
dockSession.AttachDragRouter(dockSession.MainHost, uiEventRouter, window, RefreshUI);
ContextMenu? hierarchyContextMenu = null;
ContextMenu? fileContextMenu = null;
ContextMenu? fileSubmenu = null;
ScenePickerDialog? scenePickerDialog = null;
FileSystemCreateDialog? fileSystemCreateDialog = null;
ConfirmationDialog? confirmationDialog = null;
var fileSystemTree = editorView.FileSystemTree;
var terrainBrushSettings = new TerrainBrushSettings();
using var assetDocuments = new AssetDocumentService(reference =>
{
    assetDatabase.Refresh();
    runtimeResources.Invalidate(reference.Asset);
    viewportRenderer.InvalidatePreviewAsset(reference);
    detachedSceneRenderer?.InvalidatePreviewAsset(reference);
    detachedGameRenderer?.InvalidatePreviewAsset(reference);
    foreach (var sceneObject in sceneObjects)
    {
        if (sceneObject.Mesh.Asset == reference.Asset || sceneObject.Materials.Contains(reference))
        {
            LoadAssetMeshResources(sceneObject);
            if (detachedSceneRenderer is not null)
                LoadAssetMeshResources(sceneObject, targetRenderer: detachedSceneRenderer);
            if (detachedGameRenderer is not null)
                LoadAssetMeshResources(sceneObject, targetRenderer: detachedGameRenderer);
        }
    }
    InvalidateViewports();
});
assetDocuments.Register(new StandardMaterialDocumentFactory());
assetDocuments.Register(new TerrainDocumentFactory());
var editableAssetSources = new EditableAssetSourceRegistry();
editableAssetSources.Register("nico/standard-material", "standard-material");
editableAssetSources.Register("nico/terrain", "terrain");
var assetEditors = new AssetEditorRegistry(
    assetDocuments, ResolveAssetDocument, ResolveAssetReferenceDisplayName);
assetEditors.Register(new StandardMaterialInspectorFactory());
assetEditors.Register(new TerrainInspectorFactory(terrainBrushSettings));
liveTerrainResolver = reference => ResolveTerrainDocument(reference)?.Value;
terrainBrush = new TerrainBrushController(
    sceneCamera,
    GetSceneGizmoViewport,
    () => selection.SelectedNode,
    ResolveTerrainDocument,
    ApplyTerrainEdit,
    SetTerrainBrushPreview,
    terrainBrushSettings);
selection.SelectionChanged += _ => terrainBrush.RefreshToolState();
terrainBrushSettings.Changed += () =>
{
    terrainBrush.RefreshToolState();
    renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
    window.RequestFrame();
};
dockSession.SynchronizeFloatingWindows();
var assetDropResolver = new AssetDropResolver(assetDatabase, assetImportPipeline);
using var modelPreviewController = new InspectorModelPreviewController(
    window, window.RequestFrame);
mainUIHost.LayoutUpdated += modelPreviewController.Synchronize;
var inspectorProviders = new InspectorProviderRegistry();
inspectorProviders.Register(new ModelPreviewInspectorProvider(
    assetDatabase, assetImportPipeline, modelPreviewController));
inspectorProviders.Register(new AssetContentInspectorProvider(
    assetDatabase, assetEditors, () => inspector.Width - 24f));
inspectorProviders.Register(new ImportedSubAssetInspectorProvider());
inspectorProviders.Register(new FileSystemInspectorProvider(assetDatabase));
inspector.CreateAssetInspectorContent = reference =>
    assetEditors.Create(reference, MathF.Max(0f, inspector.Width - 24f));
var requestedFileSystemExpansion = new HashSet<string>(StringComparer.Ordinal);
var createdObjectIndex = 1;
var profilerRefreshPending = 0;
long workspaceSaveTimestamp = 0;
SetProfilerEnabled(!editorView.Profiler.IsPaused);
window.FrameProfiled += sample =>
{
    if (dockWorkspace.IsTabSelected(EditorDockWorkspace.ProfilerId) &&
        editorView.Profiler.AddSample(sample))
        Interlocked.Exchange(ref profilerRefreshPending, 1);
};
AttachFileSystem(fileSystemTree);
AttachInspector(inspector);
ConfigureEditorDragDrop();
animationSetEditor.ResolveFileAnimations = ResolveAnimationArtifacts;
animationSetEditor.ResolveRootJoints = ResolveAnimationRootJoints;
animationSetEditor.ResolveSourceDisplayName = ResolveAssetReferenceDisplayName;
animationSetEditor.AddRequested += () =>
{
    var added = fileSystemTree.SelectedItem switch
    {
        ImportedSubAssetNode imported => animationSetEditor.Add(imported) ? 1 : 0,
        FileSystemNode file => animationSetEditor.Add(file),
        _ => 0
    };
    if (added == 0)
        logger.LogWarning("Select an imported Animation row or an animation GLB before adding");
    RefreshVertices();
};
animationSetEditor.Saved += path =>
{
    assetDatabase.Refresh();
    var record = assetDatabase.FindByPath(path);
    if (record is not null)
        assetImportPipeline.Import(record, "editor");
    RefreshFileSystem();
};
RefreshFileSystem();
AttachTitleBar(editorView.TitleBar);
AttachPlayButton(editorView.PlayButton);
editorView.HierarchyButton.Click += () => OpenDockPanel(
    EditorDockWorkspace.HierarchyId, EditorDockWorkspace.FileSystemId);
editorView.FileSystemButton.Click += () => OpenDockPanel(
    EditorDockWorkspace.FileSystemId, EditorDockWorkspace.HierarchyId);
editorView.SceneButton.Click += () => OpenDockPanel(
    EditorDockWorkspace.SceneId, EditorDockWorkspace.GameId);
editorView.GameButton.Click += () => OpenDockPanel(
    EditorDockWorkspace.GameId, EditorDockWorkspace.SceneId);
editorView.InspectorButton.Click += () => OpenDockPanel(
    EditorDockWorkspace.InspectorId, EditorDockWorkspace.SceneId);
editorView.ProfilerButton.Click += ToggleProfiler;
editorView.ProfilerPauseButton.Click += ToggleProfilerPause;
foreach (var panelItem in editorView.WindowPanelItems)
{
    var panelId = panelItem.Key;
    var item = panelItem.Value;
    item.Click += () => SetDockPanelVisible(panelId, item.IsChecked);
}
dockSession.WorkspaceChanged += () =>
{
    SynchronizeWindowMenuChecks();
    workspaceSaveTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
    window.RequestFrame();
};
SynchronizeWindowMenuChecks();
editorView.Profiler.Click += () =>
{
    if (editorView.Profiler.SelectFrame(lastMousePos))
    {
        SetProfilerEnabled(false);
        editorView.ProfilerPauseLabel.Text = "Record";
    }
};
QueueAssetImports(assetDatabase.Assets, reloadAllSceneAssets: true);
RefreshVertices();

/// <summary>Persists the current project-scoped dock layout with recoverable diagnostics.</summary>
void SaveDockWorkspace()
{
    try
    {
        EditorDockWorkspace.Save(project.RootPath, dockSession.Workspace);
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        logger.LogWarning(exception, "Could not save the Editor dock workspace");
    }
}

/// <summary>Connects focused Scene viewport input to one native-window UI host.</summary>
/// <param name="host">Host whose independent router owns focus for the viewport.</param>
/// <param name="includePointerLook">Whether this host also needs standalone fly-camera pointer routing.</param>
void ConfigureSceneViewportInput(UIHost host, IWindow inputWindow, bool includePointerLook)
{
    host.PreviewKey = keyEvent =>
    {
        if (keyEvent.IsPressed && keyEvent.Key == InputKey.Escape && terrainBrush.Cancel())
        {
            inputWindow.RequestFrame();
            return true;
        }
        return sceneInputContext.RouteKey(host.InputRouter, keyEvent);
    };
    host.PreviewTextInput = _ => sceneInputContext.RoutesText(host.InputRouter);
    host.PreviewTextComposition = _ => sceneInputContext.RoutesText(host.InputRouter);
    host.PreviewPointerWheel = pointerEvent =>
        ApplySceneWheelOrTrackpadGesture(pointerEvent, inputWindow);
    host.PreviewPointerMagnify = pointerEvent =>
        ApplyScenePinchGesture(pointerEvent, inputWindow);
    if (!includePointerLook)
        return;
    host.PreviewPointerMove = pointerEvent =>
    {
        if (terrainBrush.MovePointer(pointerEvent.Position))
        {
            inputWindow.RequestFrame();
            return true;
        }
        return flyCamera.MovePointer(pointerEvent.Position);
    };
    host.PreviewPointerButton = pointerEvent =>
    {
        if (!pointerEvent.IsPressed && pointerEvent.Button == InputPointerButton.Primary &&
            terrainBrush.PrimaryUp())
        {
            inputWindow.RequestFrame();
            return UIHostPointerRouting.Consume;
        }
        if (pointerEvent.IsPressed && pointerEvent.Button == InputPointerButton.Primary &&
            IsInsideSceneViewport(pointerEvent.Position) &&
            terrainBrush.PrimaryDown(pointerEvent.Position))
        {
            selection.CancelInteraction();
            inputWindow.RequestFrame();
            return UIHostPointerRouting.Consume;
        }
        return flyCamera.IsActive
            ? UIHostPointerRouting.Consume
            : UIHostPointerRouting.Route;
    };
}

/// <summary>Routes desktop wheel zoom or a macOS two-finger Scene camera gesture.</summary>
/// <param name="pointerEvent">Wheel-compatible pointer gesture.</param>
/// <param name="inputWindow">Window to wake after changing the camera.</param>
/// <returns>True when the Scene viewport consumed the gesture.</returns>
bool ApplySceneWheelOrTrackpadGesture(PointerWheelEvent pointerEvent, IWindow inputWindow)
{
    if (flyCamera.IsActive)
        return true;
    if (!IsInsideSceneViewport(pointerEvent.Position))
        return false;
    if (OperatingSystem.IsMacOS())
    {
        flyCamera.ApplyTwoFingerGesture(pointerEvent.Delta,
            (pointerEvent.Modifiers & InputModifiers.Shift) != 0);
    }
    else
    {
        flyCamera.ApplyMouseWheelZoom(pointerEvent.Delta.Y);
    }
    renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
    inputWindow.RequestFrame();
    return true;
}

/// <summary>Moves the Scene camera forward or backward from native trackpad magnification.</summary>
/// <param name="pointerEvent">Incremental magnification gesture.</param>
/// <param name="inputWindow">Window to wake after changing the camera.</param>
/// <returns>True when the Scene viewport consumed the gesture.</returns>
bool ApplyScenePinchGesture(PointerMagnifyEvent pointerEvent, IWindow inputWindow)
{
    if (flyCamera.IsActive)
        return true;
    if (!IsInsideSceneViewport(pointerEvent.Position))
        return false;
    flyCamera.ApplyPinchZoom(pointerEvent.Delta);
    renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
    inputWindow.RequestFrame();
    return true;
}

/// <summary>Checks whether a host-local position lies in the visible Scene viewport.</summary>
/// <param name="position">Logical host position.</param>
/// <returns>True when the visible Scene viewport contains the position.</returns>
bool IsInsideSceneViewport(Vector2 position)
{
    return sceneViewport.IsEffectivelyVisible &&
        position.X >= sceneViewport.Left && position.X <= sceneViewport.Right &&
        position.Y >= sceneViewport.Top && position.Y <= sceneViewport.Bottom;
}

/// <summary>Transfers Scene rendering into a newly opened dock window.</summary>
/// <param name="toolWindow">Opened floating tool window.</param>
void OpenFloatingSceneViewport(DetachedToolWindow toolWindow)
{
    detachedSceneWindow = toolWindow;
    viewportRenderer.SynchronizeSceneVisibility(false);
    window.DestroyRenderView(sceneViewportId);
    sceneViewportPresentation.Reset();
    var detachedWindow = toolWindow.Window;
    flyCamera.ReleaseFocus();
    flyInputWindow = detachedWindow;
    ConfigureSceneViewportInput(toolWindow.UIHost, detachedWindow, includePointerLook: true);
    sceneViewportId = detachedWindow.CreateRenderView(sceneViewport.Width, sceneViewport.Height);
    sceneViewport.RenderView = sceneViewportId;
    detachedWindow.SetViewportClearColor(sceneViewportId, 0f, 0f, 0f);
    sceneViewportPresentation.Synchronize(detachedWindow);
    toolWindow.UIHost.LayoutUpdated += () =>
        sceneViewportPresentation.Synchronize(detachedWindow);
    if (toolWindow.Content is DockHost sceneDockHost)
    {
        sceneDockHost.SplitResizeCompleted += () =>
        {
            detachedWindow.ResizeRenderView(
                sceneViewportId, sceneViewport.Width, sceneViewport.Height);
            detachedWindow.RequestFrame();
        };
    }
    detachedSceneRenderer = new EditorViewportRenderer(
        detachedWindow, sceneViewportId, sceneViewportId,
        sceneCamera, gameViewport.Camera ?? gameCamera, GetActiveSceneObjects(), GetActiveSceneRoot(),
        selection, ResolveCollisionMeshResource, ResolveTerrainResource);
    foreach (var previewItem in editorView.ScenePreviewItems)
        detachedSceneRenderer.SetPreviewCategoryVisible(
            previewItem.Key, previewItem.Value.IsChecked);
    foreach (var assetMesh in GetActiveSceneObjects())
        LoadAssetMeshResources(assetMesh, targetRenderer: detachedSceneRenderer);
    detachedWindow.Resized += (_, _) =>
    {
        detachedWindow.ResizeRenderView(
            sceneViewportId, sceneViewport.Width, sceneViewport.Height);
        detachedWindow.RequestFrame();
    };
    detachedWindow.Update += _ =>
    {
        detachedSceneRenderer?.SetSceneObjects(GetActiveSceneObjects());
        detachedSceneRenderer?.SetSceneRoot(GetActiveSceneRoot());
        detachedSceneRenderer?.RenderScene(
            sceneViewport, toolWindow.UIHost.PointerPosition);
    };
    ResizeEditor(width, height);
}

/// <summary>Returns Scene rendering to the main window before a dock window closes.</summary>
/// <param name="toolWindow">Closing floating tool window.</param>
void CloseFloatingSceneViewport(DetachedToolWindow toolWindow)
{
    if (!ReferenceEquals(detachedSceneWindow, toolWindow))
        return;
    detachedSceneRenderer?.Dispose();
    detachedSceneRenderer = null;
    flyCamera.ReleaseFocus();
    flyInputWindow = window;
    toolWindow.Window.DestroyRenderView(sceneViewportId);
    sceneViewportPresentation.Reset();
    detachedSceneWindow = null;
    sceneViewportId = window.CreateRenderView(sceneViewport.Width, sceneViewport.Height);
    sceneViewport.RenderView = sceneViewportId;
    viewportRenderer.SetSceneRenderView(sceneViewportId);
    window.SetViewportClearColor(sceneViewportId, 0f, 0f, 0f);
    uiRoot.InvalidateMeasure();
    ResizeEditor(width, height);
    renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
    window.RequestFrame();
}

/// <summary>Transfers Game rendering into a newly opened dock window.</summary>
/// <param name="toolWindow">Opened floating tool window.</param>
void OpenFloatingGameViewport(DetachedToolWindow toolWindow)
{
    detachedGameWindow = toolWindow;
    window.DestroyRenderView(gameViewportId);
    gameViewportPresentation.Reset();
    var detachedWindow = toolWindow.Window;
    gameViewportId = detachedWindow.CreateRenderView(gameViewport.Width, gameViewport.Height);
    gameViewport.RenderView = gameViewportId;
    detachedWindow.SetViewportClearColor(gameViewportId, 0.05f, 0.05f, 0.12f);
    gameViewportPresentation.Synchronize(detachedWindow);
    toolWindow.UIHost.LayoutUpdated += () =>
        gameViewportPresentation.Synchronize(detachedWindow);
    if (toolWindow.Content is DockHost gameDockHost)
    {
        gameDockHost.SplitResizeCompleted += () =>
        {
            detachedWindow.ResizeRenderView(
                gameViewportId, gameViewport.Width, gameViewport.Height);
            detachedWindow.RequestFrame();
        };
    }
    detachedGameRenderer = new EditorViewportRenderer(
        detachedWindow, gameViewportId, gameViewportId,
        sceneCamera, gameViewport.Camera ?? gameCamera, GetActiveSceneObjects(), GetActiveSceneRoot(),
        selection, ResolveCollisionMeshResource, ResolveTerrainResource);
    gameRenderingService.SetDetachedRenderer(detachedGameRenderer);
    foreach (var assetMesh in GetActiveSceneObjects())
        LoadAssetMeshResources(assetMesh, targetRenderer: detachedGameRenderer);
    detachedWindow.Resized += (_, _) =>
    {
        detachedWindow.ResizeRenderView(
            gameViewportId, gameViewport.Width, gameViewport.Height);
        detachedWindow.RequestFrame();
    };
    detachedWindow.Update += _ =>
    {
        detachedGameRenderer?.SetGameScene(
            gameViewport.Camera ?? gameCamera, GetActiveSceneObjects());
        detachedGameRenderer?.RenderGame(gameViewport);
    };
    ResizeEditor(width, height);
}

/// <summary>Returns Game rendering to the main window before a dock window closes.</summary>
/// <param name="toolWindow">Closing floating tool window.</param>
void CloseFloatingGameViewport(DetachedToolWindow toolWindow)
{
    if (!ReferenceEquals(detachedGameWindow, toolWindow))
        return;
    detachedGameRenderer?.Dispose();
    gameRenderingService.SetDetachedRenderer(null);
    detachedGameRenderer = null;
    toolWindow.Window.DestroyRenderView(gameViewportId);
    gameViewportPresentation.Reset();
    detachedGameWindow = null;
    gameViewportId = window.CreateRenderView(gameViewport.Width, gameViewport.Height);
    gameViewport.RenderView = gameViewportId;
    viewportRenderer.SetGameRenderView(gameViewportId);
    window.SetViewportClearColor(gameViewportId, 0.05f, 0.05f, 0.12f);
    uiRoot.InvalidateMeasure();
    ResizeEditor(width, height);
    renderScheduler.Invalidate(RenderInvalidation.GameViewport);
    window.RequestFrame();
}

/// <summary>Starts a background build of edit-mode script metadata.</summary>
void StartScriptSchemaBuild()
{
    if (isShuttingDown || isPlaying || playBuildTask is not null ||
        scriptSchemaBuildTask is not null)
        return;
    scriptSchemaBuildCancellation = new CancellationTokenSource();
    var cancellationToken = scriptSchemaBuildCancellation.Token;
    scriptSchemaBuildTask = Task.Run(
        () => scriptCompiler.BuildAndLoad(cancellationToken), cancellationToken);
}

/// <summary>Publishes a completed background schema build to edit-mode Inspector bindings.</summary>
void UpdateScriptSchemaBuild()
{
    if (scriptSchemaBuildTask is not { IsCompleted: true } build)
        return;
    scriptSchemaBuildTask = null;
    scriptSchemaBuildCancellation?.Dispose();
    scriptSchemaBuildCancellation = null;
    try
    {
        var replacement = build.GetAwaiter().GetResult();
        var previous = scriptSchemaHost;
        scriptSchemaHost = replacement;
        previous?.Dispose();
        inspector.RefreshScriptSchemas();
        logger.LogInformation("Refreshed Inspector schemas for {ScriptCount} scripts",
            replacement.Catalog is CompiledScriptTypeCatalog catalog ? catalog.Count : 0);
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Could not compile script schemas for the Inspector");
    }
}

/// <summary>Starts an isolated runtime copy of the authored scene.</summary>
void StartPlayMode()
{
    if (isPlaying || playBuildTask is not null)
        return;
    terrainBrushSettings.IsEnabled = false;
    OpenDockPanel(EditorDockWorkspace.GameId, EditorDockWorkspace.SceneId);
    try
    {
        pendingPlayScene = ScenePlayClone.Create(sceneRoot, gameCamera);
        ShowCompilationProgress();
        playBuildCancellation = new CancellationTokenSource();
        var cancellationToken = playBuildCancellation.Token;
        playBuildTask = Task.Run(
            () => scriptCompiler.BuildAndLoad(cancellationToken), cancellationToken);
    }
    catch (Exception exception)
    {
        pendingPlayScene = null;
        CloseCompilationProgress();
        logger.LogError(exception, "Could not enter play mode");
    }
}

/// <summary>Completes a background script build and enters play mode on the main thread.</summary>
/// <param name="deltaTime">Elapsed time used to animate compilation progress.</param>
void UpdatePlayModeStart(double deltaTime)
{
    if (playBuildTask is not { } build)
        return;
    if (!build.IsCompleted)
    {
        compilationProgressDialog?.Update(deltaTime);
        RefreshVertices();
        return;
    }

    var candidateScene = pendingPlayScene;
    playBuildTask = null;
    playBuildCancellation?.Dispose();
    playBuildCancellation = null;
    pendingPlayScene = null;
    CloseCompilationProgress();
    GameScriptHost? candidateHost = null;
    PhysicsWorld? candidatePhysicsWorld = null;
    try
    {
        candidateHost = build.GetAwaiter().GetResult();
        if (candidateScene is null)
            throw new InvalidOperationException("The pending play scene is unavailable.");
        viewportRenderer.SetGameScene(candidateScene.GameCamera,
            candidateScene.MeshInstances);
        foreach (var assetMesh in candidateScene.MeshInstances)
        {
            if (!viewportRenderer.HasAssetMeshResource(assetMesh))
                LoadAssetMeshResources(assetMesh);
        }
        candidateHost.LoadScene(candidateScene.Root, window,
            viewportRenderer.AnimationService, gameRenderingService,
            new SceneAssetRegistry(path => assetDatabase.FindByPath(path)?.Id));
        candidatePhysicsWorld = new PhysicsWorld(
            ResolveCollisionMeshResource, ResolveTerrainResource);
        candidatePhysicsWorld.EnableInterpolation = true;
        candidatePhysicsWorld.Attach(candidateScene.Root);
        editSelectionBeforePlay = selection.SelectedNode;
        selection.SetObjects(candidateScene.MeshInstances);
        playScene = candidateScene;
        scriptHost = candidateHost;
        physicsWorld = candidatePhysicsWorld;
        isPlaying = true;
        viewportRenderer.RemapStaticAssetMeshResources(sceneObjects,
            candidateScene.MeshInstances);
        detachedSceneRenderer?.RemapStaticAssetMeshResources(sceneObjects,
            candidateScene.MeshInstances);
        detachedGameRenderer?.RemapStaticAssetMeshResources(sceneObjects,
            candidateScene.MeshInstances);
        viewportRenderer.SetSceneObjects(candidateScene.MeshInstances);
        viewportRenderer.SetSceneRoot(candidateScene.Root);
        hierarchyTree.SetRoots(candidateScene.Root.Children);
        inspector.RefreshScriptSchemas();
        gameViewport.Camera = candidateScene.GameCamera;
        viewportRenderer.SetGameScene(candidateScene.GameCamera, candidateScene.MeshInstances);
        foreach (var assetMesh in candidateScene.MeshInstances)
        {
            if (!viewportRenderer.HasAssetMeshResource(assetMesh))
                LoadAssetMeshResources(assetMesh);
            if (detachedSceneRenderer is not null &&
                !detachedSceneRenderer.HasAssetMeshResource(assetMesh))
                LoadAssetMeshResources(assetMesh, targetRenderer: detachedSceneRenderer);
            if (detachedGameRenderer is not null &&
                !detachedGameRenderer.HasAssetMeshResource(assetMesh))
                LoadAssetMeshResources(assetMesh, targetRenderer: detachedGameRenderer);
        }
        MountPlayHud(candidateScene.Root);
        editorView.PlayButtonIcon.Kind = IconKind.Stop;
        logger.LogInformation("Entered play mode with {ScriptCount} scripts",
            candidateHost.ScriptCount);
    }
    catch (Exception exception)
    {
        UnmountPlayHud();
        viewportRenderer.SetGameScene(gameCamera, sceneObjects);
        candidatePhysicsWorld?.Dispose();
        try
        {
            candidateHost?.Dispose();
        }
        catch (Exception disposalException)
        {
            logger.LogError(disposalException, "Could not unload a failed game script build");
        }
        if (exception is ScriptBuildException buildException)
        {
            foreach (var diagnostic in buildException.Diagnostics)
            {
                logger.LogError("{File}({Line},{Column}): {Code}: {Message}",
                    diagnostic.File, diagnostic.Line, diagnostic.Column,
                    diagnostic.Code, diagnostic.Message);
            }
        }
        logger.LogError(exception, "Could not enter play mode");
    }
    RefreshVertices();
}

/// <summary>Shows modal compilation progress above the editor.</summary>
void ShowCompilationProgress()
{
    CloseCompilationProgress();
    compilationProgressDialog = new CompilationProgressDialog(width, height)
        { Name = "CompilationProgressDialog" };
    overlay.Add(compilationProgressDialog, Vector2.Zero);
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
}

/// <summary>Removes modal compilation progress from the editor.</summary>
void CloseCompilationProgress()
{
    if (compilationProgressDialog is null)
        return;
    if (ReferenceEquals(compilationProgressDialog.Parent, overlay))
        overlay.Remove(compilationProgressDialog);
    compilationProgressDialog = null;
    RefreshVertices();
}

/// <summary>Queues asset records for bounded background import without blocking the editor thread.</summary>
/// <param name="records">Current asset records to import.</param>
/// <param name="reloadAllSceneAssets">Whether every scene mesh must bind after this batch.</param>
void QueueAssetImports(
    IReadOnlyList<AssetMetadataRecord> records,
    bool reloadAllSceneAssets = false)
{
    for (var index = 0; index < records.Count; index++)
        queuedAssetImports[records[index].Id] = records[index];
    queuedReloadAllAssetsAfterImport |= reloadAllSceneAssets;
    StartNextAssetImportBatch();
}

/// <summary>Starts the pending background-import batch when no earlier batch is active.</summary>
void StartNextAssetImportBatch()
{
    if (assetImportTask is not null || queuedAssetImports.Count == 0 || isShuttingDown)
        return;
    var records = new List<AssetMetadataRecord>(queuedAssetImports.Count);
    foreach (var pair in queuedAssetImports)
        records.Add(pair.Value);
    queuedAssetImports.Clear();
    activeAssetImports = records.ToArray();
    activeReloadAllAssetsAfterImport = queuedReloadAllAssetsAfterImport;
    queuedReloadAllAssetsAfterImport = false;
    while (assetImportProgress.TryDequeue(out _))
    {
    }
    assetImportCancellation = new CancellationTokenSource();
    var progress = new InlineProgress<AssetImportProgress>(assetImportProgress.Enqueue);
    var workerCount = Math.Clamp(Environment.ProcessorCount - 1, 1, 4);
    assetImportTask = assetImportPipeline.ImportAsync(
        activeAssetImports, "editor", workerCount, assetImportCancellation.Token, progress);
    if (assetImportProgressDialog is not null &&
        ReferenceEquals(assetImportProgressDialog.Parent, overlay))
        overlay.Remove(assetImportProgressDialog);
    assetImportProgressDialog = new AssetImportProgressDialog(
        width, height, activeAssetImports.Length) { Name = "AssetImportProgressDialog" };
    overlay.Add(assetImportProgressDialog, Vector2.Zero);
    uiEventRouter.MovePointer(lastMousePos);
    window.RequestFrame();
    logger.LogInformation("Started background import for {AssetCount} assets on {WorkerCount} workers",
        activeAssetImports.Length, workerCount);
}

/// <summary>Applies background-import progress and completed publications on the editor thread.</summary>
void UpdateAssetImports()
{
    AssetImportProgress? latestProgress = null;
    while (assetImportProgress.TryDequeue(out var progress))
        latestProgress = progress;
    if (latestProgress is not null)
    {
        assetImportProgressDialog?.SetProgress(
            latestProgress.CompletedCount,
            latestProgress.TotalCount,
            latestProgress.Asset.ProjectPath);
        window.RequestFrame();
    }
    if (assetImportTask is not { IsCompleted: true } completedTask)
        return;
    assetImportTask = null;
    assetImportCancellation?.Dispose();
    assetImportCancellation = null;
    try
    {
        var outcomes = completedTask.GetAwaiter().GetResult();
        var outcomesByAsset = new Dictionary<AssetId, AssetImportOutcome>(outcomes.Count);
        for (var index = 0; index < outcomes.Count; index++)
        {
            var outcome = outcomes[index];
            outcomesByAsset[outcome.Asset] = outcome;
            if (outcome.Succeeded)
                continue;
            for (var diagnosticIndex = 0;
                 diagnosticIndex < outcome.Diagnostics.Count;
                 diagnosticIndex++)
            {
                var diagnostic = outcome.Diagnostics[diagnosticIndex];
                logger.LogError("Asset import {Asset}: {Code}: {Message}",
                    outcome.Asset, diagnostic.Code, diagnostic.Message);
            }
        }
        for (var index = 0; index < sceneObjects.Count; index++)
        {
            var instance = sceneObjects[index];
            var affected = activeReloadAllAssetsAfterImport ||
                outcomesByAsset.ContainsKey(instance.Mesh.Asset);
            if (!affected)
            {
                for (var materialIndex = 0;
                     materialIndex < instance.Materials.Count && !affected;
                     materialIndex++)
                    affected = outcomesByAsset.ContainsKey(instance.Materials[materialIndex].Asset);
            }
            if (!affected)
                continue;
            outcomesByAsset.TryGetValue(instance.Mesh.Asset, out var meshOutcome);
            assetBindings.Enqueue((instance, meshOutcome));
        }
        logger.LogInformation("Completed background import for {AssetCount} assets",
            outcomes.Count);
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Background asset import failed");
    }
    activeReloadAllAssetsAfterImport = false;
    activeAssetImports = [];
    if (assetImportProgressDialog is not null &&
        ReferenceEquals(assetImportProgressDialog.Parent, overlay))
        overlay.Remove(assetImportProgressDialog);
    assetImportProgressDialog = null;
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
    StartNextAssetImportBatch();
}

/// <summary>Binds at most one completed scene resource per frame to avoid a long publication spike.</summary>
void UpdateAssetBindings()
{
    if (!assetBindings.TryDequeue(out var binding))
        return;
    try
    {
        LoadAssetMeshResources(binding.Instance, binding.MeshOutcome);
        if (detachedSceneRenderer is not null)
            LoadAssetMeshResources(binding.Instance, binding.MeshOutcome, detachedSceneRenderer);
        if (detachedGameRenderer is not null)
            LoadAssetMeshResources(binding.Instance, binding.MeshOutcome, detachedGameRenderer);
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Could not bind imported resources for {Node}",
            binding.Instance.Name);
    }
    InvalidateViewports();
    window.RequestFrame();
}

/// <summary>Mounts the active play scene's HUD above the Game viewport texture.</summary>
/// <param name="root">Play-scene synthetic root.</param>
void MountPlayHud(Node root)
{
    UnmountPlayHud();
    HudRoot? found = null;
    FindHudRoot(root, ref found);
    if (found is null)
        return;
    presentedHud = found;
    presentedHud.ContentChanged += HandlePlayHudContentChanged;
    AttachPlayHudContent(presentedHud.Content);
}

/// <summary>Finds the single HUD root in one scene subtree.</summary>
/// <param name="node">Current subtree node.</param>
/// <param name="found">Previously found HUD root, if any.</param>
void FindHudRoot(Node node, ref HudRoot? found)
{
    if (node is HudRoot hud)
    {
        if (found is not null)
            throw new InvalidOperationException("A scene can contain only one HUD root.");
        found = hud;
    }
    var children = node.Children;
    for (var index = 0; index < children.Count; index++)
        FindHudRoot(children[index], ref found);
}

/// <summary>Replaces the HUD tree mounted over the Game viewport.</summary>
/// <param name="previous">Previously mounted tree.</param>
/// <param name="current">Replacement tree.</param>
void HandlePlayHudContentChanged(UIElement previous, UIElement current)
{
    if (ReferenceEquals(previous.Parent, gameViewport))
        gameViewport.RemoveChild(previous);
    presentedHudContent = null;
    AttachPlayHudContent(current);
}

/// <summary>Attaches one detached retained tree to the Game viewport overlay.</summary>
/// <param name="content">Detached HUD tree.</param>
void AttachPlayHudContent(UIElement content)
{
    if (content.Parent is not null)
        throw new InvalidOperationException("HUD content is already mounted by another host.");
    content.IsOverlay = true;
    content.ClipToBounds = true;
    gameViewport.AddChild(content);
    presentedHudContent = content;
}

/// <summary>Disconnects and removes the currently presented play-scene HUD.</summary>
void UnmountPlayHud()
{
    if (presentedHud is not null)
        presentedHud.ContentChanged -= HandlePlayHudContentChanged;
    if (presentedHudContent is not null &&
        ReferenceEquals(presentedHudContent.Parent, gameViewport))
    {
        gameViewport.RemoveChild(presentedHudContent);
    }
    presentedHud = null;
    presentedHudContent = null;
}

/// <summary>Stops scripts and discards the isolated runtime scene.</summary>
void StopPlayMode()
{
    if (!isPlaying && scriptHost is null && playScene is null)
        return;
    try
    {
        scriptHost?.Dispose();
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "A script failed while leaving play mode");
    }
    UnmountPlayHud();
    var runtimeObjects = playScene?.MeshInstances;
    if (runtimeObjects is not null)
    {
        viewportRenderer.RemapStaticAssetMeshResources(runtimeObjects, sceneObjects);
        detachedSceneRenderer?.RemapStaticAssetMeshResources(runtimeObjects, sceneObjects);
        detachedGameRenderer?.RemapStaticAssetMeshResources(runtimeObjects, sceneObjects);
    }
    scriptHost = null;
    physicsWorld?.Dispose();
    physicsWorld = null;
    playScene = null;
    isPlaying = false;
    selection.SetObjects(sceneObjects);
    viewportRenderer.SetSceneObjects(sceneObjects);
    viewportRenderer.SetSceneRoot(sceneRoot);
    hierarchyTree.SetRoots(sceneRoot.Children);
    selection.Select(editSelectionBeforePlay);
    inspector.RefreshScriptSchemas();
    editSelectionBeforePlay = null;
    gameViewport.Camera = gameCamera;
    viewportRenderer.SetGameScene(gameCamera, sceneObjects);
    foreach (var assetMesh in sceneObjects)
    {
        if (!viewportRenderer.HasAssetMeshResource(assetMesh))
            LoadAssetMeshResources(assetMesh);
        if (detachedSceneRenderer is not null &&
            !detachedSceneRenderer.HasAssetMeshResource(assetMesh))
            LoadAssetMeshResources(assetMesh, targetRenderer: detachedSceneRenderer);
        if (detachedGameRenderer is not null &&
            !detachedGameRenderer.HasAssetMeshResource(assetMesh))
            LoadAssetMeshResources(assetMesh, targetRenderer: detachedGameRenderer);
    }
    editorView.PlayButtonIcon.Kind = IconKind.Play;
    logger.LogInformation("Exited play mode");
    StartScriptSchemaBuild();
    OpenDockPanel(EditorDockWorkspace.SceneId, EditorDockWorkspace.GameId);
    RefreshVertices();
}

/// <summary>Connects the title-bar play control to the current play state.</summary>
/// <param name="playButton">Play/Stop button to attach.</param>
void AttachPlayButton(Button playButton)
{
    editorView.PlayButtonIcon.Kind = isPlaying ? IconKind.Stop : IconKind.Play;
    playButton.Click += () =>
    {
        if (isPlaying)
            StopPlayMode();
        else
            StartPlayMode();
    };
}

/// <summary>Activates the docked Profiler tab.</summary>
void ToggleProfiler()
{
    if (!dockSession.OpenPanel(
            EditorDockWorkspace.ProfilerId, EditorDockWorkspace.GameId))
        return;
    SetProfilerEnabled(!editorView.Profiler.IsPaused);
    ResizeEditor(width, height);
    window.RequestFrame();
}

/// <summary>Activates or restores one registered Editor panel.</summary>
/// <param name="panelId">Stable panel identifier.</param>
/// <param name="anchorId">Preferred sibling panel identifier.</param>
void OpenDockPanel(string panelId, string anchorId)
{
    if (!dockSession.OpenPanel(panelId, anchorId))
        return;
    ResizeEditor(width, height);
    ResizeViewportTargets();
    window.RequestFrame();
}

/// <summary>Shows or closes one panel selected through the title-bar Window menu.</summary>
/// <param name="panelId">Stable dock-panel identifier.</param>
/// <param name="visible">Whether the panel should remain open.</param>
void SetDockPanelVisible(string panelId, bool visible)
{
    var changed = visible
        ? dockSession.OpenPanel(panelId, GetDockPanelAnchor(panelId))
        : dockSession.ClosePanel(panelId);
    if (!changed)
    {
        SynchronizeWindowMenuChecks();
        return;
    }
    if (visible && panelId == EditorDockWorkspace.ProfilerId)
        SetProfilerEnabled(!editorView.Profiler.IsPaused);
    ResizeEditor(width, height);
    ResizeViewportTargets();
    window.RequestFrame();
}

/// <summary>Returns the preferred sibling used when restoring one closed panel.</summary>
/// <param name="panelId">Stable dock-panel identifier.</param>
/// <returns>Preferred anchor identifier.</returns>
string GetDockPanelAnchor(string panelId) => panelId switch
{
    EditorDockWorkspace.HierarchyId => EditorDockWorkspace.FileSystemId,
    EditorDockWorkspace.FileSystemId => EditorDockWorkspace.HierarchyId,
    EditorDockWorkspace.SceneId => EditorDockWorkspace.GameId,
    EditorDockWorkspace.GameId => EditorDockWorkspace.SceneId,
    EditorDockWorkspace.InspectorId => EditorDockWorkspace.SceneId,
    EditorDockWorkspace.ProfilerId => EditorDockWorkspace.GameId,
    _ => EditorDockWorkspace.SceneId
};

/// <summary>Synchronizes Window-menu checks with docked and floating panel membership.</summary>
void SynchronizeWindowMenuChecks()
{
    foreach (var panelItem in editorView.WindowPanelItems)
        panelItem.Value.SetChecked(dockWorkspace.ContainsTab(panelItem.Key));
}

/// <summary>Toggles live frame recording while retaining captured history.</summary>
void ToggleProfilerPause()
{
    editorView.Profiler.SetPaused(!editorView.Profiler.IsPaused);
    SetProfilerEnabled(!editorView.Profiler.IsPaused);
    editorView.ProfilerPauseLabel.Text = editorView.Profiler.IsPaused ? "Record" : "Pause";
}

/// <summary>Enables or disables all frame instrumentation owned by the Profiler.</summary>
/// <param name="enabled">Whether new CPU and GPU measurements should be captured.</param>
void SetProfilerEnabled(bool enabled)
{
    CpuProfiler.Enabled = enabled;
    GpuProfiling.Enabled = enabled;
}

/// <summary>Closes the hierarchy's object-creation menu.</summary>
void CloseHierarchyContextMenu()
{
    if (hierarchyContextMenu is null)
        return;
    overlay.Remove(hierarchyContextMenu);
    hierarchyContextMenu = null;
    RefreshVertices();
}

/// <summary>Closes the filesystem context menu or scene picker.</summary>
void CloseFileContextMenu()
{
    if (fileContextMenu is not null)
        overlay.Remove(fileContextMenu);
    if (fileSubmenu is not null)
        overlay.Remove(fileSubmenu);
    if (scenePickerDialog is not null)
        overlay.Remove(scenePickerDialog);
    if (fileSystemCreateDialog is not null)
        overlay.Remove(fileSystemCreateDialog);
    if (confirmationDialog is not null)
        overlay.Remove(confirmationDialog);
    fileContextMenu = null;
    fileSubmenu = null;
    scenePickerDialog = null;
    fileSystemCreateDialog = null;
    confirmationDialog = null;
    RefreshVertices();
}

/// <summary>Saves the current scene to its active scene file.</summary>
void SaveScene()
{
    if (activeScenePath is null)
    {
        ShowScenePathDialog(project.RootPath, createDefaultScene: false, saveAction: true);
        return;
    }
    try
    {
        SceneFileStore.Save(activeScenePath, sceneRoot, gameCamera);
        logger.LogInformation("Saved scene {ScenePath}", activeScenePath);
        RefreshFileSystem();
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
        or InvalidOperationException or NotSupportedException)
    {
        logger.LogError(exception, "Could not save scene {ScenePath}", activeScenePath);
    }
    CloseFileContextMenu();
}

var saveSceneCommand = new UICommand("SaveScene");
uiRoot.CommandBindings.Add(new UICommandBinding(saveSceneCommand, _ => SaveScene()));
uiRoot.KeyBindings.Add(new UIKeyBinding(
    new UIKeyGesture(InputKey.S, InputModifiers.Control), saveSceneCommand));
uiRoot.KeyBindings.Add(new UIKeyBinding(
    new UIKeyGesture(InputKey.S, InputModifiers.Super), saveSceneCommand));

/// <summary>Loads a scene into the editor and optionally makes it the active save target.</summary>
/// <param name="scenePath">Scene file to load.</param>
/// <param name="makeActive">Whether successful loading changes the active scene path.</param>
/// <returns>True when the scene loaded successfully.</returns>
bool LoadScene(string scenePath, bool makeActive)
{
    try
    {
        StopPlayMode();
        var loadedScene = SceneFileStore.Load(scenePath, HudSceneNodeFactory.Instance);
        selection.Select(null);
        sceneRoot.ClearChildren();
        foreach (var child in loadedScene.Root.Children.ToArray())
            sceneRoot.AddChild(child);
        sceneObjects.Clear();
        sceneObjects.AddRange(loadedScene.MeshInstances);
        gameCamera = loadedScene.GameCamera;
        gameViewport.Camera = gameCamera;
        viewportRenderer.SetSceneObjects(sceneObjects);
        viewportRenderer.SetSceneRoot(sceneRoot);
        viewportRenderer.SetGameScene(gameCamera, sceneObjects);
        foreach (var assetMesh in sceneObjects)
        {
            LoadAssetMeshResources(assetMesh);
            if (detachedSceneRenderer is not null)
                LoadAssetMeshResources(assetMesh, targetRenderer: detachedSceneRenderer);
            if (detachedGameRenderer is not null)
                LoadAssetMeshResources(assetMesh, targetRenderer: detachedGameRenderer);
        }
        hierarchyTree.SetRoots(sceneRoot.Children);
        if (makeActive)
        {
            activeScenePath = Path.GetFullPath(scenePath);
            editorView.ProjectLabel.Text = Path.GetFileName(activeScenePath);
        }
        logger.LogInformation("Loaded scene {ScenePath}", scenePath);
        CloseFileContextMenu();
        return true;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
        or System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
    {
        logger.LogError(exception, "Could not load scene {ScenePath}", scenePath);
        CloseFileContextMenu();
        return false;
    }
}

/// <summary>Reloads the active scene file.</summary>
void ReloadScene()
{
    if (activeScenePath is not null)
        LoadScene(activeScenePath, makeActive: false);
}

/// <summary>Displays a searchable project scene picker.</summary>
void ShowOpenSceneDialog()
{
    CloseFileContextMenu();
    IReadOnlyList<string> scenePaths;
    try
    {
        scenePaths = project.FindSceneFiles();
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        logger.LogError(exception, "Could not enumerate scenes in {ProjectRoot}", project.RootPath);
        return;
    }

    var picker = new ScenePickerDialog(width, height, project.RootPath, scenePaths)
        { Name = "OpenSceneDialog" };
    picker.OpenRequested += scenePath => LoadScene(scenePath, makeActive: true);
    picker.CancelRequested += CloseFileContextMenu;
    scenePickerDialog = picker;
    overlay.Add(picker, Vector2.Zero);
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
}

/// <summary>Returns whether a project file is a scene that this editor can load.</summary>
/// <param name="path">Absolute project file path.</param>
/// <returns>True for a node scene asset.</returns>
bool IsSceneFile(string path)
{
    return path.EndsWith(".node", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Builds one recursive project filesystem subtree.</summary>
/// <param name="path">Absolute file or directory path.</param>
/// <returns>The populated filesystem node.</returns>
FileSystemNode BuildFileSystemNode(string path)
{
    var isDirectory = Directory.Exists(path);
    var node = new FileSystemNode(path, isDirectory);
    if (!isDirectory)
    {
        AddImportedSubAssets(node);
        return node;
    }
    if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        return node;

    foreach (var directory in Directory.EnumerateDirectories(path)
                 .Where(IsVisibleProjectDirectory)
                 .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        node.AddChild(BuildFileSystemNode(directory));
    foreach (var file in Directory.EnumerateFiles(path)
                 .Where(file => !file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                 .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        node.AddChild(BuildFileSystemNode(file));
    return node;
}

/// <summary>Adds cached imported resources beneath one supported physical asset.</summary>
/// <param name="node">Physical file node receiving read-only children.</param>
void AddImportedSubAssets(FileSystemNode node)
{
    if (!Path.GetExtension(node.FullPath).Equals(".glb", StringComparison.OrdinalIgnoreCase))
        return;
    var record = assetDatabase.FindByPath(node.FullPath);
    if (record is null)
        return;
    var outcome = assetImportPipeline.TryGetLatestPublished(record, "editor");
    if (outcome is null)
        return;
    ImportedAssetTreeBuilder.AddObjects(
        node, record.Id, outcome.Objects, outcome.Artifacts);
    foreach (var artifact in outcome.Artifacts
                 .Where(artifact => IsVisibleImportedArtifact(artifact.ContentType))
                 .OrderBy(artifact => artifact.ContentType, StringComparer.Ordinal)
                 .ThenBy(artifact => artifact.Key, StringComparer.Ordinal))
    {
        node.AddChild(new ImportedSubAssetNode(node.FullPath,
            new AssetReference(record.Id, artifact.Key), artifact.ContentType,
            GetImportedArtifactDisplayName(artifact)));
    }
}

/// <summary>Imports a physical GLB and exposes all standalone animation artifacts for authoring.</summary>
/// <param name="source">Physical project file.</param>
/// <returns>Imported animation rows, or an empty collection when unsupported or invalid.</returns>
IReadOnlyList<ImportedSubAssetNode> ResolveAnimationArtifacts(FileSystemNode source)
{
    if (source.IsDirectory || !Path.GetExtension(source.FullPath).Equals(
            ".glb", StringComparison.OrdinalIgnoreCase))
        return [];
    var record = assetDatabase.FindByPath(source.FullPath);
    if (record is null)
        return [];
    var outcome = assetImportPipeline.Import(record, "editor");
    if (!outcome.Succeeded)
    {
        for (var index = 0; index < outcome.Diagnostics.Count; index++)
            logger.LogError("GLB import {Code}: {Message}", outcome.Diagnostics[index].Code,
                outcome.Diagnostics[index].Message);
        return [];
    }
    var animations = new List<ImportedSubAssetNode>();
    for (var index = 0; index < outcome.Artifacts.Count; index++)
    {
        var artifact = outcome.Artifacts[index];
        if (artifact.ContentType != "nico/skeletal-animation")
            continue;
        animations.Add(new ImportedSubAssetNode(source.FullPath,
            new AssetReference(record.Id, artifact.Key), artifact.ContentType,
            GetImportedArtifactDisplayName(artifact)));
    }
    requestedFileSystemExpansion.Add(source.FullPath);
    return animations;
}

/// <summary>Loads one animation skeleton and detects its conventional translated root joint.</summary>
/// <param name="source">Imported skeletal-animation reference.</param>
/// <returns>All skeleton joint names and the detected root-motion joint.</returns>
AnimationRootJointOptions ResolveAnimationRootJoints(AssetReference source)
{
    try
    {
        var animation = LoadAnimationSource(source);
        var joints = new string[animation.Skeleton.JointCount];
        for (var index = 0; index < joints.Length; index++)
            joints[index] = animation.Skeleton.Joints[index].Name;
        string? detected = null;
        for (var clipIndex = 0; clipIndex < animation.Animations.Count && detected is null;
             clipIndex++)
        {
            var clip = animation.Animations[clipIndex];
            for (var jointIndex = 0; jointIndex < animation.Skeleton.JointCount; jointIndex++)
            {
                var name = animation.Skeleton.Joints[jointIndex].Name;
                if (clip.Tracks[jointIndex]?.Translation is not null &&
                    (string.Equals(name, "Hips", StringComparison.OrdinalIgnoreCase) ||
                     name.EndsWith(":Hips", StringComparison.OrdinalIgnoreCase)))
                {
                    detected = name;
                    break;
                }
            }
        }
        for (var clipIndex = 0; clipIndex < animation.Animations.Count && detected is null;
             clipIndex++)
        {
            var clip = animation.Animations[clipIndex];
            for (var jointIndex = 0; jointIndex < animation.Skeleton.JointCount; jointIndex++)
            {
                if (animation.Skeleton.Joints[jointIndex].ParentIndex < 0 &&
                    clip.Tracks[jointIndex]?.Translation is not null)
                {
                    detected = animation.Skeleton.Joints[jointIndex].Name;
                    break;
                }
            }
        }
        return new AnimationRootJointOptions(joints, detected);
    }
    catch (Exception exception) when (exception is IOException or InvalidDataException or
                                       FileNotFoundException)
    {
        logger.LogWarning(exception, "Could not inspect animation root joints for {Source}", source);
        return new AnimationRootJointOptions([], null);
    }
}

/// <summary>Returns whether an imported artifact should appear as a selectable child.</summary>
/// <param name="contentType">Artifact content type.</param>
/// <returns>True for user-facing model resources.</returns>
bool IsVisibleImportedArtifact(string contentType)
{
    return contentType is "nico/static-mesh" or "nico/skinned-mesh" or
        "nico/standard-material" or "nico/texture2d" or
        "nico/skeletal-animation" or "nico/animation-set";
}

/// <summary>Builds a concise typed label for one imported artifact.</summary>
/// <param name="artifact">Imported artifact description.</param>
/// <returns>Display label including resource type.</returns>
string GetImportedArtifactDisplayName(AssetArtifact artifact)
{
    var type = artifact.ContentType switch
    {
        "nico/static-mesh" => "Mesh",
        "nico/skinned-mesh" => "Skinned Mesh",
        "nico/standard-material" => "Material",
        "nico/texture2d" => "Texture",
        "nico/skeletal-animation" => "Animation",
        "nico/animation-set" => "Animation Set",
        _ => "Asset"
    };
    var keyParts = artifact.Key.Split('/');
    var name = (artifact.ContentType is "nico/static-mesh" or "nico/skinned-mesh") &&
        keyParts.Length > 1
        ? string.Join(" / ", keyParts.Skip(1))
        : Path.GetFileNameWithoutExtension(artifact.RelativePath);
    return $"{name} [{type}]";
}

/// <summary>Returns whether a project directory belongs in the editable FileSystem tree.</summary>
/// <param name="path">Absolute directory path.</param>
/// <returns>False for generated asset, build, and version-control directories.</returns>
bool IsVisibleProjectDirectory(string path)
{
    return Path.GetFileName(path) is not (".git" or ".nico" or "bin" or "obj");
}

/// <summary>Enumerates one filesystem-node subtree.</summary>
/// <param name="root">Subtree root.</param>
/// <returns>Root and all descendants.</returns>
IEnumerable<FileSystemNode> EnumerateFileSystemNodes(FileSystemNode root)
{
    yield return root;
    foreach (var child in root.Children.OfType<FileSystemNode>())
    foreach (var descendant in EnumerateFileSystemNodes(child))
        yield return descendant;
}

/// <summary>Rebuilds the filesystem tree from the opened project root.</summary>
void RefreshFileSystem()
{
    try
    {
        var expandedPaths = fileSystemTree.ExpandedItems.OfType<FileSystemNode>()
            .Select(node => node.FullPath).ToHashSet(StringComparer.Ordinal);
        expandedPaths.UnionWith(requestedFileSystemExpansion);
        requestedFileSystemExpansion.Clear();
        var root = BuildFileSystemNode(project.RootPath);
        var expandedNodes = EnumerateFileSystemNodes(root)
            .Where(node => !ReferenceEquals(node, root) && node.CanHaveChildren
                && expandedPaths.Contains(node.FullPath)).ToArray();
        fileSystemTree.SetRoots(root.Children);
        fileSystemTree.SetExpanded(expandedNodes);
        RefreshVertices();
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        logger.LogError(exception, "Could not enumerate project directory {Directory}",
            project.RootPath);
    }
}

/// <summary>Dispatches an activated project file to its registered editor operation.</summary>
/// <param name="item">Activated filesystem node.</param>
void OpenFileSystemEntry(Node item)
{
    if (item is ImportedSubAssetNode)
        return;
    if (item is not FileSystemNode node || node.IsDirectory)
        return;
    if (IsSceneFile(node.FullPath))
    {
        LoadScene(node.FullPath, makeActive: true);
        return;
    }
    if (Path.GetExtension(node.FullPath).Equals(
            ".nanimset", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            animationSetEditor.Open(node.FullPath);
            dockSession.OpenPanel(
                EditorDockWorkspace.AnimationSetId, EditorDockWorkspace.SceneId);
            RefreshVertices();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                           UnauthorizedAccessException or ArgumentException)
        {
            logger.LogError(exception, "Could not open animation set {FilePath}", node.FullPath);
        }
        return;
    }
    if (Path.GetExtension(node.FullPath).Equals(".glb", StringComparison.OrdinalIgnoreCase))
    {
        var record = assetDatabase.FindByPath(node.FullPath);
        if (record is null)
        {
            logger.LogWarning("GLB asset metadata is unavailable for {FilePath}", node.FullPath);
            return;
        }
        var outcome = assetImportPipeline.Import(record, "editor");
        if (outcome.Succeeded)
        {
            logger.LogInformation("Imported GLB {FilePath} into {ArtifactCount} artifacts",
                node.FullPath, outcome.Artifacts.Count);
            requestedFileSystemExpansion.Add(node.FullPath);
            RefreshFileSystem();
        }
        else
        {
            foreach (var diagnostic in outcome.Diagnostics)
                logger.LogError("GLB import {Code}: {Message}", diagnostic.Code,
                    diagnostic.Message);
        }
        return;
    }
    logger.LogInformation("No editor is registered for project file {FilePath}", node.FullPath);
}

/// <summary>Creates a scene node from a dragged imported mesh sub-asset.</summary>
/// <param name="source">Dragged imported resource.</param>
/// <param name="dropTarget">Resolved hierarchy destination.</param>
void InstantiateImportedMesh(ImportedSubAssetNode source, TreeViewDropTarget dropTarget)
{
    if (source.ContentType == "nico/terrain")
    {
        var separator = source.Name.LastIndexOf(" [", StringComparison.Ordinal);
        InsertTerrainNode(dropTarget.Parent ?? sceneRoot, dropTarget.InsertionIndex,
            source.Reference, separator < 0 ? source.Name : source.Name[..separator]);
        return;
    }
    if (source.ContentType is not "nico/static-mesh" and not "nico/skinned-mesh")
    {
        logger.LogWarning("Imported {ContentType} cannot be placed in the Hierarchy",
            source.ContentType);
        return;
    }
    if (isPlaying)
    {
        logger.LogWarning("Imported meshes cannot be added while Play mode is active");
        return;
    }
    var instance = new MeshInstance3D
    {
        Mesh = source.Reference,
        Name = source.Name[..source.Name.LastIndexOf(" [", StringComparison.Ordinal)]
    };
    var destination = dropTarget.Parent ?? sceneRoot;
    try
    {
        destination.InsertChild(dropTarget.InsertionIndex, instance);
        sceneObjects.Add(instance);
        viewportRenderer.SetSceneObjects(sceneObjects);
        viewportRenderer.SetSceneRoot(sceneRoot);
        LoadAssetMeshResources(instance);
        if (detachedSceneRenderer is not null)
            LoadAssetMeshResources(instance, targetRenderer: detachedSceneRenderer);
        if (detachedGameRenderer is not null)
            LoadAssetMeshResources(instance, targetRenderer: detachedGameRenderer);
        hierarchyTree.SetRoots(sceneRoot.Children);
        if (!ReferenceEquals(destination, sceneRoot))
            hierarchyTree.Expand(destination);
        selection.Select(instance);
        renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
        renderScheduler.Invalidate(RenderInvalidation.GameViewport);
        window.RequestFrame();
    }
    catch
    {
        sceneObjects.Remove(instance);
        instance.Parent?.RemoveChild(instance);
        throw;
    }
}

/// <summary>Instantiates a dragged terrain source as renderable static scene geometry.</summary>
/// <param name="source">Dragged physical terrain file.</param>
/// <param name="dropTarget">Resolved hierarchy destination.</param>
void InstantiateTerrain(FileSystemNode source, TreeViewDropTarget dropTarget)
{
    if (!Path.GetExtension(source.FullPath).Equals(
            ".nterrain", StringComparison.OrdinalIgnoreCase))
        return;
    var record = assetDatabase.FindByPath(source.FullPath);
    if (record is null)
    {
        logger.LogWarning("Terrain asset metadata is unavailable for {FilePath}",
            source.FullPath);
        return;
    }
    var outcome = assetImportPipeline.Import(record, "editor");
    if (!outcome.Succeeded || !outcome.Artifacts.Any(artifact =>
            artifact.Key == "main" && artifact.ContentType == "nico/terrain"))
    {
        logger.LogWarning("Terrain asset {FilePath} has no valid main artifact",
            source.FullPath);
        return;
    }
    InsertTerrainNode(dropTarget.Parent ?? sceneRoot, dropTarget.InsertionIndex,
        new AssetReference(record.Id, "main"),
        Path.GetFileNameWithoutExtension(source.FullPath));
}

/// <summary>Instantiates a dragged GLB source as its complete imported node hierarchy.</summary>
/// <param name="source">Dragged physical GLB file.</param>
/// <param name="dropTarget">Resolved hierarchy destination.</param>
void InstantiateGlbPrimaryMesh(FileSystemNode source, TreeViewDropTarget dropTarget)
{
    if (!Path.GetExtension(source.FullPath).Equals(".glb", StringComparison.OrdinalIgnoreCase))
        return;
    var record = assetDatabase.FindByPath(source.FullPath);
    if (record is null)
    {
        logger.LogWarning("GLB asset metadata is unavailable for {FilePath}", source.FullPath);
        return;
    }
    var outcome = assetImportPipeline.Import(record, "editor");
    if (!outcome.Succeeded)
    {
        foreach (var diagnostic in outcome.Diagnostics)
            logger.LogError("GLB import {Code}: {Message}", diagnostic.Code,
                diagnostic.Message);
        return;
    }
    if (isPlaying)
    {
        logger.LogWarning("Imported models cannot be added while Play mode is active");
        return;
    }
    var model = GlbSceneInstantiator.Create(source.FullPath, record.Id, outcome);
    if (model.Meshes.Count == 0)
    {
        logger.LogWarning("GLB {FilePath} has no importable mesh nodes", source.FullPath);
        return;
    }
    var destination = dropTarget.Parent ?? sceneRoot;
    try
    {
        destination.InsertChild(dropTarget.InsertionIndex, model.Root);
        for (var index = 0; index < model.Meshes.Count; index++)
        {
            var mesh = model.Meshes[index];
            sceneObjects.Add(mesh);
            LoadAssetMeshResources(mesh, outcome);
            if (detachedSceneRenderer is not null)
                LoadAssetMeshResources(mesh, outcome, detachedSceneRenderer);
            if (detachedGameRenderer is not null)
                LoadAssetMeshResources(mesh, outcome, detachedGameRenderer);
        }
        viewportRenderer.SetSceneObjects(sceneObjects);
        viewportRenderer.SetSceneRoot(sceneRoot);
        hierarchyTree.SetRoots(sceneRoot.Children);
        if (!ReferenceEquals(destination, sceneRoot))
            hierarchyTree.Expand(destination);
        hierarchyTree.Expand(model.Root);
        selection.Select(model.Meshes[0]);
        FrameSceneCamera(model.Meshes);
        renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
        renderScheduler.Invalidate(RenderInvalidation.GameViewport);
        window.RequestFrame();
        logger.LogInformation("Instantiated GLB {FilePath} with {MeshCount} mesh primitives",
            source.FullPath, model.Meshes.Count);
    }
    catch
    {
        for (var index = 0; index < model.Meshes.Count; index++)
            sceneObjects.Remove(model.Meshes[index]);
        model.Root.Parent?.RemoveChild(model.Root);
        viewportRenderer.SetSceneObjects(sceneObjects);
        viewportRenderer.SetSceneRoot(sceneRoot);
        throw;
    }
}

/// <summary>Frames a newly imported model and expands clipping for its world bounds.</summary>
/// <param name="meshes">Imported model mesh instances.</param>
void FrameSceneCamera(IReadOnlyList<MeshInstance3D> meshes)
{
    SceneCameraFraming.TryFrame(sceneCamera, meshes);
}

/// <summary>Imports and binds runtime resources for one persistent model instance.</summary>
/// <param name="instance">Persistent scene model instance.</param>
/// <param name="knownOutcome">Optional already imported source outcome.</param>
/// <param name="targetRenderer">Renderer-local resource owner.</param>
void LoadAssetMeshResources(
    MeshInstance3D instance,
    AssetImportOutcome? knownOutcome = null,
    EditorViewportRenderer? targetRenderer = null)
{
    var meshReference = instance.Mesh;
    AssetImportOutcome? outcome = null;
    StaticMeshResource importedMesh;
    SkinnedMeshResource? importedSkin = null;
    if (BuiltInAssets.IsBuiltInMesh(meshReference))
    {
        importedMesh = BuiltInAssets.LoadMesh(meshReference);
    }
    else
    {
        var record = assetDatabase.Find(meshReference.Asset);
        if (record is null)
        {
            logger.LogError("Mesh asset {AssetId} is missing", meshReference.Asset);
            return;
        }
        outcome = knownOutcome ?? assetImportPipeline.Import(record, "editor");
        var meshArtifact = outcome.Artifacts.FirstOrDefault(artifact =>
            artifact.Key == meshReference.SubAsset &&
            artifact.ContentType is "nico/static-mesh" or "nico/skinned-mesh" or
                "nico/terrain");
        if (!outcome.Succeeded || outcome.ArtifactDirectory is null || meshArtifact is null)
        {
            logger.LogError("Mesh sub-asset {Reference} has no valid artifact", meshReference);
            return;
        }
        if (meshArtifact.ContentType == "nico/terrain")
        {
            if (instance.GetComponent<TerrainColliderComponent>() is not { } terrainCollider)
            {
                logger.LogError("Terrain mesh {Reference} requires a terrain collider",
                    meshReference);
                return;
            }
            var terrain = LoadRuntimeResource(meshReference,
                new TerrainResource(2, 2, [0f, 0f, 0f, 0f]));
            var (material, textureResource) = ResolveMeshMaterial(
                meshReference, instance, 0, outcome);
            (targetRenderer ?? viewportRenderer).SetTerrainResource(
                instance, terrain, terrainCollider, material, textureResource);
            return;
        }
        if (meshArtifact.ContentType == "nico/skinned-mesh")
        {
            importedSkin = LoadRuntimeResource(meshReference,
                new SkinnedMeshResource(new StaticMeshResource([], [], []), [],
                    new SkeletonResource([]), []));
            importedMesh = importedSkin.Mesh;
        }
        else
        {
            importedMesh = LoadRuntimeResource(meshReference,
                new StaticMeshResource([], [], []));
        }
    }
    instance.LocalBounds = new MeshBounds(importedMesh.BoundsMinimum, importedMesh.BoundsMaximum);
    var materialSlot = importedMesh.Submeshes.Count > 0
        ? importedMesh.Submeshes[0].MaterialSlot : -1;
    var (material, textureResource) = ResolveMeshMaterial(
        meshReference, instance, materialSlot, outcome);
    if (importedSkin is null)
    {
        (targetRenderer ?? viewportRenderer).SetAssetMeshResource(
            instance, importedMesh, material, textureResource);
    }
    else
    {
        (targetRenderer ?? viewportRenderer).SetAssetMeshResource(
            instance, importedSkin, material, textureResource,
            animations: null,
            sharedController: targetRenderer is null
                ? null : viewportRenderer.AnimationService.Get(instance));
    }
}

/// <summary>Resolves one script-selected animation set for an animated mesh.</summary>
/// <param name="animationSet">Script-facing animation-set reference.</param>
/// <param name="skin">Target skinned resource.</param>
/// <returns>Stable aliased clips bound to the target skeleton.</returns>
AnimationClipResource[] ResolveAnimationSetClips(
    AssetReference setReference,
    SkinnedMeshResource skin)
{
    var setRecord = assetDatabase.Find(setReference.Asset)
        ?? throw new FileNotFoundException(
            $"Animation set asset '{setReference.Asset}' is missing.");
    var setOutcome = assetImportPipeline.Import(setRecord, "editor");
    if (!setOutcome.Succeeded || !setOutcome.Artifacts.Any(candidate =>
            candidate.Key == setReference.SubAsset &&
            candidate.ContentType == "nico/animation-set"))
    {
        throw new InvalidDataException(
            $"Animation set sub-asset '{setReference}' is missing.");
    }
    var set = LoadRuntimeResource(setReference, new AnimationSetResource([]));
    return set.BindTo(skin.Skeleton, LoadAnimationSource, skin.MeshNodeTransform);
}

/// <summary>Loads one standalone or skinned animation artifact without target binding.</summary>
/// <param name="source">Explicit animation artifact.</param>
/// <returns>The decoded source skeleton and clips.</returns>
SkeletalAnimationResource LoadAnimationSource(AssetReference source)
{
    var record = assetDatabase.Find(source.Asset)
        ?? throw new FileNotFoundException($"Animation asset '{source.Asset}' is missing.");
    var outcome = assetImportPipeline.Import(record, "editor");
    var artifact = outcome.Artifacts.FirstOrDefault(candidate =>
        candidate.Key == source.SubAsset &&
        candidate.ContentType is "nico/skeletal-animation" or "nico/skinned-mesh");
    if (!outcome.Succeeded || artifact is null)
        throw new InvalidDataException($"Animation sub-asset '{source}' is missing.");
    if (artifact.ContentType == "nico/skeletal-animation")
    {
        var animation = LoadRuntimeResource(source,
            new SkeletalAnimationResource(new SkeletonResource([]), []));
        return animation;
    }
    var skin = LoadRuntimeResource(source,
        new SkinnedMeshResource(new StaticMeshResource([], [], []), [],
            new SkeletonResource([]), []));
    return new SkeletalAnimationResource(skin.Skeleton, skin.Animations.ToArray(),
        skin.MeshNodeTransform);
}

/// <summary>Loads one imported texture reference for a material override.</summary>
/// <param name="reference">Optional texture resource reference.</param>
/// <returns>Decoded texture data, or null when unavailable.</returns>
TextureResource? ResolveTextureResource(AssetReference? reference)
{
    if (reference is not { } textureReference)
        return null;
    var record = assetDatabase.Find(textureReference.Asset);
    if (record is null)
        return null;
    var outcome = assetImportPipeline.Import(record, "editor");
    var artifact = outcome.Artifacts.FirstOrDefault(candidate =>
        candidate.Key == textureReference.SubAsset && candidate.ContentType == "nico/texture2d");
    if (artifact is null || outcome.ArtifactDirectory is null)
        return null;
    return LoadRuntimeResource(textureReference,
        new TextureResource(0, 0, [], TextureColorSpace.Linear));
}

/// <summary>Resolves one mesh's active material and optional base-color texture.</summary>
/// <param name="meshReference">Imported mesh reference.</param>
/// <param name="instance">Scene instance receiving the resolved material.</param>
/// <param name="materialSlot">Mesh material slot used for default artifact discovery.</param>
/// <param name="outcome">Optional already-imported mesh pipeline outcome.</param>
/// <returns>Resolved material and optional decoded texture.</returns>
(StandardMaterialAsset Material, TextureResource? Texture) ResolveMeshMaterial(
    AssetReference meshReference,
    MeshInstance3D instance,
    int materialSlot,
    AssetImportOutcome? outcome = null)
{
    var resolvedMaterial = new StandardMaterialAsset();
    var defaultMaterialArtifact = outcome?.Artifacts.FirstOrDefault(artifact =>
        artifact.Key == $"material/{materialSlot}" &&
        artifact.ContentType == "nico/standard-material");
    if (instance.Materials.Count == 0 && defaultMaterialArtifact is not null)
        instance.Materials.Add(new AssetReference(meshReference.Asset, defaultMaterialArtifact.Key));
    var materialReference = instance.Materials.FirstOrDefault();
    if (materialReference.Asset.Value == Guid.Empty)
        return (resolvedMaterial, null);
    var materialRecord = assetDatabase.Find(materialReference.Asset);
    if (materialRecord is null)
        return (resolvedMaterial, null);
    var materialOutcome = materialReference.Asset == meshReference.Asset
        ? outcome ?? assetImportPipeline.Import(materialRecord, "editor")
        : assetImportPipeline.Import(materialRecord, "editor");
    if (materialOutcome is null || !materialOutcome.Succeeded ||
        materialOutcome.ArtifactDirectory is null)
    {
        return (resolvedMaterial, null);
    }
    var materialArtifact = materialOutcome.Artifacts.FirstOrDefault(artifact =>
        artifact.Key == materialReference.SubAsset &&
        artifact.ContentType == "nico/standard-material");
    if (materialArtifact is null)
        return (resolvedMaterial, null);
    resolvedMaterial = CloneMaterial(LoadRuntimeResource(materialReference, new StandardMaterialAsset()));
    return (resolvedMaterial, ResolveTextureResource(resolvedMaterial.BaseColorTexture));
}

/// <summary>Loads a decoded resource through the shared zero-reference LRU.</summary>
/// <typeparam name="TResource">Decoded resource type.</typeparam>
/// <param name="reference">Persistent artifact reference.</param>
/// <param name="fallback">Value exposed only if initial loading fails.</param>
/// <returns>The ready decoded resource.</returns>
TResource LoadRuntimeResource<TResource>(AssetReference reference, TResource fallback)
    where TResource : class
{
    var handle = runtimeResources.Acquire(reference, fallback);
    try
    {
        runtimeResources.WaitAsync(handle).GetAwaiter().GetResult();
        if (runtimeResources.GetState(handle) != ResourceLoadState.Ready)
        {
            throw new InvalidDataException($"Runtime resource '{reference}' failed to load.",
                runtimeResources.GetError(handle));
        }
        return runtimeResources.Get(handle);
    }
    finally
    {
        runtimeResources.Release(handle);
    }
}

/// <summary>Loads one imported static mesh for authoritative collision geometry.</summary>
/// <param name="reference">Imported static-mesh artifact reference.</param>
/// <returns>The decoded collision mesh.</returns>
StaticMeshResource ResolveCollisionMeshResource(AssetReference reference)
{
    return LoadRuntimeResource(reference, new StaticMeshResource([], [], []));
}

/// <summary>Loads one explicit terrain grid for physics and Scene preview.</summary>
/// <param name="reference">Published terrain artifact reference.</param>
/// <returns>The decoded terrain grid.</returns>
TerrainResource ResolveTerrainResource(AssetReference reference)
{
    if (liveTerrainResolver?.Invoke(reference) is { } liveTerrain)
        return liveTerrain;
    return LoadRuntimeResource(reference, new TerrainResource(2, 2, [0f, 0f, 0f, 0f]));
}

/// <summary>Creates mutable renderer-local material values from a shared decoded resource.</summary>
/// <param name="source">Shared decoded material.</param>
/// <returns>An independent material copy.</returns>
StandardMaterialAsset CloneMaterial(StandardMaterialAsset source)
{
    return source.Clone();
}

/// <summary>Formats slot-zero material ownership for the Inspector.</summary>
/// <param name="instance">Inspected mesh instance.</param>
/// <returns>Readable material source name.</returns>
string ResolveMaterialDisplayName(MeshInstance3D instance)
{
    var reference = instance.Materials.FirstOrDefault();
    if (reference.Asset.Value == Guid.Empty)
        return "BuiltIn/Default";
    var path = assetDatabase.Find(reference.Asset)?.ProjectPath ?? reference.Asset.ToString();
    return reference.SubAsset is null ? path : $"{path} / {reference.SubAsset}";
}

/// <summary>Formats one persistent asset reference for common Inspector fields.</summary>
/// <param name="reference">Persistent asset reference.</param>
/// <returns>Project path followed by an optional sub-asset key.</returns>
string ResolveAssetReferenceDisplayName(AssetReference reference)
{
    var path = assetDatabase.Find(reference.Asset)?.ProjectPath ?? reference.Asset.ToString();
    return reference.SubAsset is null or "main"
        ? path : $"{path} / {reference.SubAsset}";
}

/// <summary>Resolves current storage and content type for one persistent asset output.</summary>
/// <param name="reference">Persistent asset reference.</param>
/// <returns>Current document location, or null when the output is unavailable.</returns>
ResolvedAssetDocument? ResolveAssetDocument(AssetReference reference)
{
    var record = assetDatabase.Find(reference.Asset);
    if (record is null)
        return null;
    var outcome = assetImportPipeline.Import(record, "editor");
    var requestedKey = reference.SubAsset ?? "main";
    var artifact = outcome.Artifacts.FirstOrDefault(candidate =>
        candidate.Key == requestedKey);
    if (!outcome.Succeeded || artifact is null)
        return null;
    var isEditable = editableAssetSources.IsEditable(
        artifact.ContentType, record.Importer, reference.SubAsset);
    Stream OpenCurrentContent()
    {
        var current = assetDatabase.Find(reference.Asset)
            ?? throw new FileNotFoundException($"Asset '{reference.Asset}' is missing.");
        if (isEditable)
            return File.OpenRead(Path.Combine(project.RootPath, current.ProjectPath));
        var published = assetImportPipeline.Import(current, "editor");
        var currentArtifact = published.Artifacts.FirstOrDefault(candidate =>
            candidate.Key == requestedKey && candidate.ContentType == artifact.ContentType);
        if (!published.Succeeded || published.ArtifactDirectory is null || currentArtifact is null)
            throw new InvalidDataException($"Asset output '{reference}' is unavailable.");
        return File.OpenRead(Path.Combine(
            published.ArtifactDirectory, currentArtifact.RelativePath));
    }
    Action<Action<Stream>>? write = null;
    if (isEditable)
    {
        write = contentWriter =>
        {
            var current = assetDatabase.Find(reference.Asset)
                ?? throw new FileNotFoundException($"Asset '{reference.Asset}' is missing.");
            AssetDocumentStorage.WriteAtomic(
                Path.Combine(project.RootPath, current.ProjectPath), contentWriter);
        };
    }
    var location = new AssetDocumentLocation(reference,
        () => ResolveAssetReferenceDisplayName(reference), OpenCurrentContent, write);
    return new ResolvedAssetDocument(artifact.ContentType, location);
}

/// <summary>Gets the shared editable terrain document for one persistent reference.</summary>
/// <param name="reference">Terrain source reference.</param>
/// <returns>Shared terrain document, or null when unavailable or mistyped.</returns>
TerrainDocument? ResolveTerrainDocument(AssetReference reference)
{
    var resolved = ResolveAssetDocument(reference);
    if (resolved is null || resolved.ContentType != "nico/terrain")
        return null;
    return assetDocuments.GetOrLoad(resolved.Location, resolved.ContentType) as TerrainDocument;
}

/// <summary>Publishes live sculpt changes to every renderer and attached physics terrain.</summary>
/// <param name="instance">Edited terrain scene instance.</param>
/// <param name="collider">Terrain dimensions shared with collision.</param>
/// <param name="terrain">Current edited height grid.</param>
/// <param name="editRegion">Inclusive sample rectangle touched by the edit.</param>
void ApplyTerrainEdit(
    MeshInstance3D instance,
    TerrainColliderComponent collider,
    TerrainResource terrain,
    TerrainEditRegion editRegion)
{
    var (material, textureResource) = ResolveMeshMaterial(instance.Mesh, instance, 0);
    viewportRenderer.UpdateTerrainResource(instance, terrain, collider, material, textureResource);
    detachedSceneRenderer?.UpdateTerrainResource(instance, terrain, collider, material, textureResource);
    detachedGameRenderer?.UpdateTerrainResource(instance, terrain, collider, material, textureResource);
    if (physicsWorld is not null)
    {
        try
        {
            physicsWorld.RebuildTerrain(instance, terrain, terrain.GetDirtyChunkRegions(
                editRegion.MinimumX, editRegion.MinimumZ,
                editRegion.MaximumX, editRegion.MaximumZ));
        }
        catch (InvalidOperationException)
        {
            // Edit-mode worlds are normally detached; the next Attach uses the saved resource.
        }
    }
    if (collider.TerrainData is { } reference)
    {
        viewportRenderer.InvalidatePreviewAsset(reference);
        detachedSceneRenderer?.InvalidatePreviewAsset(reference);
    }
    InvalidateViewports();
}

/// <summary>Publishes the current terrain brush ring to Scene renderers.</summary>
/// <param name="preview">Brush geometry, or null to hide it.</param>
void SetTerrainBrushPreview(TerrainBrushPreview? preview)
{
    viewportRenderer.SetTerrainBrushPreview(preview);
    detachedSceneRenderer?.SetTerrainBrushPreview(preview);
    renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
    window.RequestFrame();
}

/// <summary>Connects scene opening to the project filesystem tree.</summary>
/// <param name="tree">Filesystem tree to attach.</param>
void AttachFileSystem(TreeView tree)
{
    tree.SelectionChanged += item =>
    {
        if (item is not null && inspectorProviders.TryCreate(item, out var document) &&
            document is not null)
        {
            inspector.Bind(document);
            RefreshVertices();
        }
    };
    tree.ItemActivated += OpenFileSystemEntry;
}

/// <summary>Shows a naming dialog and creates a folder or empty file.</summary>
/// <param name="parentDirectory">Directory that will contain the new item.</param>
/// <param name="createFolder">True to create a folder; false to create an empty file.</param>
void ShowCreateFileSystemDialog(string parentDirectory, bool createFolder)
{
    CloseFileContextMenu();
    var relativeParent = Path.GetRelativePath(project.RootPath, parentDirectory);
    if (relativeParent == ".")
        relativeParent = Path.GetFileName(project.RootPath);
    var dialog = new FileSystemCreateDialog(width, height,
        createFolder ? "Folder" : "File", relativeParent)
        { Name = "CreateFileSystemItemDialog" };
    dialog.CreateRequested += name =>
    {
        var itemPath = Path.Combine(parentDirectory, name);
        try
        {
            if (Directory.Exists(itemPath) || File.Exists(itemPath))
            {
                dialog.ShowError("An item with that name already exists.");
                RefreshVertices();
                return;
            }
            if (createFolder)
                Directory.CreateDirectory(itemPath);
            else
                using (File.Create(itemPath)) { }
            logger.LogInformation("Created project item {ItemPath}", itemPath);
            CloseFileContextMenu();
            RefreshFileSystem();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException)
        {
            logger.LogError(exception, "Could not create project item {ItemPath}", itemPath);
            dialog.ShowError("Could not create this item.");
            RefreshVertices();
        }
    };
    dialog.CancelRequested += CloseFileContextMenu;
    fileSystemCreateDialog = dialog;
    overlay.Add(dialog, Vector2.Zero);
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
}

/// <summary>Replaces the current editor scene with a new in-memory default node scene.</summary>
void ResetToDefaultScene()
{
    StopPlayMode();
    selection.Select(null);
    sceneRoot.ClearChildren();
    sceneObjects.Clear();
    var cube = new MeshInstance3D { Name = "SceneCube" };
    gameCamera = new PerspectiveCamera(
        fov: MathF.PI / 4f,
        aspect: gameViewport.Width / MathF.Max(1f, gameViewport.Height),
        near: 0.1f,
        far: 1000f)
    {
        Name = "GameCamera",
        Position = new Vector3(4f, 3f, 6f)
    };
    gameCamera.LookAt(Vector3.Zero);
    sceneObjects.Add(cube);
    sceneRoot.AddChild(cube);
    sceneRoot.AddChild(gameCamera);
    gameViewport.Camera = gameCamera;
    viewportRenderer.SetGameScene(gameCamera, sceneObjects);
    hierarchyTree.SetRoots(sceneRoot.Children);
}

/// <summary>Shows a project-scoped path dialog for adding or saving a node scene.</summary>
/// <param name="parentDirectory">Directory that will contain the node asset.</param>
/// <param name="createDefaultScene">Whether to replace the viewport with a fresh default scene.</param>
/// <param name="saveAction">Whether the dialog represents Save rather than Add.</param>
void ShowScenePathDialog(string parentDirectory, bool createDefaultScene, bool saveAction)
{
    CloseFileContextMenu();
    var relativeParent = Path.GetRelativePath(project.RootPath, parentDirectory);
    if (relativeParent == ".")
        relativeParent = Path.GetFileName(project.RootPath);
    var dialog = new FileSystemCreateDialog(width, height, "Scene", relativeParent,
        actionVerb: saveAction ? "Save" : "Add") { Name = "SaveNodeDialog" };
    dialog.CreateRequested += requestedName =>
    {
        var fileName = requestedName.EndsWith(".node", StringComparison.OrdinalIgnoreCase)
            ? requestedName : requestedName + ".node";
        var scenePath = Path.Combine(parentDirectory, fileName);
        if (File.Exists(scenePath) || Directory.Exists(scenePath))
        {
            dialog.ShowError("An item with that name already exists.");
            RefreshVertices();
            return;
        }
        try
        {
            if (createDefaultScene)
                ResetToDefaultScene();
            SceneFileStore.Save(scenePath, sceneRoot, gameCamera);
            activeScenePath = Path.GetFullPath(scenePath);
            editorView.ProjectLabel.Text = Path.GetFileName(activeScenePath);
            logger.LogInformation("Saved scene {ScenePath}", activeScenePath);
            CloseFileContextMenu();
            RefreshFileSystem();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or NotSupportedException)
        {
            logger.LogError(exception, "Could not save scene {ScenePath}", scenePath);
            dialog.ShowError("Could not save this scene.");
            RefreshVertices();
        }
    };
    dialog.CancelRequested += CloseFileContextMenu;
    fileSystemCreateDialog = dialog;
    overlay.Add(dialog, Vector2.Zero);
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
}

/// <summary>Shows a project-scoped path dialog for a new animation-set asset.</summary>
/// <param name="parentDirectory">Directory receiving the new asset.</param>
void ShowAnimationSetPathDialog(string parentDirectory)
{
    CloseFileContextMenu();
    var relativeParent = Path.GetRelativePath(project.RootPath, parentDirectory);
    if (relativeParent == ".")
        relativeParent = Path.GetFileName(project.RootPath);
    var dialog = new FileSystemCreateDialog(width, height, "Animation Set", relativeParent,
        actionVerb: "Add") { Name = "AddAnimationSetDialog" };
    dialog.CreateRequested += requestedName =>
    {
        var fileName = requestedName.EndsWith(".nanimset", StringComparison.OrdinalIgnoreCase)
            ? requestedName : requestedName + ".nanimset";
        var assetPath = Path.Combine(parentDirectory, fileName);
        if (File.Exists(assetPath) || Directory.Exists(assetPath))
        {
            dialog.ShowError("An item with that name already exists.");
            RefreshVertices();
            return;
        }
        try
        {
            AnimationSetAuthoring.Save(assetPath, []);
            assetDatabase.Refresh();
            CloseFileContextMenu();
            RefreshFileSystem();
            animationSetEditor.Open(assetPath);
            dockSession.OpenPanel(
                EditorDockWorkspace.AnimationSetId, EditorDockWorkspace.SceneId);
            RefreshVertices();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException)
        {
            logger.LogError(exception, "Could not create animation set {AssetPath}", assetPath);
            dialog.ShowError("Could not create this animation set.");
            RefreshVertices();
        }
    };
    dialog.CancelRequested += CloseFileContextMenu;
    fileSystemCreateDialog = dialog;
    overlay.Add(dialog, Vector2.Zero);
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
}

/// <summary>Shows a project-scoped path dialog for a new standard-material asset.</summary>
/// <param name="parentDirectory">Directory receiving the new asset.</param>
void ShowMaterialPathDialog(string parentDirectory)
{
    CloseFileContextMenu();
    var relativeParent = Path.GetRelativePath(project.RootPath, parentDirectory);
    if (relativeParent == ".")
        relativeParent = Path.GetFileName(project.RootPath);
    var dialog = new FileSystemCreateDialog(width, height, "Material", relativeParent,
        actionVerb: "Add") { Name = "AddMaterialDialog" };
    dialog.CreateRequested += requestedName =>
    {
        var fileName = requestedName.EndsWith(".nmat", StringComparison.OrdinalIgnoreCase)
            ? requestedName : requestedName + ".nmat";
        var assetPath = Path.Combine(parentDirectory, fileName);
        if (File.Exists(assetPath) || Directory.Exists(assetPath))
        {
            dialog.ShowError("An item with that name already exists.");
            RefreshVertices();
            return;
        }
        try
        {
            MaterialAuthoring.SaveDefault(assetPath);
            assetDatabase.Refresh();
            CloseFileContextMenu();
            RefreshFileSystem();
            RefreshVertices();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException)
        {
            logger.LogError(exception, "Could not create material {AssetPath}", assetPath);
            dialog.ShowError("Could not create this material.");
            RefreshVertices();
        }
    };
    dialog.CancelRequested += CloseFileContextMenu;
    fileSystemCreateDialog = dialog;
    overlay.Add(dialog, Vector2.Zero);
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
}

/// <summary>Shows a project-scoped path dialog for a new editable terrain asset.</summary>
/// <param name="parentDirectory">Directory receiving the new asset.</param>
/// <param name="created">Optional callback that places the new terrain in a scene.</param>
void ShowTerrainPathDialog(
    string parentDirectory,
    Action<AssetReference, string>? created = null)
{
    CloseFileContextMenu();
    CloseHierarchyContextMenu();
    var relativeParent = Path.GetRelativePath(project.RootPath, parentDirectory);
    if (relativeParent == ".")
        relativeParent = Path.GetFileName(project.RootPath);
    var dialog = new FileSystemCreateDialog(width, height, "Terrain", relativeParent,
        actionVerb: "Add") { Name = "AddTerrainDialog" };
    dialog.CreateRequested += requestedName =>
    {
        var fileName = requestedName.EndsWith(".nterrain", StringComparison.OrdinalIgnoreCase)
            ? requestedName : requestedName + ".nterrain";
        var assetPath = Path.Combine(parentDirectory, fileName);
        if (File.Exists(assetPath) || Directory.Exists(assetPath))
        {
            dialog.ShowError("An item with that name already exists.");
            RefreshVertices();
            return;
        }
        try
        {
            TerrainAuthoring.SaveFlat(assetPath);
            assetDatabase.Refresh();
            var record = assetDatabase.FindByPath(assetPath)
                ?? throw new InvalidOperationException("Terrain metadata was not created.");
            var outcome = assetImportPipeline.Import(record, "editor");
            if (!outcome.Succeeded || !outcome.Artifacts.Any(artifact =>
                    artifact.Key == "main" && artifact.ContentType == "nico/terrain"))
                throw new InvalidDataException("Terrain import failed.");
            CloseFileContextMenu();
            RefreshFileSystem();
            created?.Invoke(new AssetReference(record.Id, "main"),
                Path.GetFileNameWithoutExtension(assetPath));
            RefreshVertices();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                           or ArgumentException or InvalidOperationException or
                                           NotSupportedException)
        {
            logger.LogError(exception, "Could not create terrain {AssetPath}", assetPath);
            dialog.ShowError("Could not create this terrain.");
            RefreshVertices();
        }
    };
    dialog.CancelRequested += CloseFileContextMenu;
    fileSystemCreateDialog = dialog;
    overlay.Add(dialog, Vector2.Zero);
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
}

/// <summary>Shows file types that can be created in the selected project directory.</summary>
/// <param name="parentDirectory">Directory receiving the new file.</param>
/// <param name="x">Submenu screen X position.</param>
/// <param name="y">Submenu screen Y position.</param>
void ShowAddFileSubmenu(string parentDirectory, float x, float y)
{
    if (fileSubmenu is not null)
        overlay.Remove(fileSubmenu);
    var submenuPosition = new Vector2(
        Math.Clamp(x, 0f, MathF.Max(0f, width - 170f)),
        Math.Clamp(y, 0f, MathF.Max(0f, height - 60f)));
    var submenu = new ContextMenu(170f) { Name = "AddFileSubmenu" };
    submenu.AddItem("Add Scene", () => ShowScenePathDialog(parentDirectory,
        createDefaultScene: true, saveAction: false));
    submenu.AddItem("Add Animation Set", () => ShowAnimationSetPathDialog(parentDirectory));
    submenu.AddItem("Add Material", () => ShowMaterialPathDialog(parentDirectory));
    submenu.AddItem("Add Terrain", () => ShowTerrainPathDialog(parentDirectory));
    submenu.AddItem("Add Empty File", () => ShowCreateFileSystemDialog(parentDirectory,
        createFolder: false));
    fileSubmenu = submenu;
    overlay.Add(submenu, submenuPosition);
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
}

/// <summary>Returns whether a path is the active scene or contains it.</summary>
/// <param name="targetPath">File or directory considered for deletion.</param>
/// <returns>True when deleting the target would remove the active scene.</returns>
bool ContainsActiveScene(string targetPath)
{
    if (activeScenePath is null)
        return false;
    var target = Path.GetFullPath(targetPath);
    var active = Path.GetFullPath(activeScenePath);
    if (string.Equals(target, active, StringComparison.Ordinal))
        return true;
    if (!Directory.Exists(target))
        return false;
    return active.StartsWith(target.TrimEnd(Path.DirectorySeparatorChar)
        + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}

/// <summary>Shows confirmation and deletes one project file or folder.</summary>
/// <param name="targetPath">Absolute project entry path to delete.</param>
void ShowDeleteConfirmation(string targetPath)
{
    CloseFileContextMenu();
    var displayName = Path.GetFileName(targetPath.TrimEnd(Path.DirectorySeparatorChar));
    var dialog = new ConfirmationDialog(width, height, "Delete Project Item",
        $"Delete {displayName}? This cannot be undone.", "Delete")
        { Name = "DeleteProjectItemDialog" };
    dialog.Confirmed += () =>
    {
        try
        {
            var removesActiveScene = ContainsActiveScene(targetPath);
            if (Directory.Exists(targetPath))
            {
                var isLink = File.GetAttributes(targetPath).HasFlag(FileAttributes.ReparsePoint);
                Directory.Delete(targetPath, recursive: !isLink);
            }
            else if (File.Exists(targetPath))
            {
                var asset = assetDatabase.FindByPath(targetPath);
                if (asset is not null)
                {
                    assetDatabase.DeleteAsset(asset.Id);
                }
                else
                {
                    File.Delete(targetPath);
                    var sidecarPath = AssetMetadataStore.GetSidecarPath(targetPath);
                    if (File.Exists(sidecarPath))
                        File.Delete(sidecarPath);
                }
            }
            if (removesActiveScene)
            {
                activeScenePath = null;
                editorView.ProjectLabel.Text = "Untitled.node";
                ResetToDefaultScene();
            }
            logger.LogInformation("Deleted project item {ItemPath}", targetPath);
            CloseFileContextMenu();
            RefreshFileSystem();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException)
        {
            logger.LogError(exception, "Could not delete project item {ItemPath}", targetPath);
            CloseFileContextMenu();
        }
    };
    dialog.CancelRequested += CloseFileContextMenu;
    confirmationDialog = dialog;
    overlay.Add(dialog, Vector2.Zero);
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
}

/// <summary>Shows actions appropriate for filesystem blank space, a folder, or a file.</summary>
/// <returns>True when the pointer was inside the filesystem tree.</returns>
bool ShowFileSystemContextMenu()
{
    if (lastMousePos.X < fileSystemTree.Left || lastMousePos.X > fileSystemTree.Right
        || lastMousePos.Y < fileSystemTree.Top || lastMousePos.Y > fileSystemTree.Bottom)
        return false;

    CloseFileContextMenu();
    CloseHierarchyContextMenu();
    string? targetPath = null;
    if (uiEventRouter.HoveredElement is TreeViewItem row && row.Item is FileSystemNode node)
    {
        targetPath = node.FullPath;
        fileSystemTree.Select(node);
    }
    else
    {
        fileSystemTree.Select(null);
    }

    const float menuWidth = 170f;
    var menuX = Math.Clamp(lastMousePos.X, 0f, MathF.Max(0f, width - menuWidth));
    var menuY = Math.Clamp(lastMousePos.Y, 0f, MathF.Max(0f, height - 270f));
    var menu = new ContextMenu(menuWidth) { Name = "FileSystemContextMenu" };
    var creationDirectory = targetPath is not null && Directory.Exists(targetPath)
        ? targetPath : targetPath is not null ? Path.GetDirectoryName(targetPath) ?? project.RootPath
        : project.RootPath;
    menu.AddItem("Add Folder", () => ShowCreateFileSystemDialog(creationDirectory,
        createFolder: true));
    menu.AddSubmenu("Add File", item => ShowAddFileSubmenu(creationDirectory,
        menu.Right - 2f, item.Top));
    if (targetPath is not null && !Directory.Exists(targetPath) && IsSceneFile(targetPath))
    {
        var scenePath = targetPath;
        menu.AddItem("Open Scene", () => LoadScene(scenePath, makeActive: true));
    }

    menu.AddItem("Open Scene...", ShowOpenSceneDialog);
    menu.AddItem("Save Scene", SaveScene);
    if (targetPath is not null
        && string.Equals(Path.GetFullPath(targetPath), activeScenePath, StringComparison.Ordinal))
        menu.AddItem("Reload Scene", ReloadScene);
    if (targetPath is not null)
    {
        var deletePath = targetPath;
        menu.AddItem("Delete", () => ShowDeleteConfirmation(deletePath));
    }
    menu.AddItem("Refresh", RefreshFileSystem);
    fileContextMenu = menu;
    overlay.Add(menu, new Vector2(menuX, menuY));
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
    return true;
}

/// <summary>Connects custom title-bar actions to native window commands.</summary>
/// <param name="titleBar">Title bar to attach.</param>
void AttachTitleBar(TitleBar titleBar)
{
    titleBar.DragStarted += () => window.BeginWindowDrag(lastMousePos);
    titleBar.MinimizeRequested += window.Minimize;
    titleBar.MaximizeRequested += window.ToggleMaximize;
    titleBar.FullScreenRequested += window.ToggleFullScreen;
    titleBar.CloseRequested += window.Close;
}

/// <summary>Adds an empty node or one built-in mesh primitive to the active scene.</summary>
/// <param name="parent">Hierarchy parent.</param>
/// <param name="mesh">Optional built-in mesh reference.</param>
/// <param name="displayName">Base display name for the created object.</param>
void AddSceneNode(Node parent, AssetReference? mesh, string displayName)
{
    var activeRoot = GetActiveSceneRoot();
    var activeObjects = GetActiveSceneObjects();
    Node child;
    if (mesh is { } meshReference)
    {
        var meshInstance = new MeshInstance3D
        {
            Name = $"{displayName} {createdObjectIndex++}",
            Mesh = meshReference
        };
        AddPrimitivePhysics(meshInstance, meshReference);
        activeObjects.Add(meshInstance);
        LoadAssetMeshResources(meshInstance);
        if (detachedSceneRenderer is not null)
            LoadAssetMeshResources(meshInstance, targetRenderer: detachedSceneRenderer);
        if (detachedGameRenderer is not null)
            LoadAssetMeshResources(meshInstance, targetRenderer: detachedGameRenderer);
        child = meshInstance;
    }
    else
    {
        child = new Node3D { Name = $"{displayName} {createdObjectIndex++}" };
    }

    parent.AddChild(child);
    if (ReferenceEquals(parent, activeRoot))
        hierarchyTree.SetRoots(activeRoot.Children);
    else
        hierarchyTree.Expand(parent);
    hierarchyTree.Select(child);
    CloseHierarchyContextMenu();
    CloseFileContextMenu();
}

/// <summary>Adds one renderable and collidable terrain at the end of a hierarchy parent.</summary>
/// <param name="parent">Hierarchy parent.</param>
/// <param name="reference">Persistent terrain source.</param>
/// <param name="displayName">Terrain node display name.</param>
void AddTerrainNode(Node parent, AssetReference reference, string displayName)
{
    InsertTerrainNode(parent, parent.Children.Count, reference, displayName);
}

/// <summary>Inserts one renderable and collidable terrain into the authored hierarchy.</summary>
/// <param name="parent">Hierarchy parent.</param>
/// <param name="insertionIndex">Requested child insertion index.</param>
/// <param name="reference">Persistent terrain source.</param>
/// <param name="displayName">Terrain node display name.</param>
void InsertTerrainNode(
    Node parent,
    int insertionIndex,
    AssetReference reference,
    string displayName)
{
    if (isPlaying)
    {
        logger.LogWarning("Terrain cannot be added while Play mode is active");
        return;
    }
    var instance = new MeshInstance3D
    {
        Name = string.IsNullOrWhiteSpace(displayName)
            ? $"Terrain {createdObjectIndex++}" : displayName,
        Mesh = reference
    };
    instance.AddComponent(new TerrainColliderComponent
    {
        TerrainData = reference,
        HorizontalSize = new Vector2(20f, 20f),
        HeightScale = 5f
    });
    try
    {
        parent.InsertChild(insertionIndex, instance);
        sceneObjects.Add(instance);
        viewportRenderer.SetSceneObjects(sceneObjects);
        viewportRenderer.SetSceneRoot(sceneRoot);
        LoadAssetMeshResources(instance);
        if (detachedSceneRenderer is not null)
            LoadAssetMeshResources(instance, targetRenderer: detachedSceneRenderer);
        if (detachedGameRenderer is not null)
            LoadAssetMeshResources(instance, targetRenderer: detachedGameRenderer);
        hierarchyTree.SetRoots(sceneRoot.Children);
        if (!ReferenceEquals(parent, sceneRoot))
            hierarchyTree.Expand(parent);
        selection.Select(instance);
        FrameSceneCamera([instance]);
        CloseHierarchyContextMenu();
        CloseFileContextMenu();
        InvalidateViewports();
        logger.LogInformation("Added terrain {TerrainName} from {TerrainReference}",
            instance.Name, reference);
    }
    catch
    {
        sceneObjects.Remove(instance);
        instance.Parent?.RemoveChild(instance);
        viewportRenderer.SetSceneObjects(sceneObjects);
        viewportRenderer.SetSceneRoot(sceneRoot);
        throw;
    }
}

/// <summary>Adds an editable directional light below one hierarchy node.</summary>
/// <param name="parent">Hierarchy parent.</param>
void AddDirectionalLight(Node parent)
{
    var activeRoot = GetActiveSceneRoot();
    var light = new DirectionalLight3D
    {
        Name = $"Directional Light {createdObjectIndex++}",
        Rotation = new Vector3(-45f, 35f, 0f) * (MathF.PI / 180f)
    };
    parent.AddChild(light);
    if (ReferenceEquals(parent, activeRoot))
        hierarchyTree.SetRoots(activeRoot.Children);
    else
        hierarchyTree.Expand(parent);
    hierarchyTree.Select(light);
    CloseHierarchyContextMenu();
    InvalidateViewports();
}

/// <summary>Adds the scene's single screen-space HUD root.</summary>
void AddHudRoot()
{
    var activeRoot = GetActiveSceneRoot();
    HudRoot? existing = null;
    FindHudRoot(activeRoot, ref existing);
    if (existing is not null)
    {
        hierarchyTree.Select(existing);
        CloseHierarchyContextMenu();
        return;
    }
    var hud = new HudRoot();
    activeRoot.AddChild(hud);
    hierarchyTree.SetRoots(activeRoot.Children);
    hierarchyTree.Select(hud);
    if (isPlaying)
        MountPlayHud(activeRoot);
    CloseHierarchyContextMenu();
}

/// <summary>Adds matching collision geometry and default motion to a built-in primitive.</summary>
/// <param name="node">Primitive node receiving the components.</param>
/// <param name="mesh">Built-in primitive mesh.</param>
void AddPrimitivePhysics(Node3D node, AssetReference mesh)
{
    var collider = CreatePrimitiveCollider(mesh);
    node.AddComponent(collider);
    if (mesh == BuiltInAssets.PlaneMesh)
        return;
    node.AddComponent(new RigidBodyComponent());
}

/// <summary>Creates collision geometry matching a built-in mesh when possible.</summary>
/// <param name="mesh">Optional mesh used to infer the primitive shape.</param>
/// <returns>A collider configured for the inferred shape.</returns>
ColliderComponent CreatePrimitiveCollider(AssetReference? mesh)
{
    if (mesh == BuiltInAssets.PlaneMesh)
    {
        return new BoxColliderComponent
        {
            Center = new Vector3(0f, -0.01f, 0f),
            Size = new Vector3(1f, 0.02f, 1f)
        };
    }
    if (mesh == BuiltInAssets.SphereMesh)
        return new SphereColliderComponent();
    if (mesh == BuiltInAssets.CapsuleMesh)
        return new CapsuleColliderComponent();
    if (mesh == BuiltInAssets.CylinderMesh)
        return new CylinderColliderComponent();
    return new BoxColliderComponent();
}

/// <summary>Adds one explicitly selected, one-time fitted collider to a scene node.</summary>
/// <param name="target">Selected hierarchy node.</param>
/// <param name="kind">Concrete collider type requested by the user.</param>
void AddColliderComponent(Node target, ColliderAuthoringKind kind)
{
    if (target is not Node3D node)
        return;
    node.AddComponent(ColliderAuthoring.Create(kind, node));
    if (physicsWorld is not null)
        physicsWorld.Attach(GetActiveSceneRoot());
    CloseHierarchyContextMenu();
    InvalidateViewports();
}

/// <summary>Adds a dynamic rigid body to one selected scene node.</summary>
/// <param name="target">Selected hierarchy node.</param>
void AddRigidBodyComponent(Node target)
{
    if (target is not Node3D node || node.GetComponent<RigidBodyComponent>() is not null)
        return;
    node.AddComponent(new RigidBodyComponent());
    if (physicsWorld is not null)
        physicsWorld.Attach(GetActiveSceneRoot());
    CloseHierarchyContextMenu();
    InvalidateViewports();
}

/// <summary>Deletes one hierarchy subtree from the active authored or runtime scene.</summary>
/// <param name="target">Root of the subtree to remove.</param>
void DeleteSceneNode(Node target)
{
    var activeRoot = GetActiveSceneRoot();
    if (ReferenceEquals(target, activeRoot) || target.Parent is null ||
        ContainsActiveGameCamera(target))
        return;

    hierarchyTree.Select(null);
    selection.Select(null);
    if (presentedHud is not null && ContainsNode(target, presentedHud))
        UnmountPlayHud();
    var activeObjects = GetActiveSceneObjects();
    RemoveSceneObjects(target, activeObjects);
    target.Parent.RemoveChild(target);
    selection.SetObjects(activeObjects);
    viewportRenderer.SetSceneObjects(activeObjects);
    viewportRenderer.SetSceneRoot(GetActiveSceneRoot());
    viewportRenderer.SetGameScene(gameViewport.Camera ?? gameCamera, activeObjects);
    detachedSceneRenderer?.SetSceneObjects(activeObjects);
    detachedGameRenderer?.SetGameScene(gameViewport.Camera ?? gameCamera, activeObjects);
    if (physicsWorld is not null)
        physicsWorld.Attach(activeRoot);
    hierarchyTree.SetRoots(activeRoot.Children);
    CloseHierarchyContextMenu();
    InvalidateViewports();
}

/// <summary>Checks whether one subtree contains a specific node identity.</summary>
/// <param name="root">Subtree root.</param>
/// <param name="candidate">Node identity to find.</param>
/// <returns>True when the candidate is the root or one of its descendants.</returns>
bool ContainsNode(Node root, Node candidate)
{
    if (ReferenceEquals(root, candidate))
        return true;
    var children = root.Children;
    for (var index = 0; index < children.Count; index++)
    {
        if (ContainsNode(children[index], candidate))
            return true;
    }
    return false;
}

/// <summary>Removes every mesh instance in a deleted subtree from the renderable list.</summary>
/// <param name="node">Current subtree node.</param>
/// <param name="objects">Active flattened renderable collection.</param>
void RemoveSceneObjects(Node node, List<MeshInstance3D> objects)
{
    if (node is MeshInstance3D meshInstance)
        objects.Remove(meshInstance);
    var children = node.Children;
    for (var index = 0; index < children.Count; index++)
        RemoveSceneObjects(children[index], objects);
}

/// <summary>Checks whether deleting a subtree would remove the active Game camera.</summary>
/// <param name="target">Candidate subtree root.</param>
/// <returns>True when the active Game camera is the target or one of its descendants.</returns>
bool ContainsActiveGameCamera(Node target)
{
    Node? current = playScene?.GameCamera ?? gameCamera;
    while (current is not null)
    {
        if (ReferenceEquals(current, target))
            return true;
        current = current.Parent;
    }
    return false;
}

/// <summary>Shows nested object and component actions for the hierarchy target.</summary>
void ShowHierarchyContextMenu()
{
    if (lastMousePos.X < hierarchyTree.Left || lastMousePos.X > hierarchyTree.Right
        || lastMousePos.Y < hierarchyTree.Top || lastMousePos.Y > hierarchyTree.Bottom)
        return;

    CloseHierarchyContextMenu();
    var activeRoot = GetActiveSceneRoot();
    var target = uiEventRouter.HoveredElement is TreeViewItem row ? row.Item : activeRoot;
    hierarchyTree.Select(ReferenceEquals(target, activeRoot) ? null : target);

    const float menuWidth = 180f;
    const float menuHeight = 136f;
    var menuX = Math.Clamp(lastMousePos.X, 0f, MathF.Max(0f, width - menuWidth));
    var menuY = Math.Clamp(lastMousePos.Y, 0f, MathF.Max(0f, height - menuHeight));
    var menu = new ContextMenu(menuWidth) { Name = "HierarchyContextMenu" };
    menu.AddItem("Focus", () => FocusHierarchyNode(target),
        !ReferenceEquals(target, activeRoot) && target is Node3D);
    var objectMenu = new ContextMenu(menuWidth) { Name = "HierarchyAdd3DObjectMenu" };
    objectMenu.AddItem("Add Empty Object", () => AddSceneNode(target, null, "Object"));
    objectMenu.AddItem("Add Cube", () => AddSceneNode(target, BuiltInAssets.CubeMesh, "Cube"));
    objectMenu.AddItem("Add Plane", () => AddSceneNode(target, BuiltInAssets.PlaneMesh, "Plane"));
    objectMenu.AddItem("Add Sphere", () => AddSceneNode(target, BuiltInAssets.SphereMesh, "Sphere"));
    objectMenu.AddItem("Add Capsule", () => AddSceneNode(target, BuiltInAssets.CapsuleMesh, "Capsule"));
    objectMenu.AddItem("Add Cylinder", () => AddSceneNode(target, BuiltInAssets.CylinderMesh, "Cylinder"));
    objectMenu.AddItem("Add Terrain", () => ShowTerrainPathDialog(
        project.RootPath, (reference, name) => AddTerrainNode(target, reference, name)),
        !isPlaying);
    objectMenu.AddItem("Add Directional Light", () => AddDirectionalLight(target));
    menu.AddSubmenu("Add 3D Object", objectMenu);

    HudRoot? existingHud = null;
    FindHudRoot(activeRoot, ref existingHud);
    var uiMenu = new ContextMenu(menuWidth) { Name = "HierarchyAddUIObjectMenu" };
    uiMenu.AddItem("Add HUD Root", AddHudRoot, existingHud is null);
    menu.AddSubmenu("Add UI", uiMenu);

    var componentMenu = new ContextMenu(menuWidth) { Name = "HierarchyAddComponentMenu" };
    var canAddToTarget = !ReferenceEquals(target, activeRoot) && target is Node3D;
    var physicsMenu = new ContextMenu(menuWidth) { Name = "HierarchyPhysicsComponentMenu" };
    physicsMenu.AddItem("Rigid Body", () => AddRigidBodyComponent(target),
        canAddToTarget && target.GetComponent<RigidBodyComponent>() is null);
    physicsMenu.AddItem("Box Collider",
        () => AddColliderComponent(target, ColliderAuthoringKind.Box), canAddToTarget);
    physicsMenu.AddItem("Sphere Collider",
        () => AddColliderComponent(target, ColliderAuthoringKind.Sphere), canAddToTarget);
    physicsMenu.AddItem("Capsule Collider",
        () => AddColliderComponent(target, ColliderAuthoringKind.Capsule), canAddToTarget);
    physicsMenu.AddItem("Cylinder Collider",
        () => AddColliderComponent(target, ColliderAuthoringKind.Cylinder), canAddToTarget);
    physicsMenu.AddItem("Plane Collider",
        () => AddColliderComponent(target, ColliderAuthoringKind.Plane), canAddToTarget);
    physicsMenu.AddItem("Mesh Collider",
        () => AddColliderComponent(target, ColliderAuthoringKind.Mesh), canAddToTarget);
    physicsMenu.AddItem("Terrain Collider",
        () => AddColliderComponent(target, ColliderAuthoringKind.Terrain), canAddToTarget);
    componentMenu.AddSubmenu("Physics", physicsMenu);
    menu.AddSubmenu("Add Component", componentMenu);
    menu.AddItem("Delete", () => DeleteSceneNode(target),
        !ReferenceEquals(target, activeRoot) && target.Parent is not null &&
        !ContainsActiveGameCamera(target));
    hierarchyContextMenu = menu;
    overlay.Add(menu, new Vector2(menuX, menuY));
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
}

/// <summary>Frames one hierarchy node in the Scene viewport and closes its context menu.</summary>
/// <param name="target">Hierarchy item requested by the user.</param>
void FocusHierarchyNode(Node target)
{
    if (target is Node3D node && SceneCameraFraming.TryFrame(sceneCamera, node))
    {
        renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
        flyInputWindow.RequestFrame();
    }
    CloseHierarchyContextMenu();
}

void AttachHierarchy(TreeView tree)
{
    tree.SelectionChanged += item =>
    {
        inspector.Bind(item, synchronizingSelection ? selection.SelectedComponent : null);
        if (synchronizingSelection)
            return;
        synchronizingSelection = true;
        selection.Select(item as Node3D);
        synchronizingSelection = false;
    };
}

/// <summary>Refreshes hierarchy rows only when an Inspector edit changes their labels.</summary>
/// <param name="sceneInspector">Inspector to attach.</param>
void AttachInspector(SceneInspector sceneInspector)
{
    sceneInspector.NodeChanged += node =>
    {
        if (node is MeshInstance3D meshInstance)
        {
            LoadAssetMeshResources(meshInstance);
            if (detachedSceneRenderer is not null)
                LoadAssetMeshResources(meshInstance, targetRenderer: detachedSceneRenderer);
            if (detachedGameRenderer is not null)
                LoadAssetMeshResources(meshInstance, targetRenderer: detachedGameRenderer);
        }
        InvalidateViewports();
    };
    sceneInspector.NodeNameChanged += _ =>
    {
        hierarchyTree.Refresh();
        RefreshVertices();
    };
}

/// <summary>Gets the scene graph currently exposed to editing tools.</summary>
/// <returns>The runtime root during Play; otherwise, the authored root.</returns>
Node3D GetActiveSceneRoot()
{
    return playScene?.Root ?? sceneRoot;
}

/// <summary>Gets the renderable objects currently exposed to editing tools.</summary>
/// <returns>The runtime objects during Play; otherwise, the authored objects.</returns>
List<MeshInstance3D> GetActiveSceneObjects()
{
    return playScene?.MeshInstances ?? sceneObjects;
}

/// <summary>Rearranges the logical editor layout without rebuilding its UI tree.</summary>
/// <param name="newWidth">New logical window width.</param>
/// <param name="newHeight">New logical window height.</param>
void ResizeEditor(int newWidth, int newHeight)
{
    if (newWidth <= 0 || newHeight <= 0)
        return;

    width = newWidth;
    height = newHeight;
    mainUIHost.Resize(width, height);
}

/// <summary>Reallocates viewport FBOs once a live native resize has settled.</summary>
void ResizeViewportTargets()
{
    var invalidation = RenderInvalidation.None;
    if (detachedSceneWindow is null)
    {
        window.ResizeRenderView(sceneViewportId, sceneViewport.Width, sceneViewport.Height);
        invalidation |= RenderInvalidation.SceneViewport;
    }
    if (detachedGameWindow is null)
    {
        window.ResizeRenderView(gameViewportId, gameViewport.Width, gameViewport.Height);
        invalidation |= RenderInvalidation.GameViewport;
    }
    if (invalidation == RenderInvalidation.None)
        return;
    renderScheduler.Invalidate(invalidation);
    window.RequestFrame();
}

var pendingResizeWidth = 0;
var pendingResizeHeight = 0;
var pendingResizeTimestamp = 0L;
window.Resized += (newWidth, newHeight) =>
{
    pendingResizeWidth = newWidth;
    pendingResizeHeight = newHeight;
    pendingResizeTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
};

void RefreshVertices()
{
    renderScheduler.Invalidate(RenderInvalidation.SceneViewport | RenderInvalidation.GameViewport);
    RefreshUI();
}

/// <summary>Submits changed UI state without rebuilding retained viewport content.</summary>
void RefreshUI()
{
    renderScheduler.Invalidate(RenderInvalidation.UI);
    mainUIHost.Refresh();
    renderScheduler.Consume(RenderInvalidation.UI);
    detachedSceneWindow?.UIHost.Refresh();
    detachedGameWindow?.UIHost.Refresh();
}

/// <summary>Marks both scene-derived viewport textures stale and wakes the event loop.</summary>
void InvalidateViewports()
{
    renderScheduler.Invalidate(
        RenderInvalidation.SceneViewport | RenderInvalidation.GameViewport);
    window.RequestFrame();
}

GizmoViewport GetSceneGizmoViewport()
{
    return new GizmoViewport(sceneViewport.Left, sceneViewport.Top,
        sceneViewport.Width, sceneViewport.Height);
}

bool IsInSceneViewport(Vector2 screenPos)
{
    var vpX = sceneViewport.Left;
    var vpY = sceneViewport.Top;
    var vpW = sceneViewport.Width;
    var vpH = sceneViewport.Height;
    return screenPos.X >= vpX && screenPos.X <= vpX + vpW
        && screenPos.Y >= vpY && screenPos.Y <= vpY + vpH;
}

/// <summary>Reparents or reorders one hierarchy node at a resolved tree destination.</summary>
/// <param name="source">Scene node being moved.</param>
/// <param name="dropTarget">Resolved hierarchy destination.</param>
void MoveHierarchyNode(Node source, TreeViewDropTarget dropTarget)
{
    var activeRoot = GetActiveSceneRoot();
    var destination = dropTarget.Parent ?? activeRoot;
    if (ReferenceEquals(source, destination))
        return;
    try
    {
        destination.InsertChild(dropTarget.InsertionIndex, source);
        hierarchyTree.SetRoots(activeRoot.Children);
        if (!ReferenceEquals(destination, activeRoot))
            hierarchyTree.Expand(destination);
        hierarchyTree.Select(source);
        logger.LogInformation("Moved scene node {NodeName} under {ParentName}", source.Name,
            ReferenceEquals(destination, activeRoot) ? "Scene" : destination.Name);
        RefreshVertices();
    }
    catch (InvalidOperationException exception)
    {
        logger.LogWarning(exception, "Rejected hierarchy move for {NodeName}", source.Name);
    }
}

/// <summary>Moves one project file or folder into a destination folder.</summary>
/// <param name="source">Filesystem entry being moved.</param>
/// <param name="dropTarget">Resolved filesystem tree destination.</param>
void MoveFileSystemEntry(FileSystemNode source, TreeViewDropTarget dropTarget)
{
    var target = dropTarget.Parent as FileSystemNode;
    var destinationDirectory = target?.FullPath ?? project.RootPath;
    var sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source.FullPath));
    destinationDirectory = Path.TrimEndingDirectorySeparator(
        Path.GetFullPath(destinationDirectory));
    var destinationPath = Path.Combine(destinationDirectory,
        Path.GetFileName(sourcePath));
    if (ReferenceEquals(source, target)
        || string.Equals(sourcePath, destinationDirectory, StringComparison.Ordinal)
        || string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
        return;
    if (source.IsDirectory && destinationDirectory.StartsWith(
            sourcePath + Path.DirectorySeparatorChar,
            StringComparison.Ordinal))
    {
        logger.LogWarning("Cannot move folder {SourcePath} inside itself", sourcePath);
        return;
    }
    if (File.Exists(destinationPath) || Directory.Exists(destinationPath))
    {
        logger.LogWarning("Cannot move {SourcePath}; destination exists: {DestinationPath}",
            sourcePath, destinationPath);
        return;
    }

    try
    {
        string? activeRelativePath = null;
        if (ContainsActiveScene(sourcePath) && activeScenePath is not null)
            activeRelativePath = source.IsDirectory
                ? Path.GetRelativePath(sourcePath, activeScenePath) : string.Empty;
        if (source.IsDirectory)
            Directory.Move(sourcePath, destinationPath);
        else
            File.Move(sourcePath, destinationPath);
        if (activeRelativePath is not null)
        {
            activeScenePath = activeRelativePath.Length == 0
                ? destinationPath : Path.Combine(destinationPath, activeRelativePath);
            editorView.ProjectLabel.Text = Path.GetFileName(activeScenePath);
        }
        logger.LogInformation("Moved project item {SourcePath} to {DestinationPath}",
            sourcePath, destinationPath);
        RefreshFileSystem();
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
        or NotSupportedException)
    {
        logger.LogError(exception, "Could not move project item {SourcePath}", sourcePath);
    }
}

/// <summary>Connects editor trees and Inspector slots to the shared routed drag system.</summary>
void ConfigureEditorDragDrop()
{
    hierarchyTree.ItemDragData = item => new UIDragData(
        new EditorTreeDragData(item, true), item.Name);
    hierarchyTree.ItemDragEffects = UIDragEffect.Move;
    hierarchyTree.AllowItemDrop = true;
    hierarchyTree.CanDropInsideItem = static _ => true;
    hierarchyTree.AllowDrop = true;
    hierarchyTree.Drag += HandleHierarchyDrag;

    fileSystemTree.ItemDragData = item => EditorDragPolicy.CanStartFileSystemDrag(item)
        ? new UIDragData(new EditorTreeDragData(item, false), item.Name) : null;
    fileSystemTree.ItemDragEffects = UIDragEffect.Copy | UIDragEffect.Move;
    fileSystemTree.AllowItemDrop = true;
    fileSystemTree.CanDropInsideItem = static item =>
        item is FileSystemNode { IsDirectory: true };
    fileSystemTree.AllowDrop = true;
    fileSystemTree.Drag += HandleFileSystemDrag;
    inspector.Drag += HandleInspectorDrag;
}

/// <summary>Negotiates and commits hierarchy instantiation or reparenting.</summary>
/// <param name="sender">Current routed receiver.</param>
/// <param name="dragEvent">Typed routed drag event.</param>
void HandleHierarchyDrag(UIElement sender, UIDragEventArgs dragEvent)
{
    if (dragEvent.RoutePhase == UIRoutePhase.Preview ||
        !dragEvent.Data.TryGet<EditorTreeDragData>(out var payload) || payload is null)
        return;
    var dropTarget = hierarchyTree.ResolveDropTarget(dragEvent);
    var effect = payload.Item switch
    {
        ImportedSubAssetNode imported when EditorDragPolicy.CanInstantiateInHierarchy(imported) =>
            UIDragEffect.Copy,
        FileSystemNode file when EditorDragPolicy.CanInstantiateInHierarchy(file) =>
            UIDragEffect.Copy,
        _ when payload.FromHierarchy => UIDragEffect.Move,
        _ => UIDragEffect.None
    };
    if (dragEvent.Kind is UIDragEventKind.Enter or UIDragEventKind.Over)
        dragEvent.Effect = effect;
    else if (dragEvent.Kind == UIDragEventKind.Drop && effect != UIDragEffect.None)
    {
        if (payload.Item is ImportedSubAssetNode imported)
            InstantiateImportedMesh(imported, dropTarget);
        else if (payload.Item is FileSystemNode file &&
                 Path.GetExtension(file.FullPath).Equals(
                     ".nterrain", StringComparison.OrdinalIgnoreCase))
            InstantiateTerrain(file, dropTarget);
        else if (payload.Item is FileSystemNode modelFile)
            InstantiateGlbPrimaryMesh(modelFile, dropTarget);
        else
            MoveHierarchyNode(payload.Item, dropTarget);
        dragEvent.Effect = effect;
    }
    dragEvent.Handled = true;
}

/// <summary>Negotiates and commits project-file moves through routed tree drops.</summary>
/// <param name="sender">Current routed receiver.</param>
/// <param name="dragEvent">Typed routed drag event.</param>
void HandleFileSystemDrag(UIElement sender, UIDragEventArgs dragEvent)
{
    if (dragEvent.RoutePhase == UIRoutePhase.Preview ||
        !dragEvent.Data.TryGet<EditorTreeDragData>(out var payload) ||
        payload?.Item is not FileSystemNode file || payload.FromHierarchy)
        return;
    var dropTarget = fileSystemTree.ResolveDropTarget(dragEvent);
    if (dragEvent.Kind is UIDragEventKind.Enter or UIDragEventKind.Over)
        dragEvent.Effect = UIDragEffect.Move;
    else if (dragEvent.Kind == UIDragEventKind.Drop)
    {
        MoveFileSystemEntry(file, dropTarget);
        dragEvent.Effect = UIDragEffect.Move;
    }
    dragEvent.Handled = true;
}

/// <summary>Negotiates and commits typed asset drops on Inspector component slots.</summary>
/// <param name="sender">Current routed receiver.</param>
/// <param name="dragEvent">Typed routed drag event.</param>
void HandleInspectorDrag(UIElement sender, UIDragEventArgs dragEvent)
{
    if (dragEvent.RoutePhase != UIRoutePhase.Bubble ||
        !dragEvent.Data.TryGet<EditorTreeDragData>(out var payload) || payload is null)
        return;
    if (dragEvent.Target is AssetReferenceField assetField &&
        assetDropResolver.TryResolve(
            payload.Item, assetField.AcceptedContentType, out var reference))
    {
        if (dragEvent.Kind is UIDragEventKind.Enter or UIDragEventKind.Over)
            dragEvent.Effect = UIDragEffect.Copy;
        else if (dragEvent.Kind == UIDragEventKind.Drop && assetField.Assign(reference))
        {
            dragEvent.Effect = UIDragEffect.Copy;
            RefreshVertices();
        }
        dragEvent.Handled = true;
        return;
    }
    if (dragEvent.Target is not TextField { Name: "ScriptAssetField" } ||
        payload.Item is not FileSystemNode scriptFile ||
        !Path.GetExtension(scriptFile.FullPath).Equals(".cs", StringComparison.OrdinalIgnoreCase))
        return;
    if (dragEvent.Kind is UIDragEventKind.Enter or UIDragEventKind.Over)
        dragEvent.Effect = UIDragEffect.Copy;
    else if (dragEvent.Kind == UIDragEventKind.Drop && ScriptFileDrop.TryAttach(
                 scriptFile, inspector, assetDatabase))
    {
        dragEvent.Effect = UIDragEffect.Copy;
        RefreshVertices();
    }
    dragEvent.Handled = true;
}

var sceneMouseNavigation = PointerButtons.None;

mainUIHost.PreviewPointerMove = pointerEvent =>
{
    lastMousePos = pointerEvent.Position;
    window.UpdateWindowDrag(pointerEvent.Position);
    Debug.Input(LogLevel.Trace, "Mouse: ({X:F0}, {Y:F0})",
        pointerEvent.Position.X, pointerEvent.Position.Y);
    if ((sceneMouseNavigation & PointerButtons.Secondary) != 0)
    {
        flyCamera.ApplyMouseLook(pointerEvent.Delta);
        renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
        window.RequestFrame();
        return true;
    }
    if ((sceneMouseNavigation & PointerButtons.Middle) != 0)
    {
        flyCamera.ApplyMousePan(pointerEvent.Delta);
        renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
        window.RequestFrame();
        return true;
    }
    if (terrainBrush.MovePointer(pointerEvent.Position))
    {
        renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
        window.RequestFrame();
        return true;
    }
    return flyCamera.MovePointer(pointerEvent.Position);
};

mainUIHost.PointerMoveProcessed = (pointerEvent, routed) =>
{
    if (!routed)
        return;
    var pos = pointerEvent.Position;
    if (dockWorkspace.IsTabSelected(EditorDockWorkspace.ProfilerId))
        editorView.Profiler.MovePointer(pos);
    var scenePointerActive = selection.IsDragging ||
        uiEventRouter.CapturedElement is null &&
        (uiEventRouter.HoveredElement is ViewportPanel hoveredViewport &&
            hoveredViewport.RenderView == sceneViewportId ||
         uiEventRouter.HoveredElement is null && IsInSceneViewport(pos));
    if (scenePointerActive)
    {
        selection.MovePointer(pos);
        renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
        window.RequestFrame();
    }
};

mainUIHost.PreviewPointerButton = pointerEvent =>
{
    if (pointerEvent.ClickCount >= 2 && pointerEvent.IsPressed)
        return flyCamera.IsActive ? UIHostPointerRouting.Consume : UIHostPointerRouting.Route;
    if (!pointerEvent.IsPressed)
    {
        if (pointerEvent.Button == InputPointerButton.Secondary)
            sceneMouseNavigation &= ~PointerButtons.Secondary;
        else if (pointerEvent.Button == InputPointerButton.Middle)
            sceneMouseNavigation &= ~PointerButtons.Middle;
        window.EndWindowDrag();
        if (pointerEvent.Button == InputPointerButton.Primary && terrainBrush.PrimaryUp())
        {
            renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
            window.RequestFrame();
            return UIHostPointerRouting.Consume;
        }
        if (flyCamera.IsActive || pointerEvent.Button != InputPointerButton.Primary)
            return UIHostPointerRouting.Consume;
        var consumedByGizmo = selection.PrimaryUp();
        renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
        window.RequestFrame();
        return consumedByGizmo || uiEventRouter.IsDragging
            ? UIHostPointerRouting.RouteWithoutClick
            : UIHostPointerRouting.Route;
    }
    if (flyCamera.IsActive)
        return UIHostPointerRouting.Consume;
    var inSceneViewport = IsInsideSceneViewport(pointerEvent.Position);
    if (inSceneViewport && pointerEvent.Button == InputPointerButton.Primary &&
        terrainBrush.PrimaryDown(pointerEvent.Position))
    {
        selection.CancelInteraction();
        renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
        window.RequestFrame();
        return UIHostPointerRouting.Consume;
    }
    if (inSceneViewport && pointerEvent.Button == InputPointerButton.Secondary)
    {
        sceneMouseNavigation |= PointerButtons.Secondary;
        selection.CancelInteraction();
        return UIHostPointerRouting.Consume;
    }
    if (inSceneViewport && pointerEvent.Button == InputPointerButton.Middle)
    {
        sceneMouseNavigation |= PointerButtons.Middle;
        selection.CancelInteraction();
        return UIHostPointerRouting.Consume;
    }
    if (pointerEvent.Button == InputPointerButton.Secondary)
    {
        if (!ShowFileSystemContextMenu())
            ShowHierarchyContextMenu();
        return UIHostPointerRouting.Consume;
    }
    if (pointerEvent.Button != InputPointerButton.Primary)
        return UIHostPointerRouting.Consume;
    if (hierarchyContextMenu is not null && uiEventRouter.HoveredElement is not ContextMenuItem)
        CloseHierarchyContextMenu();
    if (fileContextMenu is not null && uiEventRouter.HoveredElement is not ContextMenuItem)
        CloseFileContextMenu();
    return UIHostPointerRouting.Route;
};

mainUIHost.PointerButtonProcessed = (pointerEvent, routed) =>
{
    if (!routed || !pointerEvent.IsPressed || pointerEvent.ClickCount >= 2 ||
        pointerEvent.Button != InputPointerButton.Primary)
        return;
    var inSceneViewport =
        uiEventRouter.HoveredElement is ViewportPanel vp && vp.RenderView == sceneViewportId ||
        uiEventRouter.HoveredElement is null && IsInSceneViewport(lastMousePos);
    if (!inSceneViewport)
        return;
    selection.PrimaryDown(lastMousePos, inSceneViewport);
    renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
    window.RequestFrame();
};

mainUIHost.PreviewPointerWheel = pointerEvent =>
{
    Debug.Input(LogLevel.Debug, "Scroll: offset=({OffsetX:F1}, {OffsetY:F1})",
        pointerEvent.Delta.X, pointerEvent.Delta.Y);
    return ApplySceneWheelOrTrackpadGesture(pointerEvent, window);
};
mainUIHost.PreviewPointerMagnify = pointerEvent =>
    ApplyScenePinchGesture(pointerEvent, window);

mainUIHost.PreviewKey = keyEvent =>
{
    Debug.Input(LogLevel.Debug, "Key: key={Key}, pressed={Pressed}, repeat={Repeat}",
        keyEvent.Key, keyEvent.IsPressed, keyEvent.IsRepeat);
    if (keyEvent.IsPressed && keyEvent.Key == InputKey.Escape && terrainBrush.Cancel())
    {
        renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
        window.RequestFrame();
        return true;
    }
    return sceneInputContext.RouteKey(uiEventRouter, keyEvent);
};
mainUIHost.PreviewTextInput = _ => sceneInputContext.RoutesText(uiEventRouter);
mainUIHost.PreviewTextComposition = _ => sceneInputContext.RoutesText(uiEventRouter);

// ── Game loop: Update → Render ──────────────────────────────
window.Update += delta =>
{
    if (workspaceSaveTimestamp != 0 &&
        System.Diagnostics.Stopwatch.GetElapsedTime(workspaceSaveTimestamp) >=
        TimeSpan.FromMilliseconds(300))
    {
        workspaceSaveTimestamp = 0;
        SaveDockWorkspace();
    }
    if (Interlocked.Exchange(ref profilerRefreshPending, 0) != 0)
        RefreshUI();
    if (Interlocked.Exchange(ref assetRefreshPending, 0) != 0)
    {
        var assetChanges = assetDatabase.Refresh();
        foreach (var diagnostic in assetDatabase.Diagnostics)
            logger.LogWarning("Asset metadata {AssetPath}: {Message}",
                diagnostic.Path, diagnostic.Message);
        if (assetChanges.Count > 0)
        {
            var changedRecords = new List<AssetMetadataRecord>(assetChanges.Count);
            var invalidatedAssets = new HashSet<AssetId>();
            for (var index = 0; index < assetChanges.Count; index++)
            {
                var change = assetChanges[index];
                var asset = change.Current?.Id ?? change.Previous!.Id;
                if (invalidatedAssets.Add(asset))
                    runtimeResources.Invalidate(asset);
                if (change.Current is not null)
                    changedRecords.Add(change.Current);
            }
            QueueAssetImports(changedRecords);
            logger.LogInformation("Refreshed asset database with {ChangeCount} changes",
                assetChanges.Count);
            RefreshFileSystem();
            StartScriptSchemaBuild();
        }
    }
    UpdateAssetImports();
    UpdateAssetBindings();
    UpdateScriptSchemaBuild();
    UpdatePlayModeStart(delta);
    if (pendingResizeTimestamp != 0)
    {
        var resizeSettled = System.Diagnostics.Stopwatch.GetElapsedTime(pendingResizeTimestamp)
            >= TimeSpan.FromMilliseconds(100);
        ResizeEditor(pendingResizeWidth, pendingResizeHeight);
        if (resizeSettled)
        {
            pendingResizeTimestamp = 0;
            ResizeViewportTargets();
        }
    }
    flyCamera.Update(delta);
    if (scriptHost is not null)
    {
        try
        {
            scriptHost.Update(delta);
            physicsWorld?.Update(delta);
            scriptHost.LateUpdate(delta);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "A game script failed; play mode has been stopped");
            StopPlayMode();
        }
    }
    viewportRenderer.UpdateAnimations(delta);
    detachedSceneRenderer?.UpdateAnimations(delta);
    detachedGameRenderer?.UpdateAnimations(delta);
    modelPreviewController.Update(delta);
    var sceneContinuous = flyCamera.IsActive || scriptHost is not null;
    var gameContinuous = scriptHost is not null;
    var sceneVisible = detachedSceneWindow is null
        ? sceneViewportPresentation.IsPresented
        : sceneViewport.IsEffectivelyVisible && sceneViewport.Width > 0f &&
            sceneViewport.Height > 0f;
    var gameVisible = gameViewport.IsEffectivelyVisible;
    viewportRenderer.SynchronizeSceneVisibility(
        detachedSceneWindow is null && sceneVisible);
    var sceneInvalid = sceneVisible &&
        renderScheduler.Consume(RenderInvalidation.SceneViewport);
    var gameInvalid = gameVisible &&
        renderScheduler.Consume(RenderInvalidation.GameViewport);
    if (detachedSceneWindow is null
        && sceneVisible && (sceneContinuous || sceneInvalid))
        viewportRenderer.RenderScene(sceneViewport, lastMousePos);
    if (detachedGameWindow is null
        && gameVisible && (gameContinuous || gameInvalid))
        viewportRenderer.RenderGame(gameViewport);
    secondaryWindows.PumpFrames();
    if (detachedSceneWindow is null && sceneViewportPresentation.IsPresented &&
        renderScheduler.Consume(RenderInvalidation.SceneViewport))
        viewportRenderer.RenderScene(sceneViewport, lastMousePos);
    if (detachedGameWindow is null && gameViewport.IsEffectivelyVisible &&
        renderScheduler.Consume(RenderInvalidation.GameViewport))
        viewportRenderer.RenderGame(gameViewport);
    dockSession.SynchronizeFloatingWindows();
    window.SetContinuousRendering(
        flyCamera.IsActive || scriptHost is not null || viewportRenderer.HasActiveAnimations ||
            modelPreviewController.RequiresContinuousUpdates ||
            playBuildTask is not null
            || scriptSchemaBuildTask is not null
            || assetImportTask is not null
            || assetBindings.Count > 0
            || pendingResizeTimestamp != 0
            || workspaceSaveTimestamp != 0
            || mainUIHost.RequiresContinuousUpdates
            || dockSession.RequiresContinuousUpdates);
};

logger.LogInformation("Running main loop...");
window.Run();
isShuttingDown = true;
assetImportCancellation?.Cancel();
if (assetImportTask is not null)
{
    try
    {
        assetImportTask.GetAwaiter().GetResult();
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Could not finish the pending asset import batch");
    }
}
assetImportCancellation?.Dispose();
SaveDockWorkspace();
StopPlayMode();
if (playBuildTask is not null)
{
    playBuildCancellation?.Cancel();
    try
    {
        playBuildTask.GetAwaiter().GetResult().Dispose();
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Could not finish the pending game script build");
    }
}
playBuildCancellation?.Dispose();
if (scriptSchemaBuildTask is not null)
{
    scriptSchemaBuildCancellation?.Cancel();
    try
    {
        scriptSchemaBuildTask.GetAwaiter().GetResult().Dispose();
    }
    catch (OperationCanceledException)
    {
    }
    catch (Exception exception)
    {
        logger.LogError(exception, "Could not finish the pending Inspector schema build");
    }
}
scriptSchemaBuildCancellation?.Dispose();
scriptSchemaHost?.Dispose();
logger.LogInformation("Done.");
return 0;
