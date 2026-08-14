using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Engine.Core;

namespace Engine.Graphics;

/// <summary>Describes one vertex used by static and skinned model pipelines.</summary>
public struct ModelVertex
{
    /// <summary>Gets or sets object-space position.</summary>
    public Vector3 Position;

    /// <summary>Gets or sets object-space unit normal.</summary>
    public Vector3 Normal;

    /// <summary>Gets or sets primary texture coordinates.</summary>
    public Vector2 TexCoord;

    /// <summary>Gets or sets tangent direction and handedness.</summary>
    public Vector4 Tangent;

    /// <summary>Gets or sets linear per-vertex color.</summary>
    public Vector4 Color;

    /// <summary>Gets the packed byte stride.</summary>
    public static uint Stride => sizeof(float) * 16u;

    /// <summary>Creates one model vertex.</summary>
    /// <param name="position">Object-space position.</param>
    /// <param name="normal">Object-space normal.</param>
    /// <param name="texCoord">Primary texture coordinates.</param>
    /// <param name="tangent">Tangent direction and handedness.</param>
    public ModelVertex(Vector3 position, Vector3 normal, Vector2 texCoord, Vector4 tangent)
        : this(position, normal, texCoord, tangent, Vector4.One)
    {
    }

    /// <summary>Creates one colored model vertex.</summary>
    /// <param name="position">Object-space position.</param>
    /// <param name="normal">Object-space normal.</param>
    /// <param name="texCoord">Primary texture coordinates.</param>
    /// <param name="tangent">Tangent direction and handedness.</param>
    /// <param name="color">Linear per-vertex color.</param>
    public ModelVertex(
        Vector3 position,
        Vector3 normal,
        Vector2 texCoord,
        Vector4 tangent,
        Vector4 color)
    {
        Position = position;
        Normal = normal;
        TexCoord = texCoord;
        Tangent = tangent;
        Color = color;
    }
}

/// <summary>Contains the packed vertex consumed by the built-in forward pipeline.</summary>
public struct ForwardModelVertex
{
    /// <summary>Gets or sets object-space position.</summary>
    public Vector3 Position;

    /// <summary>Gets or sets object-space normal.</summary>
    public Vector3 Normal;

    /// <summary>Gets or sets primary texture coordinates.</summary>
    public Vector2 TexCoord;

    /// <summary>Gets or sets tangent direction and handedness.</summary>
    public Vector4 Tangent;

    /// <summary>Gets or sets the linear base-color multiplier.</summary>
    public Vector4 BaseColor;

    /// <summary>Gets the packed byte stride.</summary>
    public static uint Stride => sizeof(float) * 16u;

    /// <summary>Creates one packed forward vertex.</summary>
    /// <param name="position">Object-space position.</param>
    /// <param name="normal">Object-space normal.</param>
    /// <param name="texCoord">Primary texture coordinates.</param>
    /// <param name="tangent">Tangent direction and handedness.</param>
    /// <param name="baseColor">Linear base-color multiplier.</param>
    public ForwardModelVertex(
        Vector3 position,
        Vector3 normal,
        Vector2 texCoord,
        Vector4 tangent,
        Vector4 baseColor)
    {
        Position = position;
        Normal = normal;
        TexCoord = texCoord;
        Tangent = tangent;
        BaseColor = baseColor;
    }
}

/// <summary>Contains the packed vertex consumed by the GPU skinning pipeline.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SkinnedForwardModelVertex
{
    /// <summary>Gets or sets bind-pose position.</summary>
    public Vector3 Position;
    /// <summary>Gets or sets bind-pose normal.</summary>
    public Vector3 Normal;
    /// <summary>Gets or sets primary texture coordinates.</summary>
    public Vector2 TexCoord;
    /// <summary>Gets or sets tangent direction and handedness.</summary>
    public Vector4 Tangent;
    /// <summary>Gets or sets the linear base-color multiplier.</summary>
    public Vector4 BaseColor;
    /// <summary>Gets or sets four joint indices as unsigned integer components.</summary>
    public UIntVector4 Joints;
    /// <summary>Gets or sets normalized joint weights.</summary>
    public Vector4 Weights;

    /// <summary>Gets the packed byte stride.</summary>
    public static uint Stride => sizeof(float) * 20u + sizeof(uint) * 4u;

    /// <summary>Creates one packed skinned forward vertex.</summary>
    /// <param name="position">Bind-pose position.</param>
    /// <param name="normal">Bind-pose normal.</param>
    /// <param name="texCoord">Primary texture coordinates.</param>
    /// <param name="tangent">Tangent direction and handedness.</param>
    /// <param name="baseColor">Linear base-color multiplier.</param>
    /// <param name="joints">Four joint indices.</param>
    /// <param name="weights">Four normalized joint weights.</param>
    public SkinnedForwardModelVertex(
        Vector3 position,
        Vector3 normal,
        Vector2 texCoord,
        Vector4 tangent,
        Vector4 baseColor,
        UIntVector4 joints,
        Vector4 weights)
    {
        Position = position;
        Normal = normal;
        TexCoord = texCoord;
        Tangent = tangent;
        BaseColor = baseColor;
        Joints = joints;
        Weights = weights;
    }
}

/// <summary>Stores four unsigned integer components without graphics-backend dependencies.</summary>
/// <param name="X">First component.</param>
/// <param name="Y">Second component.</param>
/// <param name="Z">Third component.</param>
/// <param name="W">Fourth component.</param>
public readonly record struct UIntVector4(uint X, uint Y, uint Z, uint W);

/// <summary>Describes one indexed primitive range and its material slot.</summary>
/// <param name="FirstIndex">First index in the shared index buffer.</param>
/// <param name="IndexCount">Number of indices in the primitive.</param>
/// <param name="MaterialSlot">Model-local material slot.</param>
public readonly record struct Submesh(uint FirstIndex, uint IndexCount, int MaterialSlot);

/// <summary>Contains renderer-independent indexed static mesh data.</summary>
public sealed class StaticMeshResource
{
    private const string Magic = "NMESH001";
    /// <summary>Gets interleaved model vertices.</summary>
    public ModelVertex[] Vertices { get; }

    /// <summary>Gets 32-bit triangle indices.</summary>
    public uint[] Indices { get; }

    /// <summary>Gets independently drawable primitive ranges.</summary>
    public IReadOnlyList<Submesh> Submeshes { get; }

    /// <summary>Gets object-space bounds minimum.</summary>
    public Vector3 BoundsMinimum { get; }

    /// <summary>Gets object-space bounds maximum.</summary>
    public Vector3 BoundsMaximum { get; }

    /// <summary>Creates validated static mesh data.</summary>
    /// <param name="vertices">Interleaved vertices.</param>
    /// <param name="indices">Triangle indices.</param>
    /// <param name="submeshes">Primitive ranges.</param>
    public StaticMeshResource(
        ModelVertex[] vertices,
        uint[] indices,
        IReadOnlyList<Submesh> submeshes)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);
        ArgumentNullException.ThrowIfNull(submeshes);
        if (indices.Length % 3 != 0)
            throw new ArgumentException("Static mesh indices must describe triangles.", nameof(indices));
        if (indices.Any(index => index >= vertices.Length))
            throw new ArgumentException("Static mesh index exceeds the vertex array.", nameof(indices));
        foreach (var submesh in submeshes)
        {
            if ((ulong)submesh.FirstIndex + submesh.IndexCount > (ulong)indices.Length)
                throw new ArgumentException("Submesh exceeds the index array.", nameof(submeshes));
        }
        Vertices = vertices;
        Indices = indices;
        Submeshes = submeshes.ToArray();
        if (vertices.Length == 0)
        {
            BoundsMinimum = Vector3.Zero;
            BoundsMaximum = Vector3.Zero;
        }
        else
        {
            var minimum = vertices[0].Position;
            var maximum = vertices[0].Position;
            foreach (var vertex in vertices.AsSpan(1))
            {
                minimum = Vector3.Min(minimum, vertex.Position);
                maximum = Vector3.Max(maximum, vertex.Position);
            }
            BoundsMinimum = minimum;
            BoundsMaximum = maximum;
        }
    }

    /// <summary>Reads one versioned Nico static-mesh artifact.</summary>
    /// <param name="stream">Readable artifact stream.</param>
    /// <returns>The decoded static mesh.</returns>
    public static StaticMeshResource Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != Magic)
            throw new InvalidDataException("Static mesh artifact has an invalid signature.");
        var version = reader.ReadUInt32();
        if (version is not 1u and not 2u)
            throw new InvalidDataException("Static mesh artifact version is unsupported.");
        var vertexCount = reader.ReadUInt32();
        var indexCount = reader.ReadUInt32();
        var materialSlot = reader.ReadInt32();
        var storedVertexStride = version >= 2u ? ModelVertex.Stride : sizeof(float) * 12u;
        var requiredBytes = checked((long)vertexCount * storedVertexStride +
            (long)indexCount * sizeof(uint));
        if (!stream.CanSeek || requiredBytes != stream.Length - stream.Position)
            throw new InvalidDataException("Static mesh artifact payload length is invalid.");
        var vertices = new ModelVertex[checked((int)vertexCount)];
        for (var index = 0; index < vertices.Length; index++)
        {
            vertices[index] = new ModelVertex(
                ReadVector3(reader), ReadVector3(reader), ReadVector2(reader), ReadVector4(reader),
                version >= 2u ? ReadVector4(reader) : Vector4.One);
        }
        var indices = new uint[checked((int)indexCount)];
        for (var index = 0; index < indices.Length; index++)
            indices[index] = reader.ReadUInt32();
        return new StaticMeshResource(vertices, indices,
            [new Submesh(0, indexCount, materialSlot)]);
    }

    /// <summary>Writes one versioned static-mesh artifact for generated collision assets.</summary>
    /// <param name="stream">Writable artifact or source stream.</param>
    /// <param name="materialSlot">Diagnostic material slot; collision assets normally use minus one.</param>
    public void Save(Stream stream, int materialSlot = -1)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(Magic));
        writer.Write(2u);
        writer.Write(checked((uint)Vertices.Length));
        writer.Write(checked((uint)Indices.Length));
        writer.Write(materialSlot);
        for (var index = 0; index < Vertices.Length; index++)
        {
            var vertex = Vertices[index];
            Write(writer, vertex.Position);
            Write(writer, vertex.Normal);
            Write(writer, vertex.TexCoord);
            Write(writer, vertex.Tangent);
            Write(writer, vertex.Color);
        }
        for (var index = 0; index < Indices.Length; index++)
            writer.Write(Indices[index]);
    }

    /// <summary>Writes a two-component vector.</summary>
    /// <param name="writer">Artifact writer.</param><param name="value">Vector value.</param>
    private static void Write(BinaryWriter writer, Vector2 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
    }

    /// <summary>Writes a three-component vector.</summary>
    /// <param name="writer">Artifact writer.</param><param name="value">Vector value.</param>
    private static void Write(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    /// <summary>Writes a four-component vector.</summary>
    /// <param name="writer">Artifact writer.</param><param name="value">Vector value.</param>
    private static void Write(BinaryWriter writer, Vector4 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
    }

    /// <summary>Reads one two-component vector.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>The decoded vector.</returns>
    private static Vector2 ReadVector2(BinaryReader reader)
    {
        return new Vector2(reader.ReadSingle(), reader.ReadSingle());
    }

    /// <summary>Reads one three-component vector.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>The decoded vector.</returns>
    private static Vector3 ReadVector3(BinaryReader reader)
    {
        return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    /// <summary>Reads one four-component vector.</summary>
    /// <param name="reader">Artifact reader.</param>
    /// <returns>The decoded vector.</returns>
    private static Vector4 ReadVector4(BinaryReader reader)
    {
        return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
            reader.ReadSingle());
    }
}

/// <summary>Describes the color interpretation of texture samples.</summary>
public enum TextureColorSpace
{
    /// <summary>Linear numeric data such as normals and roughness.</summary>
    Linear,

    /// <summary>sRGB-encoded visible color data.</summary>
    Srgb
}

/// <summary>Contains one decoded RGBA8 texture ready for runtime upload.</summary>
/// <param name="Width">Pixel width.</param>
/// <param name="Height">Pixel height.</param>
/// <param name="Pixels">Tightly packed RGBA8 pixels.</param>
/// <param name="ColorSpace">Sample color interpretation.</param>
public sealed record TextureResource(
    uint Width,
    uint Height,
    byte[] Pixels,
    TextureColorSpace ColorSpace)
{
    /// <summary>Reads one versioned Nico RGBA8 texture artifact.</summary>
    /// <param name="stream">Readable artifact stream.</param>
    /// <returns>The decoded runtime texture.</returns>
    public static TextureResource Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "NTEX0001")
            throw new InvalidDataException("Texture artifact has an invalid signature.");
        if (reader.ReadUInt32() != 1u)
            throw new InvalidDataException("Texture artifact version is unsupported.");
        var width = reader.ReadUInt32();
        var height = reader.ReadUInt32();
        var colorSpace = reader.ReadByte() switch
        {
            0 => TextureColorSpace.Linear,
            1 => TextureColorSpace.Srgb,
            _ => throw new InvalidDataException("Texture artifact color space is invalid.")
        };
        var byteCount = checked((long)width * height * 4);
        if (byteCount > int.MaxValue || !stream.CanSeek ||
            stream.Length - stream.Position != byteCount)
        {
            throw new InvalidDataException("Texture artifact payload length is invalid.");
        }
        return new TextureResource(width, height, reader.ReadBytes((int)byteCount), colorSpace);
    }
}

/// <summary>Contains renderer-resolved standard-material values and GPU resources.</summary>
public sealed class ResolvedStandardMaterial
{
    /// <summary>Gets or sets linear base-color multiplier.</summary>
    public Vector4 BaseColor { get; set; } = Vector4.One;

    /// <summary>Gets or sets an optional renderer-owned base-color texture.</summary>
    public TextureHandle BaseColorTexture { get; set; }

    /// <summary>Gets or sets an optional renderer-owned normal map texture.</summary>
    public TextureHandle NormalTexture { get; set; }

    /// <summary>Gets or sets an optional renderer-owned metallic/roughness texture.</summary>
    public TextureHandle MetallicRoughnessTexture { get; set; }

    /// <summary>Gets or sets metallic response in the range zero through one.</summary>
    public float Metallic { get; set; }

    /// <summary>Gets or sets surface roughness in the range zero through one.</summary>
    public float Roughness { get; set; } = 1f;

    /// <summary>Gets or sets whether back-face culling is disabled.</summary>
    public bool DoubleSided { get; set; }

    /// <summary>Gets the SRP queue class implied by the material opacity factor.</summary>
    public RenderSurfaceType SurfaceType => BaseColor.W < 0.999f
        ? RenderSurfaceType.Transparent : RenderSurfaceType.Opaque;

    /// <summary>Resolves persistent material values with optional GPU textures.</summary>
    /// <param name="source">Persistent material resource.</param>
    /// <param name="baseColorTexture">Renderer-owned base-color texture handle.</param>
    /// <param name="normalTexture">Renderer-owned normal-map texture handle.</param>
    /// <param name="metallicRoughnessTexture">Renderer-owned metallic/roughness texture handle.</param>
    /// <returns>A renderer-ready material.</returns>
    public static ResolvedStandardMaterial Resolve(
        StandardMaterialAsset source,
        TextureHandle baseColorTexture = default,
        TextureHandle normalTexture = default,
        TextureHandle metallicRoughnessTexture = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ResolvedStandardMaterial
        {
            BaseColor = source.BaseColor,
            BaseColorTexture = baseColorTexture,
            NormalTexture = normalTexture,
            MetallicRoughnessTexture = metallicRoughnessTexture,
            Metallic = source.Metallic,
            Roughness = source.Roughness,
            DoubleSided = source.DoubleSided
        };
    }
}

/// <summary>Identifies one renderer-owned texture resource.</summary>
/// <param name="Value">Opaque renderer-owned identifier.</param>
public readonly record struct TextureHandle(ulong Value)
{
    /// <summary>Gets whether this handle identifies a resource.</summary>
    public bool IsValid => Value != 0;
}

/// <summary>Identifies one renderer-owned material resource.</summary>
/// <param name="Value">Opaque renderer-owned identifier.</param>
public readonly record struct MaterialHandle(ulong Value)
{
    /// <summary>Gets whether this handle identifies a resource.</summary>
    public bool IsValid => Value != 0;
}
