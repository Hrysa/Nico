using System.Numerics;

namespace Engine.Graphics;

public struct Vertex : IEquatable<Vertex>
{
    public Vector3 Position;
    public Vector3 Color;

    public static readonly uint Stride = (uint)(sizeof(float) * 6);

    public Vertex(Vector3 position, Vector3 color)
    {
        Position = position;
        Color = color;
    }

    /// <summary>Compares two colored vertices without boxing.</summary>
    /// <param name="other">Vertex to compare.</param>
    /// <returns>True when position and color match.</returns>
    public readonly bool Equals(Vertex other)
    {
        return Position.Equals(other.Position) && Color.Equals(other.Color);
    }
}

public struct VertexT : IEquatable<VertexT>
{
    public Vector3 Position;
    public Vector2 TexCoord;

    public static readonly uint Stride = (uint)(sizeof(float) * 5);

    public VertexT(Vector3 position, Vector2 texCoord)
    {
        Position = position;
        TexCoord = texCoord;
    }

    /// <summary>Compares two textured vertices without boxing.</summary>
    /// <param name="other">Vertex to compare.</param>
    /// <returns>True when position and texture coordinate match.</returns>
    public readonly bool Equals(VertexT other)
    {
        return Position.Equals(other.Position) && TexCoord.Equals(other.TexCoord);
    }
}

public struct PushConstants
{
    public Matrix4x4 Model;
    public Matrix4x4 View;
    public Matrix4x4 Projection;
}
