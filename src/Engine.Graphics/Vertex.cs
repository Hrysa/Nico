using System.Numerics;

namespace Engine.Graphics;

public struct Vertex
{
    public Vector3 Position;
    public Vector3 Color;

    public static readonly uint Stride = (uint)(sizeof(float) * 6);

    public Vertex(Vector3 position, Vector3 color)
    {
        Position = position;
        Color = color;
    }
}

public struct PushConstants
{
    public Matrix4x4 Model;
    public Matrix4x4 View;
    public Matrix4x4 Projection;
}
