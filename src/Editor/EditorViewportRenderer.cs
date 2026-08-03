using System.Numerics;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>
/// Builds and submits the Scene and Game viewport render queues for each frame.
/// </summary>
public sealed class EditorViewportRenderer : IDisposable
{
    private readonly IRenderer _renderer;
    private readonly RenderViewHandle _sceneViewport;
    private readonly RenderViewHandle _gameViewport;
    private readonly PerspectiveCamera _sceneCamera;
    private ICamera _gameCamera;
    private IReadOnlyList<MeshInstance3D> _sceneObjects;
    private IReadOnlyList<MeshInstance3D> _gameObjects;
    private readonly SceneSelectionController _selection;
    private readonly OriginAxesMesh _originAxes = new();
    private readonly RenderQueue _sceneQueue = new();
    private readonly RenderQueue _gameQueue = new();
    private readonly Dictionary<Mesh, MeshHandle> _meshHandles = [];
    private bool _disposed;

    /// <summary>
    /// Creates the editor viewport renderer.
    /// </summary>
    /// <param name="renderer">Rendering service.</param>
    /// <param name="sceneViewport">Scene render view.</param>
    /// <param name="gameViewport">Game render view.</param>
    /// <param name="sceneCamera">Scene camera.</param>
    /// <param name="gameCamera">Scene-owned camera used by the Game viewport.</param>
    /// <param name="sceneObjects">Objects rendered in the Scene viewport.</param>
    /// <param name="selection">Selection and gizmo controller.</param>
    public EditorViewportRenderer(
        IRenderer renderer,
        RenderViewHandle sceneViewport,
        RenderViewHandle gameViewport,
        PerspectiveCamera sceneCamera,
        ICamera gameCamera,
        IReadOnlyList<MeshInstance3D> sceneObjects,
        SceneSelectionController selection)
    {
        _renderer = renderer;
        _sceneViewport = sceneViewport;
        _gameViewport = gameViewport;
        _sceneCamera = sceneCamera;
        _gameCamera = gameCamera;
        _sceneObjects = sceneObjects;
        _gameObjects = sceneObjects;
        _selection = selection;
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

    /// <summary>Releases retained renderer resources created by this viewport renderer.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var handle in _meshHandles.Values)
            _renderer.DestroyMesh(handle);
        _meshHandles.Clear();
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
        _sceneQueue.Clear();
        _gameQueue.Clear();
        _sceneCamera.UpdateViewport(sceneViewport.Width, sceneViewport.Height);
        _selection.Update(pointerPosition);
        RenderSceneViewport();
        _renderer.SubmitTransient(new TransientGeometry(_selection.BuildOverlay()));
        RenderGameViewport(gameViewport.Width, gameViewport.Height);
    }

    /// <summary>Builds and submits the Scene viewport queue.</summary>
    private void RenderSceneViewport()
    {
        var view = _sceneCamera.GetViewMatrix();
        var projection = _sceneCamera.GetProjectionMatrix();
        _renderer.DrawGroundGrid(_sceneViewport, view, projection);
        _sceneQueue.Add(GetMeshHandle(_originAxes), new PushConstants
        {
            Model = Matrix4x4.Identity,
            View = view,
            Projection = projection
        });

        foreach (var instance in _sceneObjects)
        {
            if (instance.Mesh is { } mesh)
                _sceneQueue.Add(GetMeshHandle(mesh), _sceneCamera.GetPushConstants(instance.GetModelMatrix()));
        }

        _renderer.Submit(_sceneViewport, _sceneQueue);
    }

    /// <summary>Builds and submits the scene through the active Game camera.</summary>
    /// <param name="width">Game viewport width.</param>
    /// <param name="height">Game viewport height.</param>
    private void RenderGameViewport(float width, float height)
    {
        _gameCamera.UpdateViewport(width, height);
        foreach (var instance in _gameObjects)
        {
            if (instance.Mesh is { } mesh)
                _gameQueue.Add(GetMeshHandle(mesh), _gameCamera.GetPushConstants(instance.GetModelMatrix()));
        }
        _renderer.Submit(_gameViewport, _gameQueue);
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
        var retained = _sceneObjects.Concat(_gameObjects)
            .Select(instance => instance.Mesh)
            .OfType<Mesh>()
            .Append(_originAxes)
            .ToHashSet();
        foreach (var mesh in _meshHandles.Keys.Where(mesh => !retained.Contains(mesh)).ToArray())
        {
            _renderer.DestroyMesh(_meshHandles[mesh]);
            _meshHandles.Remove(mesh);
        }
    }
}
