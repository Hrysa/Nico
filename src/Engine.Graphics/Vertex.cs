using System.Numerics;

namespace Engine.Graphics;

/// <summary>Stores one colored vertex.</summary>
public struct Vertex : IEquatable<Vertex>
{
    /// <summary>Vertex position.</summary>
    public Vector3 Position;
    /// <summary>Linear RGB color.</summary>
    public Vector3 Color;
    /// <summary>Color opacity.</summary>
    public float Alpha;

    /// <summary>Vertex size in bytes.</summary>
    public static readonly uint Stride = (uint)(sizeof(float) * 7);

    /// <summary>Creates an opaque colored vertex.</summary>
    /// <param name="position">Vertex position.</param>
    /// <param name="color">Linear RGB color.</param>
    public Vertex(Vector3 position, Vector3 color)
    {
        Position = position;
        Color = color;
        Alpha = 1f;
    }

    /// <summary>Creates a colored vertex with explicit alpha.</summary>
    /// <param name="position">Vertex position.</param>
    /// <param name="color">RGBA vertex color.</param>
    public Vertex(Vector3 position, Vector4 color)
    {
        Position = position;
        Color = new Vector3(color.X, color.Y, color.Z);
        Alpha = color.W;
    }

    /// <summary>Compares two colored vertices without boxing.</summary>
    /// <param name="other">Vertex to compare.</param>
    /// <returns>True when position and color match.</returns>
    public readonly bool Equals(Vertex other)
    {
        return Position.Equals(other.Position) && Color.Equals(other.Color) &&
            Alpha.Equals(other.Alpha);
    }
}

/// <summary>Stores one textured vertex.</summary>
public struct VertexT : IEquatable<VertexT>
{
    /// <summary>Vertex position.</summary>
    public Vector3 Position;
    /// <summary>Texture coordinate.</summary>
    public Vector2 TexCoord;
    /// <summary>Texture opacity.</summary>
    public float Opacity;

    /// <summary>Vertex size in bytes.</summary>
    public static readonly uint Stride = (uint)(sizeof(float) * 6);

    /// <summary>Creates a textured vertex.</summary>
    /// <param name="position">Vertex position.</param>
    /// <param name="texCoord">Texture coordinate.</param>
    /// <param name="opacity">Texture opacity.</param>
    public VertexT(Vector3 position, Vector2 texCoord, float opacity = 1f)
    {
        Position = position;
        TexCoord = texCoord;
        Opacity = opacity;
    }

    /// <summary>Compares two textured vertices without boxing.</summary>
    /// <param name="other">Vertex to compare.</param>
    /// <returns>True when position and texture coordinate match.</returns>
    public readonly bool Equals(VertexT other)
    {
        return Position.Equals(other.Position) && TexCoord.Equals(other.TexCoord) &&
            Opacity.Equals(other.Opacity);
    }
}

/// <summary>Stores per-draw transform matrices pushed to shaders.</summary>
public struct PushConstants
{
    /// <summary>Model transform.</summary>
    public Matrix4x4 Model;
    /// <summary>View transform.</summary>
    public Matrix4x4 View;
    /// <summary>Projection transform.</summary>
    public Matrix4x4 Projection;
}
