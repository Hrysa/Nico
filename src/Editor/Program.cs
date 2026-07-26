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
var rotationGizmo = new RotationGizmo();
var sceneAngle = 0.0f;

// Gizmo drag state
int dragAxis = -1; // -1 = none, 0=X, 1=Y, 2=Z
bool isDragging = false;
bool isRotating = false;
Vector3 dragOrigPos = Vector3.Zero;
Vector3 dragStartWorld = Vector3.Zero;
Vector3 rotateOrigRotation = Vector3.Zero;
float rotateStartAngle = 0f;

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
/// Finds the closest axis (0=X, 1=Y, 2=Z) using screen-space distance.
/// Projects axis line segments to 2D and checks pixel distance to mouse.
/// </summary>
int FindClosestAxis(Vector2 mousePos, Vector3 gizmoPos, float threshold, Vector3[]? axisDirs = null)
{
    axisDirs ??= new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
    var axisNames = new[] { "X", "Y", "Z" };
    var view = sceneCamera.GetViewMatrix();
    var proj = sceneCamera.GetProjectionMatrix();
    var vpX = sceneViewport.Position.X;
    var vpY = sceneViewport.Position.Y;
    var vpW = sceneViewport.Width;
    var vpH = sceneViewport.Height;

    int bestAxis = -1;
    float bestDist = threshold;

    for (int i = 0; i < 3; i++)
    {
        var dir = Vector3.Normalize(axisDirs[i]);
        var start = gizmoPos;
        var end = gizmoPos + dir * 2f;

        var screenStart = WorldToScreen(start, view, proj, vpX, vpY, vpW, vpH);
        var screenEnd = WorldToScreen(end, view, proj, vpX, vpY, vpW, vpH);

        // Distance from mouse to line segment in screen space
        var seg = screenEnd - screenStart;
        float segLenSq = Vector2.Dot(seg, seg);
        float t = segLenSq > 1e-6f ? Math.Clamp(Vector2.Dot(mousePos - screenStart, seg) / segLenSq, 0f, 1f) : 0f;
        var closest = screenStart + seg * t;
        float dist = Vector2.Distance(mousePos, closest);

        Debug.Input(LogLevel.Trace, "Gizmo axis {Axis}: screen=({SX1:F0},{SY1:F0})→({SX2:F0},{SY2:F0}) dist={Dist:F1}",
            axisNames[i], screenStart.X, screenStart.Y, screenEnd.X, screenEnd.Y, dist);

        if (dist < bestDist)
        {
            bestDist = dist;
            bestAxis = i;
        }
    }
    Debug.Input(LogLevel.Debug, "Gizmo hit: {Result} (dist={Dist:F1})", bestAxis >= 0 ? axisNames[bestAxis] : "miss", bestDist);
    return bestAxis;
}

/// <summary>
/// Finds the closest rotation circle (0=X, 1=Y, 2=Z) using screen-space distance.
/// Projects circle to 2D and checks if mouse is near the ring.
/// </summary>
int FindClosestRotationCircle(Vector2 mousePos, Vector3 gizmoPos, float ringRadius, float ringWidth, float threshold)
{
    var view = sceneCamera.GetViewMatrix();
    var proj = sceneCamera.GetProjectionMatrix();
    var vpX = sceneViewport.Position.X;
    var vpY = sceneViewport.Position.Y;
    var vpW = sceneViewport.Width;
    var vpH = sceneViewport.Height;
    var center = WorldToScreen(gizmoPos, view, proj, vpX, vpY, vpW, vpH);

    // Approximate projected radius: use center + radius along a screen-space axis
    var testPoint = WorldToScreen(gizmoPos + Vector3.UnitX * ringRadius, view, proj, vpX, vpY, vpW, vpH);
    float projectedRadius = Vector2.Distance(center, testPoint);
    if (projectedRadius < 1f) return -1;

    float ringHalf = ringWidth * projectedRadius / ringRadius;
    float distToCenter = Vector2.Distance(mousePos, center);

    var axisNames = new[] { "X", "Y", "Z" };
    var circleAxes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };

    int bestAxis = -1;
    float bestDist = threshold;

    for (int i = 0; i < 3; i++)
    {
        // Project multiple points on this circle to get its projected radius
        var perp1 = i == 0 ? Vector3.UnitY : i == 1 ? Vector3.UnitX : Vector3.UnitX;
        var perp2 = i == 0 ? Vector3.UnitZ : i == 1 ? Vector3.UnitZ : Vector3.UnitY;
        var p1 = WorldToScreen(gizmoPos + perp1 * ringRadius, view, proj, vpX, vpY, vpW, vpH);
        var p2 = WorldToScreen(gizmoPos + perp2 * ringRadius, view, proj, vpX, vpY, vpW, vpH);
        float r1 = Vector2.Distance(center, p1);
        float r2 = Vector2.Distance(center, p2);
        float thisRadius = (r1 + r2) * 0.5f;
        if (thisRadius < 1f) continue;

        float thisRingHalf = ringWidth * thisRadius / ringRadius;
        float dist = MathF.Abs(distToCenter - thisRadius);

        Debug.Input(LogLevel.Trace, "RotCircle {Axis}: projectedR={PR:F0} mouseDist={MD:F0} ringDist={RD:F1}",
            axisNames[i], thisRadius, distToCenter, dist);

        if (dist < bestDist)
        {
            bestDist = dist;
            bestAxis = i;
        }
    }
    Debug.Input(LogLevel.Debug, "RotCircle hit: {Result} (dist={Dist:F1})", bestAxis >= 0 ? axisNames[bestAxis] : "miss", bestDist);
    return bestAxis;
}

/// <summary>
/// Computes the signed angle from the gizmo center to the mouse position in screen space.
/// The angle is relative to the given rotation axis plane.
/// </summary>
float ScreenAngleAroundAxis(Vector2 mousePos, Vector3 gizmoPos, int axis)
{
    var view = sceneCamera.GetViewMatrix();
    var proj = sceneCamera.GetProjectionMatrix();
    var vpX = sceneViewport.Position.X;
    var vpY = sceneViewport.Position.Y;
    var vpW = sceneViewport.Width;
    var vpH = sceneViewport.Height;
    var center = WorldToScreen(gizmoPos, view, proj, vpX, vpY, vpW, vpH);

    // Project two reference points on the circle to get the local 2D frame
    Vector3 ref1, ref2;
    if (axis == 0) { ref1 = Vector3.UnitY; ref2 = Vector3.UnitZ; }
    else if (axis == 1) { ref1 = Vector3.UnitX; ref2 = Vector3.UnitZ; }
    else { ref1 = Vector3.UnitX; ref2 = Vector3.UnitY; }

    var s1 = Vector2.Normalize(WorldToScreen(gizmoPos + ref1, view, proj, vpX, vpY, vpW, vpH) - center);
    var s2 = Vector2.Normalize(WorldToScreen(gizmoPos + ref2, view, proj, vpX, vpY, vpW, vpH) - center);

    var delta = mousePos - center;
    float x = Vector2.Dot(delta, s1);
    float y = Vector2.Dot(delta, s2);
    return MathF.Atan2(y, x);
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

bool IsInSceneViewport(Vector2 screenPos)
{
    var vpX = sceneViewport.Position.X;
    var vpY = sceneViewport.Position.Y;
    var vpW = sceneViewport.Width;
    var vpH = sceneViewport.Height;
    return screenPos.X >= vpX && screenPos.X <= vpX + vpW
        && screenPos.Y >= vpY && screenPos.Y <= vpY + vpH;
}

window.MouseMove += pos =>
{
    lastMousePos = pos;
    Debug.Input(LogLevel.Trace, "Mouse: ({X:F0}, {Y:F0})", pos.X, pos.Y);
    HitTest(pos);

    // Gizmo drag
    if (selectedObject != null && dragAxis >= 0)
    {
        if (isRotating)
        {
            float currentAngle = ScreenAngleAroundAxis(pos, selectedObject.Position, dragAxis);
            float delta = currentAngle - rotateStartAngle;
            var axisDir = dragAxis == 0 ? Vector3.UnitX : dragAxis == 1 ? Vector3.UnitY : Vector3.UnitZ;
            if (dragAxis == 0) selectedObject.Rotation = rotateOrigRotation + new Vector3(delta, 0, 0);
            else if (dragAxis == 1) selectedObject.Rotation = rotateOrigRotation + new Vector3(0, delta, 0);
            else selectedObject.Rotation = rotateOrigRotation + new Vector3(0, 0, delta);
        }
        else if (isDragging)
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
    }
};

window.MouseDown += button =>
{
    Debug.Input(LogLevel.Debug, "MouseDown: button={Button}", button);
    SetFocus(hoveredElement);
    hoveredElement?.SetPressed(true);
    RefreshVertices();

    if (button != 0) return;

    // Must be in scene viewport area
    bool inSceneViewport = (hoveredElement is ViewportPanel vp && vp.ViewportId == sceneViewportId)
                        || (hoveredElement == null && IsInSceneViewport(lastMousePos));
    if (!inSceneViewport) return;

    // Try gizmo interaction (gizmo is not a UI element, so hoveredElement may be null)
    if (selectedObject != null)
    {
        // Try rotation circle first
        int rotAxis = FindClosestRotationCircle(lastMousePos, selectedObject.Position, 1.5f, 0.04f, 15f);
        if (rotAxis >= 0)
        {
            isRotating = true;
            dragAxis = rotAxis;
            rotateOrigRotation = selectedObject.Rotation;
            rotateStartAngle = ScreenAngleAroundAxis(lastMousePos, selectedObject.Position, rotAxis);
            var axisNames = new[] { "X", "Y", "Z" };
            Debug.Input(LogLevel.Information, "Rotate start: axis={Axis}", axisNames[rotAxis]);
            return;
        }

        // Try position axis
        var rot = selectedObject.Rotation;
        var rotMatrix = Matrix4x4.CreateRotationY(rot.Y) * Matrix4x4.CreateRotationX(rot.X);
        var localX = Vector3.Transform(Vector3.UnitX, rotMatrix);
        var localY = Vector3.Transform(Vector3.UnitY, rotMatrix);
        var localZ = Vector3.Transform(Vector3.UnitZ, rotMatrix);
        var localAxes = new[] { localX, localY, localZ };
        var axisNamesPos = new[] { "X", "Y", "Z" };

        int axis = FindClosestAxis(lastMousePos, selectedObject.Position, 20f, localAxes);
        if (axis >= 0)
        {
            var (rayOrig, rayDir) = ScreenToRay(lastMousePos);
            dragAxis = axis;
            isDragging = true;
            dragOrigPos = selectedObject.Position;
            dragStartWorld = ProjectRayOntoAxis(rayOrig, rayDir, selectedObject.Position, localAxes[axis]) * localAxes[axis];
            Debug.Input(LogLevel.Information, "Drag start: axis={Axis}", axisNamesPos[axis]);
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

    if (isDragging || isRotating)
    {
        isDragging = false;
        isRotating = false;
        dragAxis = -1;
        Debug.Input(LogLevel.Information, "Drag/Rotate end");
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
        // Axis gizmo (position)
        gizmo.Position = selectedObject.Position;
        gizmo.Rotation = selectedObject.Rotation;
        gizmo.Scale = Vector3.One;
        var gizmoPush = sceneCamera.GetPushConstants(gizmo.GetModelMatrix());
        window.DrawInViewport(sceneViewportId, gizmo.Mesh.Vertices, gizmoPush);

        // Rotation gizmo
        rotationGizmo.Position = selectedObject.Position;
        rotationGizmo.Rotation = selectedObject.Rotation;
        rotationGizmo.Scale = Vector3.One;
        var rotGizmoPush = sceneCamera.GetPushConstants(rotationGizmo.GetModelMatrix());
        window.DrawInViewport(sceneViewportId, rotationGizmo.Mesh.Vertices, rotGizmoPush);
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
