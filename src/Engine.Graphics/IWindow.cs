using System.Numerics;

namespace Engine.Graphics;

public interface IWindow : IDisposable
{
    void Initialize(WindowOptions options);
    void Run();
    void Shutdown();
    bool IsRunning { get; }
    void ProcessEvents();

    /// <summary>Fired each frame with delta time in seconds, before rendering.</summary>
    event Action<double>? Update;

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
    /// Call this from the Update event handler.
    /// </summary>
    /// <param name="viewportId">The viewport ID.</param>
    /// <param name="vertices">The vertices to draw.</param>
    /// <param name="pushConstants">Push constants (MVP matrices).</param>
    void DrawInViewport(uint viewportId, Vertex[] vertices, PushConstants pushConstants);

    /// <summary>
    /// Sets the clear color for a viewport's FBO.
    /// </summary>
    /// <param name="viewportId">The viewport ID.</param>
    /// <param name="r">Red component (0-1).</param>
    /// <param name="g">Green component (0-1).</param>
    /// <param name="b">Blue component (0-1).</param>
    /// <param name="a">Alpha component (0-1).</param>
    void SetViewportClearColor(uint viewportId, float r, float g, float b, float a = 1.0f);
}
