namespace Engine.Graphics;

public interface IMesh : IDisposable
{
    void Bind();
    void Unbind();
    void Draw();
}