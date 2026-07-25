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
sceneCamera.Position = new Vector3(0, 0, 10);
sceneCamera.Name = "SceneCamera";
sceneViewport.Camera = sceneCamera;

// Unit cube centered at origin (36 vertices = 12 triangles)
Vertex[] cubeVertices =
[
    // Front face (z = +0.5)
    new(new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(1, 0, 0)),
    new(new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(1, 0, 0)),
    new(new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(1, 0, 0)),
    new(new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(1, 0, 0)),
    new(new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(1, 0, 0)),
    new(new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(1, 0, 0)),
    // Back face (z = -0.5)
    new(new Vector3( 0.5f, -0.5f, -0.5f), new Vector3(0, 1, 0)),
    new(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0, 1, 0)),
    new(new Vector3(-0.5f,  0.5f, -0.5f), new Vector3(0, 1, 0)),
    new(new Vector3(-0.5f,  0.5f, -0.5f), new Vector3(0, 1, 0)),
    new(new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(0, 1, 0)),
    new(new Vector3( 0.5f, -0.5f, -0.5f), new Vector3(0, 1, 0)),
    // Top face (y = +0.5)
    new(new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(0, 0, 1)),
    new(new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(0, 0, 1)),
    new(new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(0, 0, 1)),
    new(new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(0, 0, 1)),
    new(new Vector3(-0.5f,  0.5f, -0.5f), new Vector3(0, 0, 1)),
    new(new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(0, 0, 1)),
    // Bottom face (y = -0.5)
    new(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(1, 1, 0)),
    new(new Vector3( 0.5f, -0.5f, -0.5f), new Vector3(1, 1, 0)),
    new(new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(1, 1, 0)),
    new(new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(1, 1, 0)),
    new(new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(1, 1, 0)),
    new(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(1, 1, 0)),
    // Right face (x = +0.5)
    new(new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(1, 0, 1)),
    new(new Vector3( 0.5f, -0.5f, -0.5f), new Vector3(1, 0, 1)),
    new(new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(1, 0, 1)),
    new(new Vector3( 0.5f,  0.5f, -0.5f), new Vector3(1, 0, 1)),
    new(new Vector3( 0.5f,  0.5f,  0.5f), new Vector3(1, 0, 1)),
    new(new Vector3( 0.5f, -0.5f,  0.5f), new Vector3(1, 0, 1)),
    // Left face (x = -0.5)
    new(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0, 1, 1)),
    new(new Vector3(-0.5f, -0.5f,  0.5f), new Vector3(0, 1, 1)),
    new(new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(0, 1, 1)),
    new(new Vector3(-0.5f,  0.5f,  0.5f), new Vector3(0, 1, 1)),
    new(new Vector3(-0.5f,  0.5f, -0.5f), new Vector3(0, 1, 1)),
    new(new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0, 1, 1)),
];

var sceneAngle = 0.0f;
window.SetViewportRenderCallback(sceneViewportId, ctx =>
{
    sceneAngle += 0.01f;
    var model = Matrix4x4.CreateRotationY(sceneAngle) * Matrix4x4.CreateRotationX(sceneAngle * 0.7f);
    var push = sceneCamera.GetPushConstants(model);

    window.DrawInViewport(sceneViewportId, cubeVertices, push);
});

// ── Game viewport: OrthographicCamera (future) ────────────────
var gameViewport = EditorUI.GetGameViewport()!;
var gameViewportId = window.RegisterViewport(gameViewport.Width, gameViewport.Height);
gameViewport.ViewportId = gameViewportId;
window.SetViewportQuadVertices(gameViewportId, EditorUI.CreateViewportQuadVertices(gameViewport));
window.SetViewportClearColor(gameViewportId, 0.05f, 0.05f, 0.12f);

// TODO: Replace with OrthographicCamera when implemented
window.SetViewportRenderCallback(gameViewportId, ctx =>
{
    var w = ctx.Width;
    var h = ctx.Height;
    var s = MathF.Min(w, h) * 0.25f;

    var model = Matrix4x4.Identity;
    var view = Matrix4x4.Identity;
    var projection = Matrix4x4.CreateOrthographicOffCenter(0, w, 0, h, -1, 1);
    var push = new PushConstants { Model = model, View = view, Projection = projection };

    var cx = w / 2.0f;
    var cy = h / 2.0f;
    var verts = new Vertex[]
    {
        new(new Vector3(cx - s, cy - s, 0), new Vector3(1, 0.5f, 0)),
        new(new Vector3(cx - s, cy + s, 0), new Vector3(0, 1, 0.5f)),
        new(new Vector3(cx + s, cy + s, 0), new Vector3(0, 0.5f, 1)),
        new(new Vector3(cx + s, cy + s, 0), new Vector3(0, 0.5f, 1)),
        new(new Vector3(cx + s, cy - s, 0), new Vector3(1, 0, 0.5f)),
        new(new Vector3(cx - s, cy - s, 0), new Vector3(1, 0.5f, 0)),
    };

    window.DrawInViewport(gameViewportId, verts, push);
});

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

logger.LogInformation("Running main loop...");
window.Run();
logger.LogInformation("Done.");
