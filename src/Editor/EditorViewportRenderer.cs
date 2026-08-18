using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Engine.Scripting;
using Engine.UI;

namespace Editor;

/// <summary>
/// Builds and submits the Scene and Game viewport render queues for each frame.
/// </summary>
public sealed class EditorViewportRenderer : IDisposable, ISceneRenderingService
{
    private readonly IRenderer _renderer;
    private RenderViewHandle _sceneViewport;
    private RenderViewHandle _gameViewport;
    private readonly PerspectiveCamera _sceneCamera;
    private ICamera _gameCamera;
    private IReadOnlyList<MeshInstance3D> _sceneObjects;
    private IReadOnlyList<MeshInstance3D> _gameObjects;
    private Node _sceneRoot;
    private Node _gameRoot;
    private readonly SceneSelectionController _selection;
    private readonly ScenePreviewRegistry _previewRegistry;
    private readonly ScenePreviewList _previews = new();
    private readonly OriginAxesMesh _originAxes = new();
    private readonly RenderQueue _sceneQueue = new();
    private readonly RenderQueue _gameQueue = new();
    private RenderPipeline _sceneRenderPipeline;
    private RenderPipeline _gameRenderPipeline;
    private readonly Dictionary<Mesh, MeshHandle> _meshHandles = [];
    private readonly Dictionary<Vector4, MeshHandle> _previewLineMeshes = [];
    private readonly Dictionary<MeshInstance3D, AssetMeshGpuResource> _assetMeshes = [];
    private readonly Dictionary<MeshInstance3D, PendingTerrainUpdate> _pendingTerrainUpdates = [];
    private readonly Dictionary<MeshInstance3D, PendingTerrainTopologyUpdate>
        _pendingTerrainTopologyUpdates = [];
    private readonly SceneAnimationRegistry _animationRegistry;
    private readonly LiveAssetDependencyRegistry? _liveAssetDependencies;
    private readonly Func<AssetReference, TextureResource?>? _skyboxTextureResolver;
    private readonly Dictionary<AssetReference, TextureHandle> _skyboxTextures = [];
    private GizmoViewport _lastSceneViewport;
    private ScenePreviewPickingId? _hoveredPreview;
    private TerrainBrushPreview? _terrainBrushPreview;
    private Vertex[] _sceneOverlayVertices = [];
    private bool _hasSubmittedSceneOverlay;
    private bool _disposed;

    /// <summary>Gets script-facing controllers owned by this renderer's scene resources.</summary>
    public ISceneAnimationService AnimationService => _animationRegistry;

    /// <summary>Gets or sets the pass composition used by the Scene render view.</summary>
    public RenderPipeline SceneRenderPipeline
    {
        get => _sceneRenderPipeline;
        set => _sceneRenderPipeline = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets or sets the pass composition used by the Game render view.</summary>
    public RenderPipeline RenderPipeline
    {
        get => _gameRenderPipeline;
        set => _gameRenderPipeline = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets whether an owned visible controller needs recurring updates.</summary>
    public bool HasActiveAnimations
    {
        get
        {
            for (var index = 0; index < _gameObjects.Count; index++)
            {
                if (_assetMeshes.TryGetValue(_gameObjects[index], out var resource) &&
                    resource.OwnsAnimation && resource.Animation?.RequiresUpdate == true)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Creates the editor viewport renderer.
    /// </summary>
    /// <param name="renderer">Rendering service.</param>
    /// <param name="sceneViewport">Scene render view.</param>
    /// <param name="gameViewport">Game render view.</param>
    /// <param name="sceneCamera">Scene camera.</param>
    /// <param name="gameCamera">Scene-owned camera used by the Game viewport.</param>
    /// <param name="sceneObjects">Objects rendered in the Scene viewport.</param>
    /// <param name="sceneRoot">Root traversed for editor-only Scene previews.</param>
    /// <param name="selection">Selection and gizmo controller.</param>
    /// <param name="previewMeshResolver">Optional explicit collision-mesh preview resolver.</param>
    /// <param name="previewTerrainResolver">Optional explicit terrain preview resolver.</param>
    /// <param name="animationSetResolver">Optional script-selected animation-set resolver.</param>
    /// <param name="skyboxTextureResolver">Optional equirectangular skybox texture resolver.</param>
    /// <param name="liveAssetDependencies">Optional registry for republished live values.</param>
    /// <param name="renderPipeline">Optional render-pass composition.</param>
    public EditorViewportRenderer(
        IRenderer renderer,
        RenderViewHandle sceneViewport,
        RenderViewHandle gameViewport,
        PerspectiveCamera sceneCamera,
        ICamera gameCamera,
        IReadOnlyList<MeshInstance3D> sceneObjects,
        Node sceneRoot,
        SceneSelectionController selection,
        Func<AssetReference, StaticMeshResource?>? previewMeshResolver = null,
        Func<AssetReference, TerrainResource?>? previewTerrainResolver = null,
        Func<AssetReference, SkinnedMeshResource, AnimationClipResource[]>?
            animationSetResolver = null,
        Func<AssetReference, TextureResource?>? skyboxTextureResolver = null,
        LiveAssetDependencyRegistry? liveAssetDependencies = null,
        RenderPipeline? renderPipeline = null)
    {
        _renderer = renderer;
        _sceneViewport = sceneViewport;
        _gameViewport = gameViewport;
        _sceneCamera = sceneCamera;
        _gameCamera = gameCamera;
        _sceneObjects = sceneObjects;
        _gameObjects = sceneObjects;
        _sceneRoot = sceneRoot;
        _gameRoot = sceneRoot;
        _selection = selection;
        _sceneRenderPipeline = renderPipeline ?? BasicForwardRenderPipeline.Instance;
        _gameRenderPipeline = renderPipeline ?? BasicForwardRenderPipeline.Instance;
        _previewRegistry = ScenePreviewRegistry.CreateDefault(
            previewMeshResolver, previewTerrainResolver);
        _liveAssetDependencies = liveAssetDependencies;
        _skyboxTextureResolver = skyboxTextureResolver;
        _animationRegistry = new SceneAnimationRegistry((_, animationSet, controller) =>
        {
            if (animationSetResolver is null)
                throw new InvalidOperationException(
                    "This viewport cannot resolve script animation sets.");
            controller.RefreshClips(animationSetResolver(animationSet, controller.Resource));
            _liveAssetDependencies?.Bind(controller, animationSet.Asset, () =>
            {
                if (controller.IsValid)
                    controller.RefreshClips(
                        animationSetResolver(animationSet, controller.Resource));
            });
        });
    }

    /// <summary>Changes the camera and objects rendered in the Game viewport.</summary>
    /// <param name="gameCamera">Active game camera.</param>
    /// <param name="gameObjects">Objects belonging to the active game scene.</param>
    /// <param name="gameRoot">Optional root belonging to the active game scene.</param>
    public void SetGameScene(
        ICamera gameCamera,
        IReadOnlyList<MeshInstance3D> gameObjects,
        Node? gameRoot = null)
    {
        ArgumentNullException.ThrowIfNull(gameCamera);
        ArgumentNullException.ThrowIfNull(gameObjects);
        _gameCamera = gameCamera;
        _gameObjects = gameObjects;
        _gameRoot = gameRoot ?? _sceneRoot;
        ReleaseUnusedMeshes();
    }

    /// <summary>Changes the objects rendered and edited in the Scene viewport.</summary>
    /// <param name="sceneObjects">Objects belonging to the active editing scene.</param>
    public void SetSceneObjects(IReadOnlyList<MeshInstance3D> sceneObjects)
    {
        ArgumentNullException.ThrowIfNull(sceneObjects);
        _sceneObjects = sceneObjects;
        ReleaseUnusedMeshes();
    }

    /// <summary>Changes the hierarchy traversed for editor-only Scene previews.</summary>
    /// <param name="sceneRoot">Active editing scene root.</param>
    public void SetSceneRoot(Node sceneRoot)
    {
        ArgumentNullException.ThrowIfNull(sceneRoot);
        _sceneRoot = sceneRoot;
    }

    /// <summary>Changes one Scene diagnostic category without changing scene visibility.</summary>
    /// <param name="category">Preview category.</param><param name="visible">Desired visibility.</param>
    public void SetPreviewCategoryVisible(ScenePreviewCategory category, bool visible)
    {
        _previewRegistry.SetCategoryVisible(category, visible);
    }

    /// <summary>Invalidates cached preview geometry decoded from one edited asset.</summary>
    /// <param name="reference">Edited or reimported asset output.</param>
    public void InvalidatePreviewAsset(AssetReference reference)
    {
        _previewRegistry.InvalidateAsset(reference);
    }

    /// <summary>Invalidates cached skybox textures decoded from one edited asset.</summary>
    /// <param name="asset">Edited or reimported asset identity.</param>
    public void InvalidateSkyboxAsset(AssetId asset)
    {
        List<AssetReference>? staleReferences = null;
        foreach (var pair in _skyboxTextures)
        {
            if (pair.Key.Asset != asset)
                continue;
            _renderer.DestroyTexture(pair.Value);
            staleReferences ??= [];
            staleReferences.Add(pair.Key);
        }
        if (staleReferences is null)
            return;
        for (var index = 0; index < staleReferences.Count; index++)
            _skyboxTextures.Remove(staleReferences[index]);
    }

    /// <summary>Changes the current Scene terrain-brush ring.</summary>
    /// <param name="preview">Current brush preview, or null to hide it.</param>
    public void SetTerrainBrushPreview(TerrainBrushPreview? preview)
    {
        _terrainBrushPreview = preview;
    }

    /// <summary>Transfers reusable static GPU resources between corresponding scene copies.</summary>
    /// <param name="source">Current scene instances.</param>
    /// <param name="destination">Replacement scene instances in matching clone order.</param>
    public void RemapStaticAssetMeshResources(
        IReadOnlyList<MeshInstance3D> source,
        IReadOnlyList<MeshInstance3D> destination)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        var count = Math.Min(source.Count, destination.Count);
        for (var index = 0; index < count; index++)
        {
            var previous = source[index];
            var replacement = destination[index];
            if (previous.Mesh != replacement.Mesh ||
                !_assetMeshes.TryGetValue(previous, out var resource) ||
                resource.Palette.IsValid || _assetMeshes.ContainsKey(replacement))
            {
                continue;
            }
            _assetMeshes.Remove(previous);
            _assetMeshes.Add(replacement, resource);
        }
    }

    /// <summary>Gets whether one instance already owns a renderer-local mesh resource.</summary>
    /// <param name="instance">Scene mesh instance.</param>
    /// <returns>True when no GPU upload is required.</returns>
    public bool HasAssetMeshResource(MeshInstance3D instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return _assetMeshes.ContainsKey(instance);
    }

    /// <summary>Changes the renderer-local target used by the Scene viewport.</summary>
    /// <param name="view">New Scene render view.</param>
    public void SetSceneRenderView(RenderViewHandle view)
    {
        if (!view.IsValid)
            throw new ArgumentException("A valid Scene render view is required.", nameof(view));
        _sceneViewport = view;
    }

    /// <summary>Changes the renderer-local target used by the Game viewport.</summary>
    /// <param name="view">New Game render view.</param>
    public void SetGameRenderView(RenderViewHandle view)
    {
        if (!view.IsValid)
            throw new ArgumentException("A valid Game render view is required.", nameof(view));
        _gameViewport = view;
    }

    /// <summary>Releases retained renderer resources created by this viewport renderer.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ClearSceneOverlay();
        foreach (var handle in _meshHandles.Values)
            _renderer.DestroyMesh(handle);
        foreach (var resource in _assetMeshes.Values)
            DestroyAssetMeshResource(resource);
        foreach (var handle in _previewLineMeshes.Values)
            _renderer.DestroyMesh(handle);
        foreach (var handle in _skyboxTextures.Values)
            _renderer.DestroyTexture(handle);
        _animationRegistry.Dispose();
        _assetMeshes.Clear();
        _pendingTerrainUpdates.Clear();
        _pendingTerrainTopologyUpdates.Clear();
        _meshHandles.Clear();
        _previewLineMeshes.Clear();
        _skyboxTextures.Clear();
    }

    /// <summary>Builds and submits all editor viewport work for one frame.</summary>
    /// <param name="sceneViewport">Current Scene viewport layout.</param>
    /// <param name="gameViewport">Current Game viewport layout.</param>
    /// <param name="pointerPosition">Current pointer position.</param>
    public void Render(
        ViewportPanel sceneViewport,
        ViewportPanel gameViewport,
        Vector2 pointerPosition)
    {
        RenderScene(sceneViewport, pointerPosition);
        RenderGame(gameViewport);
    }

    /// <summary>Creates or replaces renderer resources for one persistent imported model.</summary>
    /// <param name="instance">Persistent scene instance.</param>
    /// <param name="mesh">Imported indexed mesh.</param>
    /// <param name="material">Imported standard material.</param>
    /// <param name="baseColorTexture">Optional imported base-color texture.</param>
    /// <param name="normalTexture">Optional imported normal-map texture.</param>
    /// <param name="metallicRoughnessTexture">Optional imported metallic-roughness texture.</param>
    public void SetAssetMeshResource(
        MeshInstance3D instance,
        StaticMeshResource mesh,
        StandardMaterialAsset material,
        TextureResource? baseColorTexture = null,
        TextureResource? normalTexture = null,
        TextureResource? metallicRoughnessTexture = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);
        if (_assetMeshes.Remove(instance, out var previous))
            DestroyAssetMeshResource(previous);
        _pendingTerrainUpdates.Remove(instance);
        _pendingTerrainTopologyUpdates.Remove(instance);
        _pendingTerrainTopologyUpdates.Remove(instance);
        var baseColorHandle = CreateMaterialTexture(baseColorTexture, TextureColorSpace.Srgb);
        var normalHandle = CreateMaterialTexture(normalTexture, TextureColorSpace.Linear);
        var metallicRoughnessHandle = CreateMaterialTexture(
            metallicRoughnessTexture, TextureColorSpace.Linear);
        try
        {
            var resolvedMaterial = ResolvedStandardMaterial.Resolve(
                material, baseColorHandle, normalHandle, metallicRoughnessHandle);
            var meshHandle = _renderer.CreateStaticMesh(mesh, resolvedMaterial);
            _assetMeshes.Add(instance, new AssetMeshGpuResource(
                instance, meshHandle, baseColorHandle, normalHandle,
                metallicRoughnessHandle, default, null, resolvedMaterial));
        }
        catch
        {
            DestroyMaterialTextures(baseColorHandle, normalHandle, metallicRoughnessHandle);
            throw;
        }
    }

    /// <summary>Creates or replaces a dynamic colored terrain surface.</summary>
    /// <param name="instance">Persistent scene terrain instance.</param>
    /// <param name="terrain">Current height samples.</param>
    /// <param name="collider">Terrain dimensions shared with collision.</param>
    public void SetTerrainResource(
        MeshInstance3D instance,
        TerrainResource terrain,
        TerrainColliderComponent collider)
    {
        SetTerrainResource(instance, terrain, collider, new StandardMaterialAsset());
    }

    /// <summary>Creates or replaces a static terrain surface with optional material values.</summary>
    /// <param name="instance">Persistent scene terrain instance.</param>
    /// <param name="terrain">Current height samples.</param>
    /// <param name="collider">Terrain dimensions shared with collision.</param>
    /// <param name="material">Material assigned to the terrain surface.</param>
    /// <param name="baseColorTexture">Optional base-color texture.</param>
    /// <param name="normalTexture">Optional normal-map texture.</param>
    /// <param name="metallicRoughnessTexture">Optional metallic-roughness texture.</param>
    /// <param name="useHeightTint">Whether procedural terrain vertex tint remains enabled.</param>
    public void SetTerrainResource(
        MeshInstance3D instance,
        TerrainResource terrain,
        TerrainColliderComponent collider,
        StandardMaterialAsset material,
        TextureResource? baseColorTexture = null,
        TextureResource? normalTexture = null,
        TextureResource? metallicRoughnessTexture = null,
        bool useHeightTint = true)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(collider);
        ArgumentNullException.ThrowIfNull(material);
        if (_assetMeshes.Remove(instance, out var previous))
            DestroyAssetMeshResource(previous);
        _pendingTerrainUpdates.Remove(instance);
        var terrainMesh = TerrainMeshBuilder.BuildTerrainStaticMesh(
            terrain, collider.HorizontalSize, collider.HeightScale, collider.Center,
            useHeightTint);
        var mesh = terrainMesh.Mesh;
        var baseColorHandle = CreateMaterialTexture(baseColorTexture, TextureColorSpace.Srgb);
        var normalHandle = CreateMaterialTexture(normalTexture, TextureColorSpace.Linear);
        var metallicRoughnessHandle = CreateMaterialTexture(
            metallicRoughnessTexture, TextureColorSpace.Linear);
        try
        {
            var resolvedMaterial = ResolvedStandardMaterial.Resolve(
                material, baseColorHandle, normalHandle, metallicRoughnessHandle);
            var handle = _renderer.CreateStaticMesh(mesh, resolvedMaterial);
            instance.LocalBounds = TerrainMeshBuilder.GetBounds(
                terrain, collider.HorizontalSize, collider.HeightScale, collider.Center);
            _assetMeshes.Add(instance, new AssetMeshGpuResource(
                instance, handle, baseColorHandle, normalHandle, metallicRoughnessHandle,
                default, null, resolvedMaterial, isTerrain: true,
                terrainVertices: mesh.Vertices,
                terrainSamples: terrainMesh.Samples,
                terrainIndices: mesh.Indices,
                terrainTopologyVersion: terrain.TopologyVersion,
                useHeightTint: useHeightTint));
        }
        catch
        {
            DestroyMaterialTextures(baseColorHandle, normalHandle, metallicRoughnessHandle);
            throw;
        }
    }

    /// <summary>Updates a retained terrain surface without changing its identity.</summary>
    /// <param name="instance">Persistent scene terrain instance.</param>
    /// <param name="terrain">Current height samples.</param>
    /// <param name="collider">Terrain dimensions shared with collision.</param>
    public void UpdateTerrainResource(
        MeshInstance3D instance,
        TerrainResource terrain,
        TerrainColliderComponent collider)
    {
        UpdateTerrainResource(instance, terrain, collider, new StandardMaterialAsset());
    }

    /// <summary>Updates a retained terrain surface with material values.</summary>
    /// <param name="instance">Persistent scene terrain instance.</param>
    /// <param name="terrain">Current height samples.</param>
    /// <param name="collider">Terrain dimensions shared with collision.</param>
    /// <param name="material">Material assigned to the terrain surface.</param>
    /// <param name="baseColorTexture">Optional base-color texture.</param>
    /// <param name="normalTexture">Optional normal-map texture.</param>
    /// <param name="metallicRoughnessTexture">Optional metallic-roughness texture.</param>
    /// <param name="useHeightTint">Whether procedural terrain vertex tint remains enabled.</param>
    public void UpdateTerrainResource(
        MeshInstance3D instance,
        TerrainResource terrain,
        TerrainColliderComponent collider,
        StandardMaterialAsset material,
        TextureResource? baseColorTexture = null,
        TextureResource? normalTexture = null,
        TextureResource? metallicRoughnessTexture = null,
        bool useHeightTint = true)
    {
        SetTerrainResource(instance, terrain, collider, material, baseColorTexture,
            normalTexture, metallicRoughnessTexture, useHeightTint);
    }

    /// <summary>Queues one live sculpt region for a retained update before the next render.</summary>
    /// <param name="instance">Persistent scene terrain instance.</param>
    /// <param name="terrain">Current edited height samples.</param>
    /// <param name="collider">Terrain dimensions shared with collision.</param>
    /// <param name="region">Inclusive sample rectangle changed by the sculpt dab.</param>
    public void QueueTerrainGeometryUpdate(
        MeshInstance3D instance,
        TerrainResource terrain,
        TerrainColliderComponent collider,
        TerrainEditRegion region)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(collider);
        if (!_assetMeshes.TryGetValue(instance, out var resource) ||
            resource.TerrainVertices is null ||
            resource.TerrainSamples is null ||
            resource.TerrainIndices is null ||
            resource.TerrainTopologyVersion != terrain.TopologyVersion)
            return;
        var expanded = ExpandTerrainRegion(region, terrain.Width, terrain.Depth);
        if (_pendingTerrainUpdates.TryGetValue(instance, out var pending))
        {
            expanded = UnionTerrainRegions(pending.Region, expanded);
        }
        _pendingTerrainUpdates[instance] = new PendingTerrainUpdate(
            terrain, collider, resource, expanded);
    }

    /// <summary>Queues a coalesced adaptive-topology replacement before the next render.</summary>
    /// <param name="instance">Persistent scene terrain instance.</param>
    /// <param name="terrain">Latest adaptive terrain state.</param>
    /// <param name="collider">Terrain dimensions shared with collision.</param>
    public void QueueTerrainTopologyUpdate(
        MeshInstance3D instance,
        TerrainResource terrain,
        TerrainColliderComponent collider)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(collider);
        if (!_assetMeshes.TryGetValue(instance, out var resource) || !resource.IsTerrain)
            return;
        _pendingTerrainUpdates.Remove(instance);
        _pendingTerrainTopologyUpdates[instance] = new PendingTerrainTopologyUpdate(
            terrain, collider, resource);
    }

    /// <summary>Creates or replaces renderer resources for one imported skinned model.</summary>
    /// <param name="instance">Persistent scene instance.</param>
    /// <param name="mesh">Imported skinned mesh.</param>
    /// <param name="material">Imported standard material.</param>
    /// <param name="baseColorTexture">Optional imported base-color texture.</param>
    /// <param name="normalTexture">Optional imported normal-map texture.</param>
    /// <param name="metallicRoughnessTexture">Optional imported metallic-roughness texture.</param>
    /// <param name="animations">Optional standalone clips already bound to this skeleton.</param>
    /// <param name="sharedController">Optional controller owned by another viewport renderer.</param>
    public void SetAssetMeshResource(
        MeshInstance3D instance,
        SkinnedMeshResource mesh,
        StandardMaterialAsset material,
        TextureResource? baseColorTexture = null,
        TextureResource? normalTexture = null,
        TextureResource? metallicRoughnessTexture = null,
        AnimationClipResource[]? animations = null,
        AnimationController? sharedController = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);
        if (_assetMeshes.Remove(instance, out var previous))
            DestroyAssetMeshResource(previous);
        var baseColorHandle = CreateMaterialTexture(baseColorTexture, TextureColorSpace.Srgb);
        var normalHandle = CreateMaterialTexture(normalTexture, TextureColorSpace.Linear);
        var metallicRoughnessHandle = CreateMaterialTexture(
            metallicRoughnessTexture, TextureColorSpace.Linear);
        try
        {
            var resolvedMaterial = ResolvedStandardMaterial.Resolve(
                material, baseColorHandle, normalHandle, metallicRoughnessHandle);
            var handles = _renderer.CreateSkinnedMesh(mesh, resolvedMaterial);
            var playbackResource = animations is null
                ? mesh
                : new SkinnedMeshResource(mesh.Mesh, mesh.Influences, mesh.Skeleton,
                    animations, mesh.MeshNodeTransform);
            var controller = sharedController ?? new AnimationController(playbackResource);
            var ownsController = sharedController is null;
            _renderer.UpdateSkinPalette(handles.Palette, controller.Pose.SkinMatrices);
            if (ownsController)
                _animationRegistry.Register(instance, controller);
            _assetMeshes.Add(instance, new AssetMeshGpuResource(
                instance, handles.Mesh, baseColorHandle, normalHandle,
                metallicRoughnessHandle, handles.Palette, controller,
                resolvedMaterial, ownsController)
            { UploadedPoseRevision = controller.PoseRevision });
        }
        catch
        {
            DestroyMaterialTextures(baseColorHandle, normalHandle, metallicRoughnessHandle);
            throw;
        }
    }

    /// <summary>Advances runtime skeletal animations and uploads changed palettes.</summary>
    /// <param name="deltaTime">Elapsed simulation seconds.</param>
    public void UpdateAnimations(double deltaTime)
    {
        for (var index = 0; index < _gameObjects.Count; index++)
        {
            var instance = _gameObjects[index];
            if (!_assetMeshes.TryGetValue(instance, out var resource) ||
                resource.Animation is null || !resource.Palette.IsValid)
            {
                continue;
            }
            if (resource.OwnsAnimation)
                resource.Animation.Advance(deltaTime);
        }
        for (var index = 0; index < _gameObjects.Count; index++)
        {
            var instance = _gameObjects[index];
            if (!_assetMeshes.TryGetValue(instance, out var resource) ||
                resource.Animation is null || !resource.Palette.IsValid)
                continue;
            if (resource.OwnsAnimation)
                resource.Animation.DispatchEvents();
            if (resource.UploadedPoseRevision == resource.Animation.PoseRevision)
                continue;
            _renderer.UpdateSkinPalette(resource.Palette,
                resource.Animation.Pose.SkinMatrices);
            resource.UploadedPoseRevision = resource.Animation.PoseRevision;
        }
    }

    /// <summary>Builds and submits only the Scene viewport for its owning native window.</summary>
    /// <param name="sceneViewport">Current Scene viewport layout.</param>
    /// <param name="pointerPosition">Pointer position in the same window.</param>
    public void RenderScene(ViewportPanel sceneViewport, Vector2 pointerPosition)
    {
        if (!sceneViewport.IsEffectivelyVisible ||
            sceneViewport.Width <= 0f || sceneViewport.Height <= 0f)
        {
            ClearSceneOverlay();
            return;
        }
        FlushTerrainUpdates();
        _sceneQueue.Clear();
        _lastSceneViewport = new GizmoViewport(sceneViewport.Left, sceneViewport.Top,
            sceneViewport.Width, sceneViewport.Height);
        _sceneCamera.UpdateViewport(sceneViewport.Width, sceneViewport.Height);
        _selection.Update(pointerPosition);
        _previewRegistry.Build(_sceneRoot, _selection.SelectedNode, _previews, _hoveredPreview);
        AddTerrainBrushPreview();
        _hoveredPreview = PickPreview(pointerPosition);
        RenderSceneViewport();
        var overlayVertexCount = ScenePreviewOverlayBuilder.Build(
            _previews, _sceneCamera.GetViewMatrix(), _sceneCamera.GetProjectionMatrix(),
            _lastSceneViewport, _selection.BuildOverlay(), ref _sceneOverlayVertices);
        var clip = new UIClipRect(
            sceneViewport.Left,
            sceneViewport.Top,
            sceneViewport.Right,
            sceneViewport.Bottom);
        _renderer.SubmitTransient(new TransientGeometry(
            _sceneOverlayVertices, overlayVertexCount, clip));
        _hasSubmittedSceneOverlay = overlayVertexCount > 0;
    }

    /// <summary>Adds the active terrain brush as an always-visible world-space ring.</summary>
    private void AddTerrainBrushPreview()
    {
        if (_terrainBrushPreview is not { } preview)
            return;
        const int segments = 48;
        var color = new Vector4(1f, 0.62f, 0.08f, 1f);
        var pickingId = new ScenePreviewPickingId(
            ulong.MaxValue, preview.Node, preview.Component);
        var previous = Vector3.Transform(
            preview.LocalCenter + new Vector3(preview.LocalRadiusX, 0.015f, 0f),
            preview.Transform);
        for (var index = 1; index <= segments; index++)
        {
            var angle = MathF.Tau * index / segments;
            var current = Vector3.Transform(
                preview.LocalCenter + new Vector3(
                    MathF.Cos(angle) * preview.LocalRadiusX,
                    0.015f,
                    MathF.Sin(angle) * preview.LocalRadiusZ),
                preview.Transform);
            _previews.AddLine(new ScenePreviewLine(previous, current, color,
                ScenePreviewDepthMode.AlwaysVisible, pickingId));
            previous = current;
        }
    }

    /// <summary>Finds the closest selectable preview line near one pointer position.</summary>
    /// <param name="pointer">Logical editor pointer position.</param>
    /// <returns>Owning preview identity, or null when no line is close enough.</returns>
    public ScenePreviewPickingId? PickPreview(Vector2 pointer)
    {
        const float maximumDistanceSquared = 36f;
        var bestDistance = maximumDistanceSquared;
        ScenePreviewPickingId? best = null;
        var view = _sceneCamera.GetViewMatrix();
        var projection = _sceneCamera.GetProjectionMatrix();
        _sceneQueue.Camera = RenderCameraData.Create(view, projection);
        var lines = _previews.Lines;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (!ScenePreviewOverlayBuilder.TryProject(line.Start, view, projection,
                    _lastSceneViewport, out var start) ||
                !ScenePreviewOverlayBuilder.TryProject(line.End, view, projection,
                    _lastSceneViewport, out var end))
                continue;
            var distance = DistanceToSegmentSquared(pointer, start, end);
            if (distance >= bestDistance)
                continue;
            bestDistance = distance;
            best = line.PickingId;
        }
        return best;
    }

    /// <summary>Computes squared distance between a point and a finite segment.</summary>
    /// <param name="point">Query point.</param><param name="start">Segment start.</param>
    /// <param name="end">Segment end.</param><returns>Squared distance.</returns>
    private static float DistanceToSegmentSquared(Vector2 point, Vector2 start, Vector2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.LengthSquared();
        if (lengthSquared <= float.Epsilon)
            return Vector2.DistanceSquared(point, start);
        var amount = Math.Clamp(Vector2.Dot(point - start, segment) / lengthSquared, 0f, 1f);
        return Vector2.DistanceSquared(point, start + segment * amount);
    }

    /// <summary>Clears retained Scene overlay geometry when its viewport is not presented.</summary>
    /// <param name="visible">Whether this renderer currently presents the Scene viewport.</param>
    public void SynchronizeSceneVisibility(bool visible)
    {
        if (!visible)
            ClearSceneOverlay();
    }

    /// <summary>Removes the last submitted gizmo overlay on a visibility transition.</summary>
    private void ClearSceneOverlay()
    {
        if (!_hasSubmittedSceneOverlay)
            return;
        _renderer.SubmitTransient(new TransientGeometry(Array.Empty<Vertex>()));
        _hasSubmittedSceneOverlay = false;
    }

    /// <summary>Builds and submits only the Game viewport for its owning native window.</summary>
    /// <param name="gameViewport">Current Game viewport layout.</param>
    public void RenderGame(ViewportPanel gameViewport)
    {
        if (!gameViewport.IsEffectivelyVisible)
            return;
        FlushTerrainUpdates();
        _gameQueue.Clear();
        RenderGameViewport(gameViewport.Width, gameViewport.Height);
    }

    /// <summary>Builds and submits the Scene viewport queue.</summary>
    private void RenderSceneViewport()
    {
        _sceneQueue.ResolveLighting(_sceneRoot);
        PrepareSkybox(_sceneQueue, _sceneRoot);
        var view = _sceneCamera.GetViewMatrix();
        var projection = _sceneCamera.GetProjectionMatrix();
        _renderer.DrawGroundGrid(_sceneViewport, view, projection);
        _sceneQueue.Add(GetMeshHandle(_originAxes), new PushConstants
        {
            Model = Matrix4x4.Identity,
            View = view,
            Projection = projection
        }, castsShadows: false);
        for (var index = 0; index < _sceneObjects.Count; index++)
        {
            var instance = _sceneObjects[index];
            if (_assetMeshes.TryGetValue(instance, out var resource))
            {
                AddAssetMesh(_sceneQueue, resource,
                    _sceneCamera.GetPushConstants(instance.GetModelMatrix()));
            }
        }
        AddDepthTestedPreviews(view, projection);

        _sceneRenderPipeline.Render(_renderer, _sceneViewport, _sceneQueue);
    }

    /// <summary>Queues depth-tested preview lines as cached thin unit-box meshes.</summary>
    /// <param name="view">Scene view matrix.</param><param name="projection">Scene projection matrix.</param>
    private void AddDepthTestedPreviews(Matrix4x4 view, Matrix4x4 projection)
    {
        const float thickness = 0.012f;
        var lines = _previews.Lines;
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (line.DepthMode != ScenePreviewDepthMode.DepthTested)
                continue;
            var direction = line.End - line.Start;
            var length = direction.Length();
            if (!float.IsFinite(length) || length <= 0.0001f)
                continue;
            direction /= length;
            var rotation = RotationFromUnitZ(direction);
            var model = Matrix4x4.CreateScale(thickness, thickness, length) *
                Matrix4x4.CreateFromQuaternion(rotation) *
                Matrix4x4.CreateTranslation((line.Start + line.End) * .5f);
            _sceneQueue.Add(GetPreviewLineMesh(line.Color), new PushConstants
            {
                Model = model,
                View = view,
                Projection = projection
            }, castsShadows: false);
        }
    }

    /// <summary>Gets a cached colored unit-box mesh used for world-space diagnostic lines.</summary>
    /// <param name="color">Linear line color.</param><returns>Renderer mesh handle.</returns>
    private MeshHandle GetPreviewLineMesh(Vector4 color)
    {
        if (_previewLineMeshes.TryGetValue(color, out var handle))
            return handle;
        handle = _renderer.CreateMesh(new MeshDescription(CreatePreviewLineVertices(color)));
        _previewLineMeshes.Add(color, handle);
        return handle;
    }

    /// <summary>Creates a colored unit box extending from minus to plus one half.</summary>
    /// <param name="color">Linear RGBA vertex color.</param><returns>Triangle vertices.</returns>
    private static Vertex[] CreatePreviewLineVertices(Vector4 color)
    {
        Vector3[] corners =
        [
            new(-.5f,-.5f,-.5f), new(.5f,-.5f,-.5f), new(.5f,.5f,-.5f), new(-.5f,.5f,-.5f),
            new(-.5f,-.5f,.5f), new(.5f,-.5f,.5f), new(.5f,.5f,.5f), new(-.5f,.5f,.5f)
        ];
        int[] indices =
        [
            0,2,1, 0,3,2, 4,5,6, 4,6,7,
            0,1,5, 0,5,4, 3,7,6, 3,6,2,
            1,2,6, 1,6,5, 0,4,7, 0,7,3
        ];
        var vertices = new Vertex[indices.Length];
        for (var index = 0; index < indices.Length; index++)
            vertices[index] = new Vertex(corners[indices[index]], color);
        return vertices;
    }

    /// <summary>Creates a shortest-arc rotation from local positive Z to a direction.</summary>
    /// <param name="direction">Normalized target direction.</param><returns>Rotation quaternion.</returns>
    private static Quaternion RotationFromUnitZ(Vector3 direction)
    {
        var dot = Math.Clamp(direction.Z, -1f, 1f);
        if (dot < -0.9999f)
            return Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        var axis = Vector3.Cross(Vector3.UnitZ, direction);
        return Quaternion.Normalize(new Quaternion(axis, 1f + dot));
    }

    /// <summary>Builds and submits the scene through the active Game camera.</summary>
    /// <param name="width">Game viewport width.</param>
    /// <param name="height">Game viewport height.</param>
    private void RenderGameViewport(float width, float height)
    {
        _gameQueue.ResolveLighting(_gameRoot);
        PrepareSkybox(_gameQueue, _gameRoot);
        _gameCamera.UpdateViewport(width, height);
        _gameQueue.Camera = RenderCameraData.Create(
            _gameCamera.GetViewMatrix(), _gameCamera.GetProjectionMatrix());
        for (var index = 0; index < _gameObjects.Count; index++)
        {
            var instance = _gameObjects[index];
            if (_assetMeshes.TryGetValue(instance, out var resource))
            {
                AddAssetMesh(_gameQueue, resource,
                    _gameCamera.GetPushConstants(instance.GetModelMatrix()));
            }
        }
        _gameRenderPipeline.Render(_renderer, _gameViewport, _gameQueue);
    }

    /// <summary>Resolves the first enabled authored skybox into one render submission.</summary>
    /// <param name="queue">Queue receiving the skybox settings.</param>
    /// <param name="root">Active scene hierarchy.</param>
    private void PrepareSkybox(RenderQueue queue, Node root)
    {
        var skybox = FindSkybox(root);
        if (skybox?.Texture is not { } textureReference ||
            _skyboxTextureResolver is null)
            return;
        if (!_skyboxTextures.TryGetValue(textureReference, out var texture))
        {
            var resource = _skyboxTextureResolver(textureReference);
            if (resource is null)
                return;
            texture = _renderer.CreateTexture(resource with { ColorSpace = TextureColorSpace.Srgb });
            _skyboxTextures.Add(textureReference, texture);
        }
        queue.Skybox = SkyboxRenderSettings.Create(
            texture, skybox.Tint, skybox.Intensity, skybox.GetWorldRotation().Y);
    }

    /// <summary>Finds the first enabled textured skybox in hierarchy order.</summary>
    /// <param name="node">Subtree root.</param>
    /// <returns>The authored skybox, or null when this subtree has none.</returns>
    private static Skybox3D? FindSkybox(Node node)
    {
        if (node is Skybox3D { IsEnabled: true, Texture: not null } skybox)
            return skybox;
        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
        {
            var found = FindSkybox(children[index]);
            if (found is not null)
                return found;
        }
        return null;
    }

    /// <summary>Gets or creates the retained renderer resource for a mesh.</summary>
    /// <param name="mesh">Engine mesh resource.</param>
    /// <returns>Renderer-local mesh handle.</returns>
    private MeshHandle GetMeshHandle(Mesh mesh)
    {
        if (_meshHandles.TryGetValue(mesh, out var handle))
            return handle;
        handle = _renderer.CreateMesh(new MeshDescription(mesh.Vertices));
        _meshHandles.Add(mesh, handle);
        return handle;
    }

    /// <summary>Releases meshes no longer referenced by either editor viewport.</summary>
    private void ReleaseUnusedMeshes()
    {
        var retained = new HashSet<Mesh> { _originAxes };
        foreach (var mesh in _meshHandles.Keys.Where(mesh => !retained.Contains(mesh)).ToArray())
        {
            _renderer.DestroyMesh(_meshHandles[mesh]);
            _meshHandles.Remove(mesh);
        }
        var retainedAssetMeshes = _sceneObjects.Concat(_gameObjects)
            .Where(instance => instance.Mesh.Asset.Value != Guid.Empty)
            .ToHashSet();
        foreach (var instance in _assetMeshes.Keys
            .Where(instance => !retainedAssetMeshes.Contains(instance)).ToArray())
        {
            DestroyAssetMeshResource(_assetMeshes[instance]);
            _assetMeshes.Remove(instance);
            _pendingTerrainUpdates.Remove(instance);
            _pendingTerrainTopologyUpdates.Remove(instance);
        }
    }

    /// <summary>Applies all coalesced terrain regions to retained CPU and GPU vertices.</summary>
    private void FlushTerrainUpdates()
    {
        FlushTerrainTopologyUpdates();
        if (_pendingTerrainUpdates.Count == 0)
            return;
        foreach (var pair in _pendingTerrainUpdates)
        {
            var update = pair.Value;
            var vertices = update.Resource.TerrainVertices!;
            if (update.Terrain.RefinedQuadCount == 0)
            {
                var region = update.Region;
                TerrainMeshBuilder.UpdateStaticMeshVertices(
                    update.Terrain,
                    update.Collider.HorizontalSize,
                    update.Collider.HeightScale,
                    update.Collider.Center,
                    update.Resource.UseHeightTint,
                    vertices,
                    region.MinimumX,
                    region.MinimumZ,
                    region.MaximumX,
                    region.MaximumZ);
                var firstVertex = region.MinimumZ * update.Terrain.Width + region.MinimumX;
                var lastVertex = region.MaximumZ * update.Terrain.Width + region.MaximumX;
                _renderer.UpdateStaticMeshVertices(update.Resource.Mesh,
                    new StaticMeshVertexUpdate(
                        checked((uint)firstVertex), vertices, firstVertex,
                        lastVertex - firstVertex + 1));
            }
            else
            {
                TerrainMeshBuilder.UpdateStaticMeshVertices(
                    update.Terrain,
                    update.Collider.HorizontalSize,
                    update.Collider.HeightScale,
                    update.Collider.Center,
                    update.Resource.UseHeightTint,
                    update.Resource.TerrainSamples!,
                    update.Resource.TerrainIndices!,
                    vertices);
                _renderer.UpdateStaticMeshVertices(update.Resource.Mesh,
                    new StaticMeshVertexUpdate(0, vertices, 0, vertices.Length));
            }
            pair.Key.LocalBounds = TerrainMeshBuilder.GetBounds(
                update.Terrain, update.Collider.HorizontalSize,
                update.Collider.HeightScale, update.Collider.Center);
        }
        _pendingTerrainUpdates.Clear();
    }

    /// <summary>Replaces coalesced adaptive indices while retaining material textures.</summary>
    private void FlushTerrainTopologyUpdates()
    {
        if (_pendingTerrainTopologyUpdates.Count == 0)
            return;
        foreach (var pair in _pendingTerrainTopologyUpdates)
        {
            var update = pair.Value;
            var terrainMesh = TerrainMeshBuilder.BuildTerrainStaticMesh(
                update.Terrain,
                update.Collider.HorizontalSize,
                update.Collider.HeightScale,
                update.Collider.Center,
                update.Resource.UseHeightTint);
            var replacement = _renderer.CreateStaticMesh(
                terrainMesh.Mesh, update.Resource.Material);
            _renderer.DestroyMesh(update.Resource.Mesh);
            update.Resource.Mesh = replacement;
            update.Resource.TerrainVertices = terrainMesh.Mesh.Vertices;
            update.Resource.TerrainSamples = terrainMesh.Samples;
            update.Resource.TerrainIndices = terrainMesh.Mesh.Indices;
            update.Resource.TerrainTopologyVersion = update.Terrain.TopologyVersion;
            pair.Key.LocalBounds = TerrainMeshBuilder.GetBounds(
                update.Terrain, update.Collider.HorizontalSize,
                update.Collider.HeightScale, update.Collider.Center);
        }
        _pendingTerrainTopologyUpdates.Clear();
    }

    /// <summary>Expands a dirty sample rectangle for neighboring normal recalculation.</summary>
    /// <param name="region">Changed sample rectangle.</param>
    /// <param name="width">Terrain sample columns.</param>
    /// <param name="depth">Terrain sample rows.</param>
    /// <returns>Clamped expanded rectangle.</returns>
    private static TerrainEditRegion ExpandTerrainRegion(
        TerrainEditRegion region,
        int width,
        int depth) => new(
            Math.Max(0, region.MinimumX - 1),
            Math.Max(0, region.MinimumZ - 1),
            Math.Min(width - 1, region.MaximumX + 1),
            Math.Min(depth - 1, region.MaximumZ + 1));

    /// <summary>Unites two inclusive dirty sample rectangles.</summary>
    /// <param name="first">First rectangle.</param>
    /// <param name="second">Second rectangle.</param>
    /// <returns>Smallest rectangle containing both inputs.</returns>
    private static TerrainEditRegion UnionTerrainRegions(
        TerrainEditRegion first,
        TerrainEditRegion second) => new(
            Math.Min(first.MinimumX, second.MinimumX),
            Math.Min(first.MinimumZ, second.MinimumZ),
            Math.Max(first.MaximumX, second.MaximumX),
            Math.Max(first.MaximumZ, second.MaximumZ));

    /// <summary>Queues renderer-owned imported model resources for destruction.</summary>
    /// <param name="resource">Imported GPU resource pair.</param>
    private void DestroyAssetMeshResource(AssetMeshGpuResource resource)
    {
        if (resource.Animation is not null && resource.OwnsAnimation)
        {
            _liveAssetDependencies?.Unbind(resource.Animation);
            _animationRegistry.Unregister(resource.Instance);
            resource.Animation.Dispose();
        }
        if (resource.Palette.IsValid)
            _renderer.DestroySkinPalette(resource.Palette);
        _renderer.DestroyMesh(resource.Mesh);
        DestroyMaterialTextures(resource.BaseColorTexture, resource.NormalTexture,
            resource.MetallicRoughnessTexture);
    }

    /// <summary>Creates one renderer texture with the material slot's required color space.</summary>
    /// <param name="texture">Optional decoded texture.</param>
    /// <param name="colorSpace">Required sample interpretation.</param>
    /// <returns>The renderer-owned handle, or an invalid handle when omitted.</returns>
    private TextureHandle CreateMaterialTexture(
        TextureResource? texture,
        TextureColorSpace colorSpace)
    {
        return texture is null
            ? default
            : _renderer.CreateTexture(texture with { ColorSpace = colorSpace });
    }

    /// <summary>Destroys all renderer-owned texture handles for one material.</summary>
    /// <param name="baseColorTexture">Optional base-color texture handle.</param>
    /// <param name="normalTexture">Optional normal-map texture handle.</param>
    /// <param name="metallicRoughnessTexture">Optional metallic-roughness texture handle.</param>
    private void DestroyMaterialTextures(
        TextureHandle baseColorTexture,
        TextureHandle normalTexture,
        TextureHandle metallicRoughnessTexture)
    {
        if (baseColorTexture.IsValid)
            _renderer.DestroyTexture(baseColorTexture);
        if (normalTexture.IsValid)
            _renderer.DestroyTexture(normalTexture);
        if (metallicRoughnessTexture.IsValid)
            _renderer.DestroyTexture(metallicRoughnessTexture);
    }

    /// <summary>Adds static or skinned imported geometry to one queue.</summary>
    /// <param name="queue">Destination queue.</param>
    /// <param name="resource">Imported renderer resource.</param>
    /// <param name="pushConstants">Object and camera transforms.</param>
    private static void AddAssetMesh(
        RenderQueue queue,
        AssetMeshGpuResource resource,
        PushConstants pushConstants)
    {
        if (resource.Palette.IsValid)
        {
            pushConstants.Model = resource.Animation!.Resource.ComposeModelTransform(
                pushConstants.Model);
            queue.AddSkinned(resource.Mesh, resource.Palette, pushConstants,
                castsShadows: resource.SurfaceType != RenderSurfaceType.Transparent,
                surfaceType: resource.SurfaceType);
        }
        else
            queue.Add(resource.Mesh, pushConstants,
                castsShadows: resource.SurfaceType != RenderSurfaceType.Transparent,
                surfaceType: resource.SurfaceType);
    }

    /// <summary>Groups renderer handles owned for one imported scene instance.</summary>
    /// <param name="Mesh">Indexed mesh handle.</param>
    /// <param name="BaseColorTexture">Optional base-color texture handle.</param>
    /// <param name="NormalTexture">Optional normal-map texture handle.</param>
    /// <param name="MetallicRoughnessTexture">Optional metallic-roughness texture handle.</param>
    /// <param name="Palette">Optional joint-palette handle.</param>
    private sealed class AssetMeshGpuResource
    {
        /// <summary>Gets the owning scene mesh instance.</summary>
        internal MeshInstance3D Instance { get; }

        /// <summary>Gets the indexed mesh handle.</summary>
        internal MeshHandle Mesh { get; set; }

        /// <summary>Gets the optional base-color texture handle.</summary>
        internal TextureHandle BaseColorTexture { get; }

        /// <summary>Gets the optional normal-map texture handle.</summary>
        internal TextureHandle NormalTexture { get; }

        /// <summary>Gets the optional metallic-roughness texture handle.</summary>
        internal TextureHandle MetallicRoughnessTexture { get; }

        /// <summary>Gets the optional joint-palette handle.</summary>
        internal SkinPaletteHandle Palette { get; }

        /// <summary>Gets the SRP queue class owned by the resolved material.</summary>
        internal RenderSurfaceType SurfaceType => Material.SurfaceType;

        /// <summary>Gets renderer-resolved material retained across topology replacements.</summary>
        internal ResolvedStandardMaterial Material { get; }

        /// <summary>Gets the optional runtime animation controller.</summary>
        internal AnimationController? Animation { get; }

        /// <summary>Gets whether this renderer advances and disposes the controller.</summary>
        internal bool OwnsAnimation { get; }

        /// <summary>Gets whether the handle uses the mutable colored terrain path.</summary>
        internal bool IsTerrain { get; }

        /// <summary>Gets reusable model vertices for a mutable terrain mesh.</summary>
        internal ModelVertex[]? TerrainVertices { get; set; }

        /// <summary>Gets the active source sample corresponding to each terrain vertex.</summary>
        internal TerrainSamplePoint[]? TerrainSamples { get; set; }

        /// <summary>Gets stable adaptive triangle indices used for normal recalculation.</summary>
        internal uint[]? TerrainIndices { get; set; }

        /// <summary>Gets the terrain topology revision used to create the mesh handle.</summary>
        internal int TerrainTopologyVersion { get; set; }

        /// <summary>Gets whether mutable terrain vertices use procedural height tint.</summary>
        internal bool UseHeightTint { get; }

        /// <summary>Gets or sets the pose revision most recently uploaded.</summary>
        internal ulong UploadedPoseRevision { get; set; }

        /// <summary>Creates renderer resources for one scene mesh instance.</summary>
        /// <param name="mesh">Indexed mesh handle.</param>
        /// <param name="baseColorTexture">Optional base-color texture handle.</param>
        /// <param name="normalTexture">Optional normal-map texture handle.</param>
        /// <param name="metallicRoughnessTexture">Optional metallic-roughness texture handle.</param>
        /// <param name="palette">Optional joint-palette handle.</param>
        /// <param name="animation">Optional runtime animation controller.</param>
        /// <param name="material">Renderer-resolved material retained by this resource.</param>
        /// <param name="ownsAnimation">Whether this resource owns controller lifetime.</param>
        /// <param name="isTerrain">Whether this is a mutable colored terrain mesh.</param>
        /// <param name="terrainVertices">Optional reusable indexed terrain vertices.</param>
        /// <param name="terrainSamples">Optional source sample mapping for terrain vertices.</param>
        /// <param name="terrainIndices">Optional adaptive terrain indices.</param>
        /// <param name="terrainTopologyVersion">Topology revision used by the mesh.</param>
        /// <param name="useHeightTint">Whether terrain vertices use procedural height tint.</param>
        /// <param name="instance">Owning scene mesh instance.</param>
        internal AssetMeshGpuResource(MeshInstance3D instance, MeshHandle mesh,
            TextureHandle baseColorTexture, TextureHandle normalTexture,
            TextureHandle metallicRoughnessTexture, SkinPaletteHandle palette,
            AnimationController? animation, ResolvedStandardMaterial material,
            bool ownsAnimation = false,
            bool isTerrain = false,
            ModelVertex[]? terrainVertices = null,
            TerrainSamplePoint[]? terrainSamples = null,
            uint[]? terrainIndices = null,
            int terrainTopologyVersion = 0,
            bool useHeightTint = true)
        {
            Instance = instance;
            Mesh = mesh;
            BaseColorTexture = baseColorTexture;
            NormalTexture = normalTexture;
            MetallicRoughnessTexture = metallicRoughnessTexture;
            Palette = palette;
            Material = material;
            Animation = animation;
            OwnsAnimation = ownsAnimation;
            IsTerrain = isTerrain;
            TerrainVertices = terrainVertices;
            TerrainSamples = terrainSamples;
            TerrainIndices = terrainIndices;
            TerrainTopologyVersion = terrainTopologyVersion;
            UseHeightTint = useHeightTint;
        }
    }

    /// <summary>Stores one renderer-local coalesced terrain geometry update.</summary>
    /// <param name="Terrain">Latest mutable terrain samples.</param>
    /// <param name="Collider">Terrain dimensions.</param>
    /// <param name="Resource">Retained renderer resource.</param>
    /// <param name="Region">Expanded inclusive dirty sample rectangle.</param>
    private readonly record struct PendingTerrainUpdate(
        TerrainResource Terrain,
        TerrainColliderComponent Collider,
        AssetMeshGpuResource Resource,
        TerrainEditRegion Region);

    /// <summary>Stores one renderer-local coalesced adaptive topology replacement.</summary>
    /// <param name="Terrain">Latest adaptive terrain state.</param>
    /// <param name="Collider">Terrain dimensions.</param>
    /// <param name="Resource">Retained renderer resource and material.</param>
    private readonly record struct PendingTerrainTopologyUpdate(
        TerrainResource Terrain,
        TerrainColliderComponent Collider,
        AssetMeshGpuResource Resource);
}
