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
    private readonly Action<IReadOnlyList<MeshInstance3D>, IReadOnlyList<MeshInstance3D>>
        _objectsEdited;
    private readonly Func<AssetReference, string> _resolveObjectName;
    private readonly Random _random;
    private readonly Stack<ObjectStroke> _objectUndo = [];
    private readonly Stack<ObjectStroke> _objectRedo = [];
    private readonly List<ObjectPlacement> _strokeAdded = [];
    private readonly List<ObjectPlacement> _strokeRemoved = [];
    private readonly List<MeshInstance3D> _changedAdded = [];
    private readonly List<MeshInstance3D> _changedRemoved = [];
    private BrushTarget? _activeTarget;
    private Vector3 _lastDabWorld;
    private float _flattenHeight;
    private bool _hasLastDab;
    private TerrainBrushPreview? _preview;

    /// <summary>Gets shared brush options displayed by terrain Inspectors.</summary>
    public TerrainBrushSettings Settings { get; }

    /// <summary>Gets whether one undoable pointer stroke is active.</summary>
    public bool IsStrokeActive => _activeTarget is not null;

    /// <summary>Gets whether a completed terrain object stroke can be undone.</summary>
    public bool CanUndoObjects => _objectUndo.Count > 0;

    /// <summary>Gets whether an undone terrain object stroke can be reapplied.</summary>
    public bool CanRedoObjects => _objectRedo.Count > 0;

    /// <summary>Creates a controller for the main Scene viewport.</summary>
    /// <param name="camera">Scene camera used to create pointer rays.</param>
    /// <param name="getViewport">Current Scene viewport geometry.</param>
    /// <param name="getSelection">Current transform selection.</param>
    /// <param name="resolveDocument">Shared terrain document resolver.</param>
    /// <param name="resolveMaterialDocument">Shared painted material document resolver.</param>
    /// <param name="terrainEdited">Live render and physics update callback.</param>
    /// <param name="previewChanged">Brush-ring update callback.</param>
    /// <param name="settings">Optional shared brush options.</param>
    /// <param name="objectsEdited">Optional scene-object publication callback.</param>
    /// <param name="resolveObjectName">Optional painted-mesh display-name resolver.</param>
    /// <param name="random">Optional random source used by deterministic tests.</param>
    public TerrainBrushController(
        PerspectiveCamera camera,
        Func<GizmoViewport> getViewport,
        Func<Node3D?> getSelection,
        Func<AssetReference, TerrainDocument?> resolveDocument,
        Func<AssetReference, TerrainMaterialDocument?> resolveMaterialDocument,
        Action<MeshInstance3D, TerrainColliderComponent, TerrainResource,
            TerrainEditRegion, bool> terrainEdited,
        Action<TerrainBrushPreview?> previewChanged,
        TerrainBrushSettings? settings = null,
        Action<IReadOnlyList<MeshInstance3D>, IReadOnlyList<MeshInstance3D>>?
            objectsEdited = null,
        Func<AssetReference, string>? resolveObjectName = null,
        Random? random = null)
    {
        _camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _getViewport = getViewport ?? throw new ArgumentNullException(nameof(getViewport));
        _getSelection = getSelection ?? throw new ArgumentNullException(nameof(getSelection));
        _resolveDocument = resolveDocument ?? throw new ArgumentNullException(nameof(resolveDocument));
        _resolveMaterialDocument = resolveMaterialDocument ??
            throw new ArgumentNullException(nameof(resolveMaterialDocument));
        _terrainEdited = terrainEdited ?? throw new ArgumentNullException(nameof(terrainEdited));
        _previewChanged = previewChanged ?? throw new ArgumentNullException(nameof(previewChanged));
        _objectsEdited = objectsEdited ?? (static (_, _) => { });
        _resolveObjectName = resolveObjectName ?? (static reference => reference.ToString());
        _random = random ?? new Random();
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
        switch (resolved.ToolMode)
        {
            case TerrainToolMode.Paint:
                resolved.MaterialDocument!.BeginStroke();
                break;
            case TerrainToolMode.Objects:
                _strokeAdded.Clear();
                _strokeRemoved.Clear();
                break;
            default:
                resolved.Document.BeginStroke();
                break;
        }
        _activeTarget = resolved;
        if (resolved.ToolMode == TerrainToolMode.Sculpt)
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
        var target = _activeTarget.Value;
        switch (target.ToolMode)
        {
            case TerrainToolMode.Paint:
                target.MaterialDocument!.EndStroke(save: true);
                break;
            case TerrainToolMode.Objects:
                CommitObjectStroke();
                break;
            default:
                target.Document.EndStroke(save: true);
                break;
        }
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
        var changed = target.ToolMode switch
        {
            TerrainToolMode.Paint => target.MaterialDocument!.CancelStroke(),
            TerrainToolMode.Objects => CancelObjectStroke(),
            _ => target.Document.CancelStroke()
        };
        _activeTarget = null;
        _hasLastDab = false;
        if (changed && target.ToolMode != TerrainToolMode.Objects)
            _terrainEdited(target.Instance, target.Collider, target.Document.Value,
                new TerrainEditRegion(0, 0,
                    target.Document.Value.Width - 1,
                    target.Document.Value.Depth - 1), target.ToolMode == TerrainToolMode.Sculpt);
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
        if (document is null)
            return null;
        if (Settings.ToolMode == TerrainToolMode.Objects)
        {
            if (!Settings.EraseObjects && Settings.ObjectMesh is null)
                return null;
            return new BrushTarget(instance, collider, document, null, TerrainToolMode.Objects);
        }
        if (!document.IsEditable)
            return null;
        if (Settings.ToolMode != TerrainToolMode.Paint)
            return new BrushTarget(instance, collider, document, null, TerrainToolMode.Sculpt);
        var materialReference = instance.Materials.FirstOrDefault();
        if (materialReference.Asset.Value == Guid.Empty ||
            _resolveMaterialDocument(materialReference) is not { IsEditable: true } material)
            return null;
        material.EnsureDimensions(document.Value.Width, document.Value.Depth);
        if (Settings.PaintLayer >= material.Value.Layers.Count)
            return null;
        return new BrushTarget(instance, collider, document, material, TerrainToolMode.Paint);
    }

    /// <summary>Applies one brush dab when it is sufficiently separated from the prior dab.</summary>
    /// <param name="target">Active terrain target.</param>
    /// <param name="hit">Current surface hit.</param>
    /// <param name="force">Whether to ignore dab spacing.</param>
    private void ApplyDab(BrushTarget target, BrushHit hit, bool force)
    {
        var minimumSpacing = target.ToolMode == TerrainToolMode.Objects
            ? MathF.Max(0.01f, Settings.ObjectSpacing * 0.5f)
            : MathF.Max(0.01f, Settings.Radius * 0.12f);
        if (!force && _hasLastDab &&
            Vector3.DistanceSquared(hit.WorldPosition, _lastDabWorld) <
            minimumSpacing * minimumSpacing)
            return;
        if (target.ToolMode == TerrainToolMode.Objects)
        {
            ApplyObjectDab(target, hit);
            _lastDabWorld = hit.WorldPosition;
            _hasLastDab = true;
            return;
        }
        var painting = target.ToolMode == TerrainToolMode.Paint;
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

    /// <summary>Undoes the most recently completed terrain object stroke.</summary>
    /// <returns>True when scene objects changed.</returns>
    public bool UndoObjects()
    {
        if (IsStrokeActive || !_objectUndo.TryPop(out var stroke))
            return false;
        ApplyObjectStroke(stroke, undo: true);
        _objectRedo.Push(stroke);
        Settings.RefreshObservers();
        return true;
    }

    /// <summary>Reapplies the most recently undone terrain object stroke.</summary>
    /// <returns>True when scene objects changed.</returns>
    public bool RedoObjects()
    {
        if (IsStrokeActive || !_objectRedo.TryPop(out var stroke))
            return false;
        ApplyObjectStroke(stroke, undo: false);
        _objectUndo.Push(stroke);
        Settings.RefreshObservers();
        return true;
    }

    /// <summary>Discards object-stroke history after replacing the active scene.</summary>
    public void ClearObjectHistory()
    {
        _objectUndo.Clear();
        _objectRedo.Clear();
        _strokeAdded.Clear();
        _strokeRemoved.Clear();
        Settings.RefreshObservers();
    }

    /// <summary>Places or erases brush-authored mesh instances around one surface hit.</summary>
    /// <param name="target">Selected terrain target.</param>
    /// <param name="hit">Current terrain hit.</param>
    private void ApplyObjectDab(BrushTarget target, BrushHit hit)
    {
        _changedAdded.Clear();
        _changedRemoved.Clear();
        if (Settings.EraseObjects)
            EraseObjects(target, hit);
        else
            PlaceObjects(target, hit);
        PublishObjectChanges();
    }

    /// <summary>Places randomized, spaced mesh instances inside one brush footprint.</summary>
    /// <param name="target">Selected terrain target.</param>
    /// <param name="hit">Current terrain hit.</param>
    private void PlaceObjects(BrushTarget target, BrushHit hit)
    {
        if (Settings.ObjectMesh is not { } mesh)
            return;
        var spacing = Settings.ObjectSpacing;
        var area = MathF.PI * Settings.Radius * Settings.Radius;
        var attempts = Math.Clamp(
            (int)MathF.Ceiling(area / (spacing * spacing) * Settings.ObjectDensity), 1, 128);
        var terrain = target.Document.Value;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            var offset = attempt == 0 ? Vector2.Zero : RandomDiscOffset();
            var u = hit.U + offset.X * hit.RadiusU;
            var v = hit.V + offset.Y * hit.RadiusV;
            if (u is < 0f or > 1f || v is < 0f or > 1f)
                continue;
            var localPosition = target.Collider.Center +
                GetLocalSurfacePoint(terrain, target.Collider, u, v);
            var worldPosition = Vector3.Transform(localPosition, target.Instance.GetModelMatrix());
            if (!HasObjectSpacing(target.Instance, worldPosition, spacing))
                continue;
            var scale = Settings.MinimumObjectScale + _random.NextSingle() *
                (Settings.MaximumObjectScale - Settings.MinimumObjectScale);
            var normal = Settings.AlignObjectsToNormal
                ? GetLocalSurfaceNormal(terrain, target.Collider, u, v) : Vector3.UnitY;
            var yaw = Settings.RandomizeObjectYaw ? _random.NextSingle() * MathF.Tau : 0f;
            var instance = new MeshInstance3D
            {
                Name = $"Scattered {_resolveObjectName(mesh)}",
                Mesh = mesh,
                Position = localPosition,
                Orientation = CreateSurfaceOrientation(normal, yaw),
                Scale = new Vector3(scale)
            };
            instance.AddComponent(new TerrainScatterInstanceComponent());
            var index = target.Instance.Children.Count;
            target.Instance.AddChild(instance);
            var placement = new ObjectPlacement(target.Instance, instance, index);
            _strokeAdded.Add(placement);
            _changedAdded.Add(instance);
        }
    }

    /// <summary>Erases matching brush-authored mesh instances inside one brush radius.</summary>
    /// <param name="target">Selected terrain target.</param>
    /// <param name="hit">Current terrain hit.</param>
    private void EraseObjects(BrushTarget target, BrushHit hit)
    {
        var children = target.Instance.Children;
        var radiusSquared = Settings.Radius * Settings.Radius;
        for (var index = children.Count - 1; index >= 0; index--)
        {
            if (children[index] is not MeshInstance3D instance ||
                instance.GetComponent<TerrainScatterInstanceComponent>() is null ||
                Settings.ObjectMesh is { } mesh && instance.Mesh != mesh ||
                Vector3.DistanceSquared(instance.GetWorldPosition(), hit.WorldPosition) >
                    radiusSquared)
                continue;
            var placement = new ObjectPlacement(target.Instance, instance, index);
            target.Instance.RemoveChild(instance);
            _strokeRemoved.Add(placement);
            _changedRemoved.Add(instance);
        }
    }

    /// <summary>Checks one candidate against all brush-authored children and current additions.</summary>
    /// <param name="terrain">Terrain node owning painted objects.</param>
    /// <param name="worldPosition">Candidate world position.</param>
    /// <param name="spacing">Required world-space separation.</param>
    /// <returns>True when the candidate has sufficient separation.</returns>
    private static bool HasObjectSpacing(
        MeshInstance3D terrain,
        Vector3 worldPosition,
        float spacing)
    {
        var children = terrain.Children;
        var spacingSquared = spacing * spacing;
        for (var index = 0; index < children.Count; index++)
        {
            if (children[index] is MeshInstance3D instance &&
                instance.GetComponent<TerrainScatterInstanceComponent>() is not null &&
                Vector3.DistanceSquared(instance.GetWorldPosition(), worldPosition) < spacingSquared)
                return false;
        }
        return true;
    }

    /// <summary>Commits active object mutations as one reversible stroke.</summary>
    private void CommitObjectStroke()
    {
        if (_strokeAdded.Count > 0 || _strokeRemoved.Count > 0)
        {
            _objectUndo.Push(new ObjectStroke(_strokeAdded.ToArray(), _strokeRemoved.ToArray()));
            _objectRedo.Clear();
            Settings.RefreshObservers();
        }
        _strokeAdded.Clear();
        _strokeRemoved.Clear();
    }

    /// <summary>Reverts active object mutations without adding a history entry.</summary>
    /// <returns>True when scene objects changed.</returns>
    private bool CancelObjectStroke()
    {
        if (_strokeAdded.Count == 0 && _strokeRemoved.Count == 0)
            return false;
        ApplyObjectStroke(new ObjectStroke(_strokeAdded.ToArray(), _strokeRemoved.ToArray()),
            undo: true);
        _strokeAdded.Clear();
        _strokeRemoved.Clear();
        return true;
    }

    /// <summary>Applies one object history entry in its forward or reverse direction.</summary>
    /// <param name="stroke">Stored node mutations.</param>
    /// <param name="undo">True to reverse the stroke.</param>
    private void ApplyObjectStroke(ObjectStroke stroke, bool undo)
    {
        _changedAdded.Clear();
        _changedRemoved.Clear();
        if (undo)
        {
            for (var index = stroke.Added.Length - 1; index >= 0; index--)
                RemovePlacement(stroke.Added[index]);
            for (var index = stroke.Removed.Length - 1; index >= 0; index--)
                RestorePlacement(stroke.Removed[index]);
        }
        else
        {
            for (var index = stroke.Removed.Length - 1; index >= 0; index--)
                RemovePlacement(stroke.Removed[index]);
            for (var index = 0; index < stroke.Added.Length; index++)
                RestorePlacement(stroke.Added[index]);
        }
        PublishObjectChanges();
    }

    /// <summary>Removes one placement when it still belongs to its recorded terrain.</summary>
    /// <param name="placement">Placement to remove.</param>
    private void RemovePlacement(ObjectPlacement placement)
    {
        if (!ReferenceEquals(placement.Instance.Parent, placement.Parent) ||
            !placement.Parent.RemoveChild(placement.Instance))
            return;
        _changedRemoved.Add(placement.Instance);
    }

    /// <summary>Restores one detached placement at its prior child index.</summary>
    /// <param name="placement">Placement to restore.</param>
    private void RestorePlacement(ObjectPlacement placement)
    {
        if (placement.Instance.Parent is not null)
            return;
        placement.Parent.InsertChild(
            Math.Min(placement.Index, placement.Parent.Children.Count), placement.Instance);
        _changedAdded.Add(placement.Instance);
    }

    /// <summary>Publishes the reusable object mutation buffers when either contains nodes.</summary>
    private void PublishObjectChanges()
    {
        if (_changedAdded.Count > 0 || _changedRemoved.Count > 0)
            _objectsEdited(_changedAdded, _changedRemoved);
    }

    /// <summary>Creates one uniformly distributed random offset inside a unit disc.</summary>
    /// <returns>Two-dimensional unit-disc offset.</returns>
    private Vector2 RandomDiscOffset()
    {
        var angle = _random.NextSingle() * MathF.Tau;
        var radius = MathF.Sqrt(_random.NextSingle());
        return new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
    }

    /// <summary>Samples one local terrain surface point at normalized coordinates.</summary>
    /// <param name="terrain">Height grid.</param>
    /// <param name="collider">Terrain dimensions.</param>
    /// <param name="u">Normalized X coordinate.</param>
    /// <param name="v">Normalized Z coordinate.</param>
    /// <returns>Local surface point excluding collider center.</returns>
    private static Vector3 GetLocalSurfacePoint(
        TerrainResource terrain,
        TerrainColliderComponent collider,
        float u,
        float v) => new(
            (u - 0.5f) * collider.HorizontalSize.X,
            terrain.Sample(u, v) * collider.HeightScale,
            (v - 0.5f) * collider.HorizontalSize.Y);

    /// <summary>Samples a stable local surface normal from centered finite differences.</summary>
    /// <param name="terrain">Height grid.</param>
    /// <param name="collider">Terrain dimensions.</param>
    /// <param name="u">Normalized X coordinate.</param>
    /// <param name="v">Normalized Z coordinate.</param>
    /// <returns>Normalized local up direction.</returns>
    private static Vector3 GetLocalSurfaceNormal(
        TerrainResource terrain,
        TerrainColliderComponent collider,
        float u,
        float v)
    {
        var du = 1f / (terrain.Width - 1);
        var dv = 1f / (terrain.Depth - 1);
        var left = GetLocalSurfacePoint(terrain, collider, MathF.Max(0f, u - du), v);
        var right = GetLocalSurfacePoint(terrain, collider, MathF.Min(1f, u + du), v);
        var back = GetLocalSurfacePoint(terrain, collider, u, MathF.Max(0f, v - dv));
        var forward = GetLocalSurfacePoint(terrain, collider, u, MathF.Min(1f, v + dv));
        var normal = Vector3.Cross(forward - back, right - left);
        return normal.LengthSquared() <= float.Epsilon
            ? Vector3.UnitY : Vector3.Normalize(normal);
    }

    /// <summary>Creates an orientation aligned to a surface normal with optional local yaw.</summary>
    /// <param name="normal">Normalized surface direction.</param>
    /// <param name="yaw">Rotation around the aligned up axis.</param>
    /// <returns>Local object orientation.</returns>
    private static Quaternion CreateSurfaceOrientation(Vector3 normal, float yaw)
    {
        var dot = Math.Clamp(Vector3.Dot(Vector3.UnitY, normal), -1f, 1f);
        Quaternion alignment;
        if (dot >= 0.999999f)
            alignment = Quaternion.Identity;
        else if (dot <= -0.999999f)
            alignment = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI);
        else
        {
            var axis = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, normal));
            alignment = Quaternion.CreateFromAxisAngle(axis, MathF.Acos(dot));
        }
        var yawRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw);
        return Quaternion.Normalize(yawRotation * alignment);
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
    /// <param name="ToolMode">Operation captured when the stroke began.</param>
    private readonly record struct BrushTarget(
        MeshInstance3D Instance,
        TerrainColliderComponent Collider,
        TerrainDocument Document,
        TerrainMaterialDocument? MaterialDocument,
        TerrainToolMode ToolMode);

    /// <summary>Stores one brush-created node and its stable hierarchy location.</summary>
    /// <param name="Parent">Terrain node that owns the painted instance.</param>
    /// <param name="Instance">Painted mesh instance.</param>
    /// <param name="Index">Original child index.</param>
    private readonly record struct ObjectPlacement(
        MeshInstance3D Parent,
        MeshInstance3D Instance,
        int Index);

    /// <summary>Stores all additions and removals committed by one pointer stroke.</summary>
    /// <param name="Added">Instances added by the stroke.</param>
    /// <param name="Removed">Instances removed by the stroke.</param>
    private sealed record ObjectStroke(
        ObjectPlacement[] Added,
        ObjectPlacement[] Removed);

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
