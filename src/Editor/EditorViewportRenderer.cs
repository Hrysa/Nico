using System.Numerics;
using Engine.Graphics;
using Engine.UI;

namespace Editor;

/// <summary>
/// Builds and submits the Scene and Game viewport render queues for each frame.
/// </summary>
public sealed class EditorViewportRenderer
{
    private readonly IRenderer _renderer;
    private readonly uint _sceneViewportId;
    private readonly uint _gameViewportId;
    private readonly PerspectiveCamera _sceneCamera;
    private ICamera _gameCamera;
    private readonly IReadOnlyList<MeshInstance3D> _sceneObjects;
    private readonly SceneSelectionController _selection;
    private readonly OriginAxesMesh _originAxes = new();
    private readonly RenderQueue _sceneQueue = new();
    private readonly RenderQueue _gameQueue = new();

    /// <summary>
    /// Creates the editor viewport renderer.
    /// </summary>
    /// <param name="renderer">Rendering service.</param>
    /// <param name="sceneViewportId">Scene viewport identifier.</param>
    /// <param name="gameViewportId">Game viewport identifier.</param>
    /// <param name="sceneCamera">Scene camera.</param>
    /// <param name="gameCamera">Scene-owned camera used by the Game viewport.</param>
    /// <param name="sceneObjects">Objects rendered in the Scene viewport.</param>
    /// <param name="selection">Selection and gizmo controller.</param>
    public EditorViewportRenderer(
        IRenderer renderer,
        uint sceneViewportId,
        uint gameViewportId,
        PerspectiveCamera sceneCamera,
        ICamera gameCamera,
        IReadOnlyList<MeshInstance3D> sceneObjects,
        SceneSelectionController selection)
    {
        _renderer = renderer;
        _sceneViewportId = sceneViewportId;
        _gameViewportId = gameViewportId;
        _sceneCamera = sceneCamera;
        _gameCamera = gameCamera;
        _sceneObjects = sceneObjects;
        _selection = selection;
    }

    /// <summary>Changes the scene-owned camera used by the Game viewport.</summary>
    /// <param name="gameCamera">New active game camera.</param>
    public void SetGameCamera(ICamera gameCamera)
    {
        ArgumentNullException.ThrowIfNull(gameCamera);
        _gameCamera = gameCamera;
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
        _renderer.DrawOverlay(_selection.BuildOverlay());
        RenderGameViewport(gameViewport.Width, gameViewport.Height);
    }

    /// <summary>Builds and submits the Scene viewport queue.</summary>
    private void RenderSceneViewport()
    {
        var view = _sceneCamera.GetViewMatrix();
        var projection = _sceneCamera.GetProjectionMatrix();
        _renderer.DrawGroundGrid(_sceneViewportId, view, projection);
        _sceneQueue.Add(_originAxes.Vertices, new PushConstants
        {
            Model = Matrix4x4.Identity,
            View = view,
            Projection = projection
        });

        foreach (var instance in _sceneObjects)
        {
            if (instance.Mesh is { } mesh)
                _sceneQueue.Add(mesh.Vertices, _sceneCamera.GetPushConstants(instance.GetModelMatrix()));
        }

        _renderer.Submit(_sceneViewportId, _sceneQueue);
    }

    /// <summary>Builds and submits the scene through the active Game camera.</summary>
    /// <param name="width">Game viewport width.</param>
    /// <param name="height">Game viewport height.</param>
    private void RenderGameViewport(float width, float height)
    {
        _gameCamera.UpdateViewport(width, height);
        foreach (var instance in _sceneObjects)
        {
            if (instance.Mesh is { } mesh)
                _gameQueue.Add(mesh.Vertices, _gameCamera.GetPushConstants(instance.GetModelMatrix()));
        }
        _renderer.Submit(_gameViewportId, _gameQueue);
    }
}
