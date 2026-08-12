using System.Numerics;
using Engine.Assets;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>Coordinates the single Inspector model preview that can be active at a time.</summary>
public sealed class InspectorModelPreviewController : IDisposable
{
    private readonly IRenderer _renderer;
    private readonly Action _requestFrame;
    private ModelPreviewInspectorContent? _active;
    private SkinnedMeshResource? _lastPreviewModel;
    private string? _lastPreviewModelName;
    private bool _disposed;

    /// <summary>Gets whether the active preview needs continuous animation frames.</summary>
    public bool RequiresContinuousUpdates => _active?.HasAnimation == true;

    /// <summary>Creates an Inspector preview coordinator.</summary>
    /// <param name="renderer">Renderer owning preview resources.</param>
    /// <param name="requestFrame">Callback requesting one editor frame.</param>
    public InspectorModelPreviewController(IRenderer renderer, Action requestFrame)
    {
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _requestFrame = requestFrame ?? throw new ArgumentNullException(nameof(requestFrame));
    }

    /// <summary>Activates one preview and releases the previously active preview.</summary>
    /// <param name="content">Preview entering the Inspector visual tree.</param>
    internal void Activate(ModelPreviewInspectorContent content)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(content);
        if (ReferenceEquals(_active, content))
            return;
        _active?.Release(_renderer);
        _active = content;
        content.Acquire(_renderer);
        if (content.ModelToRemember is { } model &&
            !string.IsNullOrWhiteSpace(content.PreviewModelName))
        {
            _lastPreviewModel = model;
            _lastPreviewModelName = content.PreviewModelName;
        }
        _requestFrame();
    }

    /// <summary>Attempts to bind standalone animation clips to the last compatible model.</summary>
    /// <param name="animation">Standalone animation and source skeleton.</param>
    /// <param name="mesh">Preview mesh using remembered geometry when compatible.</param>
    /// <param name="modelName">Remembered model display name.</param>
    /// <returns>True when a remembered model accepted the animation skeleton.</returns>
    internal bool TryCreateRememberedModelPreview(
        SkeletalAnimationResource animation,
        out ModelPreviewMesh? mesh,
        out string? modelName)
    {
        ArgumentNullException.ThrowIfNull(animation);
        mesh = null;
        modelName = null;
        if (_lastPreviewModel is null)
            return false;
        try
        {
            var clips = animation.BindTo(_lastPreviewModel.Skeleton,
                AnimationRetargetMode.Auto, _lastPreviewModel.MeshNodeTransform);
            mesh = new ModelPreviewMesh(new SkinnedMeshResource(
                _lastPreviewModel.Mesh,
                _lastPreviewModel.Influences,
                _lastPreviewModel.Skeleton,
                clips,
                _lastPreviewModel.MeshNodeTransform));
            modelName = _lastPreviewModelName;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Deactivates a preview leaving the Inspector visual tree.</summary>
    /// <param name="content">Preview leaving the Inspector visual tree.</param>
    internal void Deactivate(ModelPreviewInspectorContent content)
    {
        if (!ReferenceEquals(_active, content))
            return;
        content.Release(_renderer);
        _active = null;
        _requestFrame();
    }

    /// <summary>Synchronizes preview presentation with the latest Inspector layout.</summary>
    public void Synchronize()
    {
        if (_disposed || _active is null)
            return;
        if (_active.Synchronize(_renderer))
            _requestFrame();
    }

    /// <summary>Advances and renders the active preview when needed.</summary>
    /// <param name="deltaTime">Elapsed editor-frame seconds.</param>
    public void Update(double deltaTime)
    {
        if (_disposed || _active is null)
            return;
        _active.Synchronize(_renderer);
        _active.UpdateAndRender(_renderer, deltaTime);
    }

    /// <summary>Releases the active preview and its renderer resources.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _active?.Release(_renderer);
        _active = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>Provides model previews for physical GLB assets and imported mesh resources.</summary>
public sealed class ModelPreviewInspectorProvider : IInspectorProvider
{
    private const string StaticMeshContentType = "nico/static-mesh";
    private const string SkinnedMeshContentType = "nico/skinned-mesh";
    private const string SkeletalAnimationContentType = "nico/skeletal-animation";
    private readonly AssetDatabase _database;
    private readonly AssetImportPipeline _pipeline;
    private readonly InspectorModelPreviewController _controller;
    private readonly UITheme _theme;

    /// <summary>Creates a model-preview Inspector provider.</summary>
    /// <param name="database">Project asset identity database.</param>
    /// <param name="pipeline">Published artifact pipeline.</param>
    /// <param name="controller">Shared preview lifecycle coordinator.</param>
    /// <param name="theme">Theme supplying preview visuals.</param>
    public ModelPreviewInspectorProvider(
        AssetDatabase database,
        AssetImportPipeline pipeline,
        InspectorModelPreviewController controller,
        UITheme? theme = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _theme = theme ?? UITheme.Dark;
    }

    /// <inheritdoc/>
    public bool TryCreate(object target, out InspectorDocument? document)
    {
        AssetMetadataRecord? record;
        string? artifactKey = null;
        string displayName;
        if (target is ImportedSubAssetNode imported && IsPreviewable(imported.ContentType))
        {
            record = _database.Find(imported.Reference.Asset);
            artifactKey = imported.Reference.SubAsset;
            displayName = imported.Name;
        }
        else if (target is FileSystemNode { IsDirectory: false } file &&
                 string.Equals(Path.GetExtension(file.FullPath), ".glb",
                     StringComparison.OrdinalIgnoreCase))
        {
            record = _database.FindByPath(file.FullPath);
            displayName = file.Name;
        }
        else
        {
            document = null;
            return false;
        }

        if (record is null)
        {
            document = null;
            return false;
        }
        try
        {
            var outcome = _pipeline.TryGetLatestPublished(record, "editor") ??
                _pipeline.Import(record, "editor");
            if (!outcome.Succeeded || outcome.ArtifactDirectory is null)
            {
                document = CreateError(displayName, "The model import did not produce a preview.");
                return true;
            }
            var meshes = LoadMeshes(outcome, artifactKey);
            if (meshes.Count > 0)
            {
                document = new InspectorDocument("Model", displayName,
                    new ModelPreviewInspectorContent(meshes, _controller, _theme,
                        displayName, rememberPreviewModel: true));
                return true;
            }
            var animation = LoadAnimation(outcome, artifactKey);
            if (animation is null)
            {
                document = CreateError(displayName,
                    "This asset contains no model or skeletal animation to preview.");
                return true;
            }
            ModelPreviewMesh animationMesh;
            string previewModelName;
            if (_controller.TryCreateRememberedModelPreview(
                    animation, out var remembered, out var rememberedName))
            {
                animationMesh = remembered!;
                previewModelName = rememberedName ?? "Previous model";
            }
            else
            {
                animationMesh = new ModelPreviewMesh(
                    SkeletonPreviewMeshBuilder.Build(animation));
                previewModelName = "Procedural skeleton";
            }
            document = new InspectorDocument("Animation", displayName,
                new ModelPreviewInspectorContent([animationMesh], _controller, _theme,
                    previewModelName));
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            document = CreateError(displayName, exception.Message);
            return true;
        }
    }

    /// <summary>Loads previewable mesh artifacts without duplicating static model batches.</summary>
    /// <param name="outcome">Published model import.</param>
    /// <param name="artifactKey">Exact selected artifact, or null for the complete model.</param>
    /// <returns>Decoded preview mesh resources.</returns>
    private static List<ModelPreviewMesh> LoadMeshes(
        AssetImportOutcome outcome,
        string? artifactKey)
    {
        var result = new List<ModelPreviewMesh>();
        var artifacts = outcome.Artifacts;
        var hasModelBatches = false;
        if (artifactKey is null)
        {
            for (var index = 0; index < artifacts.Count; index++)
            {
                if (artifacts[index].Key.StartsWith("model-batch/", StringComparison.Ordinal))
                {
                    hasModelBatches = true;
                    break;
                }
            }
        }
        for (var index = 0; index < artifacts.Count; index++)
        {
            var artifact = artifacts[index];
            if (artifactKey is not null && artifact.Key != artifactKey)
                continue;
            if (!IsMesh(artifact.ContentType))
                continue;
            if (artifactKey is null && artifact.ContentType == StaticMeshContentType &&
                hasModelBatches && !artifact.Key.StartsWith("model-batch/", StringComparison.Ordinal))
            {
                continue;
            }
            var path = Path.Combine(outcome.ArtifactDirectory!, artifact.RelativePath);
            using var stream = File.OpenRead(path);
            result.Add(artifact.ContentType == SkinnedMeshContentType
                ? new ModelPreviewMesh(SkinnedMeshResource.Load(stream))
                : new ModelPreviewMesh(StaticMeshResource.Load(stream)));
        }
        return result;
    }

    /// <summary>Loads the selected or first standalone skeletal-animation artifact.</summary>
    /// <param name="outcome">Published source import.</param>
    /// <param name="artifactKey">Exact selected artifact, or null for the first clip.</param>
    /// <returns>Decoded animation resource, or null when the import has none.</returns>
    private static SkeletalAnimationResource? LoadAnimation(
        AssetImportOutcome outcome,
        string? artifactKey)
    {
        var artifacts = outcome.Artifacts;
        for (var index = 0; index < artifacts.Count; index++)
        {
            var artifact = artifacts[index];
            if (artifactKey is not null && artifact.Key != artifactKey)
                continue;
            if (artifact.ContentType != SkeletalAnimationContentType)
                continue;
            var path = Path.Combine(outcome.ArtifactDirectory!, artifact.RelativePath);
            using var stream = File.OpenRead(path);
            return SkeletalAnimationResource.Load(stream);
        }
        return null;
    }

    /// <summary>Checks whether a published content type is previewable mesh geometry.</summary>
    /// <param name="contentType">Published artifact content type.</param>
    /// <returns>True for static or skinned mesh resources.</returns>
    private static bool IsMesh(string contentType) =>
        contentType is StaticMeshContentType or SkinnedMeshContentType;

    /// <summary>Checks whether a published artifact can produce a model preview.</summary>
    /// <param name="contentType">Published artifact content type.</param>
    /// <returns>True for model geometry or standalone skeletal animation.</returns>
    private static bool IsPreviewable(string contentType) =>
        IsMesh(contentType) || contentType == SkeletalAnimationContentType;

    /// <summary>Creates a stable Inspector error document.</summary>
    /// <param name="displayName">Selected asset name.</param>
    /// <param name="message">Preview failure detail.</param>
    /// <returns>Error Inspector document.</returns>
    private static InspectorDocument CreateError(string displayName, string message) =>
        new("Model", displayName, new PropertyInspectorContent([
            new InspectorProperty("Preview", message)]));
}

/// <summary>Stores one decoded static or skinned mesh used by an Inspector preview.</summary>
public sealed class ModelPreviewMesh
{
    /// <summary>Gets static mesh data shared by both preview paths.</summary>
    public StaticMeshResource Mesh { get; }

    /// <summary>Gets optional skin and embedded animation data.</summary>
    public SkinnedMeshResource? SkinnedMesh { get; }

    /// <summary>Gets the model transform applied after optional skin deformation.</summary>
    public Matrix4x4 ModelTransform => SkinnedMesh?.MeshNodeTransform ?? Matrix4x4.Identity;

    /// <summary>Creates a static preview mesh.</summary>
    /// <param name="mesh">Decoded static geometry.</param>
    public ModelPreviewMesh(StaticMeshResource mesh)
    {
        Mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
    }

    /// <summary>Creates a skinned preview mesh.</summary>
    /// <param name="mesh">Decoded skinned geometry and clips.</param>
    public ModelPreviewMesh(SkinnedMeshResource mesh)
    {
        SkinnedMesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
        Mesh = mesh.Mesh;
    }
}

/// <summary>Builds a minimal rigid-bone proxy for animation-only skeletons.</summary>
internal static class SkeletonPreviewMeshBuilder
{
    private static readonly uint[] BoxIndices =
    [
        0, 2, 1, 0, 3, 2,
        4, 5, 6, 4, 6, 7,
        0, 1, 5, 0, 5, 4,
        3, 7, 6, 3, 6, 2,
        1, 2, 6, 1, 6, 5,
        0, 4, 7, 0, 7, 3
    ];

    /// <summary>Creates bone prisms driven by the standalone animation skeleton.</summary>
    /// <param name="animation">Standalone animation supplying skeleton and clips.</param>
    /// <returns>Renderable skinned proxy using the source skeleton directly.</returns>
    internal static SkinnedMeshResource Build(SkeletalAnimationResource animation)
    {
        ArgumentNullException.ThrowIfNull(animation);
        var skeleton = animation.Skeleton;
        var bindWorld = BuildBindWorldTransforms(
            skeleton, animation.SkeletonTransform);
        var thickness = CalculateThickness(bindWorld);
        var vertices = new List<ModelVertex>(Math.Max(8, skeleton.JointCount * 8));
        var influences = new List<SkinInfluence>(vertices.Capacity);
        var indices = new List<uint>(Math.Max(36, skeleton.JointCount * 36));
        var joints = skeleton.Joints;
        for (var jointIndex = 0; jointIndex < joints.Count; jointIndex++)
        {
            var parentIndex = joints[jointIndex].ParentIndex;
            if (parentIndex < 0)
            {
                AppendJointBox(vertices, influences, indices,
                    bindWorld[jointIndex].Translation, thickness, jointIndex);
                continue;
            }
            var start = bindWorld[parentIndex].Translation;
            var end = bindWorld[jointIndex].Translation;
            if (Vector3.DistanceSquared(start, end) <= float.Epsilon)
            {
                AppendJointBox(vertices, influences, indices,
                    end, thickness, jointIndex);
            }
            else
            {
                AppendBoneBox(vertices, influences, indices,
                    start, end, thickness, parentIndex, jointIndex);
            }
        }
        if (vertices.Count == 0)
        {
            throw new InvalidDataException(
                "A skeletal animation preview requires at least one joint.");
        }
        var vertexArray = new ModelVertex[vertices.Count];
        vertices.CopyTo(vertexArray);
        var influenceArray = new SkinInfluence[influences.Count];
        influences.CopyTo(influenceArray);
        var indexArray = new uint[indices.Count];
        indices.CopyTo(indexArray);
        var mesh = new StaticMeshResource(vertexArray, indexArray,
            [new Submesh(0u, checked((uint)indexArray.Length), -1)]);
        var clips = new AnimationClipResource[animation.Animations.Count];
        for (var index = 0; index < clips.Length; index++)
            clips[index] = animation.Animations[index];
        return new SkinnedMeshResource(
            mesh, influenceArray, skeleton, clips, animation.SkeletonTransform);
    }

    /// <summary>Composes bind-pose joint transforms into rendered preview space.</summary>
    /// <param name="skeleton">Source animation skeleton.</param>
    /// <param name="skeletonTransform">Skeleton-space to rendered-space transform.</param>
    /// <returns>One rendered-space bind transform per joint.</returns>
    private static Matrix4x4[] BuildBindWorldTransforms(
        SkeletonResource skeleton, Matrix4x4 skeletonTransform)
    {
        var result = new Matrix4x4[skeleton.JointCount];
        var joints = skeleton.Joints;
        for (var index = 0; index < joints.Count; index++)
        {
            var local = joints[index].BindTransform.ToMatrix();
            result[index] = joints[index].ParentIndex < 0
                ? local : local * result[joints[index].ParentIndex];
        }
        for (var index = 0; index < result.Length; index++)
            result[index] *= skeletonTransform;
        return result;
    }

    /// <summary>Chooses readable bone thickness from bind-pose skeleton extent.</summary>
    /// <param name="bindWorld">Mesh-space bind transforms.</param>
    /// <returns>Rendered-space proxy half-thickness.</returns>
    private static float CalculateThickness(Matrix4x4[] bindWorld)
    {
        if (bindWorld.Length == 0)
            return 0.01f;
        var minimum = bindWorld[0].Translation;
        var maximum = minimum;
        for (var index = 1; index < bindWorld.Length; index++)
        {
            minimum = Vector3.Min(minimum, bindWorld[index].Translation);
            maximum = Vector3.Max(maximum, bindWorld[index].Translation);
        }
        return MathF.Max(0.005f, (maximum - minimum).Length() * 0.008f);
    }

    /// <summary>Adds one joint-centered cube rigidly weighted to that joint.</summary>
    /// <param name="vertices">Destination vertices.</param>
    /// <param name="influences">Destination skin influences.</param>
    /// <param name="indices">Destination triangle indices.</param>
    /// <param name="center">Bind-pose joint position.</param>
    /// <param name="halfThickness">Cube half extent.</param>
    /// <param name="jointIndex">Owning joint index.</param>
    private static void AppendJointBox(
        List<ModelVertex> vertices,
        List<SkinInfluence> influences,
        List<uint> indices,
        Vector3 center,
        float halfThickness,
        int jointIndex)
    {
        var side = new Vector3(halfThickness, 0f, 0f);
        var up = new Vector3(0f, halfThickness, 0f);
        var depth = new Vector3(0f, 0f, halfThickness);
        Span<Vector3> corners = stackalloc Vector3[8]
        {
            center - side - up - depth,
            center + side - up - depth,
            center + side + up - depth,
            center - side + up - depth,
            center - side - up + depth,
            center + side - up + depth,
            center + side + up + depth,
            center - side + up + depth
        };
        AppendBox(vertices, influences, indices, corners,
            jointIndex, jointIndex);
    }

    /// <summary>Adds a bind-pose bone prism weighted to its parent and child endpoints.</summary>
    /// <param name="vertices">Destination vertices.</param>
    /// <param name="influences">Destination skin influences.</param>
    /// <param name="indices">Destination triangle indices.</param>
    /// <param name="start">Parent bind-pose position.</param>
    /// <param name="end">Child bind-pose position.</param>
    /// <param name="halfThickness">Prism half-thickness.</param>
    /// <param name="parentIndex">Parent joint index.</param>
    /// <param name="jointIndex">Child joint index.</param>
    private static void AppendBoneBox(
        List<ModelVertex> vertices,
        List<SkinInfluence> influences,
        List<uint> indices,
        Vector3 start,
        Vector3 end,
        float halfThickness,
        int parentIndex,
        int jointIndex)
    {
        var direction = Vector3.Normalize(end - start);
        var reference = MathF.Abs(Vector3.Dot(direction, Vector3.UnitY)) < 0.95f
            ? Vector3.UnitY : Vector3.UnitX;
        var side = Vector3.Normalize(Vector3.Cross(direction, reference)) * halfThickness;
        var up = Vector3.Normalize(Vector3.Cross(side, direction)) * halfThickness;
        Span<Vector3> corners = stackalloc Vector3[8]
        {
            start - side - up,
            start + side - up,
            start + side + up,
            start - side + up,
            end - side - up,
            end + side - up,
            end + side + up,
            end - side + up
        };
        AppendBox(vertices, influences, indices, corners,
            parentIndex, jointIndex);
    }

    /// <summary>Appends common box geometry and rigid endpoint influences.</summary>
    /// <param name="vertices">Destination vertices.</param>
    /// <param name="influences">Destination skin influences.</param>
    /// <param name="indices">Destination triangle indices.</param>
    /// <param name="corners">Eight box corners with the second cap at indices four through seven.</param>
    /// <param name="startJoint">Joint influencing the first cap.</param>
    /// <param name="endJoint">Joint influencing the second cap.</param>
    private static void AppendBox(
        List<ModelVertex> vertices,
        List<SkinInfluence> influences,
        List<uint> indices,
        ReadOnlySpan<Vector3> corners,
        int startJoint,
        int endJoint)
    {
        var firstVertex = checked((uint)vertices.Count);
        var center = Vector3.Zero;
        for (var index = 0; index < corners.Length; index++)
            center += corners[index];
        center /= corners.Length;
        for (var index = 0; index < corners.Length; index++)
        {
            var normal = Vector3.Normalize(corners[index] - center);
            vertices.Add(new ModelVertex(corners[index], normal, Vector2.Zero,
                new Vector4(1f, 0f, 0f, 1f), Vector4.One));
            var joint = index < 4 ? startJoint : endJoint;
            influences.Add(new SkinInfluence(
                checked((uint)joint), 0u, 0u, 0u, Vector4.UnitX));
        }
        for (var index = 0; index < BoxIndices.Length; index++)
            indices.Add(firstVertex + BoxIndices[index]);
    }
}

/// <summary>Hosts and renders a lifecycle-bound model preview inside the Inspector.</summary>
public sealed class ModelPreviewInspectorContent : Panel, IInspectorContentLifecycle
{
    private const float PreviewHeight = 210f;
    private readonly IReadOnlyList<ModelPreviewMesh> _meshes;
    private readonly InspectorModelPreviewController _controller;
    private readonly bool _rememberPreviewModel;
    private readonly ViewportPanel _viewport;
    private readonly ViewportPresentationTracker _presentation;
    private readonly PerspectiveCamera _camera = new();
    private readonly RenderQueue _queue = new();
    private readonly List<PreviewGpuMesh> _gpuMeshes = [];
    private MeshHandle _axisMesh;
    private Vector3 _baseCameraPosition;
    private Vector3 _baseCameraTarget;
    private Vector3 _initialFollowAnchor;
    private float _frameRadius;
    private bool _followAnchorValid;
    private bool _dirty;

    /// <summary>Gets whether any preview mesh has an embedded animation clip.</summary>
    public bool HasAnimation { get; }

    /// <summary>Gets the model name shown for an animation preview.</summary>
    public string? PreviewModelName { get; }

    /// <summary>Gets the first skinned model retained for subsequent animation previews.</summary>
    internal SkinnedMeshResource? ModelToRemember
    {
        get
        {
            if (!_rememberPreviewModel)
                return null;
            for (var index = 0; index < _meshes.Count; index++)
            {
                if (_meshes[index].SkinnedMesh is { } skinned)
                    return skinned;
            }
            return null;
        }
    }

    /// <summary>Creates model preview Inspector content.</summary>
    /// <param name="meshes">Decoded meshes displayed together.</param>
    /// <param name="controller">Shared preview lifecycle coordinator.</param>
    /// <param name="theme">Theme supplying preview visuals.</param>
    /// <param name="previewModelName">Model or procedural proxy used for the preview.</param>
    /// <param name="rememberPreviewModel">Whether this model becomes the subsequent animation rig.</param>
    public ModelPreviewInspectorContent(
        IReadOnlyList<ModelPreviewMesh> meshes,
        InspectorModelPreviewController controller,
        UITheme? theme = null,
        string? previewModelName = null,
        bool rememberPreviewModel = false)
        : base(null, 0f, PreviewHeight +
            (string.IsNullOrWhiteSpace(previewModelName) ? 38f : 64f), theme)
    {
        ArgumentNullException.ThrowIfNull(meshes);
        if (meshes.Count == 0)
            throw new ArgumentException("A model preview requires at least one mesh.", nameof(meshes));
        _meshes = meshes;
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        PreviewModelName = previewModelName;
        _rememberPreviewModel = rememberPreviewModel;
        var resolvedTheme = theme ?? UITheme.Dark;
        PaintBackground = false;
        _viewport = new ViewportPanel(0f, PreviewHeight, resolvedTheme.Viewport)
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Camera = _camera
        };
        _presentation = new ViewportPresentationTracker(_viewport);
        AddChild(_viewport);
        HasAnimation = FindFirstAnimationName() is not null;
        AddChild(new Label(CreateDescription(), 0f, 28f)
        {
            ForegroundColor = resolvedTheme.TextSecondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0f, PreviewHeight + 8f, 0f, 0f)
        });
        if (!string.IsNullOrWhiteSpace(PreviewModelName))
        {
            AddChild(new Label($"Preview model: {PreviewModelName}", 0f, 24f)
            {
                ForegroundColor = resolvedTheme.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0f, PreviewHeight + 34f, 0f, 0f)
            });
        }
    }

    /// <inheritdoc/>
    public void Activate() => _controller.Activate(this);

    /// <inheritdoc/>
    public void Deactivate() => _controller.Deactivate(this);

    /// <summary>Creates renderer resources for this preview.</summary>
    /// <param name="renderer">Renderer owning the resources.</param>
    internal void Acquire(IRenderer renderer)
    {
        if (_viewport.RenderView.IsValid)
            return;
        var width = MathF.Max(1f, _viewport.Width);
        var height = MathF.Max(1f, _viewport.Height);
        _viewport.RenderView = renderer.CreateRenderView(width, height);
        renderer.SetViewportClearColor(_viewport.RenderView, 0.035f, 0.04f, 0.05f, 1f);
        var material = new ResolvedStandardMaterial
        {
            BaseColor = new Vector4(0.82f, 0.84f, 0.88f, 1f),
            Metallic = 0f,
            Roughness = 0.7f,
            DoubleSided = true
        };
        try
        {
            for (var index = 0; index < _meshes.Count; index++)
            {
                var source = _meshes[index];
                if (source.SkinnedMesh is { } skinned)
                {
                    var handles = renderer.CreateSkinnedMesh(skinned, material);
                    var animation = new AnimationController(skinned);
                    if (skinned.Animations.Count > 0)
                        animation.Play(skinned.Animations[0].Name);
                    renderer.UpdateSkinPalette(handles.Palette,
                        animation.Pose.SkinMatrices);
                    _gpuMeshes.Add(new PreviewGpuMesh(
                        handles.Mesh, handles.Palette, source.ModelTransform, animation));
                }
                else
                {
                    var handle = renderer.CreateStaticMesh(source.Mesh, material);
                    _gpuMeshes.Add(new PreviewGpuMesh(
                        handle, default, source.ModelTransform, null));
                }
            }
            FrameCamera();
            CreateAxisMesh(renderer);
            _dirty = true;
        }
        catch
        {
            Release(renderer);
            throw;
        }
    }

    /// <summary>Releases all preview renderer resources.</summary>
    /// <param name="renderer">Renderer owning the resources.</param>
    internal void Release(IRenderer renderer)
    {
        for (var index = 0; index < _gpuMeshes.Count; index++)
        {
            var resource = _gpuMeshes[index];
            resource.Animation?.Dispose();
            if (resource.Palette.IsValid)
                renderer.DestroySkinPalette(resource.Palette);
            if (resource.Mesh.IsValid)
                renderer.DestroyMesh(resource.Mesh);
        }
        _gpuMeshes.Clear();
        if (_axisMesh.IsValid)
        {
            renderer.DestroyMesh(_axisMesh);
            _axisMesh = default;
        }
        if (_viewport.RenderView.IsValid)
        {
            renderer.DestroyRenderView(_viewport.RenderView);
            _viewport.RenderView = default;
        }
        _presentation.Reset();
        _followAnchorValid = false;
        _dirty = false;
    }

    /// <summary>Synchronizes the retained viewport quad and render-target dimensions.</summary>
    /// <param name="renderer">Renderer owning the preview.</param>
    /// <returns>True when presentation geometry or target dimensions changed.</returns>
    internal bool Synchronize(IRenderer renderer)
    {
        if (!_viewport.RenderView.IsValid)
            return false;
        var changed = _presentation.Synchronize(renderer, out var resized);
        if (resized)
        {
            FrameCamera();
            _dirty = true;
        }
        return changed || resized;
    }

    /// <summary>Advances embedded animation and submits the preview render queue.</summary>
    /// <param name="renderer">Renderer receiving preview work.</param>
    /// <param name="deltaTime">Elapsed editor-frame seconds.</param>
    internal void UpdateAndRender(IRenderer renderer, double deltaTime)
    {
        if (!_viewport.RenderView.IsValid || !_viewport.IsEffectivelyVisible)
            return;
        var animated = false;
        for (var index = 0; index < _gpuMeshes.Count; index++)
        {
            var resource = _gpuMeshes[index];
            if (resource.Animation is null || !resource.Animation.RequiresUpdate)
                continue;
            resource.Animation.Advance(Math.Max(0d, deltaTime));
            resource.Animation.DispatchEvents();
            renderer.UpdateSkinPalette(resource.Palette,
                resource.Animation.Pose.SkinMatrices);
            animated = true;
        }
        if (animated)
            FollowAnimatedModel();
        if (!_dirty && !animated)
            return;
        _queue.Clear();
        _queue.Lighting = new SceneLighting(
            Vector3.Normalize(new Vector3(0.7f, 1f, 0.8f)),
            Vector3.One, 1.15f, 0.3f);
        if (_axisMesh.IsValid)
            _queue.Add(_axisMesh, _camera.GetPushConstants(Matrix4x4.Identity));
        for (var index = 0; index < _gpuMeshes.Count; index++)
        {
            var resource = _gpuMeshes[index];
            var pushConstants = _camera.GetPushConstants(resource.ModelTransform);
            if (resource.Palette.IsValid)
                _queue.AddSkinned(resource.Mesh, resource.Palette, pushConstants);
            else
                _queue.Add(resource.Mesh, pushConstants);
        }
        BasicForwardRenderPipeline.Instance.Render(renderer, _viewport.RenderView, _queue);
        _dirty = false;
    }

    /// <summary>Frames every preview mesh within the perspective camera.</summary>
    private void FrameCamera()
    {
        var minimum = new Vector3(float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity);
        for (var index = 0; index < _meshes.Count; index++)
        {
            var source = _meshes[index];
            var animation = index < _gpuMeshes.Count ? _gpuMeshes[index].Animation : null;
            if (source.SkinnedMesh is { } skinned && animation is not null)
            {
                IncludeSkinnedBounds(ref minimum, ref maximum,
                    skinned, animation.Pose.SkinMatrices, source.ModelTransform);
            }
            else
            {
                IncludeBounds(ref minimum, ref maximum,
                    source.Mesh.BoundsMinimum, source.Mesh.BoundsMaximum, source.ModelTransform);
            }
        }
        if (!IsFinite(minimum) || !IsFinite(maximum))
        {
            minimum = new Vector3(-0.5f);
            maximum = new Vector3(0.5f);
        }
        var center = (minimum + maximum) * 0.5f;
        var extent = maximum - minimum;
        var radius = MathF.Max(extent.Length() * 0.5f, 0.1f);
        var cameraBack = Vector3.Normalize(new Vector3(1f, 0.55f, 1f));
        var forward = -cameraBack;
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Normalize(Vector3.Cross(right, forward));
        var width = MathF.Max(1f, _viewport.Width);
        var height = MathF.Max(1f, _viewport.Height);
        var aspect = width / height;
        var verticalTangent = MathF.Tan(_camera.Fov * 0.5f);
        var horizontalTangent = verticalTangent * aspect;
        var distance = 0f;
        for (var corner = 0; corner < 8; corner++)
        {
            var point = new Vector3(
                (corner & 1) == 0 ? minimum.X : maximum.X,
                (corner & 2) == 0 ? minimum.Y : maximum.Y,
                (corner & 4) == 0 ? minimum.Z : maximum.Z);
            var offset = point - center;
            var depthOffset = Vector3.Dot(offset, cameraBack);
            var horizontalDistance = depthOffset +
                MathF.Abs(Vector3.Dot(offset, right)) / horizontalTangent;
            var verticalDistance = depthOffset +
                MathF.Abs(Vector3.Dot(offset, up)) / verticalTangent;
            distance = MathF.Max(distance,
                MathF.Max(horizontalDistance, verticalDistance));
        }
        distance = MathF.Max(distance * 1.12f, radius * 1.1f);
        _camera.Near = MathF.Max(0.001f, distance - radius * 1.05f);
        _camera.Far = MathF.Max(_camera.Near + 1f, distance + radius * 1.25f);
        _camera.Position = center + cameraBack * distance;
        _camera.LookAt(center);
        _camera.UpdateViewport(width, height);
        _frameRadius = radius;
        _baseCameraPosition = _camera.Position;
        _baseCameraTarget = center;
        _followAnchorValid = TryGetFollowAnchor(out _initialFollowAnchor);
    }

    /// <summary>Creates RGB world-origin axes scaled to the initially framed model.</summary>
    /// <param name="renderer">Renderer owning the retained axis mesh.</param>
    private void CreateAxisMesh(IRenderer renderer)
    {
        if (_axisMesh.IsValid)
            return;
        var extent = MathF.Max(0.25f, _frameRadius * 1.5f);
        var thickness = MathF.Max(0.0025f, _frameRadius * 0.008f);
        var axes = new OriginAxesMesh(extent, thickness);
        _axisMesh = renderer.CreateMesh(new MeshDescription(axes.Vertices));
    }

    /// <summary>Moves the camera by current root-motion displacement without changing framing.</summary>
    private void FollowAnimatedModel()
    {
        if (!_followAnchorValid || !TryGetFollowAnchor(out var currentAnchor))
            return;
        var movement = currentAnchor - _initialFollowAnchor;
        _camera.Position = _baseCameraPosition + movement;
        _camera.LookAt(_baseCameraTarget + movement);
    }

    /// <summary>Finds the average preview-space origin of evaluated skeleton roots.</summary>
    /// <param name="anchor">Average root position when an evaluated skeleton is available.</param>
    /// <returns>True when at least one skeleton root contributed.</returns>
    private bool TryGetFollowAnchor(out Vector3 anchor)
    {
        anchor = Vector3.Zero;
        var count = 0;
        for (var meshIndex = 0; meshIndex < _meshes.Count && meshIndex < _gpuMeshes.Count;
             meshIndex++)
        {
            var source = _meshes[meshIndex];
            var animation = _gpuMeshes[meshIndex].Animation;
            if (source.SkinnedMesh is not { } skinned || animation is null)
                continue;
            var roots = skinned.Skeleton.Joints;
            var worldTransforms = animation.Pose.WorldTransforms;
            for (var jointIndex = 0; jointIndex < roots.Count; jointIndex++)
            {
                if (roots[jointIndex].ParentIndex >= 0)
                    continue;
                var transform = worldTransforms[jointIndex] * source.ModelTransform;
                anchor += Vector3.Transform(Vector3.Zero, transform);
                count++;
            }
        }
        if (count == 0)
            return false;
        anchor /= count;
        return IsFinite(anchor);
    }

    /// <summary>Expands world bounds by the eight transformed corners of local bounds.</summary>
    /// <param name="minimum">Accumulated world minimum.</param>
    /// <param name="maximum">Accumulated world maximum.</param>
    /// <param name="localMinimum">Mesh-local minimum.</param>
    /// <param name="localMaximum">Mesh-local maximum.</param>
    /// <param name="transform">Mesh-to-preview transform.</param>
    private static void IncludeBounds(
        ref Vector3 minimum,
        ref Vector3 maximum,
        Vector3 localMinimum,
        Vector3 localMaximum,
        Matrix4x4 transform)
    {
        for (var corner = 0; corner < 8; corner++)
        {
            var local = new Vector3(
                (corner & 1) == 0 ? localMinimum.X : localMaximum.X,
                (corner & 2) == 0 ? localMinimum.Y : localMaximum.Y,
                (corner & 4) == 0 ? localMinimum.Z : localMaximum.Z);
            var world = Vector3.Transform(local, transform);
            minimum = Vector3.Min(minimum, world);
            maximum = Vector3.Max(maximum, world);
        }
    }

    /// <summary>Expands bounds from vertices evaluated through the current skin pose.</summary>
    /// <param name="minimum">Accumulated preview-space minimum.</param>
    /// <param name="maximum">Accumulated preview-space maximum.</param>
    /// <param name="mesh">Skinned mesh supplying vertices and influences.</param>
    /// <param name="skinMatrices">Current mesh-space joint matrices.</param>
    /// <param name="modelTransform">Transform applied after skin deformation.</param>
    private static void IncludeSkinnedBounds(
        ref Vector3 minimum,
        ref Vector3 maximum,
        SkinnedMeshResource mesh,
        ReadOnlySpan<Matrix4x4> skinMatrices,
        Matrix4x4 modelTransform)
    {
        var vertices = mesh.Mesh.Vertices;
        var influences = mesh.Influences;
        for (var index = 0; index < vertices.Length; index++)
        {
            var position = vertices[index].Position;
            var influence = influences[index];
            var skinned =
                Vector3.Transform(position, skinMatrices[(int)influence.Joint0]) *
                    influence.Weights.X +
                Vector3.Transform(position, skinMatrices[(int)influence.Joint1]) *
                    influence.Weights.Y +
                Vector3.Transform(position, skinMatrices[(int)influence.Joint2]) *
                    influence.Weights.Z +
                Vector3.Transform(position, skinMatrices[(int)influence.Joint3]) *
                    influence.Weights.W;
            var world = Vector3.Transform(skinned, modelTransform);
            minimum = Vector3.Min(minimum, world);
            maximum = Vector3.Max(maximum, world);
        }
    }

    /// <summary>Checks whether all vector components are finite.</summary>
    /// <param name="value">Vector to validate.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>Builds a compact geometry and animation summary.</summary>
    /// <returns>Inspector caption beneath the viewport.</returns>
    private string CreateDescription()
    {
        var animation = FindFirstAnimationName();
        return animation is null
            ? $"{_meshes.Count} mesh{(_meshes.Count == 1 ? string.Empty : "es")} · Static"
            : $"{_meshes.Count} mesh{(_meshes.Count == 1 ? string.Empty : "es")} · Playing {animation}";
    }

    /// <summary>Finds the first embedded animation name without allocating an iterator.</summary>
    /// <returns>First clip name, or null when all meshes are static.</returns>
    private string? FindFirstAnimationName()
    {
        for (var index = 0; index < _meshes.Count; index++)
        {
            var animations = _meshes[index].SkinnedMesh?.Animations;
            if (animations is { Count: > 0 })
                return animations[0].Name;
        }
        return null;
    }

    /// <summary>Stores renderer handles and optional playback for one preview mesh.</summary>
    /// <param name="Mesh">Renderer-owned geometry.</param>
    /// <param name="Palette">Optional renderer-owned skin palette.</param>
    /// <param name="ModelTransform">Mesh-to-preview transform.</param>
    /// <param name="Animation">Optional embedded animation playback.</param>
    private sealed record PreviewGpuMesh(
        MeshHandle Mesh,
        SkinPaletteHandle Palette,
        Matrix4x4 ModelTransform,
        AnimationController? Animation);
}
