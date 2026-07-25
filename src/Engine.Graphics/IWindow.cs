using System.Numerics;

namespace Engine.Graphics;

public interface IWindow : IDisposable
{
    void Initialize(WindowOptions options);
    void Run();
    void Shutdown();
    bool IsRunning { get; }
    void ProcessEvents();

    /// <summary>Occurs when the mouse moves. Provides screen-space position.</summary>
    event Action<Vector2>? MouseMove;

    /// <summary>Occurs when a mouse button is pressed. Provides button index.</summary>
    event Action<int>? MouseDown;

    /// <summary>Occurs when a mouse button is released. Provides button index.</summary>
    event Action<int>? MouseUp;
}