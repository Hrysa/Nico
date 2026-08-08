using System.Numerics;

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

    /// <summary>Processes events, updates, and renders one frame without entering a blocking loop.</summary>
    void PumpFrame();

    /// <summary>Wakes an event-driven window so pending state is updated and presented.</summary>
    void RequestFrame();

    /// <summary>Enables or disables capped continuous updates and rendering.</summary>
    /// <param name="enabled">True while time-based work requires continuous frames.</param>
    void SetContinuousRendering(bool enabled);

    /// <summary>Begins moving a borderless window from a client-area pointer position.</summary>
    /// <param name="pointerPosition">Pointer position inside the client area.</param>
    void BeginWindowDrag(Vector2 pointerPosition);

    /// <summary>Updates an active borderless-window drag.</summary>
    /// <param name="pointerPosition">Current pointer position inside the client area.</param>
    void UpdateWindowDrag(Vector2 pointerPosition);

    /// <summary>Ends an active borderless-window drag.</summary>
    void EndWindowDrag();

    /// <summary>Minimizes the native window.</summary>
    void Minimize();

    /// <summary>Toggles between normal and maximized native window state.</summary>
    void ToggleMaximize();

    /// <summary>Toggles native fullscreen presentation.</summary>
    void ToggleFullScreen();

    /// <summary>Requests native window closure.</summary>
    void Close();

    /// <summary>Requests a logical pointer cursor style for the host window.</summary>
    /// <param name="kind">Requested cursor kind.</param>
    void SetPointerCursor(PointerCursorKind kind);

    /// <summary>Occurs before rendering each frame.</summary>
    event Action<double>? Update;

    /// <summary>Occurs after a rendered frame has been measured.</summary>
    event Action<FrameProfileSample>? FrameProfiled;

    /// <summary>Occurs after the native client area changes size.</summary>
    event Action<int, int>? Resized;
}
