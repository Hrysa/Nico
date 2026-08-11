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
    private readonly SceneAnimationRegistry _animationRegistry = new();
    private GizmoViewport _lastSceneViewport;
    private ScenePreviewPickingId? _hoveredPreview;
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
        _selection = selection;
        _sceneRenderPipeline = renderPipeline ?? BasicForwardRenderPipeline.Instance;
        _gameRenderPipeline = renderPipeline ?? BasicForwardRenderPipeline.Instance;
        _previewRegistry = ScenePreviewRegistry.CreateDefault(
            previewMeshResolver, previewTerrainResolver);
    }

    /// <summary>Changes the camera and objects rendered in the Game viewport.</summary>
    /// <param name="gameCamera">Active game camera.</param>
    /// <param name="gameObjects">Objects belonging to the active game scene.</param>
    public void SetGameScene(
        ICamera gameCamera,
        IReadOnlyList<MeshInstance3D> gameObjects)
    {
        ArgumentNullException.ThrowIfNull(gameCamera);
        ArgumentNullException.ThrowIfNull(gameObjects);
        _gameCamera = gameCamera;
        _gameObjects = gameObjects;
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
        _animationRegistry.Dispose();
        _assetMeshes.Clear();
        _meshHandles.Clear();
        _previewLineMeshes.Clear();
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
    /// <param name="texture">Optional imported base-color texture.</param>
    public void SetAssetMeshResource(
        MeshInstance3D instance,
        StaticMeshResource mesh,
        StandardMaterialResource material,
        TextureResource? texture = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);
        if (_assetMeshes.Remove(instance, out var previous))
            DestroyAssetMeshResource(previous);
        var textureHandle = texture is null ? default : _renderer.CreateTexture(texture);
        try
        {
            material.BaseColorTexture = textureHandle;
            var meshHandle = _renderer.CreateStaticMesh(mesh, material);
            _assetMeshes.Add(instance, new AssetMeshGpuResource(
                instance, meshHandle, textureHandle, default, null));
        }
        catch
        {
            if (textureHandle.IsValid)
                _renderer.DestroyTexture(textureHandle);
            throw;
        }
    }

    /// <summary>Creates or replaces renderer resources for one imported skinned model.</summary>
    /// <param name="instance">Persistent scene instance.</param>
    /// <param name="mesh">Imported skinned mesh.</param>
    /// <param name="material">Imported standard material.</param>
    /// <param name="texture">Optional imported base-color texture.</param>
    /// <param name="animations">Optional standalone clips already bound to this skeleton.</param>
    /// <param name="sharedController">Optional controller owned by another viewport renderer.</param>
    public void SetAssetMeshResource(
        MeshInstance3D instance,
        SkinnedMeshResource mesh,
        StandardMaterialResource material,
        TextureResource? texture = null,
        AnimationClipResource[]? animations = null,
        AnimationController? sharedController = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);
        if (_assetMeshes.Remove(instance, out var previous))
            DestroyAssetMeshResource(previous);
        var textureHandle = texture is null ? default : _renderer.CreateTexture(texture);
        try
        {
            material.BaseColorTexture = textureHandle;
            var handles = _renderer.CreateSkinnedMesh(mesh, material);
            var playbackResource = animations is null
                ? mesh
                : new SkinnedMeshResource(mesh.Mesh, mesh.Influences, mesh.Skeleton,
                    animations, mesh.MeshNodeTransform);
            var controller = sharedController ?? new AnimationController(playbackResource);
            var ownsController = sharedController is null;
            if (ownsController)
                ConfigureAnimationController(instance, controller);
            _renderer.UpdateSkinPalette(handles.Palette, controller.Pose.SkinMatrices);
            if (ownsController)
                _animationRegistry.Register(instance, controller);
            _assetMeshes.Add(instance, new AssetMeshGpuResource(
                instance, handles.Mesh, textureHandle, handles.Palette, controller,
                ownsController)
                { UploadedPoseRevision = controller.PoseRevision });
        }
        catch
        {
            if (textureHandle.IsValid)
                _renderer.DestroyTexture(textureHandle);
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
            var animator = instance.GetComponent<AnimatorComponent>();
            if (resource.OwnsAnimation && (animator is null || animator.Enabled))
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
        if (!sceneViewport.IsEffectivelyVisible)
        {
            ClearSceneOverlay();
            return;
        }
        _sceneQueue.Clear();
        _lastSceneViewport = new GizmoViewport(sceneViewport.Left, sceneViewport.Top,
            sceneViewport.Width, sceneViewport.Height);
        _sceneCamera.UpdateViewport(sceneViewport.Width, sceneViewport.Height);
        _selection.Update(pointerPosition);
        _previewRegistry.Build(_sceneRoot, _selection.SelectedNode, _previews, _hoveredPreview);
        _hoveredPreview = PickPreview(pointerPosition);
        RenderSceneViewport();
        var overlay = ScenePreviewOverlayBuilder.Build(
            _previews, _sceneCamera.GetViewMatrix(), _sceneCamera.GetProjectionMatrix(),
            _lastSceneViewport, _selection.BuildOverlay());
        var clip = new UIClipRect(
            sceneViewport.Left,
            sceneViewport.Top,
            sceneViewport.Right,
            sceneViewport.Bottom);
        _renderer.SubmitTransient(new TransientGeometry(overlay, clip));
        _hasSubmittedSceneOverlay = overlay.Length > 0;
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
        _gameQueue.Clear();
        RenderGameViewport(gameViewport.Width, gameViewport.Height);
    }

    /// <summary>Builds and submits the Scene viewport queue.</summary>
    private void RenderSceneViewport()
    {
        _sceneQueue.Lighting = SceneLighting.Resolve(_sceneRoot);
        var view = _sceneCamera.GetViewMatrix();
        var projection = _sceneCamera.GetProjectionMatrix();
        _renderer.DrawGroundGrid(_sceneViewport, view, projection);
        _sceneQueue.Add(GetMeshHandle(_originAxes), new PushConstants
        {
            Model = Matrix4x4.Identity,
            View = view,
            Projection = projection
        });
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
            });
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
        _gameQueue.Lighting = SceneLighting.Resolve(_sceneRoot);
        _gameCamera.UpdateViewport(width, height);
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
        }
    }

    /// <summary>Queues renderer-owned imported model resources for destruction.</summary>
    /// <param name="resource">Imported GPU resource pair.</param>
    private void DestroyAssetMeshResource(AssetMeshGpuResource resource)
    {
        if (resource.Animation is not null && resource.OwnsAnimation)
        {
            _animationRegistry.Unregister(resource.Instance);
            resource.Animation.Dispose();
        }
        if (resource.Palette.IsValid)
            _renderer.DestroySkinPalette(resource.Palette);
        _renderer.DestroyMesh(resource.Mesh);
        if (resource.Texture.IsValid)
            _renderer.DestroyTexture(resource.Texture);
    }

    /// <summary>Configures initial playback from the instance animator component.</summary>
    /// <param name="instance">Scene instance owning the animator.</param>
    /// <param name="controller">New animation controller.</param>
    private static void ConfigureAnimationController(
        MeshInstance3D instance,
        AnimationController controller)
    {
        var animator = instance.GetComponent<AnimatorComponent>();
        if (animator is null || !animator.Enabled)
            return;
        controller.DefaultFadeDuration = animator.DefaultFadeDuration;
        var clip = controller.Resource.FindAnimation(animator.DefaultClip);
        if (clip is null)
            return;
        var state = controller.GetOrCreate(clip.Name);
        state.Speed = animator.Speed;
        state.Loop = animator.Loop;
        if (animator.PlayAutomatically)
            controller.PlayFromStart(clip.Name);
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
            queue.AddSkinned(resource.Mesh, resource.Palette, pushConstants);
        }
        else
            queue.Add(resource.Mesh, pushConstants);
    }

    /// <summary>Groups renderer handles owned for one imported scene instance.</summary>
    /// <param name="Mesh">Indexed mesh handle.</param>
    /// <param name="Texture">Optional sampled texture handle.</param>
    /// <param name="Palette">Optional joint-palette handle.</param>
    private sealed class AssetMeshGpuResource
    {
        /// <summary>Gets the owning scene mesh instance.</summary>
        internal MeshInstance3D Instance { get; }

        /// <summary>Gets the indexed mesh handle.</summary>
        internal MeshHandle Mesh { get; }

        /// <summary>Gets the optional sampled texture handle.</summary>
        internal TextureHandle Texture { get; }

        /// <summary>Gets the optional joint-palette handle.</summary>
        internal SkinPaletteHandle Palette { get; }

        /// <summary>Gets the optional runtime animation controller.</summary>
        internal AnimationController? Animation { get; }

        /// <summary>Gets whether this renderer advances and disposes the controller.</summary>
        internal bool OwnsAnimation { get; }

        /// <summary>Gets or sets the pose revision most recently uploaded.</summary>
        internal ulong UploadedPoseRevision { get; set; }

        /// <summary>Creates renderer resources for one scene mesh instance.</summary>
        /// <param name="mesh">Indexed mesh handle.</param>
        /// <param name="texture">Optional sampled texture handle.</param>
        /// <param name="palette">Optional joint-palette handle.</param>
        /// <param name="animation">Optional runtime animation controller.</param>
        /// <param name="ownsAnimation">Whether this resource owns controller lifetime.</param>
        /// <param name="instance">Owning scene mesh instance.</param>
        internal AssetMeshGpuResource(MeshInstance3D instance, MeshHandle mesh,
            TextureHandle texture, SkinPaletteHandle palette,
            AnimationController? animation, bool ownsAnimation = false)
        {
            Instance = instance;
            Mesh = mesh;
            Texture = texture;
            Palette = palette;
            Animation = animation;
            OwnsAnimation = ownsAnimation;
        }
    }
}
