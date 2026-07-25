namespace Engine.Graphics;

public interface IGraphicsContext : IDisposable
{
    void Initialize();
    void Shutdown();
}