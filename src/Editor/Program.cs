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
/// Accounts for gizmo rotation via the provided rotation matrix.
/// </summary>
int FindClosestRotationCircle(Vector2 mousePos, Vector3 gizmoPos, Matrix4x4 rotMatrix, float ringRadius, float ringWidth, float threshold)
{
    var view = sceneCamera.GetViewMatrix();
    var proj = sceneCamera.GetProjectionMatrix();
    var vpX = sceneViewport.Position.X;
    var vpY = sceneViewport.Position.Y;
    var vpW = sceneViewport.Width;
    var vpH = sceneViewport.Height;
    var center = WorldToScreen(gizmoPos, view, proj, vpX, vpY, vpW, vpH);

    var axisNames = new[] { "X", "Y", "Z" };
    var localAxes = new[] {
        Vector3.Transform(Vector3.UnitX, rotMatrix),
        Vector3.Transform(Vector3.UnitY, rotMatrix),
        Vector3.Transform(Vector3.UnitZ, rotMatrix)
    };

    float mouseDist = Vector2.Distance(mousePos, center);
    int bestAxis = -1;
    float bestScore = float.MaxValue;

    for (int i = 0; i < 3; i++)
    {
        var axis = localAxes[i];
        Vector3 perp1, perp2;
        if (MathF.Abs(axis.Y) < 0.99f)
        {
            perp1 = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitY));
            perp2 = Vector3.Cross(axis, perp1);
        }
        else
        {
            perp1 = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitX));
            perp2 = Vector3.Cross(axis, perp1);
        }

        var p1 = WorldToScreen(gizmoPos + perp1 * ringRadius, view, proj, vpX, vpY, vpW, vpH);
        var p2 = WorldToScreen(gizmoPos + perp2 * ringRadius, view, proj, vpX, vpY, vpW, vpH);
        float r1 = Vector2.Distance(center, p1);
        float r2 = Vector2.Distance(center, p2);
        float thisRadius = (r1 + r2) * 0.5f;
        if (thisRadius < 1f) continue;

        // Check if mouse is within the ring area (between inner and outer radius)
        float ringPixelWidth = ringWidth * thisRadius / ringRadius;
        float outerR = thisRadius + ringPixelWidth;
        float innerR = thisRadius - ringPixelWidth;
        if (innerR < 0) innerR = 0;

        // Score: 0 if mouse is on the ring, increases as mouse moves away
        float dist = MathF.Abs(mouseDist - thisRadius);
        float score = dist / threshold;

        // Penalize circles that are nearly edge-on (thin projected ring)
        float thinness = MathF.Min(r1, r2) / MathF.Max(r1, r2);
        if (thinness < 0.3f) score += (0.3f - thinness) * 5f;

        Debug.Input(LogLevel.Trace, "RotCircle {Axis}: R={R:F0} thin={T:F2} score={S:F2}",
            axisNames[i], thisRadius, thinness, score);

        if (score < bestScore && dist < threshold)
        {
            bestScore = score;
            bestAxis = i;
        }
    }
    Debug.Input(LogLevel.Debug, "RotCircle hit: {Result} (score={Score:F2})", bestAxis >= 0 ? axisNames[bestAxis] : "miss", bestScore);
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
        var rot = selectedObject.Rotation;
        var rotMatrix = Matrix4x4.CreateRotationY(rot.Y) * Matrix4x4.CreateRotationX(rot.X);

        // Try rotation circle first
        int rotAxis = FindClosestRotationCircle(lastMousePos, selectedObject.Position, rotMatrix, 1.5f, 0.08f, 20f);
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
// ── Game loop: Update → Render ──────────────────────────────
window.Update += delta =>
{
    Vertex[] GenerateGizmoOverlay(Vector3 worldPos, Vector3 rotation, int gizmoHL, int rotGizmoHL)
    {
        var view = sceneCamera.GetViewMatrix();
        var proj = sceneCamera.GetProjectionMatrix();
        var vpX = sceneViewport.Position.X;
        var vpY = sceneViewport.Position.Y;
        var vpW = sceneViewport.Width;
        var vpH = sceneViewport.Height;

        var rotMatrix = Matrix4x4.CreateRotationY(rotation.Y) * Matrix4x4.CreateRotationX(rotation.X);
        var verts = new List<Vertex>();

        // Axis lines (screen-space quads)
        var axisDirs = new[] {
            Vector3.Transform(Vector3.UnitX, rotMatrix),
            Vector3.Transform(Vector3.UnitY, rotMatrix),
            Vector3.Transform(Vector3.UnitZ, rotMatrix)
        };
        var axisColors = new[] {
            gizmoHL == 0 ? new Vector3(1, 1, 0.5f) : new Vector3(1, 0, 0),
            gizmoHL == 1 ? new Vector3(1, 1, 0.5f) : new Vector3(0, 1, 0),
            gizmoHL == 2 ? new Vector3(1, 1, 0.5f) : new Vector3(0, 0, 1)
        };
        float lineWidth = 2.5f;

        for (int i = 0; i < 3; i++)
        {
            var start = WorldToScreen(worldPos, view, proj, vpX, vpY, vpW, vpH);
            var end = WorldToScreen(worldPos + axisDirs[i] * 2f, view, proj, vpX, vpY, vpW, vpH);
            var dir = end - start;
            float len = dir.Length();
            if (len < 1f) continue;
            var normal = new Vector2(-dir.Y, dir.X) / len * lineWidth * 0.5f;

            var c = axisColors[i];
            // CCW winding for Vulkan (Y-down NDC, front-face = CCW)
            verts.Add(new Vertex(new Vector3(start.X - normal.X, start.Y - normal.Y, 0), c));
            verts.Add(new Vertex(new Vector3(start.X + normal.X, start.Y + normal.Y, 0), c));
            verts.Add(new Vertex(new Vector3(end.X + normal.X, end.Y + normal.Y, 0), c));
            verts.Add(new Vertex(new Vector3(start.X - normal.X, start.Y - normal.Y, 0), c));
            verts.Add(new Vertex(new Vector3(end.X + normal.X, end.Y + normal.Y, 0), c));
            verts.Add(new Vertex(new Vector3(end.X - normal.X, end.Y - normal.Y, 0), c));
        }

        // Rotation circles (screen-space line segments)
        float circleRadius = 1.5f;
        int segments = 64;
        var circleColors = new[] {
            rotGizmoHL == 0 ? new Vector3(1, 1, 0.5f) : new Vector3(1, 0, 0),
            rotGizmoHL == 1 ? new Vector3(1, 1, 0.5f) : new Vector3(0, 1, 0),
            rotGizmoHL == 2 ? new Vector3(1, 1, 0.5f) : new Vector3(0, 0, 1)
        };
        float ringWidth = 1.0f;

        for (int ci = 0; ci < 3; ci++)
        {
            var axis = axisDirs[ci];
            Vector3 perp1, perp2;
            if (MathF.Abs(axis.Y) < 0.99f)
            {
                perp1 = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitY));
                perp2 = Vector3.Cross(axis, perp1);
            }
            else
            {
                perp1 = Vector3.Normalize(Vector3.Cross(axis, Vector3.UnitX));
                perp2 = Vector3.Cross(axis, perp1);
            }

            var color = circleColors[ci];
            float step = MathF.PI * 2f / segments;

            for (int s = 0; s < segments; s++)
            {
                float a0 = s * step;
                float a1 = (s + 1) * step;

                var p0 = WorldToScreen(worldPos + (perp1 * MathF.Cos(a0) + perp2 * MathF.Sin(a0)) * circleRadius, view, proj, vpX, vpY, vpW, vpH);
                var p1 = WorldToScreen(worldPos + (perp1 * MathF.Cos(a1) + perp2 * MathF.Sin(a1)) * circleRadius, view, proj, vpX, vpY, vpW, vpH);

                var dir = p1 - p0;
                float len = dir.Length();
                if (len < 0.5f) continue;
                var normal = new Vector2(-dir.Y, dir.X) / len * ringWidth * 0.5f;

                // CCW winding for Vulkan (Y-down NDC, front-face = CCW)
                verts.Add(new Vertex(new Vector3(p0.X - normal.X, p0.Y - normal.Y, 0), color));
                verts.Add(new Vertex(new Vector3(p0.X + normal.X, p0.Y + normal.Y, 0), color));
                verts.Add(new Vertex(new Vector3(p1.X + normal.X, p1.Y + normal.Y, 0), color));
                verts.Add(new Vertex(new Vector3(p0.X - normal.X, p0.Y - normal.Y, 0), color));
                verts.Add(new Vertex(new Vector3(p1.X + normal.X, p1.Y + normal.Y, 0), color));
                verts.Add(new Vertex(new Vector3(p1.X - normal.X, p1.Y - normal.Y, 0), color));
            }
        }

        return verts.ToArray();
    }
    // LogicUpdate: Scene viewport
    sceneCamera.UpdateViewport(sceneViewport.Width, sceneViewport.Height);

    // Gizmo hover/press detection
    int gizmoHighlight = -1;
    int rotGizmoHighlight = -1;

    if (selectedObject != null)
    {
        if (isDragging || isRotating)
        {
            // During drag/rotate, keep the active axis highlighted
            if (isRotating) rotGizmoHighlight = dragAxis;
            else gizmoHighlight = dragAxis;
        }
        else
        {
            var rot = selectedObject.Rotation;
            var rotMatrix = Matrix4x4.CreateRotationY(rot.Y) * Matrix4x4.CreateRotationX(rot.X);
            var localX = Vector3.Transform(Vector3.UnitX, rotMatrix);
            var localY = Vector3.Transform(Vector3.UnitY, rotMatrix);
            var localZ = Vector3.Transform(Vector3.UnitZ, rotMatrix);
            var localAxes = new[] { localX, localY, localZ };

            int hoverCircle = FindClosestRotationCircle(lastMousePos, selectedObject.Position, rotMatrix, 1.5f, 0.08f, 30f);
            int hoverAxis = FindClosestAxis(lastMousePos, selectedObject.Position, 30f, localAxes);

            if (hoverCircle >= 0) rotGizmoHighlight = hoverCircle;
            else if (hoverAxis >= 0) gizmoHighlight = hoverAxis;
        }
    }

    gizmo.SetHighlight(gizmoHighlight);
    rotationGizmo.SetHighlight(rotGizmoHighlight);
    sceneAngle += 0.01f;
    // cube.Rotation = new Vector3(sceneAngle * 0.7f, sceneAngle, 0);
    // cube.Scale = new Vector3(0.5f);
    var scenePush = sceneCamera.GetPushConstants(cube.GetModelMatrix());
    window.DrawInViewport(sceneViewportId, cube.Mesh!.Vertices, scenePush);

    // Render selection gizmo as 2D overlay
    if (selectedObject != null)
    {
        var gizmoVerts = GenerateGizmoOverlay(selectedObject.Position, selectedObject.Rotation, gizmoHighlight, rotGizmoHighlight);
        window.DrawOverlay(gizmoVerts);
    }
    else
    {
        window.DrawOverlay([]);
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
