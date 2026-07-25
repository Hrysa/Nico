using System.Numerics;

namespace Engine.Graphics;

public interface IWindow : IDisposable
{
    void Initialize(WindowOptions options);
    void Run();
    void Shutdown();
    bool IsRunning { get; }
    void ProcessEvents();

    /// <summary>Updates the GPU vertex buffer with new vertex data.</summary>
    /// <param name="vertices">The new vertices to upload.</param>
    void UpdateVertexBuffer(Vertex[] vertices);

    // ── Viewport FBO Management ────────────────────────────────

    /// <summary>
    /// Registers a viewport and creates its FBO resources.
    /// </summary>
    /// <param name="width">Initial viewport width in pixels.</param>
    /// <param name="height">Initial viewport height in pixels.</param>
    /// <returns>A unique viewport ID.</returns>
    uint RegisterViewport(float width, float height);

    /// <summary>
    /// Unregisters a viewport and destroys its FBO resources.
    /// </summary>
    /// <param name="viewportId">The viewport ID to unregister.</param>
    void UnregisterViewport(uint viewportId);

    /// <summary>
    /// Resizes a viewport's FBO. Actual GPU recreation is deferred to the next frame.
    /// </summary>
    /// <param name="viewportId">The viewport ID.</param>
    /// <param name="width">New width in pixels.</param>
    /// <param name="height">New height in pixels.</param>
    void ResizeViewport(uint viewportId, float width, float height);

    /// <summary>
    /// Sets the render callback invoked during Pass 1 for this viewport.
    /// </summary>
    /// <param name="viewportId">The viewport ID.</param>
    /// <param name="callback">The render callback receiving a ViewportRenderContext.</param>
    void SetViewportRenderCallback(uint viewportId, Action<ViewportRenderContext> callback);

    /// <summary>
    /// Sets the textured-quad vertices for a viewport's display quad.
    /// </summary>
    /// <param name="viewportId">The viewport ID.</param>
    /// <param name="vertices">The textured quad vertices.</param>
    void SetViewportQuadVertices(uint viewportId, VertexT[] vertices);

    /// <summary>
    /// Creates a render context for the specified viewport.
    /// </summary>
    /// <param name="viewportId">The viewport ID.</param>
    /// <returns>A new ViewportRenderContext.</returns>
    ViewportRenderContext CreateRenderContext(uint viewportId);

    /// <summary>
    /// Queues vertices to be drawn during the current viewport's FBO pass.
    /// Call this from a viewport render callback.
    /// </summary>
    /// <param name="viewportId">The viewport ID.</param>
    /// <param name="vertices">The vertices to draw.</param>
    /// <param name="pushConstants">Push constants (MVP matrices).</param>
    void DrawInViewport(uint viewportId, Vertex[] vertices, PushConstants pushConstants);
}
