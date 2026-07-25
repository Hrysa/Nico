namespace Engine.Graphics;

public interface ITexture : IDisposable
{
    int Width { get; }
    int Height { get; }
    void Bind(int slot = 0);
    void Unbind();
}