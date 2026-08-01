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
    /// <param name="sceneObjects">Objects rendered in the Scene viewport.</param>
    /// <param name="selection">Selection and gizmo controller.</param>
    public EditorViewportRenderer(
        IRenderer renderer,
        uint sceneViewportId,
        uint gameViewportId,
        PerspectiveCamera sceneCamera,
        IReadOnlyList<MeshInstance3D> sceneObjects,
        SceneSelectionController selection)
    {
        _renderer = renderer;
        _sceneViewportId = sceneViewportId;
        _gameViewportId = gameViewportId;
        _sceneCamera = sceneCamera;
        _sceneObjects = sceneObjects;
        _selection = selection;
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

    /// <summary>Builds and submits the temporary Game viewport queue.</summary>
    /// <param name="width">Game viewport width.</param>
    /// <param name="height">Game viewport height.</param>
    private void RenderGameViewport(float width, float height)
    {
        var size = MathF.Min(width, height) * 0.25f;
        var centerX = width * 0.5f;
        var centerY = height * 0.5f;
        var pushConstants = new PushConstants
        {
            Model = Matrix4x4.Identity,
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.CreateOrthographicOffCenter(0f, width, 0f, height, -1f, 1f)
        };
        var vertices = new[]
        {
            new Vertex(new Vector3(centerX - size, centerY - size, 0f), new Vector3(1f, 0.5f, 0f)),
            new Vertex(new Vector3(centerX - size, centerY + size, 0f), new Vector3(0f, 1f, 0.5f)),
            new Vertex(new Vector3(centerX + size, centerY + size, 0f), new Vector3(0f, 0.5f, 1f)),
            new Vertex(new Vector3(centerX + size, centerY + size, 0f), new Vector3(0f, 0.5f, 1f)),
            new Vertex(new Vector3(centerX + size, centerY - size, 0f), new Vector3(1f, 0f, 0.5f)),
            new Vertex(new Vector3(centerX - size, centerY - size, 0f), new Vector3(1f, 0.5f, 0f))
        };
        _gameQueue.Add(vertices, pushConstants);
        _renderer.Submit(_gameViewportId, _gameQueue);
    }
}
