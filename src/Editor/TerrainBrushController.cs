using System.Numerics;
using Engine.Core;
using Engine.Graphics;

namespace Editor;

/// <summary>Describes the current Scene viewport terrain-brush ring.</summary>
/// <param name="Transform">Terrain-local to world transform.</param>
/// <param name="LocalCenter">Current local surface hit.</param>
/// <param name="LocalRadiusX">Brush radius along local X.</param>
/// <param name="LocalRadiusZ">Brush radius along local Z.</param>
/// <param name="Node">Terrain node owning the preview.</param>
/// <param name="Component">Terrain collider defining surface dimensions.</param>
public readonly record struct TerrainBrushPreview(
    Matrix4x4 Transform,
    Vector3 LocalCenter,
    float LocalRadiusX,
    float LocalRadiusZ,
    Node3D Node,
    TerrainColliderComponent Component);

/// <summary>Routes Scene pointer strokes into shared terrain documents.</summary>
public sealed class TerrainBrushController
{
    private readonly PerspectiveCamera _camera;
    private readonly Func<GizmoViewport> _getViewport;
    private readonly Func<Node3D?> _getSelection;
    private readonly Func<AssetReference, TerrainDocument?> _resolveDocument;
    private readonly Func<AssetReference, TerrainMaterialDocument?> _resolveMaterialDocument;
    private readonly Action<MeshInstance3D, TerrainColliderComponent, TerrainResource,
        TerrainEditRegion, bool> _terrainEdited;
    private readonly Action<TerrainBrushPreview?> _previewChanged;
    private BrushTarget? _activeTarget;
    private Vector3 _lastDabWorld;
    private float _flattenHeight;
    private bool _hasLastDab;
    private TerrainBrushPreview? _preview;

    /// <summary>Gets shared brush options displayed by terrain Inspectors.</summary>
    public TerrainBrushSettings Settings { get; }

    /// <summary>Gets whether one undoable pointer stroke is active.</summary>
    public bool IsStrokeActive => _activeTarget is not null;

    /// <summary>Creates a controller for the main Scene viewport.</summary>
    /// <param name="camera">Scene camera used to create pointer rays.</param>
    /// <param name="getViewport">Current Scene viewport geometry.</param>
    /// <param name="getSelection">Current transform selection.</param>
    /// <param name="resolveDocument">Shared terrain document resolver.</param>
    /// <param name="resolveMaterialDocument">Shared painted material document resolver.</param>
    /// <param name="terrainEdited">Live render and physics update callback.</param>
    /// <param name="previewChanged">Brush-ring update callback.</param>
    /// <param name="settings">Optional shared brush options.</param>
    public TerrainBrushController(
        PerspectiveCamera camera,
        Func<GizmoViewport> getViewport,
        Func<Node3D?> getSelection,
        Func<AssetReference, TerrainDocument?> resolveDocument,
        Func<AssetReference, TerrainMaterialDocument?> resolveMaterialDocument,
        Action<MeshInstance3D, TerrainColliderComponent, TerrainResource,
            TerrainEditRegion, bool> terrainEdited,
        Action<TerrainBrushPreview?> previewChanged,
        TerrainBrushSettings? settings = null)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _getViewport = getViewport ?? throw new ArgumentNullException(nameof(getViewport));
        _getSelection = getSelection ?? throw new ArgumentNullException(nameof(getSelection));
        _resolveDocument = resolveDocument ?? throw new ArgumentNullException(nameof(resolveDocument));
        _resolveMaterialDocument = resolveMaterialDocument ??
            throw new ArgumentNullException(nameof(resolveMaterialDocument));
        _terrainEdited = terrainEdited ?? throw new ArgumentNullException(nameof(terrainEdited));
        _previewChanged = previewChanged ?? throw new ArgumentNullException(nameof(previewChanged));
        Settings = settings ?? new TerrainBrushSettings();
    }

    /// <summary>Updates brush hover and applies spaced dabs during an active stroke.</summary>
    /// <param name="position">Pointer position in editor coordinates.</param>
    /// <returns>True when an active stroke consumes pointer movement.</returns>
    public bool MovePointer(Vector2 position)
    {
        if (!Settings.IsEnabled)
        {
            ClearPreview();
            return IsStrokeActive;
        }
        var target = _activeTarget ?? ResolveTarget();
        if (target is not { } resolved || !TryHit(resolved, position, out var hit))
        {
            ClearPreview();
            return IsStrokeActive;
        }
        SetPreview(hit.Preview);
        if (_activeTarget is { } active)
            ApplyDab(active, hit, force: false);
        return IsStrokeActive;
    }

    /// <summary>Begins sculpting selected terrain under one primary press.</summary>
    /// <param name="position">Pointer position in editor coordinates.</param>
    /// <returns>True when terrain consumed the press.</returns>
    public bool PrimaryDown(Vector2 position)
    {
        if (!Settings.IsEnabled || _activeTarget is not null)
            return false;
        var target = ResolveTarget();
        if (target is not { } resolved || !TryHit(resolved, position, out var hit))
            return false;
        if (resolved.IsPainting)
            resolved.MaterialDocument!.BeginStroke();
        else
            resolved.Document.BeginStroke();
        _activeTarget = resolved;
        _flattenHeight = resolved.Document.Value.Sample(hit.U, hit.V);
        _hasLastDab = false;
        SetPreview(hit.Preview);
        ApplyDab(resolved, hit, force: true);
        return true;
    }

    /// <summary>Completes and saves the active primary-pointer stroke.</summary>
    /// <returns>True when terrain owned the preceding primary press.</returns>
    public bool PrimaryUp()
    {
        if (_activeTarget is null)
            return false;
        if (_activeTarget.Value.IsPainting)
            _activeTarget.Value.MaterialDocument!.EndStroke(save: true);
        else
            _activeTarget.Value.Document.EndStroke(save: true);
        _activeTarget = null;
        _hasLastDab = false;
        return true;
    }

    /// <summary>Cancels an active stroke and restores its starting samples.</summary>
    /// <returns>True when an active stroke was cancelled.</returns>
    public bool Cancel()
    {
        if (_activeTarget is null)
        {
            ClearPreview();
            return false;
        }
        var target = _activeTarget.Value;
        var painting = target.IsPainting;
        var changed = painting
            ? target.MaterialDocument!.CancelStroke()
            : target.Document.CancelStroke();
        _activeTarget = null;
        _hasLastDab = false;
        if (changed)
            _terrainEdited(target.Instance, target.Collider, target.Document.Value,
                new TerrainEditRegion(0, 0,
                    target.Document.Value.Width - 1,
                    target.Document.Value.Depth - 1), !painting);
        ClearPreview();
        return true;
    }

    /// <summary>Clears stale hover state after tool settings or selection change.</summary>
    public void RefreshToolState()
    {
        if (!Settings.IsEnabled)
            Cancel();
        else
            ClearPreview();
    }

    /// <summary>Resolves selected editable terrain and its shared source document.</summary>
    /// <returns>A brush target, or null when selection is not editable terrain.</returns>
    private BrushTarget? ResolveTarget()
    {
        if (_getSelection() is not MeshInstance3D instance ||
            instance.GetComponent<TerrainColliderComponent>() is not { TerrainData: { } reference }
                collider)
            return null;
        var document = _resolveDocument(reference);
        if (document is not { IsEditable: true })
            return null;
        if (Settings.ToolMode != TerrainToolMode.Paint)
            return new BrushTarget(instance, collider, document, null, false);
        var materialReference = instance.Materials.FirstOrDefault();
        if (materialReference.Asset.Value == Guid.Empty ||
            _resolveMaterialDocument(materialReference) is not { IsEditable: true } material)
            return null;
        material.EnsureDimensions(document.Value.Width, document.Value.Depth);
        if (Settings.PaintLayer >= material.Value.Layers.Count)
            return null;
        return new BrushTarget(instance, collider, document, material, true);
    }

    /// <summary>Applies one brush dab when it is sufficiently separated from the prior dab.</summary>
    /// <param name="target">Active terrain target.</param>
    /// <param name="hit">Current surface hit.</param>
    /// <param name="force">Whether to ignore dab spacing.</param>
    private void ApplyDab(BrushTarget target, BrushHit hit, bool force)
    {
        var minimumSpacing = MathF.Max(0.01f, Settings.Radius * 0.12f);
        if (!force && _hasLastDab &&
            Vector3.DistanceSquared(hit.WorldPosition, _lastDabWorld) <
            minimumSpacing * minimumSpacing)
            return;
        var painting = target.IsPainting;
        TerrainEditRegion? region;
        if (painting)
        {
            region = target.MaterialDocument!.ApplyPaint(
                hit.U, hit.V, hit.RadiusU, hit.RadiusV,
                Math.Clamp(Settings.Strength, 0.001f, 1f), Settings.PaintLayer);
        }
        else
        {
            var nodeModel = target.Instance.GetModelMatrix();
            var worldHeightScale = target.Collider.HeightScale * MathF.Max(0.0001f,
                Vector3.TransformNormal(Vector3.UnitY, nodeModel).Length());
            var amount = Settings.Strength / worldHeightScale;
            region = target.Document.ApplyBrush(
                hit.U, hit.V, hit.RadiusU, hit.RadiusV, amount,
                Settings.Mode, _flattenHeight);
        }
        _lastDabWorld = hit.WorldPosition;
        _hasLastDab = true;
        if (region is not { } changed)
            return;
        var terrain = target.Document.Value;
        _terrainEdited(target.Instance, target.Collider, terrain, changed, !painting);
    }

    /// <summary>Creates a pointer ray and finds its closest triangle on one height grid.</summary>
    /// <param name="target">Terrain target.</param>
    /// <param name="position">Pointer position in editor coordinates.</param>
    /// <param name="hit">Resolved brush hit.</param>
    /// <returns>True when the pointer ray intersects the terrain surface.</returns>
    private bool TryHit(BrushTarget target, Vector2 position, out BrushHit hit)
    {
        hit = default;
        var viewport = _getViewport();
        if (!TryCreateRay(_camera, viewport, position, out var rayOrigin, out var rayDirection))
            return false;
        var transform = Matrix4x4.CreateTranslation(target.Collider.Center) *
            target.Instance.GetModelMatrix();
        if (!Matrix4x4.Invert(transform, out var inverse))
            return false;
        var localOrigin = Vector3.Transform(rayOrigin, inverse);
        var localDirection = Vector3.TransformNormal(rayDirection, inverse);
        if (!TryIntersectTerrain(target.Document.Value, target.Collider,
                localOrigin, localDirection, out var localPosition))
            return false;
        var u = localPosition.X / target.Collider.HorizontalSize.X + 0.5f;
        var v = localPosition.Z / target.Collider.HorizontalSize.Y + 0.5f;
        var axisXScale = Vector3.TransformNormal(Vector3.UnitX, transform).Length();
        var axisZScale = Vector3.TransformNormal(Vector3.UnitZ, transform).Length();
        if (axisXScale <= 0.0001f || axisZScale <= 0.0001f)
            return false;
        var localRadiusX = Settings.Radius / axisXScale;
        var localRadiusZ = Settings.Radius / axisZScale;
        var radiusU = localRadiusX / target.Collider.HorizontalSize.X;
        var radiusV = localRadiusZ / target.Collider.HorizontalSize.Y;
        var worldPosition = Vector3.Transform(localPosition, transform);
        hit = new BrushHit(u, v, radiusU, radiusV, worldPosition,
            new TerrainBrushPreview(transform, localPosition,
                localRadiusX, localRadiusZ, target.Instance, target.Collider));
        return true;
    }

    /// <summary>Creates a world-space ray through one viewport pixel.</summary>
    /// <param name="camera">Viewport camera.</param>
    /// <param name="viewport">Viewport rectangle.</param>
    /// <param name="position">Pointer position.</param>
    /// <param name="origin">Created ray origin.</param>
    /// <param name="direction">Created normalized ray direction.</param>
    /// <returns>True when a finite ray was created.</returns>
    private static bool TryCreateRay(
        ICamera camera,
        GizmoViewport viewport,
        Vector2 position,
        out Vector3 origin,
        out Vector3 direction)
    {
        origin = default;
        direction = default;
        if (viewport.Width <= 0f || viewport.Height <= 0f ||
            position.X < viewport.X || position.X > viewport.X + viewport.Width ||
            position.Y < viewport.Y || position.Y > viewport.Y + viewport.Height ||
            !Matrix4x4.Invert(camera.GetViewMatrix() * camera.GetProjectionMatrix(),
                out var inverse))
            return false;
        var x = (position.X - viewport.X) / viewport.Width * 2f - 1f;
        var y = (position.Y - viewport.Y) / viewport.Height * 2f - 1f;
        var near = Vector4.Transform(new Vector4(x, y, 0f, 1f), inverse);
        var far = Vector4.Transform(new Vector4(x, y, 1f, 1f), inverse);
        if (MathF.Abs(near.W) <= float.Epsilon || MathF.Abs(far.W) <= float.Epsilon)
            return false;
        origin = new Vector3(near.X, near.Y, near.Z) / near.W;
        var farPoint = new Vector3(far.X, far.Y, far.Z) / far.W;
        var delta = farPoint - origin;
        if (!IsFinite(origin) || !IsFinite(delta) || delta.LengthSquared() <= float.Epsilon)
            return false;
        direction = Vector3.Normalize(delta);
        return true;
    }

    /// <summary>Finds the nearest two-sided triangle hit on a local terrain grid.</summary>
    /// <param name="terrain">Current height samples.</param>
    /// <param name="collider">Terrain surface dimensions.</param>
    /// <param name="origin">Local ray origin.</param>
    /// <param name="direction">Local ray direction.</param>
    /// <param name="position">Closest local hit position.</param>
    /// <returns>True when the ray intersects the finite surface.</returns>
    private static bool TryIntersectTerrain(
        TerrainResource terrain,
        TerrainColliderComponent collider,
        Vector3 origin,
        Vector3 direction,
        out Vector3 position)
    {
        position = default;
        var closest = float.PositiveInfinity;
        for (var z = 0; z < terrain.Depth - 1; z++)
        {
            for (var x = 0; x < terrain.Width - 1; x++)
            {
                var a = GetLocalPoint(terrain, collider, x, z);
                var b = GetLocalPoint(terrain, collider, x + 1, z);
                var c = GetLocalPoint(terrain, collider, x + 1, z + 1);
                var d = GetLocalPoint(terrain, collider, x, z + 1);
                if (TryIntersectTriangle(origin, direction, a, d, c, out var first) &&
                    first < closest)
                    closest = first;
                if (TryIntersectTriangle(origin, direction, a, c, b, out var second) &&
                    second < closest)
                    closest = second;
            }
        }
        if (!float.IsFinite(closest))
            return false;
        position = origin + direction * closest;
        return IsFinite(position);
    }

    /// <summary>Computes one centered local terrain sample position.</summary>
    /// <param name="terrain">Height grid.</param>
    /// <param name="collider">Terrain dimensions.</param>
    /// <param name="x">Sample column.</param>
    /// <param name="z">Sample row.</param>
    /// <returns>Local sample position.</returns>
    private static Vector3 GetLocalPoint(
        TerrainResource terrain,
        TerrainColliderComponent collider,
        int x,
        int z)
    {
        var u = x / (float)(terrain.Width - 1);
        var v = z / (float)(terrain.Depth - 1);
        return new Vector3(
            (u - 0.5f) * collider.HorizontalSize.X,
            terrain.GetHeight(x, z) * collider.HeightScale,
            (v - 0.5f) * collider.HorizontalSize.Y);
    }

    /// <summary>Intersects a two-sided triangle using the Möller-Trumbore test.</summary>
    /// <param name="origin">Ray origin.</param>
    /// <param name="direction">Ray direction.</param>
    /// <param name="a">First triangle vertex.</param>
    /// <param name="b">Second triangle vertex.</param>
    /// <param name="c">Third triangle vertex.</param>
    /// <param name="distance">Positive ray parameter when hit.</param>
    /// <returns>True when the finite triangle is hit in front of the ray.</returns>
    private static bool TryIntersectTriangle(
        Vector3 origin,
        Vector3 direction,
        Vector3 a,
        Vector3 b,
        Vector3 c,
        out float distance)
    {
        var edge1 = b - a;
        var edge2 = c - a;
        var p = Vector3.Cross(direction, edge2);
        var determinant = Vector3.Dot(edge1, p);
        if (MathF.Abs(determinant) <= 0.000001f)
        {
            distance = 0f;
            return false;
        }
        var inverse = 1f / determinant;
        var translated = origin - a;
        var u = Vector3.Dot(translated, p) * inverse;
        if (u < 0f || u > 1f)
        {
            distance = 0f;
            return false;
        }
        var q = Vector3.Cross(translated, edge1);
        var v = Vector3.Dot(direction, q) * inverse;
        if (v < 0f || u + v > 1f)
        {
            distance = 0f;
            return false;
        }
        distance = Vector3.Dot(edge2, q) * inverse;
        return distance >= 0f && float.IsFinite(distance);
    }

    /// <summary>Publishes a brush preview only when its geometry changed.</summary>
    /// <param name="preview">Current preview geometry.</param>
    private void SetPreview(TerrainBrushPreview preview)
    {
        if (_preview == preview)
            return;
        _preview = preview;
        _previewChanged(preview);
    }

    /// <summary>Removes the current Scene brush preview.</summary>
    private void ClearPreview()
    {
        if (_preview is null)
            return;
        _preview = null;
        _previewChanged(null);
    }

    /// <summary>Checks whether every vector component is finite.</summary>
    /// <param name="value">Vector to inspect.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>Groups one selected scene node, collider, and shared source document.</summary>
    /// <param name="Instance">Selected renderable terrain node.</param>
    /// <param name="Collider">Terrain surface dimensions and collision settings.</param>
    /// <param name="Document">Shared editable height document.</param>
    /// <param name="MaterialDocument">Shared editable painted material document.</param>
    /// <param name="IsPainting">Whether this stroke edits material weights.</param>
    private readonly record struct BrushTarget(
        MeshInstance3D Instance,
        TerrainColliderComponent Collider,
        TerrainDocument Document,
        TerrainMaterialDocument? MaterialDocument,
        bool IsPainting);

    /// <summary>Groups normalized and world-space data for one surface hit.</summary>
    /// <param name="U">Normalized terrain X coordinate.</param>
    /// <param name="V">Normalized terrain Z coordinate.</param>
    /// <param name="RadiusU">Normalized brush X radius.</param>
    /// <param name="RadiusV">Normalized brush Z radius.</param>
    /// <param name="WorldPosition">World-space hit used for dab spacing.</param>
    /// <param name="Preview">Brush-ring preview.</param>
    private readonly record struct BrushHit(
        float U,
        float V,
        float RadiusU,
        float RadiusV,
        Vector3 WorldPosition,
        TerrainBrushPreview Preview);
}
