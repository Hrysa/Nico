namespace Engine.Graphics;

public interface IWindow : IDisposable
{
    void Initialize(WindowOptions options);
    void Run();
    void Shutdown();
    bool IsRunning { get; }
    void ProcessEvents();
}