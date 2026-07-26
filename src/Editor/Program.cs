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
var sceneObjects = new List<MeshInstance3D> { cube };
MeshInstance3D? selectedObject = null;
var gizmo = new AxisGizmo();
var sceneAngle = 0.0f;

// Gizmo drag state
int dragAxis = -1; // -1 = none, 0=X, 1=Y, 2=Z
bool isDragging = false;
Vector3 dragOrigPos = Vector3.Zero;
Vector3 dragStartWorld = Vector3.Zero;

// ── Game viewport: OrthographicCamera (future) ────────────────
var gameViewport = EditorUI.GetGameViewport()!;
var gameViewportId = window.RegisterViewport(gameViewport.Width, gameViewport.Height);
gameViewport.ViewportId = gameViewportId;
window.SetViewportQuadVertices(gameViewportId, EditorUI.CreateViewportQuadVertices(gameViewport));
window.SetViewportClearColor(gameViewportId, 0.05f, 0.05f, 0.12f);

UIElement? hoveredElement = null;
UIElement? focusedElement = null;
Vector2 lastMousePos = Vector2.Zero;

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

Vector2 WorldToScreen(Vector3 worldPos, Matrix4x4 view, Matrix4x4 projection, float vpX, float vpY, float vpW, float vpH)
{
    var clip = Vector4.Transform(new Vector4(worldPos, 1), view * projection);
    if (MathF.Abs(clip.W) < 0.001f) return new Vector2(-1, -1);
    var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
    var sx = vpX + (ndc.X + 1f) * 0.5f * vpW;
    var sy = vpY + (ndc.Y + 1f) * 0.5f * vpH;
    return new Vector2(sx, sy);
}

MeshInstance3D? FindObjectAtScreen(Vector2 mousePos)
{
    var view = sceneCamera.GetViewMatrix();
    var proj = sceneCamera.GetProjectionMatrix();
    var vpX = sceneViewport.Position.X;
    var vpY = sceneViewport.Position.Y;
    var vpW = sceneViewport.Width;
    var vpH = sceneViewport.Height;

    MeshInstance3D? closest = null;
    var closestDist = 50f; // pixel threshold

    foreach (var obj in sceneObjects)
    {
        var screen = WorldToScreen(obj.Position, view, proj, vpX, vpY, vpW, vpH);
        if (screen.X < 0) continue;
        var dist = Vector2.Distance(mousePos, screen);
        if (dist < closestDist)
        {
            closestDist = dist;
            closest = obj;
        }
    }
    return closest;
}

(Vector3 origin, Vector3 direction) ScreenToRay(Vector2 screenPos)
{
    var view = sceneCamera.GetViewMatrix();
    var proj = sceneCamera.GetProjectionMatrix();
    var vpX = sceneViewport.Position.X;
    var vpY = sceneViewport.Position.Y;
    var vpW = sceneViewport.Width;
    var vpH = sceneViewport.Height;

    // Screen → NDC (projection already has Y-flip via M22=-M22, so no extra flip here)
    var ndcX = ((screenPos.X - vpX) / vpW) * 2f - 1f;
    var ndcY = ((screenPos.Y - vpY) / vpH) * 2f - 1f;

    Debug.Input(LogLevel.Trace, "ScreenToRay: screen=({SX:F0},{SY:F0}) vp=({VPX:F0},{VPY:F0},{VPW:F0},{VPH:F0}) ndc=({NX:F3},{NY:F3})",
        screenPos.X, screenPos.Y, vpX, vpY, vpW, vpH, ndcX, ndcY);

    // NDC → clip → world (near plane)
    Matrix4x4.Invert(view * proj, out var invViewProj);
    var nearPoint = Vector4.Transform(new Vector4(ndcX, ndcY, 0, 1), invViewProj);
    var farPoint = Vector4.Transform(new Vector4(ndcX, ndcY, 1, 1), invViewProj);
    nearPoint /= nearPoint.W;
    farPoint /= farPoint.W;

    var origin = new Vector3(nearPoint.X, nearPoint.Y, nearPoint.Z);
    var direction = Vector3.Normalize(new Vector3(farPoint.X, farPoint.Y, farPoint.Z) - origin);
    Debug.Input(LogLevel.Trace, "ScreenToRay: origin=({OX:F3},{OY:F3},{OZ:F3}) dir=({DX:F3},{DY:F3},{DZ:F3})",
        origin.X, origin.Y, origin.Z, direction.X, direction.Y, direction.Z);
    return (origin, direction);
}

/// <summary>
/// Finds the closest axis (0=X, 1=Y, 2=Z) to the given ray.
/// Returns -1 if no axis is within threshold.
/// </summary>
int FindClosestAxis(Vector3 rayOrigin, Vector3 rayDir, Vector3 gizmoPos, float threshold, Vector3[]? axisDirs = null)
{
    axisDirs ??= new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
    var axisNames = new[] { "X", "Y", "Z" };
    int bestAxis = -1;
    float bestDist = threshold;

    for (int i = 0; i < 3; i++)
    {
        var u = Vector3.Normalize(axisDirs[i]);
        var a = gizmoPos;
        var v = rayDir;
        var w = rayOrigin - a;
        float dotUU = Vector3.Dot(u, u);
        float dotUV = Vector3.Dot(u, v);
        float dotVV = Vector3.Dot(v, v);
        float dotWU = Vector3.Dot(w, u);
        float dotWV = Vector3.Dot(w, v);

        float denom = dotUU * dotVV - dotUV * dotUV;
        if (MathF.Abs(denom) < 1e-6f)
        {
            Debug.Input(LogLevel.Trace, "Gizmo axis {Axis}: denom≈0 (parallel)", axisNames[i]);
            continue;
        }

        float s = Math.Clamp((dotUV * dotWV - dotVV * dotWU) / denom, 0f, 2f);
        float t = (dotUV * dotWU - dotUU * dotWV) / denom;

        var closestOnAxis = a + s * u;
        var closestOnRay = rayOrigin + t * v;
        float dist = Vector3.Distance(closestOnAxis, closestOnRay);

        Debug.Input(LogLevel.Trace, "Gizmo axis {Axis}: s={S:F3} t={T:F3} dist={Dist:F3} best={Best:F3}",
            axisNames[i], s, t, dist, bestDist);

        if (dist < bestDist)
        {
            bestDist = dist;
            bestAxis = i;
        }
    }
    Debug.Input(LogLevel.Debug, "Gizmo hit test: bestAxis={Axis} dist={Dist:F3}", bestAxis >= 0 ? axisNames[bestAxis] : "none", bestDist);
    return bestAxis;
}

/// <summary>
/// Returns the parameter t along the axis line (origin + t * axisDir)
/// that is closest to the given ray. t can be any value (unbounded).
/// </summary>
float ProjectRayOntoAxis(Vector3 rayOrigin, Vector3 rayDir, Vector3 lineOrigin, Vector3 axisDir)
{
    // Solve for closest points between two lines:
    // L1(t) = lineOrigin + t * axisDir
    // L2(s) = rayOrigin + s * rayDir
    var w = lineOrigin - rayOrigin;
    float a = Vector3.Dot(axisDir, axisDir);
    float b = Vector3.Dot(axisDir, rayDir);
    float c = Vector3.Dot(rayDir, rayDir);
    float d = Vector3.Dot(axisDir, w);
    float e = Vector3.Dot(rayDir, w);

    float denom = a * c - b * b;
    if (MathF.Abs(denom) < 1e-6f) return 0f;
    return (b * e - c * d) / denom;
}

window.MouseMove += pos =>
{
    lastMousePos = pos;
    Debug.Input(LogLevel.Trace, "Mouse: ({X:F0}, {Y:F0})", pos.X, pos.Y);
    HitTest(pos);

    // Gizmo drag
    if (isDragging && selectedObject != null && dragAxis >= 0)
    {
        var (rayOrig, rayDir) = ScreenToRay(pos);
        var rot = selectedObject.Rotation;
        var rotMatrix = Matrix4x4.CreateRotationY(rot.Y) * Matrix4x4.CreateRotationX(rot.X);
        var axisDir = dragAxis == 0
            ? Vector3.Transform(Vector3.UnitX, rotMatrix)
            : dragAxis == 1
                ? Vector3.Transform(Vector3.UnitY, rotMatrix)
                : Vector3.Transform(Vector3.UnitZ, rotMatrix);
        float currentT = ProjectRayOntoAxis(rayOrig, rayDir, selectedObject.Position, axisDir);
        float startT = Vector3.Dot(dragStartWorld, axisDir);
        float delta = currentT - startT;

        selectedObject.Position = dragOrigPos + axisDir * delta;
    }
};

window.MouseDown += button =>
{
    Debug.Input(LogLevel.Debug, "MouseDown: button={Button}", button);
    SetFocus(hoveredElement);
    hoveredElement?.SetPressed(true);
    RefreshVertices();

    if (button != 0) return;

    if (hoveredElement is not ViewportPanel vp || vp.ViewportId != sceneViewportId) return;

    // Try gizmo axis first (only when mouse is over the viewport panel itself)
    if (selectedObject != null)
    {
        Debug.Input(LogLevel.Debug, "Gizmo check: objPos=({X:F2},{Y:F2},{Z:F2})",
            selectedObject.Position.X, selectedObject.Position.Y, selectedObject.Position.Z);
        var (rayOrig, rayDir) = ScreenToRay(lastMousePos);

        // Compute rotated axis directions from object rotation
        var rot = selectedObject.Rotation;
        var rotMatrix = Matrix4x4.CreateRotationY(rot.Y) * Matrix4x4.CreateRotationX(rot.X);
        var localX = Vector3.Transform(Vector3.UnitX, rotMatrix);
        var localY = Vector3.Transform(Vector3.UnitY, rotMatrix);
        var localZ = Vector3.Transform(Vector3.UnitZ, rotMatrix);
        var localAxes = new[] { localX, localY, localZ };
        var axisNames = new[] { "X", "Y", "Z" };

        int axis = FindClosestAxis(rayOrig, rayDir, selectedObject.Position, 0.15f, localAxes);
        Debug.Input(LogLevel.Debug, "Gizmo result: axis={Axis}", axis >= 0 ? axisNames[axis] : "miss");
        if (axis >= 0)
        {
            dragAxis = axis;
            isDragging = true;
            dragOrigPos = selectedObject.Position;
            dragStartWorld = ProjectRayOntoAxis(rayOrig, rayDir, selectedObject.Position, localAxes[axis]) * localAxes[axis];
            Debug.Input(LogLevel.Information, "Drag start: axis={Axis}", axisNames[axis]);
            return;
        }
    }

    // Otherwise, try object selection
    var hit = FindObjectAtScreen(lastMousePos);
    if (hit != selectedObject)
    {
        selectedObject = hit;
        Debug.Input(LogLevel.Information, "Selected: {Name}", selectedObject?.Name ?? "(none)");
    }
};

window.MouseUp += button =>
{
    Debug.Input(LogLevel.Debug, "MouseUp: button={Button}", button);

    if (isDragging)
    {
        isDragging = false;
        dragAxis = -1;
        Debug.Input(LogLevel.Information, "Drag end");
    }

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
    // cube.Rotation = new Vector3(sceneAngle * 0.7f, sceneAngle, 0);
    // cube.Scale = new Vector3(0.5f);
    var scenePush = sceneCamera.GetPushConstants(cube.GetModelMatrix());
    window.DrawInViewport(sceneViewportId, cube.Mesh!.Vertices, scenePush);

    // Render selection gizmo
    if (selectedObject != null)
    {
        gizmo.Position = selectedObject.Position;
        gizmo.Rotation = selectedObject.Rotation;
        gizmo.Scale = Vector3.One;
        var gizmoPush = sceneCamera.GetPushConstants(gizmo.GetModelMatrix());
        window.DrawInViewport(sceneViewportId, gizmo.Mesh.Vertices, gizmoPush);
    }

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
