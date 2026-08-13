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

/// <summary>Stores model transforms and basic forward-lighting parameters.</summary>
public struct ModelPushConstants
{
    /// <summary>Object-to-world transform.</summary>
    public Matrix4x4 Model;
    /// <summary>World-to-camera transform.</summary>
    public Matrix4x4 View;
    /// <summary>Camera projection transform.</summary>
    public Matrix4x4 Projection;
    /// <summary>XYZ direction toward light and W direct intensity.</summary>
    public Vector4 LightDirectionIntensity;
    /// <summary>RGB linear light color and W ambient intensity.</summary>
    public Vector4 LightColorAmbient;
    /// <summary>XYZ world-space camera position and W metallic factor.</summary>
    public Vector4 CameraPositionMetallic;
    /// <summary>X roughness factor; remaining channels are reserved.</summary>
    public Vector4 MaterialFactors;

    /// <summary>Combines camera/object constants with one queue's lighting.</summary>
    /// <param name="transforms">Object and camera transforms.</param>
    /// <param name="lighting">Resolved scene lighting.</param>
    /// <param name="metallic">Material metallic factor.</param>
    /// <param name="roughness">Material roughness factor.</param>
    /// <returns>Constants consumed by static and skinned forward shaders.</returns>
    public static ModelPushConstants Create(
        PushConstants transforms,
        SceneLighting lighting,
        float metallic = 0f,
        float roughness = 1f)
    {
        var cameraPosition = Matrix4x4.Invert(transforms.View, out var inverseView)
            ? inverseView.Translation : Vector3.Zero;
        return new ModelPushConstants
        {
            Model = transforms.Model,
            View = transforms.View,
            Projection = transforms.Projection,
            LightDirectionIntensity = new Vector4(lighting.DirectionToLight, lighting.Intensity),
            LightColorAmbient = new Vector4(lighting.Color, lighting.AmbientIntensity),
            CameraPositionMetallic = new Vector4(cameraPosition, Math.Clamp(metallic, 0f, 1f)),
            MaterialFactors = new Vector4(Math.Clamp(roughness, 0f, 1f), 0f, 0f, 0f)
        };
    }
}

/// <summary>Stores texture transforms and presentation-effect parameters.</summary>
public struct TexturePushConstants
{
    /// <summary>Object-to-world transform.</summary>
    public Matrix4x4 Model;
    /// <summary>World-to-camera transform.</summary>
    public Matrix4x4 View;
    /// <summary>Camera projection transform.</summary>
    public Matrix4x4 Projection;
    /// <summary>X stores grayscale strength; remaining channels are reserved.</summary>
    public Vector4 OutputEffects;

    /// <summary>Combines texture transforms with one render view's output settings.</summary>
    /// <param name="transforms">Texture geometry transforms.</param>
    /// <param name="output">Presentation effects for the sampled render view.</param>
    /// <returns>Constants consumed by the texture shader.</returns>
    public static TexturePushConstants Create(
        PushConstants transforms,
        RenderOutputSettings output) => new()
        {
            Model = transforms.Model,
            View = transforms.View,
            Projection = transforms.Projection,
            OutputEffects = new Vector4(output.GrayscaleStrength, 0f, 0f, 0f)
        };
}
