using System.Numerics;
using Editor;
using Engine.Core;
using Engine.Graphics;
using Engine.UI;
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(b =>
{
    b.AddConsole();
    b.SetMinimumLevel(LogLevel.Trace);
});

Debug.SetLoggerFactory(loggerFactory);

var logger = loggerFactory.CreateLogger<Program>();
logger.LogInformation("Starting Editor...");

using var window = new SilkWindow(loggerFactory);
var width = 1280;
var height = 720;
var options = new WindowOptions
{
    Title = "Game Engine Editor",
    Width = width,
    Height = height
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

var cube = new MeshInstance3D(new CubeMesh()) { Name = "SceneCube" };
var sceneObjects = new List<MeshInstance3D> { cube };
var sceneRoot = new Node3D { Name = "Scene" };
sceneRoot.AddChild(cube);
var hierarchyTree = editorView.HierarchyTree;
hierarchyTree.SetRoots([sceneRoot]);

// ── Game viewport: OrthographicCamera (future) ────────────────
var gameViewport = editorView.GameViewport;
var gameViewportId = window.RegisterViewport(gameViewport.Width, gameViewport.Height);
gameViewport.ViewportId = gameViewportId;
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
    window, sceneViewportId, gameViewportId, sceneCamera, sceneObjects, selection);

var uiEventRouter = new UIEventRouter(uiRoot, RefreshVertices);
ContextMenu? hierarchyContextMenu = null;
var createdObjectIndex = 1;
RefreshVertices();

void CloseHierarchyContextMenu()
{
    if (hierarchyContextMenu is null)
        return;
    uiRoot.RemoveChild(hierarchyContextMenu);
    hierarchyContextMenu = null;
    RefreshVertices();
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
    hierarchyTree.Expand(parent);
    hierarchyTree.Select(child);
    CloseHierarchyContextMenu();
}

void ShowHierarchyContextMenu()
{
    if (lastMousePos.X < hierarchyTree.Left || lastMousePos.X > hierarchyTree.Right
        || lastMousePos.Y < hierarchyTree.Top || lastMousePos.Y > hierarchyTree.Bottom)
        return;

    CloseHierarchyContextMenu();
    var target = uiEventRouter.HoveredElement is TreeViewItem row ? row.Item : sceneRoot;
    hierarchyTree.Select(target);

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
        selection.Select(item as MeshInstance3D);
        synchronizingSelection = false;
    };
}

void ResizeEditor(int newWidth, int newHeight)
{
    if (newWidth <= 0 || newHeight <= 0)
        return;

    width = newWidth;
    height = newHeight;
    hierarchyContextMenu = null;
    editorView = EditorUI.BuildView(width, height);
    uiRoot = editorView.Root;
    uiEventRouter.SetRoot(uiRoot);
    sceneViewport = editorView.SceneViewport;
    gameViewport = editorView.GameViewport;
    hierarchyTree = editorView.HierarchyTree;
    hierarchyTree.SetRoots([sceneRoot]);
    hierarchyTree.Select(selection.SelectedObject);
    AttachHierarchy(hierarchyTree);
    sceneViewport.ViewportId = sceneViewportId;
    sceneViewport.Camera = sceneCamera;
    gameViewport.ViewportId = gameViewportId;

    window.ResizeViewport(sceneViewportId, sceneViewport.Width, sceneViewport.Height);
    window.ResizeViewport(gameViewportId, gameViewport.Width, gameViewport.Height);
    window.SetViewportQuadVertices(sceneViewportId, EditorUI.CreateViewportQuadVertices(sceneViewport));
    window.SetViewportQuadVertices(gameViewportId, EditorUI.CreateViewportQuadVertices(gameViewport));
    window.SetPushConstants(EditorUI.CreatePushConstants(width, height));
    window.UpdateUI(uiRoot.BuildDrawList());
}

window.Resized += ResizeEditor;

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

    uiEventRouter.Press();

    // Must be in scene viewport area
    bool inSceneViewport = (uiEventRouter.HoveredElement is ViewportPanel vp && vp.ViewportId == sceneViewportId)
                        || (uiEventRouter.HoveredElement == null && IsInSceneViewport(lastMousePos));
    if (!inSceneViewport) return;

    selection.PrimaryDown(lastMousePos, inSceneViewport);
};

window.MouseUp += button =>
{
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

// ── Game loop: Update → Render ──────────────────────────────
window.Update += delta =>
{
    flyCamera.Update(delta);
    viewportRenderer.Render(sceneViewport, gameViewport, lastMousePos);
};

logger.LogInformation("Running main loop...");
window.Run();
logger.LogInformation("Done.");
