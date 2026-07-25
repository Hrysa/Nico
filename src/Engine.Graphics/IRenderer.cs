namespace Engine.Graphics;

public interface IRenderer : IDisposable
{
    void Initialize(IGraphicsContext context);
    void Shutdown();
    void BeginFrame();
    void EndFrame();
    void Clear();
}