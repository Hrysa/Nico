using System.Numerics;
using Editor;
using Engine.Assets;
using Engine.Core;
using Engine.Graphics;
using Engine.Physics;
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
assetImporterRegistry.Register(new GlbModelImporter());
var assetImportPipeline = new AssetImportPipeline(assetDatabase, assetImporterRegistry);
using var runtimeResources = new RuntimeResourceManager(
    new PublishedArtifactResolver(assetDatabase, assetImportPipeline, "editor"),
    new AssetStorageRouter(new MountedVirtualFileSystem()),
    unusedCapacity: 128);
runtimeResources.RegisterLoader(new DelegateRuntimeResourceLoader<StaticMeshResource>(
    "nico/static-mesh", (stream, _, _) => StaticMeshResource.Load(stream)));
runtimeResources.RegisterLoader(new DelegateRuntimeResourceLoader<DecodedStandardMaterial>(
    "nico/standard-material", (stream, _, _) =>
    {
        var (material, textureSlot) = StandardMaterialResource.Load(stream);
        return new DecodedStandardMaterial(material, textureSlot);
    }));
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
    PresentationMode = PresentationModePreference.LowLatency
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
using var dockSession = new DockSession(
    dockWorkspace,
    EditorDockWorkspace.CreateRegistry(editorView, allowViewportFloating: true),
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
        var loadedScene = SceneFileStore.Load(activeScenePath);
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
inspector.ResolveMaterial = ResolveMaterialProperties;
inspector.ResolveMaterialName = ResolveMaterialDisplayName;
hierarchyTree.SetRoots(sceneRoot.Children);

GameScriptHost? scriptHost = null;
PhysicsWorld? physicsWorld = null;
GameScriptHost? scriptSchemaHost = null;
LoadedScene? playScene = null;
LoadedScene? pendingPlayScene = null;
Task<GameScriptHost>? playBuildTask = null;
CancellationTokenSource? playBuildCancellation = null;
Task<GameScriptHost>? scriptSchemaBuildTask = null;
CancellationTokenSource? scriptSchemaBuildCancellation = null;
CompilationProgressDialog? compilationProgressDialog = null;
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
var gameViewportId = window.CreateRenderView(gameViewport.Width, gameViewport.Height);
gameViewport.RenderView = gameViewportId;
gameViewport.Camera = gameCamera;
var gameViewportPresentation = new ViewportPresentationTracker(gameViewport);
gameViewportPresentation.Synchronize(window);
window.SetViewportClearColor(gameViewportId, 0.05f, 0.05f, 0.12f);

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
    window, sceneViewportId, gameViewportId, sceneCamera, gameCamera, sceneObjects, selection);
var renderScheduler = new EditorRenderScheduler();
foreach (var assetMesh in sceneObjects)
    LoadAssetMeshResources(assetMesh);
DetachedToolWindow? detachedSceneWindow = null;
DetachedToolWindow? detachedGameWindow = null;
EditorViewportRenderer? detachedSceneRenderer = null;
EditorViewportRenderer? detachedGameRenderer = null;

/// <summary>Aligns main-window viewport textures with their latest retained layout bounds.</summary>
void SynchronizeMainViewportPresentations()
{
    if (detachedSceneWindow is null)
        sceneViewportPresentation.Synchronize(window);
    if (detachedGameWindow is null)
        gameViewportPresentation.Synchronize(window);
}

mainUIHost.LayoutUpdated += SynchronizeMainViewportPresentations;
dockSession.MainHost.SplitResizeCompleted += ResizeViewportTargets;
dockWindowFactory.RegisterLifecycle(
    EditorDockWorkspace.SceneId, OpenFloatingSceneViewport, CloseFloatingSceneViewport);
dockWindowFactory.RegisterLifecycle(
    EditorDockWorkspace.GameId, OpenFloatingGameViewport, CloseFloatingGameViewport);
dockSession.SynchronizeFloatingWindows();

var uiEventRouter = mainUIHost.InputRouter;
dockSession.AttachDragRouter(dockSession.MainHost, uiEventRouter, window, RefreshUI);
ContextMenu? hierarchyContextMenu = null;
ContextMenu? fileContextMenu = null;
ContextMenu? fileSubmenu = null;
ScenePickerDialog? scenePickerDialog = null;
FileSystemCreateDialog? fileSystemCreateDialog = null;
ConfirmationDialog? confirmationDialog = null;
DragPreview? dragPreview = null;
var fileSystemTree = editorView.FileSystemTree;
var requestedFileSystemExpansion = new HashSet<string>(StringComparer.Ordinal);
var createdObjectIndex = 1;
var profilerRefreshPending = 0;
CpuProfiler.Enabled = !editorView.Profiler.IsPaused;
window.FrameProfiled += sample =>
{
    if (dockWorkspace.IsTabSelected(EditorDockWorkspace.ProfilerId) &&
        editorView.Profiler.AddSample(sample))
        Interlocked.Exchange(ref profilerRefreshPending, 1);
};
AttachFileSystem(fileSystemTree);
AttachInspector(inspector);
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
editorView.Profiler.Click += () =>
{
    if (editorView.Profiler.SelectFrame(lastMousePos))
    {
        CpuProfiler.Enabled = false;
        editorView.ProfilerPauseLabel.Text = "Record";
    }
};
RefreshVertices();

/// <summary>Connects focused Scene viewport input to one native-window UI host.</summary>
/// <param name="host">Host whose independent router owns focus for the viewport.</param>
/// <param name="includePointerLook">Whether this host also needs standalone fly-camera pointer routing.</param>
void ConfigureSceneViewportInput(UIHost host, bool includePointerLook)
{
    host.PreviewKey = keyEvent => sceneInputContext.RouteKey(host.InputRouter, keyEvent);
    host.PreviewTextInput = _ => sceneInputContext.RoutesText(host.InputRouter);
    host.PreviewTextComposition = _ => sceneInputContext.RoutesText(host.InputRouter);
    if (!includePointerLook)
        return;
    host.PreviewPointerMove = pointerEvent => flyCamera.MovePointer(pointerEvent.Position);
    host.PreviewPointerWheel = _ => flyCamera.IsActive;
    host.PreviewPointerButton = _ => flyCamera.IsActive
        ? UIHostPointerRouting.Consume
        : UIHostPointerRouting.Route;
}

/// <summary>Transfers Scene rendering into a newly opened dock window.</summary>
/// <param name="toolWindow">Opened floating tool window.</param>
void OpenFloatingSceneViewport(DetachedToolWindow toolWindow)
{
    detachedSceneWindow = toolWindow;
    window.DestroyRenderView(sceneViewportId);
    sceneViewportPresentation.Reset();
    var detachedWindow = toolWindow.Window;
    flyCamera.ReleaseFocus();
    flyInputWindow = detachedWindow;
    ConfigureSceneViewportInput(toolWindow.UIHost, includePointerLook: true);
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
        sceneCamera, gameViewport.Camera ?? gameCamera, GetActiveSceneObjects(), selection);
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
        sceneCamera, gameViewport.Camera ?? gameCamera, GetActiveSceneObjects(), selection);
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
    try
    {
        candidateHost = build.GetAwaiter().GetResult();
        if (candidateScene is null)
            throw new InvalidOperationException("The pending play scene is unavailable.");
        candidateHost.LoadScene(candidateScene.Root, window);
        var candidatePhysicsWorld = new PhysicsWorld();
        candidatePhysicsWorld.EnableInterpolation = true;
        candidatePhysicsWorld.Attach(candidateScene.Root);
        editSelectionBeforePlay = selection.SelectedNode;
        selection.SetObjects(candidateScene.MeshInstances);
        playScene = candidateScene;
        scriptHost = candidateHost;
        physicsWorld = candidatePhysicsWorld;
        isPlaying = true;
        viewportRenderer.SetSceneObjects(candidateScene.MeshInstances);
        hierarchyTree.SetRoots(candidateScene.Root.Children);
        inspector.RefreshScriptSchemas();
        gameViewport.Camera = candidateScene.GameCamera;
        viewportRenderer.SetGameScene(candidateScene.GameCamera, candidateScene.MeshInstances);
        foreach (var assetMesh in candidateScene.MeshInstances)
        {
            LoadAssetMeshResources(assetMesh);
            if (detachedSceneRenderer is not null)
                LoadAssetMeshResources(assetMesh, targetRenderer: detachedSceneRenderer);
            if (detachedGameRenderer is not null)
                LoadAssetMeshResources(assetMesh, targetRenderer: detachedGameRenderer);
        }
        editorView.PlayButtonLabel.Text = "Stop";
        logger.LogInformation("Entered play mode with {ScriptCount} scripts",
            candidateHost.ScriptCount);
    }
    catch (Exception exception)
    {
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
    scriptHost = null;
    physicsWorld = null;
    playScene = null;
    isPlaying = false;
    selection.SetObjects(sceneObjects);
    viewportRenderer.SetSceneObjects(sceneObjects);
    hierarchyTree.SetRoots(sceneRoot.Children);
    selection.Select(editSelectionBeforePlay);
    inspector.RefreshScriptSchemas();
    editSelectionBeforePlay = null;
    gameViewport.Camera = gameCamera;
    viewportRenderer.SetGameScene(gameCamera, sceneObjects);
    foreach (var assetMesh in sceneObjects)
    {
        LoadAssetMeshResources(assetMesh);
        if (detachedSceneRenderer is not null)
            LoadAssetMeshResources(assetMesh, targetRenderer: detachedSceneRenderer);
        if (detachedGameRenderer is not null)
            LoadAssetMeshResources(assetMesh, targetRenderer: detachedGameRenderer);
    }
    editorView.PlayButtonLabel.Text = "Play";
    logger.LogInformation("Exited play mode");
    StartScriptSchemaBuild();
    RefreshVertices();
}

/// <summary>Connects the title-bar play control to the current play state.</summary>
/// <param name="playButton">Play/Stop button to attach.</param>
void AttachPlayButton(Button playButton)
{
    editorView.PlayButtonLabel.Text = isPlaying ? "Stop" : "Play";
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
    CpuProfiler.Enabled = !editorView.Profiler.IsPaused;
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

/// <summary>Toggles live frame recording while retaining captured history.</summary>
void ToggleProfilerPause()
{
    editorView.Profiler.SetPaused(!editorView.Profiler.IsPaused);
    CpuProfiler.Enabled = !editorView.Profiler.IsPaused;
    editorView.ProfilerPauseLabel.Text = editorView.Profiler.IsPaused ? "Record" : "Pause";
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
    if (dragPreview is not null)
        overlay.Remove(dragPreview);
    fileContextMenu = null;
    fileSubmenu = null;
    scenePickerDialog = null;
    fileSystemCreateDialog = null;
    confirmationDialog = null;
    dragPreview = null;
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
        var loadedScene = SceneFileStore.Load(scenePath);
        selection.Select(null);
        sceneRoot.ClearChildren();
        foreach (var child in loadedScene.Root.Children.ToArray())
            sceneRoot.AddChild(child);
        sceneObjects.Clear();
        sceneObjects.AddRange(loadedScene.MeshInstances);
        gameCamera = loadedScene.GameCamera;
        gameViewport.Camera = gameCamera;
        viewportRenderer.SetSceneObjects(sceneObjects);
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

/// <summary>Returns whether an imported artifact should appear as a selectable child.</summary>
/// <param name="contentType">Artifact content type.</param>
/// <returns>True for user-facing model resources.</returns>
bool IsVisibleImportedArtifact(string contentType)
{
    return contentType is "nico/static-mesh" or "nico/standard-material" or "nico/texture2d";
}

/// <summary>Builds a concise typed label for one imported artifact.</summary>
/// <param name="artifact">Imported artifact description.</param>
/// <returns>Display label including resource type.</returns>
string GetImportedArtifactDisplayName(AssetArtifact artifact)
{
    var type = artifact.ContentType switch
    {
        "nico/static-mesh" => "Mesh",
        "nico/standard-material" => "Material",
        "nico/texture2d" => "Texture",
        _ => "Asset"
    };
    var keyParts = artifact.Key.Split('/');
    var name = artifact.ContentType == "nico/static-mesh" && keyParts.Length > 1
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
/// <param name="target">Hierarchy parent target, or null for the scene root.</param>
void InstantiateImportedMesh(ImportedSubAssetNode source, Node? target)
{
    if (source.ContentType != "nico/static-mesh")
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
    var destination = target ?? sceneRoot;
    try
    {
        destination.AddChild(instance);
        sceneObjects.Add(instance);
        viewportRenderer.SetSceneObjects(sceneObjects);
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

/// <summary>Treats a dragged GLB source as its primary imported mesh.</summary>
/// <param name="source">Dragged physical GLB file.</param>
/// <param name="target">Hierarchy parent target, or null for the scene root.</param>
void InstantiateGlbPrimaryMesh(FileSystemNode source, Node? target)
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
    var primaryMesh = outcome.Artifacts.FirstOrDefault(artifact =>
        artifact.ContentType == "nico/static-mesh");
    if (!outcome.Succeeded || primaryMesh is null)
    {
        logger.LogWarning("GLB {FilePath} has no importable primary mesh", source.FullPath);
        return;
    }
    var displayName = $"{Path.GetFileNameWithoutExtension(source.FullPath)} [Mesh]";
    InstantiateImportedMesh(new ImportedSubAssetNode(source.FullPath,
        new AssetReference(record.Id, primaryMesh.Key), primaryMesh.ContentType, displayName),
        target);
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
        if (!outcome.Succeeded || outcome.ArtifactDirectory is null ||
            !outcome.Artifacts.Any(artifact => artifact.Key == meshReference.SubAsset &&
                artifact.ContentType == "nico/static-mesh"))
        {
            logger.LogError("Mesh sub-asset {Reference} has no valid artifact", meshReference);
            return;
        }
        importedMesh = LoadRuntimeResource(meshReference,
            new StaticMeshResource([], [], []));
    }
    instance.LocalBounds = new MeshBounds(importedMesh.BoundsMinimum, importedMesh.BoundsMaximum);
    var defaultMaterial = MaterialProperties.Default;
    var material = new StandardMaterialResource
    {
        BaseColor = defaultMaterial.BaseColor,
        Metallic = defaultMaterial.Metallic,
        Roughness = defaultMaterial.Roughness,
        DoubleSided = defaultMaterial.DoubleSided
    };
    TextureResource? textureResource = null;
    var textureSlot = -1;
    var materialSlot = importedMesh.Submeshes.Count > 0
        ? importedMesh.Submeshes[0].MaterialSlot : -1;
    var defaultMaterialArtifact = outcome?.Artifacts.FirstOrDefault(artifact =>
        artifact.Key == $"material/{materialSlot}");
    if (instance.Materials.Count == 0 && defaultMaterialArtifact is not null)
        instance.Materials.Add(new AssetReference(meshReference.Asset, defaultMaterialArtifact.Key));
    var materialReference = instance.Materials.FirstOrDefault();
    var materialRecord = materialReference.Asset.Value == Guid.Empty
        ? null : assetDatabase.Find(materialReference.Asset);
    var materialOutcome = materialRecord is null
        ? null
        : materialReference.Asset == meshReference.Asset
            ? outcome : assetImportPipeline.Import(materialRecord, "editor");
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
    var textureArtifact = materialOutcome?.Artifacts.FirstOrDefault(artifact =>
        artifact.Key == $"texture/{textureSlot}");
    if (textureArtifact is not null)
    {
        textureResource = LoadRuntimeResource(
            new AssetReference(materialReference.Asset, textureArtifact.Key),
            new TextureResource(0, 0, [], TextureColorSpace.Linear));
    }
    if (instance.MaterialOverride is { } materialOverride)
    {
        material.BaseColor = materialOverride.BaseColor;
        material.Metallic = materialOverride.Metallic;
        material.Roughness = materialOverride.Roughness;
        material.DoubleSided = materialOverride.DoubleSided;
        textureResource = ResolveTextureResource(materialOverride.BaseColorTexture)
            ?? textureResource;
    }
    (targetRenderer ?? viewportRenderer).SetAssetMeshResource(instance, importedMesh, material,
        textureResource);
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

/// <summary>Creates mutable renderer-local material values from a shared decoded resource.</summary>
/// <param name="source">Shared decoded material.</param>
/// <returns>An independent material copy.</returns>
StandardMaterialResource CloneMaterial(StandardMaterialResource source)
{
    return new StandardMaterialResource
    {
        BaseColor = source.BaseColor,
        Metallic = source.Metallic,
        Roughness = source.Roughness,
        DoubleSided = source.DoubleSided
    };
}

/// <summary>Resolves material values used for Inspector copy-on-write editing.</summary>
/// <param name="instance">Mesh instance whose effective slot zero is requested.</param>
/// <returns>Resolved imported material or shared default values.</returns>
MaterialProperties ResolveMaterialProperties(MeshInstance3D instance)
{
    if (instance.MaterialOverride is not null)
        return instance.MaterialOverride;
    var reference = instance.Materials.FirstOrDefault();
    if (reference.Asset.Value == Guid.Empty)
        return MaterialProperties.Default;
    var record = assetDatabase.Find(reference.Asset);
    if (record is null)
        return MaterialProperties.Default;
    var outcome = assetImportPipeline.TryGetLatestPublished(record, "editor");
    if (outcome is null)
        return MaterialProperties.Default;
    var artifact = outcome.Artifacts.FirstOrDefault(candidate =>
        candidate.Key == reference.SubAsset &&
        candidate.ContentType == "nico/standard-material");
    if (artifact is null || outcome.ArtifactDirectory is null)
        return MaterialProperties.Default;
    var decoded = LoadRuntimeResource(reference,
        new DecodedStandardMaterial(new StandardMaterialResource(), -1));
    var material = decoded.Material;
    var textureSlot = decoded.BaseColorTextureSlot;
    return new MaterialProperties
    {
        BaseColor = material.BaseColor,
        Metallic = material.Metallic,
        Roughness = material.Roughness,
        DoubleSided = material.DoubleSided,
        BaseColorTexture = textureSlot >= 0
            ? new AssetReference(reference.Asset, $"texture/{textureSlot}") : null
    };
}

/// <summary>Formats slot-zero material ownership for the Inspector.</summary>
/// <param name="instance">Inspected mesh instance.</param>
/// <returns>Readable material source name.</returns>
string ResolveMaterialDisplayName(MeshInstance3D instance)
{
    if (instance.MaterialOverride is not null)
        return "Scene Override";
    var reference = instance.Materials.FirstOrDefault();
    if (reference.Asset.Value == Guid.Empty)
        return "BuiltIn/Default";
    var path = assetDatabase.Find(reference.Asset)?.ProjectPath ?? reference.Asset.ToString();
    return reference.SubAsset is null ? path : $"{path} / {reference.SubAsset}";
}

/// <summary>Connects scene opening to the project filesystem tree.</summary>
/// <param name="tree">Filesystem tree to attach.</param>
void AttachFileSystem(TreeView tree)
{
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
                File.Delete(targetPath);
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

/// <summary>Adds matching collision geometry and default motion to a built-in primitive.</summary>
/// <param name="node">Primitive node receiving the components.</param>
/// <param name="mesh">Built-in primitive mesh.</param>
void AddPrimitivePhysics(Node3D node, AssetReference mesh)
{
    var collider = new ColliderComponent();
    if (mesh == BuiltInAssets.PlaneMesh)
    {
        collider.Shape = ColliderShape.Plane;
        node.AddComponent(collider);
        return;
    }
    if (mesh == BuiltInAssets.SphereMesh)
    {
        collider.Shape = ColliderShape.Sphere;
    }
    else if (mesh == BuiltInAssets.CapsuleMesh)
    {
        collider.Shape = ColliderShape.Capsule;
        collider.Height = 2f;
    }
    else if (mesh == BuiltInAssets.CylinderMesh)
    {
        collider.Shape = ColliderShape.Cylinder;
    }
    else
    {
        collider.Shape = ColliderShape.Box;
    }
    node.AddComponent(collider);
    node.AddComponent(new RigidBodyComponent());
}

void ShowHierarchyContextMenu()
{
    if (lastMousePos.X < hierarchyTree.Left || lastMousePos.X > hierarchyTree.Right
        || lastMousePos.Y < hierarchyTree.Top || lastMousePos.Y > hierarchyTree.Bottom)
        return;

    CloseHierarchyContextMenu();
    var activeRoot = GetActiveSceneRoot();
    var target = uiEventRouter.HoveredElement is TreeViewItem row ? row.Item : activeRoot;
    hierarchyTree.Select(ReferenceEquals(target, activeRoot) ? null : target);

    const float menuWidth = 160f;
    const float menuHeight = 184f;
    var menuX = Math.Clamp(lastMousePos.X, 0f, MathF.Max(0f, width - menuWidth));
    var menuY = Math.Clamp(lastMousePos.Y, 0f, MathF.Max(0f, height - menuHeight));
    var menu = new ContextMenu(menuWidth) { Name = "HierarchyContextMenu" };
    menu.AddItem("Add Empty Object", () => AddSceneNode(target, null, "Object"));
    menu.AddItem("Add Cube", () => AddSceneNode(target, BuiltInAssets.CubeMesh, "Cube"));
    menu.AddItem("Add Plane", () => AddSceneNode(target, BuiltInAssets.PlaneMesh, "Plane"));
    menu.AddItem("Add Sphere", () => AddSceneNode(target, BuiltInAssets.SphereMesh, "Sphere"));
    menu.AddItem("Add Capsule", () => AddSceneNode(target, BuiltInAssets.CapsuleMesh, "Capsule"));
    menu.AddItem("Add Cylinder", () => AddSceneNode(target, BuiltInAssets.CylinderMesh, "Cylinder"));
    hierarchyContextMenu = menu;
    overlay.Add(menu, new Vector2(menuX, menuY));
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
}

void AttachHierarchy(TreeView tree)
{
    tree.SelectionChanged += item =>
    {
        inspector.Bind(item);
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

/// <summary>Returns whether a screen position is inside a UI element.</summary>
/// <param name="element">Element whose bounds are tested.</param>
/// <param name="position">Screen-space pointer position.</param>
/// <returns>True when the position lies within the element.</returns>
bool IsInside(UIElement element, Vector2 position)
{
    return position.X >= element.Left && position.X <= element.Right
        && position.Y >= element.Top && position.Y <= element.Bottom;
}

/// <summary>Reparents one hierarchy node to a row target or the scene root.</summary>
/// <param name="source">Scene node being moved.</param>
/// <param name="target">Drop target, or null for the hierarchy root.</param>
void MoveHierarchyNode(Node source, Node? target)
{
    var activeRoot = GetActiveSceneRoot();
    var destination = target ?? activeRoot;
    if (ReferenceEquals(source, destination) || ReferenceEquals(source.Parent, destination))
        return;
    try
    {
        destination.AddChild(source);
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
/// <param name="target">Drop target, or null for the project root.</param>
void MoveFileSystemEntry(FileSystemNode source, FileSystemNode? target)
{
    var destinationDirectory = target is { IsDirectory: true }
        ? target.FullPath
        : target is not null ? Path.GetDirectoryName(target.FullPath) ?? project.RootPath
        : project.RootPath;
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

/// <summary>Assigns a typed imported resource to the active Inspector material panel.</summary>
/// <param name="source">Dragged imported sub-asset.</param>
/// <returns>True when the Inspector accepted the resource.</returns>
bool TryAssignInspectorSubAsset(ImportedSubAssetNode source)
{
    return source.ContentType switch
    {
        "nico/standard-material" => inspector.AssignMaterial(source.Reference),
        "nico/texture2d" => inspector.AssignBaseColorTexture(source.Reference),
        _ => false
    };
}

Node? pendingDragItem = null;
var pendingDragStart = Vector2.Zero;
var primaryPointerDown = false;
var dragActive = false;

mainUIHost.PreviewPointerMove = pointerEvent =>
{
    lastMousePos = pointerEvent.Position;
    window.UpdateWindowDrag(pointerEvent.Position);
    Debug.Input(LogLevel.Trace, "Mouse: ({X:F0}, {Y:F0})",
        pointerEvent.Position.X, pointerEvent.Position.Y);
    return flyCamera.MovePointer(pointerEvent.Position);
};

mainUIHost.PointerMoveProcessed = (pointerEvent, routed) =>
{
    if (!routed)
        return;
    var pos = pointerEvent.Position;
    if (dockWorkspace.IsTabSelected(EditorDockWorkspace.ProfilerId))
        editorView.Profiler.MovePointer(pos);
    if (primaryPointerDown && pendingDragItem is not null &&
        Vector2.DistanceSquared(pos, pendingDragStart) >= 25f)
    {
        if (!dragActive)
        {
            dragActive = true;
            if (pendingDragItem is FileSystemNode or ImportedSubAssetNode)
                fileSystemTree.Select(pendingDragItem);
            else
                hierarchyTree.Select(pendingDragItem);
            dragPreview = new DragPreview(string.IsNullOrWhiteSpace(pendingDragItem.Name)
                    ? pendingDragItem.GetType().Name : pendingDragItem.Name)
                { Name = "DragPreview" };
            overlay.Add(dragPreview, pos + new Vector2(12f, 12f));
        }
        else if (dragPreview is not null)
        {
            overlay.SetPosition(dragPreview, pos + new Vector2(12f, 12f));
        }
        RefreshVertices();
    }
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
        window.EndWindowDrag();
        if (flyCamera.IsActive || pointerEvent.Button != InputPointerButton.Primary)
            return UIHostPointerRouting.Consume;
        primaryPointerDown = false;
        if (dragActive && pendingDragItem is { } draggedItem)
        {
            var targetRow = uiEventRouter.HoveredElement as TreeViewItem;
            if (draggedItem is ImportedSubAssetNode importedSource &&
                IsInside(inspector, lastMousePos) && TryAssignInspectorSubAsset(importedSource))
                RefreshVertices();
            else if (draggedItem is ImportedSubAssetNode hierarchySource &&
                     IsInside(hierarchyTree, lastMousePos))
                InstantiateImportedMesh(hierarchySource, targetRow?.Item);
            else if (draggedItem is FileSystemNode fileSource)
            {
                if (IsInside(hierarchyTree, lastMousePos) &&
                    Path.GetExtension(fileSource.FullPath).Equals(".glb",
                        StringComparison.OrdinalIgnoreCase))
                    InstantiateGlbPrimaryMesh(fileSource, targetRow?.Item);
                else if (ScriptFileDrop.TryAttach(
                             fileSource, uiEventRouter.HoveredElement, inspector, assetDatabase))
                {
                    logger.LogInformation("Attached script from {ScriptPath} to {NodeName}",
                        fileSource.FullPath, inspector.InspectedNode?.Name);
                    RefreshVertices();
                }
                else if (IsInside(fileSystemTree, lastMousePos))
                    MoveFileSystemEntry(fileSource, targetRow?.Item as FileSystemNode);
            }
            else if (draggedItem is not FileSystemNode && IsInside(hierarchyTree, lastMousePos))
                MoveHierarchyNode(draggedItem, targetRow?.Item);
        }
        if (dragPreview is not null)
        {
            overlay.Remove(dragPreview);
            dragPreview = null;
        }
        var consumedByGizmo = selection.PrimaryUp();
        renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
        window.RequestFrame();
        pendingDragItem = null;
        var suppressClick = consumedByGizmo || dragActive;
        dragActive = false;
        return suppressClick
            ? UIHostPointerRouting.RouteWithoutClick
            : UIHostPointerRouting.Route;
    }
    if (flyCamera.IsActive)
        return UIHostPointerRouting.Consume;
    if (pointerEvent.Button == InputPointerButton.Secondary)
    {
        if (!ShowFileSystemContextMenu())
            ShowHierarchyContextMenu();
        return UIHostPointerRouting.Consume;
    }
    if (pointerEvent.Button != InputPointerButton.Primary)
        return UIHostPointerRouting.Consume;
    primaryPointerDown = true;
    pendingDragStart = lastMousePos;
    dragActive = false;
    pendingDragItem = uiEventRouter.HoveredElement is TreeViewItem dragRow &&
        (IsInside(hierarchyTree, lastMousePos) || IsInside(fileSystemTree, lastMousePos))
        ? dragRow.Item : null;
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
    Debug.Input(LogLevel.Debug, "Scroll: offset={Offset:F1}", pointerEvent.Delta.Y);
    return flyCamera.IsActive;
};

mainUIHost.PreviewKey = keyEvent =>
{
    Debug.Input(LogLevel.Debug, "Key: key={Key}, pressed={Pressed}, repeat={Repeat}",
        keyEvent.Key, keyEvent.IsPressed, keyEvent.IsRepeat);
    return sceneInputContext.RouteKey(uiEventRouter, keyEvent);
};
mainUIHost.PreviewTextInput = _ => sceneInputContext.RoutesText(uiEventRouter);
mainUIHost.PreviewTextComposition = _ => sceneInputContext.RoutesText(uiEventRouter);

// ── Game loop: Update → Render ──────────────────────────────
window.Update += delta =>
{
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
            foreach (var asset in assetChanges.Select(change =>
                         change.Current?.Id ?? change.Previous!.Id).Distinct())
                runtimeResources.Invalidate(asset);
            logger.LogInformation("Refreshed asset database with {ChangeCount} changes",
                assetChanges.Count);
            RefreshFileSystem();
            StartScriptSchemaBuild();
        }
    }
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
    var sceneContinuous = flyCamera.IsActive || scriptHost is not null;
    var gameContinuous = scriptHost is not null;
    var sceneVisible = sceneViewport.IsEffectivelyVisible;
    var gameVisible = gameViewport.IsEffectivelyVisible;
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
    if (detachedSceneWindow is null && sceneViewport.IsEffectivelyVisible &&
        renderScheduler.Consume(RenderInvalidation.SceneViewport))
        viewportRenderer.RenderScene(sceneViewport, lastMousePos);
    if (detachedGameWindow is null && gameViewport.IsEffectivelyVisible &&
        renderScheduler.Consume(RenderInvalidation.GameViewport))
        viewportRenderer.RenderGame(gameViewport);
    dockSession.SynchronizeFloatingWindows();
    window.SetContinuousRendering(
        flyCamera.IsActive || scriptHost is not null || playBuildTask is not null
            || scriptSchemaBuildTask is not null
            || pendingResizeTimestamp != 0
            || mainUIHost.RequiresContinuousUpdates
            || dockSession.RequiresContinuousUpdates
            || dockWorkspace.IsTabSelected(EditorDockWorkspace.ProfilerId) &&
               !editorView.Profiler.IsPaused);
};

logger.LogInformation("Running main loop...");
window.Run();
isShuttingDown = true;
try
{
    EditorDockWorkspace.Save(project.RootPath, dockSession.Workspace);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    logger.LogWarning(exception, "Could not save the Editor dock workspace");
}
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
