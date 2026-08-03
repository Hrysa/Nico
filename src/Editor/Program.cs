using System.Numerics;
using Editor;
using Engine.Core;
using Engine.Graphics;
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
using var scriptCompiler = new GameScriptCompiler(scriptingWorkspace);
var width = 1280;
var height = 720;
var options = new WindowOptions
{
    Title = $"{Path.GetFileName(project.RootPath)} - Game Engine Editor",
    Width = width,
    Height = height,
    CustomTitleBar = true,
    IsEventDriven = true,
    TargetFrameRate = 120d
};

logger.LogInformation("Initializing window...");
window.Initialize(options);
using var secondaryWindows = new SilkWindowGroup(window, loggerFactory);

logger.LogInformation("Setting up editor UI...");
var editorView = EditorUI.BuildView(width, height);
var uiRoot = editorView.Root;
var overlay = editorView.Overlay;
window.SubmitUI(uiRoot.BuildDrawList());
window.SetPushConstants(EditorUI.CreatePushConstants(width, height));

// ── Scene viewport: PerspectiveCamera for 3D scene ────────────
var sceneViewport = editorView.SceneViewport;
var sceneViewportId = window.CreateRenderView(sceneViewport.Width, sceneViewport.Height);
sceneViewport.RenderView = sceneViewportId;
window.SetViewportQuadVertices(sceneViewportId, EditorUI.CreateViewportQuadVertices(sceneViewport));
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
    var cube = new MeshInstance3D(new CubeMesh()) { Name = "SceneCube" };
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
hierarchyTree.SetRoots(sceneRoot.Children);

GameScriptHost? scriptHost = null;
LoadedScene? playScene = null;
LoadedScene? pendingPlayScene = null;
Task<GameScriptHost>? playBuildTask = null;
CancellationTokenSource? playBuildCancellation = null;
CompilationProgressDialog? compilationProgressDialog = null;
Node3D? editSelectionBeforePlay = null;
var isPlaying = false;

// ── Game viewport: scene rendered through its GameCamera ─────
var gameViewport = editorView.GameViewport;
var gameViewportId = window.CreateRenderView(gameViewport.Width, gameViewport.Height);
gameViewport.RenderView = gameViewportId;
gameViewport.Camera = gameCamera;
window.SetViewportQuadVertices(gameViewportId, EditorUI.CreateViewportQuadVertices(gameViewport));
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
var flyCamera = new FlyCameraController(sceneCamera, window.SetMouseCaptured, selection.CancelInteraction);
using var viewportRenderer = new EditorViewportRenderer(
    window, sceneViewportId, gameViewportId, sceneCamera, gameCamera, sceneObjects, selection);
var renderScheduler = new EditorRenderScheduler();
DetachedToolWindow? detachedSceneWindow = null;
DetachedToolWindow? detachedGameWindow = null;
EditorViewportRenderer? detachedSceneRenderer = null;
EditorViewportRenderer? detachedGameRenderer = null;
DetachedToolWindow? detachedHierarchyWindow = null;
DetachedToolWindow? detachedFileSystemWindow = null;
DetachedToolWindow? detachedInspectorWindow = null;

var uiEventRouter = new UIEventRouter(uiRoot, RefreshUI);
ContextMenu? hierarchyContextMenu = null;
ContextMenu? fileContextMenu = null;
ContextMenu? fileSubmenu = null;
ScenePickerDialog? scenePickerDialog = null;
FileSystemCreateDialog? fileSystemCreateDialog = null;
ConfirmationDialog? confirmationDialog = null;
DragPreview? dragPreview = null;
var fileSystemTree = editorView.FileSystemTree;
var createdObjectIndex = 1;
AttachFileSystem(fileSystemTree);
AttachInspector(inspector);
RefreshFileSystem();
AttachTitleBar(editorView.TitleBar);
AttachPlayButton(editorView.PlayButton);
editorView.SceneToolbar.DoubleClick += DetachSceneViewport;
editorView.GameHeader.DoubleClick += DetachGameViewport;
editorView.HierarchyPanel.Header.DoubleClick += DetachHierarchy;
editorView.FileSystemPanel.Header.DoubleClick += DetachFileSystem;
editorView.InspectorPanel.Header.DoubleClick += DetachInspector;
RefreshVertices();

/// <summary>Moves the Scene tool into an independent shared-device window.</summary>
void DetachSceneViewport()
{
    if (detachedSceneWindow is not null)
        return;
    editorView.ViewportDock.Remove(editorView.SceneSlot);
    editorView.ViewportDock.Rows[0] = GridLength.Pixels(0f);
    editorView.ViewportDock.Rows[1] = GridLength.Pixels(0f);
    uiRoot.InvalidateMeasure();
    window.DestroyRenderView(sceneViewportId);
    detachedSceneWindow = new DetachedToolWindow(
        secondaryWindows, "Scene", 900, 600, editorView.SceneSlot);
    var detachedWindow = detachedSceneWindow.Window;
    sceneViewportId = detachedWindow.CreateRenderView(sceneViewport.Width, sceneViewport.Height);
    sceneViewport.RenderView = sceneViewportId;
    detachedWindow.SetViewportClearColor(sceneViewportId, 0f, 0f, 0f);
    detachedWindow.SetViewportQuadVertices(
        sceneViewportId, EditorUI.CreateViewportQuadVertices(sceneViewport));
    detachedSceneRenderer = new EditorViewportRenderer(
        detachedWindow, sceneViewportId, sceneViewportId,
        sceneCamera, gameViewport.Camera ?? gameCamera, GetActiveSceneObjects(), selection);
    detachedWindow.Resized += (_, _) =>
    {
        detachedWindow.ResizeRenderView(sceneViewportId, sceneViewport.Width, sceneViewport.Height);
        detachedWindow.SetViewportQuadVertices(
            sceneViewportId, EditorUI.CreateViewportQuadVertices(sceneViewport));
    };
    detachedWindow.Update += _ =>
    {
        detachedSceneRenderer?.SetSceneObjects(GetActiveSceneObjects());
        detachedSceneRenderer?.RenderScene(
            sceneViewport, detachedSceneWindow?.UIHost.PointerPosition ?? Vector2.Zero);
    };
    ResizeEditor(width, height);
}

/// <summary>Moves the detached Scene tool back into the main dock.</summary>
void DockSceneViewport()
{
    if (detachedSceneWindow is null)
        return;
    detachedSceneRenderer?.Dispose();
    detachedSceneRenderer = null;
    detachedSceneWindow.Window.DestroyRenderView(sceneViewportId);
    detachedSceneWindow.ReleaseContent();
    detachedSceneWindow.Dispose();
    detachedSceneWindow = null;
    editorView.ViewportDock.Rows[0] = GridLength.Star(0.73f);
    editorView.ViewportDock.Rows[1] = GridLength.Pixels(1f);
    editorView.ViewportDock.Add(editorView.SceneSlot, 0, 0);
    sceneViewportId = window.CreateRenderView(sceneViewport.Width, sceneViewport.Height);
    sceneViewport.RenderView = sceneViewportId;
    viewportRenderer.SetSceneRenderView(sceneViewportId);
    window.SetViewportClearColor(sceneViewportId, 0f, 0f, 0f);
    uiRoot.InvalidateMeasure();
    ResizeEditor(width, height);
    window.SetViewportQuadVertices(sceneViewportId, EditorUI.CreateViewportQuadVertices(sceneViewport));
}

/// <summary>Moves the Game tool into an independent shared-device window.</summary>
void DetachGameViewport()
{
    if (detachedGameWindow is not null)
        return;
    editorView.ViewportDock.Remove(editorView.GameSlot);
    editorView.ViewportDock.Rows[2] = GridLength.Pixels(0f);
    editorView.ViewportDock.Rows[1] = GridLength.Pixels(0f);
    uiRoot.InvalidateMeasure();
    window.DestroyRenderView(gameViewportId);
    detachedGameWindow = new DetachedToolWindow(
        secondaryWindows, "Game", 900, 600, editorView.GameSlot);
    var detachedWindow = detachedGameWindow.Window;
    gameViewportId = detachedWindow.CreateRenderView(gameViewport.Width, gameViewport.Height);
    gameViewport.RenderView = gameViewportId;
    detachedWindow.SetViewportClearColor(gameViewportId, 0.05f, 0.05f, 0.12f);
    detachedWindow.SetViewportQuadVertices(
        gameViewportId, EditorUI.CreateViewportQuadVertices(gameViewport));
    detachedGameRenderer = new EditorViewportRenderer(
        detachedWindow, gameViewportId, gameViewportId,
        sceneCamera, gameViewport.Camera ?? gameCamera, GetActiveSceneObjects(), selection);
    detachedWindow.Resized += (_, _) =>
    {
        detachedWindow.ResizeRenderView(gameViewportId, gameViewport.Width, gameViewport.Height);
        detachedWindow.SetViewportQuadVertices(
            gameViewportId, EditorUI.CreateViewportQuadVertices(gameViewport));
    };
    detachedWindow.Update += _ =>
    {
        detachedGameRenderer?.SetGameScene(
            gameViewport.Camera ?? gameCamera, GetActiveSceneObjects());
        detachedGameRenderer?.RenderGame(gameViewport);
    };
    ResizeEditor(width, height);
}

/// <summary>Moves the detached Game tool back into the main dock.</summary>
void DockGameViewport()
{
    if (detachedGameWindow is null)
        return;
    detachedGameRenderer?.Dispose();
    detachedGameRenderer = null;
    detachedGameWindow.Window.DestroyRenderView(gameViewportId);
    detachedGameWindow.ReleaseContent();
    detachedGameWindow.Dispose();
    detachedGameWindow = null;
    editorView.ViewportDock.Rows[2] = GridLength.Star(0.27f);
    editorView.ViewportDock.Rows[1] = GridLength.Pixels(1f);
    editorView.ViewportDock.Add(editorView.GameSlot, 2, 0);
    gameViewportId = window.CreateRenderView(gameViewport.Width, gameViewport.Height);
    gameViewport.RenderView = gameViewportId;
    viewportRenderer.SetGameRenderView(gameViewportId);
    window.SetViewportClearColor(gameViewportId, 0.05f, 0.05f, 0.12f);
    uiRoot.InvalidateMeasure();
    ResizeEditor(width, height);
    window.SetViewportQuadVertices(gameViewportId, EditorUI.CreateViewportQuadVertices(gameViewport));
}

/// <summary>Moves the Hierarchy tool into an independent native window.</summary>
void DetachHierarchy()
{
    if (detachedHierarchyWindow is not null)
        return;
    editorView.LeftDock.Remove(editorView.HierarchyPanel);
    editorView.LeftDock.Rows[0] = GridLength.Pixels(0f);
    editorView.LeftDock.Rows[1] = GridLength.Pixels(0f);
    detachedHierarchyWindow = new DetachedToolWindow(
        secondaryWindows, "Hierarchy", 360, 620, editorView.HierarchyPanel);
    UpdateLeftDockWorkspaceTracks();
    uiRoot.InvalidateMeasure();
    ResizeEditor(width, height);
}

/// <summary>Returns the Hierarchy tool to the main left dock.</summary>
void DockHierarchy()
{
    if (detachedHierarchyWindow is null)
        return;
    detachedHierarchyWindow.ReleaseContent();
    detachedHierarchyWindow.Dispose();
    detachedHierarchyWindow = null;
    editorView.LeftDock.Rows[0] = GridLength.Star(0.58f);
    editorView.LeftDock.Rows[1] = detachedFileSystemWindow is null
        ? GridLength.Pixels(1f) : GridLength.Pixels(0f);
    editorView.LeftDock.Add(editorView.HierarchyPanel, 0, 0);
    UpdateLeftDockWorkspaceTracks();
    uiRoot.InvalidateMeasure();
    ResizeEditor(width, height);
}

/// <summary>Moves the File System tool into an independent native window.</summary>
void DetachFileSystem()
{
    if (detachedFileSystemWindow is not null)
        return;
    editorView.LeftDock.Remove(editorView.FileSystemPanel);
    editorView.LeftDock.Rows[2] = GridLength.Pixels(0f);
    editorView.LeftDock.Rows[1] = GridLength.Pixels(0f);
    detachedFileSystemWindow = new DetachedToolWindow(
        secondaryWindows, "File System", 440, 520, editorView.FileSystemPanel);
    UpdateLeftDockWorkspaceTracks();
    uiRoot.InvalidateMeasure();
    ResizeEditor(width, height);
}

/// <summary>Returns the File System tool to the main left dock.</summary>
void DockFileSystem()
{
    if (detachedFileSystemWindow is null)
        return;
    detachedFileSystemWindow.ReleaseContent();
    detachedFileSystemWindow.Dispose();
    detachedFileSystemWindow = null;
    editorView.LeftDock.Rows[2] = GridLength.Star(0.42f);
    editorView.LeftDock.Rows[1] = detachedHierarchyWindow is null
        ? GridLength.Pixels(1f) : GridLength.Pixels(0f);
    editorView.LeftDock.Add(editorView.FileSystemPanel, 2, 0);
    UpdateLeftDockWorkspaceTracks();
    uiRoot.InvalidateMeasure();
    ResizeEditor(width, height);
}

/// <summary>Reclaims the main left-dock width only while both of its tools are detached.</summary>
void UpdateLeftDockWorkspaceTracks()
{
    var hasDockedTool = detachedHierarchyWindow is null || detachedFileSystemWindow is null;
    editorView.Workspace.Columns[0] = GridLength.Pixels(hasDockedTool ? 252f : 0f);
    editorView.Workspace.Columns[1] = GridLength.Pixels(hasDockedTool ? 1f : 0f);
}

/// <summary>Moves the Inspector tool into an independent native window.</summary>
void DetachInspector()
{
    if (detachedInspectorWindow is not null)
        return;
    editorView.Workspace.Remove(editorView.InspectorPanel);
    editorView.Workspace.Columns[3] = GridLength.Pixels(0f);
    editorView.Workspace.Columns[4] = GridLength.Pixels(0f);
    detachedInspectorWindow = new DetachedToolWindow(
        secondaryWindows, "Inspector", 420, 680, editorView.InspectorPanel);
    uiRoot.InvalidateMeasure();
    ResizeEditor(width, height);
}

/// <summary>Returns the Inspector tool to the main workspace.</summary>
void DockInspector()
{
    if (detachedInspectorWindow is null)
        return;
    detachedInspectorWindow.ReleaseContent();
    detachedInspectorWindow.Dispose();
    detachedInspectorWindow = null;
    editorView.Workspace.Columns[3] = GridLength.Pixels(1f);
    editorView.Workspace.Columns[4] = GridLength.Pixels(300f);
    editorView.Workspace.Add(editorView.InspectorPanel, 0, 4);
    uiRoot.InvalidateMeasure();
    ResizeEditor(width, height);
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
        candidateHost.LoadScene(candidateScene.Root);
        editSelectionBeforePlay = selection.SelectedNode;
        selection.SetObjects(candidateScene.MeshInstances);
        playScene = candidateScene;
        scriptHost = candidateHost;
        isPlaying = true;
        viewportRenderer.SetSceneObjects(candidateScene.MeshInstances);
        hierarchyTree.SetRoots(candidateScene.Root.Children);
        gameViewport.Camera = candidateScene.GameCamera;
        viewportRenderer.SetGameScene(candidateScene.GameCamera, candidateScene.MeshInstances);
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
    playScene = null;
    isPlaying = false;
    selection.SetObjects(sceneObjects);
    viewportRenderer.SetSceneObjects(sceneObjects);
    hierarchyTree.SetRoots(sceneRoot.Children);
    selection.Select(editSelectionBeforePlay);
    editSelectionBeforePlay = null;
    gameViewport.Camera = gameCamera;
    viewportRenderer.SetGameScene(gameCamera, sceneObjects);
    editorView.PlayButtonLabel.Text = "Play";
    logger.LogInformation("Exited play mode");
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
        viewportRenderer.SetGameScene(gameCamera, sceneObjects);
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
    if (!isDirectory || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        return node;

    foreach (var directory in Directory.EnumerateDirectories(path)
                 .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        node.AddChild(BuildFileSystemNode(directory));
    foreach (var file in Directory.EnumerateFiles(path)
                 .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        node.AddChild(new FileSystemNode(file, isDirectory: false));
    return node;
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
        var root = BuildFileSystemNode(project.RootPath);
        var expandedNodes = EnumerateFileSystemNodes(root)
            .Where(node => !ReferenceEquals(node, root) && node.IsDirectory
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
    if (item is not FileSystemNode node || node.IsDirectory)
        return;
    if (IsSceneFile(node.FullPath))
    {
        LoadScene(node.FullPath, makeActive: true);
        return;
    }
    logger.LogInformation("No editor is registered for project file {FilePath}", node.FullPath);
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
    var cube = new MeshInstance3D(new CubeMesh()) { Name = "SceneCube" };
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

void AddSceneNode(Node parent, bool withCubeMesh)
{
    var activeRoot = GetActiveSceneRoot();
    var activeObjects = GetActiveSceneObjects();
    Node child;
    if (withCubeMesh)
    {
        var meshInstance = new MeshInstance3D(new CubeMesh()) { Name = $"Cube {createdObjectIndex++}" };
        activeObjects.Add(meshInstance);
        child = meshInstance;
    }
    else
    {
        child = new Node3D { Name = $"Object {createdObjectIndex++}" };
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
    const float menuHeight = 56f;
    var menuX = Math.Clamp(lastMousePos.X, 0f, MathF.Max(0f, width - menuWidth));
    var menuY = Math.Clamp(lastMousePos.Y, 0f, MathF.Max(0f, height - menuHeight));
    var menu = new ContextMenu(menuWidth) { Name = "HierarchyContextMenu" };
    menu.AddItem("Add Empty Object", () => AddSceneNode(target, withCubeMesh: false));
    menu.AddItem("Add Cube", () => AddSceneNode(target, withCubeMesh: true));
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
    sceneInspector.NodeChanged += _ => InvalidateViewports();
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
    uiRoot.Measure(new Vector2(width, height));
    uiRoot.Arrange(Vector2.Zero, new Vector2(width, height));

    if (detachedSceneWindow is null)
        window.SetViewportQuadVertices(
            sceneViewportId, EditorUI.CreateViewportQuadVertices(sceneViewport));
    if (detachedGameWindow is null)
        window.SetViewportQuadVertices(
            gameViewportId, EditorUI.CreateViewportQuadVertices(gameViewport));
    window.SetPushConstants(EditorUI.CreatePushConstants(width, height));
    window.SubmitUI(uiRoot.BuildDrawList());
}

/// <summary>Reallocates viewport FBOs once a live native resize has settled.</summary>
void ResizeViewportTargets()
{
    if (detachedSceneWindow is null)
        window.ResizeRenderView(sceneViewportId, sceneViewport.Width, sceneViewport.Height);
    if (detachedGameWindow is null)
        window.ResizeRenderView(gameViewportId, gameViewport.Width, gameViewport.Height);
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
    window.SubmitUI(uiRoot.BuildDrawList());
    renderScheduler.Consume(RenderInvalidation.UI);
    window.RequestFrame();
    detachedSceneWindow?.UIHost.Refresh();
    detachedGameWindow?.UIHost.Refresh();
    detachedHierarchyWindow?.UIHost.Refresh();
    detachedFileSystemWindow?.UIHost.Refresh();
    detachedInspectorWindow?.UIHost.Refresh();
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
    var sourcePath = source.FullPath;
    var destinationPath = Path.Combine(destinationDirectory,
        Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar)));
    if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
        return;
    if (source.IsDirectory && destinationDirectory.StartsWith(
            sourcePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
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

Node? pendingDragItem = null;
var pendingDragStart = Vector2.Zero;
var primaryPointerDown = false;
var dragActive = false;

window.MouseMove += pos =>
{
    lastMousePos = pos;
    window.UpdateWindowDrag(pos);
    Debug.Input(LogLevel.Trace, "Mouse: ({X:F0}, {Y:F0})", pos.X, pos.Y);

    if (flyCamera.MovePointer(pos))
        return;

    uiEventRouter.MovePointer(pos);

    if (primaryPointerDown && pendingDragItem is not null
        && Vector2.DistanceSquared(pos, pendingDragStart) >= 25f)
    {
        if (!dragActive)
        {
            dragActive = true;
            if (pendingDragItem is FileSystemNode fileNode)
                fileSystemTree.Select(fileNode);
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

    selection.MovePointer(pos);
    renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
    window.RequestFrame();
};

window.MouseDown += button =>
{
    if (flyCamera.IsActive)
        return;

    Debug.Input(LogLevel.Debug, "MouseDown: button={Button}", button);
    if (button == 1)
    {
        if (!ShowFileSystemContextMenu())
            ShowHierarchyContextMenu();
        return;
    }

    if (button != 0)
        return;

    primaryPointerDown = true;
    pendingDragStart = lastMousePos;
    dragActive = false;
    pendingDragItem = uiEventRouter.HoveredElement is TreeViewItem dragRow
        && (IsInside(hierarchyTree, lastMousePos) || IsInside(fileSystemTree, lastMousePos))
        ? dragRow.Item : null;

    if (hierarchyContextMenu is not null
        && uiEventRouter.HoveredElement is not ContextMenuItem)
        CloseHierarchyContextMenu();
    if (fileContextMenu is not null
        && uiEventRouter.HoveredElement is not ContextMenuItem)
        CloseFileContextMenu();

    uiEventRouter.Press();

    // Must be in scene viewport area
    bool inSceneViewport = (uiEventRouter.HoveredElement is ViewportPanel vp && vp.RenderView == sceneViewportId)
                        || (uiEventRouter.HoveredElement == null && IsInSceneViewport(lastMousePos));
    if (!inSceneViewport) return;

    selection.PrimaryDown(lastMousePos, inSceneViewport);
    renderScheduler.Invalidate(RenderInvalidation.SceneViewport);
    window.RequestFrame();
};

window.MouseUp += button =>
{
    window.EndWindowDrag();
    if (flyCamera.IsActive)
        return;

    Debug.Input(LogLevel.Debug, "MouseUp: button={Button}", button);

    if (button != 0)
        return;

    primaryPointerDown = false;

    if (dragActive && pendingDragItem is { } draggedItem)
    {
        var targetRow = uiEventRouter.HoveredElement as TreeViewItem;
        if (draggedItem is FileSystemNode fileSource && IsInside(fileSystemTree, lastMousePos))
            MoveFileSystemEntry(fileSource, targetRow?.Item as FileSystemNode);
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

    uiEventRouter.Release(!consumedByGizmo && !dragActive);
    pendingDragItem = null;
    dragActive = false;
};

window.MouseDoubleClick += button =>
{
    if (flyCamera.IsActive)
        return;

    Debug.Input(LogLevel.Debug, "DoubleClick: button={Button}", button);
    if (button == 0)
        uiEventRouter.DoubleClick();
};

window.MouseScroll += offset =>
{
    if (flyCamera.IsActive)
        return;

    Debug.Input(LogLevel.Debug, "Scroll: offset={Offset:F1}", offset);
    uiEventRouter.Scroll(offset);
};

var controlDown = false;
var commandDown = false;
window.KeyDown += keyCode =>
{
    Debug.Input(LogLevel.Debug, "KeyDown: key={Key}", keyCode);
    if (keyCode is InputKey.LeftControl or InputKey.RightControl)
        controlDown = true;
    if (keyCode is InputKey.LeftSuper or InputKey.RightSuper)
        commandDown = true;
    if (keyCode == InputKey.S && (controlDown || commandDown))
    {
        SaveScene();
        return;
    }
    if (!flyCamera.KeyDown(keyCode))
        uiEventRouter.KeyDown((int)keyCode);
};

window.KeyUp += keyCode =>
{
    Debug.Input(LogLevel.Debug, "KeyUp: key={Key}", keyCode);
    if (keyCode is InputKey.LeftControl or InputKey.RightControl)
        controlDown = false;
    if (keyCode is InputKey.LeftSuper or InputKey.RightSuper)
        commandDown = false;
    if (!flyCamera.KeyUp(keyCode))
        uiEventRouter.KeyUp((int)keyCode);
};

window.TextInput += character =>
{
    if (!flyCamera.IsActive)
        uiEventRouter.TextInput(character);
};

// ── Game loop: Update → Render ──────────────────────────────
window.Update += delta =>
{
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
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "A game script failed; play mode has been stopped");
            StopPlayMode();
        }
    }
    if (inspector.RefreshValues())
        RefreshVertices();
    var sceneContinuous = flyCamera.IsActive || scriptHost is not null;
    var gameContinuous = scriptHost is not null;
    var sceneInvalid = renderScheduler.Consume(RenderInvalidation.SceneViewport);
    var gameInvalid = renderScheduler.Consume(RenderInvalidation.GameViewport);
    if (detachedSceneWindow is null
        && (sceneContinuous || sceneInvalid))
        viewportRenderer.RenderScene(sceneViewport, lastMousePos);
    if (detachedGameWindow is null
        && (gameContinuous || gameInvalid))
        viewportRenderer.RenderGame(gameViewport);
    secondaryWindows.PumpFrames();
    if (detachedSceneWindow is { IsOpen: false })
        DockSceneViewport();
    if (detachedGameWindow is { IsOpen: false })
        DockGameViewport();
    if (detachedHierarchyWindow is { IsOpen: false })
        DockHierarchy();
    if (detachedFileSystemWindow is { IsOpen: false })
        DockFileSystem();
    if (detachedInspectorWindow is { IsOpen: false })
        DockInspector();
    window.SetContinuousRendering(
        flyCamera.IsActive || scriptHost is not null || playBuildTask is not null
            || pendingResizeTimestamp != 0);
};

logger.LogInformation("Running main loop...");
window.Run();
DockSceneViewport();
DockGameViewport();
DockHierarchy();
DockFileSystem();
DockInspector();
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
logger.LogInformation("Done.");
return 0;
