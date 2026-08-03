using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Accepts editor UI and viewport rendering work independently of window lifecycle.
/// </summary>
public interface IRenderer
{
    /// <summary>Creates a retained mesh resource.</summary>
    /// <param name="description">Mesh data and lifetime policy.</param>
    /// <returns>Opaque handle used by render submissions.</returns>
    MeshHandle CreateMesh(MeshDescription description);

    /// <summary>Updates a contiguous range of an existing mesh.</summary>
    /// <param name="mesh">Mesh to update.</param>
    /// <param name="update">Replacement range.</param>
    void UpdateMesh(MeshHandle mesh, MeshUpdate update);

    /// <summary>Releases a retained mesh resource after in-flight work completes.</summary>
    /// <param name="mesh">Mesh to release.</param>
    void DestroyMesh(MeshHandle mesh);

    /// <summary>Submits the latest retained UI paint snapshot.</summary>
    /// <param name="drawList">UI draw list.</param>
    void SubmitUI(UIDrawList drawList);

    /// <summary>Sets push constants used for persistent UI geometry.</summary>
    /// <param name="pushConstants">UI transform constants.</param>
    void SetPushConstants(PushConstants pushConstants);

    /// <summary>Registers a viewport render target.</summary>
    /// <param name="width">Initial width.</param>
    /// <param name="height">Initial height.</param>
    /// <returns>The viewport identifier.</returns>
    RenderViewHandle CreateRenderView(float width, float height);

    /// <summary>Unregisters a viewport render target.</summary>
    /// <param name="view">Render view to destroy.</param>
    void DestroyRenderView(RenderViewHandle view);

    /// <summary>Requests a viewport render-target resize.</summary>
    /// <param name="view">Render view to resize.</param>
    /// <param name="width">New width.</param>
    /// <param name="height">New height.</param>
    void ResizeRenderView(RenderViewHandle view, float width, float height);

    /// <summary>Sets geometry used to present a viewport texture.</summary>
    /// <param name="view">Render view whose presentation geometry changes.</param>
    /// <param name="vertices">Presentation quad.</param>
    void SetViewportQuadVertices(RenderViewHandle view, VertexT[] vertices);

    /// <summary>Gets current viewport render-target information.</summary>
    /// <param name="view">Render view to describe.</param>
    /// <returns>The viewport context.</returns>
    ViewportRenderContext CreateRenderContext(RenderViewHandle view);

    /// <summary>Submits an ordered render queue to a viewport.</summary>
    /// <param name="view">Render view receiving the queue.</param>
    /// <param name="renderQueue">Commands to enqueue for the current frame.</param>
    void Submit(RenderViewHandle view, RenderQueue renderQueue);

    /// <summary>Queues a procedural ground grid in a viewport.</summary>
    /// <param name="renderView">Render view receiving the grid.</param>
    /// <param name="view">Camera view matrix.</param>
    /// <param name="projection">Camera projection matrix.</param>
    void DrawGroundGrid(RenderViewHandle renderView, Matrix4x4 view, Matrix4x4 projection);

    /// <summary>Sets a viewport clear color.</summary>
    /// <param name="view">Render view whose clear color changes.</param>
    /// <param name="r">Red component.</param>
    /// <param name="g">Green component.</param>
    /// <param name="b">Blue component.</param>
    /// <param name="a">Alpha component.</param>
    void SetViewportClearColor(RenderViewHandle view, float r, float g, float b, float a = 1f);

    /// <summary>Submits screen-space geometry valid only for the current frame.</summary>
    /// <param name="geometry">Transient geometry.</param>
    void SubmitTransient(TransientGeometry geometry);
}
