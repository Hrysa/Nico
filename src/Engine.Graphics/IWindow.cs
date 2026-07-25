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

    /// <summary>Occurs when the mouse moves. Provides screen-space position.</summary>
    event Action<Vector2>? MouseMove;

    /// <summary>Occurs when a mouse button is pressed. Provides button index.</summary>
    event Action<int>? MouseDown;

    /// <summary>Occurs when a mouse button is released. Provides button index.</summary>
    event Action<int>? MouseUp;

    /// <summary>Occurs when a mouse button is double-clicked. Provides button index.</summary>
    event Action<int>? MouseDoubleClick;

    /// <summary>Occurs when the mouse wheel scrolls. Provides scroll offset (Y axis).</summary>
    event Action<float>? MouseScroll;

    /// <summary>Occurs when a key is pressed. Provides key code.</summary>
    event Action<int>? KeyDown;

    /// <summary>Occurs when a key is released. Provides key code.</summary>
    event Action<int>? KeyUp;
}