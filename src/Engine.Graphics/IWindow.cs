namespace Engine.Graphics;

/// <summary>
/// Owns application-window lifecycle and frame timing.
/// </summary>
public interface IWindow : IDisposable
{
    /// <summary>Initializes the native window.</summary>
    /// <param name="options">Window configuration.</param>
    void Initialize(WindowOptions options);

    /// <summary>Runs the window event loop.</summary>
    void Run();

    /// <summary>Releases native window resources.</summary>
    void Shutdown();

    /// <summary>Gets whether the window is running.</summary>
    bool IsRunning { get; }

    /// <summary>Processes pending native events.</summary>
    void ProcessEvents();

    /// <summary>Occurs before rendering each frame.</summary>
    event Action<double>? Update;

    /// <summary>Occurs after the native client area changes size.</summary>
    event Action<int, int>? Resized;
}
