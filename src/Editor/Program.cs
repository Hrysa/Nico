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

var window = new SilkWindow(loggerFactory);
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
var uiRoot = EditorUI.BuildUI(width, height);
window.SetVertices(uiRoot.CollectVertices().ToArray());
window.SetPushConstants(EditorUI.CreatePushConstants(width, height));
window.CreateVertexBuffer();

// ── Scene viewport: PerspectiveCamera for 3D scene ────────────
var sceneViewport = EditorUI.GetSceneViewport()!;
var sceneViewportId = window.RegisterViewport(sceneViewport.Width, sceneViewport.Height);
sceneViewport.ViewportId = sceneViewportId;
window.SetViewportQuadVertices(sceneViewportId, EditorUI.CreateViewportQuadVertices(sceneViewport));
window.SetViewportClearColor(sceneViewportId, 0.0f, 0.0f, 0.0f);

var sceneCamera = new PerspectiveCamera(
    fov: MathF.PI / 4f,
    aspect: sceneViewport.Width / sceneViewport.Height,
    near: 0.1f,
    far: 1000f);
sceneCamera.Position = new Vector3(0, 0, 6);
sceneCamera.Name = "SceneCamera";
sceneViewport.Camera = sceneCamera;

var cube = new MeshInstance3D(new CubeMesh()) { Name = "SceneCube" };
var sceneAngle = 0.0f;

// ── Game viewport: OrthographicCamera (future) ────────────────
var gameViewport = EditorUI.GetGameViewport()!;
var gameViewportId = window.RegisterViewport(gameViewport.Width, gameViewport.Height);
gameViewport.ViewportId = gameViewportId;
window.SetViewportQuadVertices(gameViewportId, EditorUI.CreateViewportQuadVertices(gameViewport));
window.SetViewportClearColor(gameViewportId, 0.05f, 0.05f, 0.12f);

UIElement? hoveredElement = null;
UIElement? focusedElement = null;

void RefreshVertices()
{
    window.UpdateVertexBuffer(uiRoot.CollectVertices().ToArray());
}

void HitTest(Vector2 mousePos)
{
    var hit = HitTestElement(uiRoot, mousePos);

    if (hit != hoveredElement)
    {
        hoveredElement?.SetHover(false);
        hoveredElement = hit;
        hoveredElement?.SetHover(true);
        Debug.Input(LogLevel.Debug, "Hover: {Name}", hoveredElement?.Name ?? "(none)");
        RefreshVertices();
    }
}

UIElement? HitTestElement(UIElement element, Vector2 pos)
{
    if (!element.IsVisible || !element.ContainsPoint(pos))
        return null;

    for (int i = element.Children.Count - 1; i >= 0; i--)
    {
        if (element.Children[i] is UIElement child)
        {
            var childHit = HitTestElement(child, pos);
            if (childHit != null)
                return childHit;
        }
    }

    return element;
}

void SetFocus(UIElement? element)
{
    if (element == focusedElement)
        return;

    focusedElement?.SetFocus(false);
    focusedElement = element;
    focusedElement?.SetFocus(true);
    Debug.Input(LogLevel.Debug, "Focus: {Name}", focusedElement?.Name ?? "(none)");
}

window.MouseMove += pos =>
{
    Debug.Input(LogLevel.Trace, "Mouse: ({X:F0}, {Y:F0})", pos.X, pos.Y);
    HitTest(pos);
};

window.MouseDown += button =>
{
    Debug.Input(LogLevel.Debug, "MouseDown: button={Button}", button);
    SetFocus(hoveredElement);
    hoveredElement?.SetPressed(true);
    RefreshVertices();
};

window.MouseUp += button =>
{
    Debug.Input(LogLevel.Debug, "MouseUp: button={Button}", button);
    if (hoveredElement != null)
    {
        hoveredElement.SetPressed(false);
        hoveredElement.InvokeClick();
        RefreshVertices();
    }
};

window.MouseDoubleClick += button =>
{
    Debug.Input(LogLevel.Debug, "DoubleClick: button={Button}", button);
    hoveredElement?.InvokeDoubleClick();
    RefreshVertices();
};

window.MouseScroll += offset =>
{
    Debug.Input(LogLevel.Debug, "Scroll: offset={Offset:F1}", offset);
    hoveredElement?.InvokeScroll(offset);
    RefreshVertices();
};

window.KeyDown += keyCode =>
{
    Debug.Input(LogLevel.Debug, "KeyDown: key={Key}", keyCode);
    focusedElement?.InvokeKeyDown(keyCode);
    RefreshVertices();
};

window.KeyUp += keyCode =>
{
    Debug.Input(LogLevel.Debug, "KeyUp: key={Key}", keyCode);
    focusedElement?.InvokeKeyUp(keyCode);
    RefreshVertices();
};

// ── Game loop: Update → Render ──────────────────────────────
window.Update += delta =>
{
    // LogicUpdate: Scene viewport
    sceneCamera.UpdateViewport(sceneViewport.Width, sceneViewport.Height);
    sceneAngle += 0.01f;
    cube.Rotation = new Vector3(sceneAngle * 0.7f, sceneAngle, 0);
    cube.Scale = new Vector3(0.5f);
    var scenePush = sceneCamera.GetPushConstants(cube.GetModelMatrix());
    window.DrawInViewport(sceneViewportId, cube.Mesh!.Vertices, scenePush);

    // LogicUpdate: Game viewport
    var gw = gameViewport.Width;
    var gh = gameViewport.Height;
    var gs = MathF.Min(gw, gh) * 0.25f;
    var gamePush = new PushConstants
    {
        Model = Matrix4x4.Identity,
        View = Matrix4x4.Identity,
        Projection = Matrix4x4.CreateOrthographicOffCenter(0, gw, 0, gh, -1, 1)
    };
    var gcx = gw / 2.0f;
    var gcy = gh / 2.0f;
    var gameVerts = new Vertex[]
    {
        new(new Vector3(gcx - gs, gcy - gs, 0), new Vector3(1, 0.5f, 0)),
        new(new Vector3(gcx - gs, gcy + gs, 0), new Vector3(0, 1, 0.5f)),
        new(new Vector3(gcx + gs, gcy + gs, 0), new Vector3(0, 0.5f, 1)),
        new(new Vector3(gcx + gs, gcy + gs, 0), new Vector3(0, 0.5f, 1)),
        new(new Vector3(gcx + gs, gcy - gs, 0), new Vector3(1, 0, 0.5f)),
        new(new Vector3(gcx - gs, gcy - gs, 0), new Vector3(1, 0.5f, 0)),
    };
    window.DrawInViewport(gameViewportId, gameVerts, gamePush);
};

logger.LogInformation("Running main loop...");
window.Run();
logger.LogInformation("Done.");
