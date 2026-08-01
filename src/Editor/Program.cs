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

using var window = new SilkWindow(loggerFactory);
var width = 1280;
var height = 720;
var options = new WindowOptions
{
    Title = $"{Path.GetFileName(project.RootPath)} - Game Engine Editor",
    Width = width,
    Height = height,
    CustomTitleBar = true
};

logger.LogInformation("Initializing window...");
window.Initialize(options);

logger.LogInformation("Setting up editor UI...");
var editorView = EditorUI.BuildView(width, height);
var uiRoot = editorView.Root;
window.SetUI(uiRoot.BuildDrawList());
window.SetPushConstants(EditorUI.CreatePushConstants(width, height));
window.CreateVertexBuffer();

// ── Scene viewport: PerspectiveCamera for 3D scene ────────────
var sceneViewport = editorView.SceneViewport;
var sceneViewportId = window.RegisterViewport(sceneViewport.Width, sceneViewport.Height);
sceneViewport.ViewportId = sceneViewportId;
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
var activeScenePath = project.ScenePath;
if (File.Exists(activeScenePath))
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
        or System.Text.Json.JsonException or NotSupportedException)
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
hierarchyTree.SetRoots(sceneRoot.Children);

// ── Game viewport: scene rendered through its GameCamera ─────
var gameViewport = editorView.GameViewport;
var gameViewportId = window.RegisterViewport(gameViewport.Width, gameViewport.Height);
gameViewport.ViewportId = gameViewportId;
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
var viewportRenderer = new EditorViewportRenderer(
    window, sceneViewportId, gameViewportId, sceneCamera, gameCamera, sceneObjects, selection);

var uiEventRouter = new UIEventRouter(uiRoot, RefreshVertices);
ContextMenu? hierarchyContextMenu = null;
ContextMenu? fileContextMenu = null;
ScenePickerDialog? scenePickerDialog = null;
var createdObjectIndex = 1;
AttachFileMenu(editorView.FileButton);
AttachTitleBar(editorView.TitleBar);
RefreshVertices();

/// <summary>Closes the hierarchy's object-creation menu.</summary>
void CloseHierarchyContextMenu()
{
    if (hierarchyContextMenu is null)
        return;
    uiRoot.RemoveChild(hierarchyContextMenu);
    hierarchyContextMenu = null;
    RefreshVertices();
}

/// <summary>Closes the File menu.</summary>
void CloseFileContextMenu()
{
    if (fileContextMenu is not null)
        uiRoot.RemoveChild(fileContextMenu);
    if (scenePickerDialog is not null)
        uiRoot.RemoveChild(scenePickerDialog);
    fileContextMenu = null;
    scenePickerDialog = null;
    RefreshVertices();
}

/// <summary>Saves the current scene to its active scene file.</summary>
void SaveScene()
{
    try
    {
        SceneFileStore.Save(activeScenePath, sceneRoot, gameCamera);
        logger.LogInformation("Saved scene {ScenePath}", activeScenePath);
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
        var loadedScene = SceneFileStore.Load(scenePath);
        selection.Select(null);
        sceneRoot.ClearChildren();
        foreach (var child in loadedScene.Root.Children.ToArray())
            sceneRoot.AddChild(child);
        sceneObjects.Clear();
        sceneObjects.AddRange(loadedScene.MeshInstances);
        gameCamera = loadedScene.GameCamera;
        gameViewport.Camera = gameCamera;
        viewportRenderer.SetGameCamera(gameCamera);
        hierarchyTree.SetRoots(sceneRoot.Children);
        if (makeActive)
            activeScenePath = Path.GetFullPath(scenePath);
        logger.LogInformation("Loaded scene {ScenePath}", scenePath);
        CloseFileContextMenu();
        return true;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
        or System.Text.Json.JsonException or NotSupportedException)
    {
        logger.LogError(exception, "Could not load scene {ScenePath}", scenePath);
        CloseFileContextMenu();
        return false;
    }
}

/// <summary>Reloads the active scene file.</summary>
void ReloadScene()
{
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
    uiRoot.AddChild(picker);
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
}

/// <summary>Opens the File menu containing scene persistence actions.</summary>
void ShowFileContextMenu()
{
    CloseFileContextMenu();
    CloseHierarchyContextMenu();
    var menu = new ContextMenu(8f, 78f, 170f) { Name = "FileContextMenu" };
    menu.AddItem("Open Scene", ShowOpenSceneDialog);
    menu.AddItem("Save Scene", SaveScene);
    menu.AddItem("Reload Scene", ReloadScene);
    fileContextMenu = menu;
    uiRoot.AddChild(menu);
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
}

/// <summary>Connects a rebuilt File button to its menu action.</summary>
/// <param name="button">File button to attach.</param>
void AttachFileMenu(Button button)
{
    button.Click += ShowFileContextMenu;
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
    Node child;
    if (withCubeMesh)
    {
        var meshInstance = new MeshInstance3D(new CubeMesh()) { Name = $"Cube {createdObjectIndex++}" };
        sceneObjects.Add(meshInstance);
        child = meshInstance;
    }
    else
    {
        child = new Node3D { Name = $"Object {createdObjectIndex++}" };
    }

    parent.AddChild(child);
    if (ReferenceEquals(parent, sceneRoot))
        hierarchyTree.SetRoots(sceneRoot.Children);
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
    var target = uiEventRouter.HoveredElement is TreeViewItem row ? row.Item : sceneRoot;
    hierarchyTree.Select(ReferenceEquals(target, sceneRoot) ? null : target);

    const float menuWidth = 160f;
    const float menuHeight = 56f;
    var menuX = Math.Clamp(lastMousePos.X, 0f, MathF.Max(0f, width - menuWidth));
    var menuY = Math.Clamp(lastMousePos.Y, 0f, MathF.Max(0f, height - menuHeight));
    var menu = new ContextMenu(menuX, menuY, menuWidth) { Name = "HierarchyContextMenu" };
    menu.AddItem("Add Empty Object", () => AddSceneNode(target, withCubeMesh: false));
    menu.AddItem("Add Cube", () => AddSceneNode(target, withCubeMesh: true));
    hierarchyContextMenu = menu;
    uiRoot.AddChild(menu);
    uiEventRouter.MovePointer(lastMousePos);
    RefreshVertices();
}

void AttachHierarchy(TreeView tree)
{
    tree.SelectionChanged += item =>
    {
        if (synchronizingSelection)
            return;
        synchronizingSelection = true;
        selection.Select(item as Node3D);
        synchronizingSelection = false;
    };
}

/// <summary>Rebuilds the logical editor layout without reallocating viewport render targets.</summary>
/// <param name="newWidth">New logical window width.</param>
/// <param name="newHeight">New logical window height.</param>
void ResizeEditor(int newWidth, int newHeight)
{
    if (newWidth <= 0 || newHeight <= 0)
        return;

    width = newWidth;
    height = newHeight;
    hierarchyContextMenu = null;
    fileContextMenu = null;
    scenePickerDialog = null;
    editorView = EditorUI.BuildView(width, height);
    uiRoot = editorView.Root;
    uiEventRouter.SetRoot(uiRoot);
    sceneViewport = editorView.SceneViewport;
    gameViewport = editorView.GameViewport;
    hierarchyTree = editorView.HierarchyTree;
    hierarchyTree.SetRoots(sceneRoot.Children);
    hierarchyTree.Select(selection.SelectedNode);
    AttachHierarchy(hierarchyTree);
    AttachFileMenu(editorView.FileButton);
    AttachTitleBar(editorView.TitleBar);
    sceneViewport.ViewportId = sceneViewportId;
    sceneViewport.Camera = sceneCamera;
    gameViewport.ViewportId = gameViewportId;
    gameViewport.Camera = gameCamera;

    window.SetViewportQuadVertices(sceneViewportId, EditorUI.CreateViewportQuadVertices(sceneViewport));
    window.SetViewportQuadVertices(gameViewportId, EditorUI.CreateViewportQuadVertices(gameViewport));
    window.SetPushConstants(EditorUI.CreatePushConstants(width, height));
    window.UpdateUI(uiRoot.BuildDrawList());
}

/// <summary>Reallocates viewport FBOs once a live native resize has settled.</summary>
void ResizeViewportTargets()
{
    window.ResizeViewport(sceneViewportId, sceneViewport.Width, sceneViewport.Height);
    window.ResizeViewport(gameViewportId, gameViewport.Width, gameViewport.Height);
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
    window.UpdateUI(uiRoot.BuildDrawList());
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

window.MouseMove += pos =>
{
    lastMousePos = pos;
    window.UpdateWindowDrag(pos);
    Debug.Input(LogLevel.Trace, "Mouse: ({X:F0}, {Y:F0})", pos.X, pos.Y);

    if (flyCamera.MovePointer(pos))
        return;

    uiEventRouter.MovePointer(pos);

    selection.MovePointer(pos);
};

window.MouseDown += button =>
{
    if (flyCamera.IsActive)
        return;

    Debug.Input(LogLevel.Debug, "MouseDown: button={Button}", button);
    if (button == 1)
    {
        ShowHierarchyContextMenu();
        return;
    }

    if (button != 0)
        return;

    if (hierarchyContextMenu is not null
        && uiEventRouter.HoveredElement is not ContextMenuItem)
        CloseHierarchyContextMenu();
    if (fileContextMenu is not null
        && uiEventRouter.HoveredElement is not ContextMenuItem)
        CloseFileContextMenu();

    uiEventRouter.Press();

    // Must be in scene viewport area
    bool inSceneViewport = (uiEventRouter.HoveredElement is ViewportPanel vp && vp.ViewportId == sceneViewportId)
                        || (uiEventRouter.HoveredElement == null && IsInSceneViewport(lastMousePos));
    if (!inSceneViewport) return;

    selection.PrimaryDown(lastMousePos, inSceneViewport);
};

window.MouseUp += button =>
{
    window.EndWindowDrag();
    if (flyCamera.IsActive)
        return;

    Debug.Input(LogLevel.Debug, "MouseUp: button={Button}", button);

    if (button != 0)
        return;

    var consumedByGizmo = selection.PrimaryUp();

    uiEventRouter.Release(!consumedByGizmo);
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

window.KeyDown += keyCode =>
{
    Debug.Input(LogLevel.Debug, "KeyDown: key={Key}", keyCode);
    if (!flyCamera.KeyDown(keyCode))
        uiEventRouter.KeyDown((int)keyCode);
};

window.KeyUp += keyCode =>
{
    Debug.Input(LogLevel.Debug, "KeyUp: key={Key}", keyCode);
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
    viewportRenderer.Render(sceneViewport, gameViewport, lastMousePos);
};

logger.LogInformation("Running main loop...");
window.Run();
logger.LogInformation("Done.");
return 0;
