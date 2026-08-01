using System.Numerics;

namespace Engine.Graphics;

/// <summary>
/// Accepts editor UI and viewport rendering work independently of window lifecycle.
/// </summary>
public interface IRenderer
{
    /// <summary>Sets the persistent UI paint commands.</summary>
    /// <param name="drawList">UI draw list.</param>
    void SetUI(UIDrawList drawList);

    /// <summary>Sets push constants used for persistent UI geometry.</summary>
    /// <param name="pushConstants">UI transform constants.</param>
    void SetPushConstants(PushConstants pushConstants);

    /// <summary>Creates the persistent UI vertex buffer.</summary>
    void CreateVertexBuffer();

    /// <summary>Updates persistent UI paint commands.</summary>
    /// <param name="drawList">New UI draw list.</param>
    void UpdateUI(UIDrawList drawList);

    /// <summary>Registers a viewport render target.</summary>
    /// <param name="width">Initial width.</param>
    /// <param name="height">Initial height.</param>
    /// <returns>The viewport identifier.</returns>
    uint RegisterViewport(float width, float height);

    /// <summary>Unregisters a viewport render target.</summary>
    /// <param name="viewportId">Viewport identifier.</param>
    void UnregisterViewport(uint viewportId);

    /// <summary>Requests a viewport render-target resize.</summary>
    /// <param name="viewportId">Viewport identifier.</param>
    /// <param name="width">New width.</param>
    /// <param name="height">New height.</param>
    void ResizeViewport(uint viewportId, float width, float height);

    /// <summary>Sets geometry used to present a viewport texture.</summary>
    /// <param name="viewportId">Viewport identifier.</param>
    /// <param name="vertices">Presentation quad.</param>
    void SetViewportQuadVertices(uint viewportId, VertexT[] vertices);

    /// <summary>Gets current viewport render-target information.</summary>
    /// <param name="viewportId">Viewport identifier.</param>
    /// <returns>The viewport context.</returns>
    ViewportRenderContext CreateRenderContext(uint viewportId);

    /// <summary>Submits an ordered render queue to a viewport.</summary>
    /// <param name="viewportId">Viewport identifier.</param>
    /// <param name="renderQueue">Commands to enqueue for the current frame.</param>
    void Submit(uint viewportId, RenderQueue renderQueue);

    /// <summary>Queues a procedural ground grid in a viewport.</summary>
    /// <param name="viewportId">Viewport identifier.</param>
    /// <param name="view">Camera view matrix.</param>
    /// <param name="projection">Camera projection matrix.</param>
    void DrawGroundGrid(uint viewportId, Matrix4x4 view, Matrix4x4 projection);

    /// <summary>Sets a viewport clear color.</summary>
    /// <param name="viewportId">Viewport identifier.</param>
    /// <param name="r">Red component.</param>
    /// <param name="g">Green component.</param>
    /// <param name="b">Blue component.</param>
    /// <param name="a">Alpha component.</param>
    void SetViewportClearColor(uint viewportId, float r, float g, float b, float a = 1f);

    /// <summary>Sets the screen-space overlay geometry for the next frame.</summary>
    /// <param name="vertices">Overlay vertices.</param>
    void DrawOverlay(Vertex[] vertices);
}
