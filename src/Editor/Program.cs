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

// Unit cube: 6 faces, each with 2 triangles (6 vertices)
Vector3[][] cubeFaceVertices =
[
    // Front face (z = +0.5)
    [new(-0.5f, -0.5f,  0.5f), new( 0.5f, -0.5f,  0.5f), new( 0.5f,  0.5f,  0.5f),
     new( 0.5f,  0.5f,  0.5f), new(-0.5f,  0.5f,  0.5f), new(-0.5f, -0.5f,  0.5f)],
    // Back face (z = -0.5)
    [new(-0.5f, -0.5f, -0.5f), new( 0.5f, -0.5f, -0.5f), new( 0.5f,  0.5f, -0.5f),
     new( 0.5f,  0.5f, -0.5f), new(-0.5f,  0.5f, -0.5f), new(-0.5f, -0.5f, -0.5f)],
    // Top face (y = +0.5)
    [new(-0.5f,  0.5f,  0.5f), new( 0.5f,  0.5f,  0.5f), new( 0.5f,  0.5f, -0.5f),
     new( 0.5f,  0.5f, -0.5f), new(-0.5f,  0.5f, -0.5f), new(-0.5f,  0.5f,  0.5f)],
    // Bottom face (y = -0.5)
    [new(-0.5f, -0.5f, -0.5f), new( 0.5f, -0.5f, -0.5f), new( 0.5f, -0.5f,  0.5f),
     new( 0.5f, -0.5f,  0.5f), new(-0.5f, -0.5f,  0.5f), new(-0.5f, -0.5f, -0.5f)],
    // Right face (x = +0.5)
    [new( 0.5f, -0.5f,  0.5f), new( 0.5f, -0.5f, -0.5f), new( 0.5f,  0.5f, -0.5f),
     new( 0.5f,  0.5f, -0.5f), new( 0.5f,  0.5f,  0.5f), new( 0.5f, -0.5f,  0.5f)],
    // Left face (x = -0.5)
    [new(-0.5f, -0.5f, -0.5f), new(-0.5f, -0.5f,  0.5f), new(-0.5f,  0.5f,  0.5f),
     new(-0.5f,  0.5f,  0.5f), new(-0.5f,  0.5f, -0.5f), new(-0.5f, -0.5f, -0.5f)],
];

Vector3[] cubeFaceColors =
[
    new(1, 0, 0), new(0, 1, 0), new(0, 0, 1),
    new(1, 1, 0), new(1, 0, 1), new(0, 1, 1)
];

Vector3[] cubeFaceCentroids =
[
    new(0, 0, 0.5f), new(0, 0, -0.5f), new(0, 0.5f, 0),
    new(0, -0.5f, 0), new(0.5f, 0, 0), new(-0.5f, 0, 0)
];

int[] faceOrder = [0, 1, 2, 3, 4, 5];
Vertex[] sortedVertices = new Vertex[36];

var sceneAngle = 0.0f;
window.SetViewportRenderCallback(sceneViewportId, ctx =>
{
    var w = ctx.Width;
    var h = ctx.Height;

    sceneAngle += 0.01f;
    var model = Matrix4x4.CreateRotationY(sceneAngle) * Matrix4x4.CreateRotationX(sceneAngle * 0.7f);

    // Sort faces back-to-front (painter's algorithm)
    // Camera is at -Z looking toward +Z in view space, so sort by centroid Z descending
    for (int i = 0; i < 6; i++)
    {
        var centroid = Vector3.Transform(cubeFaceCentroids[i], model);
        faceOrder[i] = i;
        // Store distance as sort key via a simple insertion sort
    }
    // Insertion sort by -Z (farthest first)
    for (int i = 1; i < 6; i++)
    {
        int key = faceOrder[i];
        var keyCentroid = Vector3.Transform(cubeFaceCentroids[key], model);
        int j = i - 1;
        while (j >= 0)
        {
            var compCentroid = Vector3.Transform(cubeFaceCentroids[faceOrder[j]], model);
            if (compCentroid.Z >= keyCentroid.Z)
                break;
            faceOrder[j + 1] = faceOrder[j];
            j--;
        }
        faceOrder[j + 1] = key;
    }

    // Build sorted vertex array
    for (int i = 0; i < 6; i++)
    {
        int face = faceOrder[i];
        var color = cubeFaceColors[face];
        for (int v = 0; v < 6; v++)
            sortedVertices[i * 6 + v] = new Vertex(cubeFaceVertices[face][v], color);
    }

    var view = Matrix4x4.Identity;
    var aspect = w / h;
    var halfHeight = 1.5f;
    var halfWidth = halfHeight * aspect;
    var projection = Matrix4x4.CreateOrthographicOffCenter(-halfWidth, halfWidth, -halfHeight, halfHeight, -10, 10);
    var push = new PushConstants { Model = model, View = view, Projection = projection };

    window.DrawInViewport(sceneViewportId, sortedVertices, push);
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
