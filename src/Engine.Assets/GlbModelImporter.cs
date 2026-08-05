using System.Numerics;
using System.Text;
using System.Text.Json;
using StbImageSharp;

namespace Engine.Assets;

/// <summary>Imports static triangle primitives from a GLB 2.0 source into Nico mesh artifacts.</summary>
public sealed class GlbModelImporter : IAssetImporter
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinaryChunk = 0x004E4942;

    /// <inheritdoc/>
    public string Id => "gltf-model";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public AssetImportResult Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var source = context.OpenSource();
        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        var (document, binary) = ReadContainer(reader);
        using (document)
        {
            var artifacts = ImportMeshes(context, document.RootElement, binary).ToList();
            artifacts.AddRange(ImportMaterials(context, document.RootElement));
            artifacts.AddRange(ImportTextures(context, document.RootElement, binary));
            return new AssetImportResult(artifacts, [], []);
        }
    }

    /// <summary>Reads and validates the GLB header and required chunks.</summary>
    /// <param name="reader">Source binary reader.</param>
    /// <returns>Parsed JSON and binary buffer.</returns>
    private static (JsonDocument Document, byte[] Binary) ReadContainer(BinaryReader reader)
    {
        if (reader.BaseStream.Length < 20 || reader.ReadUInt32() != GlbMagic)
            throw new InvalidDataException("Source is not a GLB container.");
        if (reader.ReadUInt32() != 2)
            throw new InvalidDataException("Only GLB version 2 is supported.");
        var declaredLength = reader.ReadUInt32();
        if (declaredLength != reader.BaseStream.Length)
            throw new InvalidDataException("GLB declared length does not match the source file.");
        JsonDocument? document = null;
        byte[] binary = [];
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var length = reader.ReadUInt32();
            var type = reader.ReadUInt32();
            if (length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("GLB chunk exceeds the source file.");
            var bytes = reader.ReadBytes(checked((int)length));
            if (type == JsonChunk && document is null)
                document = JsonDocument.Parse(bytes);
            else if (type == BinaryChunk && binary.Length == 0)
                binary = bytes;
        }
        return (document ?? throw new InvalidDataException("GLB has no JSON chunk."), binary);
    }

    /// <summary>Imports every static triangle primitive as an independently addressable mesh.</summary>
    /// <param name="context">Artifact output context.</param>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <returns>Published mesh artifacts.</returns>
    private static IReadOnlyList<AssetArtifact> ImportMeshes(
        AssetImportContext context,
        JsonElement root,
        byte[] binary)
    {
        if (!root.TryGetProperty("asset", out var asset) ||
            !asset.TryGetProperty("version", out var version) ||
            version.GetString() is not { } versionText || !versionText.StartsWith("2", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Only glTF 2.x assets are supported.");
        }
        if (!root.TryGetProperty("meshes", out var meshes))
            throw new InvalidDataException("GLB contains no meshes.");
        var artifacts = new List<AssetArtifact>();
        var meshIndex = 0;
        foreach (var mesh in meshes.EnumerateArray())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var meshName = mesh.TryGetProperty("name", out var name)
                ? Sanitize(name.GetString(), $"mesh-{meshIndex}") : $"mesh-{meshIndex}";
            var primitiveIndex = 0;
            foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                var mode = primitive.TryGetProperty("mode", out var modeElement)
                    ? modeElement.GetInt32() : 4;
                if (mode != 4)
                    throw new InvalidDataException("Only triangle-list GLB primitives are supported.");
                var attributes = primitive.GetProperty("attributes");
                var positions = ReadVector3(root, binary,
                    attributes.GetProperty("POSITION").GetInt32(), "POSITION");
                var normals = attributes.TryGetProperty("NORMAL", out var normalAccessor)
                    ? ReadVector3(root, binary, normalAccessor.GetInt32(), "NORMAL")
                    : new Vector3[positions.Length];
                var texCoords = attributes.TryGetProperty("TEXCOORD_0", out var uvAccessor)
                    ? ReadVector2(root, binary, uvAccessor.GetInt32(), "TEXCOORD_0")
                    : new Vector2[positions.Length];
                var tangents = attributes.TryGetProperty("TANGENT", out var tangentAccessor)
                    ? ReadVector4(root, binary, tangentAccessor.GetInt32(), "TANGENT")
                    : Enumerable.Repeat(new Vector4(1f, 0f, 0f, 1f), positions.Length).ToArray();
                if (normals.Length != positions.Length || texCoords.Length != positions.Length ||
                    tangents.Length != positions.Length)
                {
                    throw new InvalidDataException("GLB vertex attribute counts do not match POSITION.");
                }
                var indices = primitive.TryGetProperty("indices", out var indexAccessor)
                    ? ReadIndices(root, binary, indexAccessor.GetInt32())
                    : Enumerable.Range(0, positions.Length).Select(index => checked((uint)index)).ToArray();
                if (indices.Length % 3 != 0 || indices.Any(index => index >= positions.Length))
                    throw new InvalidDataException("GLB primitive contains invalid triangle indices.");
                if (!attributes.TryGetProperty("NORMAL", out _))
                    GenerateNormals(positions, indices, normals);
                var materialSlot = primitive.TryGetProperty("material", out var materialElement)
                    ? materialElement.GetInt32() : -1;
                var relativePath = $"meshes/{meshName}-{primitiveIndex}.nmesh";
                using (var output = context.CreateArtifact(relativePath))
                    WriteMesh(output, positions, normals, texCoords, tangents, indices,
                        materialSlot);
                artifacts.Add(new AssetArtifact(
                    $"mesh/{meshName}/{primitiveIndex}", "nico/static-mesh", relativePath));
                primitiveIndex++;
            }
            meshIndex++;
        }
        if (artifacts.Count == 0)
            throw new InvalidDataException("GLB contains no mesh primitives.");
        return artifacts;
    }

    /// <summary>Imports glTF standard material factors as independently addressable artifacts.</summary>
    /// <param name="context">Artifact output context.</param>
    /// <param name="root">glTF JSON root.</param>
    /// <returns>Published material artifacts.</returns>
    private static IReadOnlyList<AssetArtifact> ImportMaterials(
        AssetImportContext context,
        JsonElement root)
    {
        if (!root.TryGetProperty("materials", out var materials))
            return [];
        var artifacts = new List<AssetArtifact>();
        var materialIndex = 0;
        foreach (var material in materials.EnumerateArray())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var name = material.TryGetProperty("name", out var nameElement)
                ? Sanitize(nameElement.GetString(), $"material-{materialIndex}")
                : $"material-{materialIndex}";
            var pbr = material.TryGetProperty("pbrMetallicRoughness", out var pbrElement)
                ? pbrElement : default;
            var baseColor = Vector4.One;
            if (pbr.ValueKind != JsonValueKind.Undefined &&
                pbr.TryGetProperty("baseColorFactor", out var factor))
            {
                baseColor = new Vector4(factor[0].GetSingle(), factor[1].GetSingle(),
                    factor[2].GetSingle(), factor[3].GetSingle());
            }
            var metallic = pbr.ValueKind != JsonValueKind.Undefined &&
                pbr.TryGetProperty("metallicFactor", out var metallicElement)
                ? metallicElement.GetSingle() : 1f;
            var roughness = pbr.ValueKind != JsonValueKind.Undefined &&
                pbr.TryGetProperty("roughnessFactor", out var roughnessElement)
                ? roughnessElement.GetSingle() : 1f;
            var textureSlot = -1;
            if (pbr.ValueKind != JsonValueKind.Undefined &&
                pbr.TryGetProperty("baseColorTexture", out var texture) &&
                texture.TryGetProperty("index", out var textureIndex))
            {
                textureSlot = textureIndex.GetInt32();
            }
            var doubleSided = material.TryGetProperty("doubleSided", out var doubleSidedElement) &&
                doubleSidedElement.GetBoolean();
            var relativePath = $"materials/{name}-{materialIndex}.nmaterial";
            using (var output = context.CreateArtifact(relativePath))
            using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("NMATL001"u8);
                writer.Write(1u);
                Write(writer, baseColor);
                writer.Write(metallic);
                writer.Write(roughness);
                writer.Write(doubleSided);
                writer.Write(textureSlot);
            }
            artifacts.Add(new AssetArtifact($"material/{materialIndex}",
                "nico/standard-material", relativePath));
            materialIndex++;
        }
        return artifacts;
    }

    /// <summary>Extracts embedded GLB images using texture-index sub-asset identities.</summary>
    /// <param name="context">Artifact output context.</param>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <returns>Published compressed texture artifacts.</returns>
    private static IReadOnlyList<AssetArtifact> ImportTextures(
        AssetImportContext context,
        JsonElement root,
        byte[] binary)
    {
        if (!root.TryGetProperty("textures", out var textures))
            return [];
        if (!root.TryGetProperty("images", out var images))
            throw new InvalidDataException("GLB textures reference a missing images array.");
        var artifacts = new List<AssetArtifact>();
        var textureIndex = 0;
        foreach (var texture in textures.EnumerateArray())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var sourceIndex = texture.GetProperty("source").GetInt32();
            if ((uint)sourceIndex >= images.GetArrayLength())
                throw new InvalidDataException("GLB texture image index is out of range.");
            var image = images[sourceIndex];
            if (!image.TryGetProperty("bufferView", out var bufferViewElement))
                throw new InvalidDataException("GLB external image URIs are not supported.");
            var mimeType = image.GetProperty("mimeType").GetString();
            _ = mimeType switch
            {
                "image/png" => true,
                "image/jpeg" => true,
                _ => throw new InvalidDataException($"GLB image type '{mimeType}' is unsupported.")
            };
            var bytes = ReadBufferView(root, binary, bufferViewElement.GetInt32(), "image");
            ImageResult decoded;
            try
            {
                decoded = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("GLB embedded image could not be decoded.", exception);
            }
            var relativePath = $"textures/texture-{textureIndex}.ntexture";
            using (var output = context.CreateArtifact(relativePath))
            using (var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("NTEX0001"u8);
                writer.Write(1u);
                writer.Write(checked((uint)decoded.Width));
                writer.Write(checked((uint)decoded.Height));
                writer.Write((byte)1);
                writer.Write(decoded.Data);
            }
            artifacts.Add(new AssetArtifact($"texture/{textureIndex}",
                "nico/texture2d", relativePath));
            textureIndex++;
        }
        return artifacts;
    }

    /// <summary>Copies one validated binary buffer view.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="viewIndex">Buffer-view index.</param>
    /// <param name="purpose">Purpose used in diagnostics.</param>
    /// <returns>The copied buffer-view bytes.</returns>
    private static byte[] ReadBufferView(
        JsonElement root,
        byte[] binary,
        int viewIndex,
        string purpose)
    {
        var views = root.GetProperty("bufferViews");
        if ((uint)viewIndex >= views.GetArrayLength())
            throw new InvalidDataException($"GLB {purpose} buffer view is out of range.");
        var view = views[viewIndex];
        if (view.GetProperty("buffer").GetInt32() != 0)
            throw new InvalidDataException("GLB external buffers are not supported.");
        var offset = view.TryGetProperty("byteOffset", out var offsetElement)
            ? offsetElement.GetInt32() : 0;
        var length = view.GetProperty("byteLength").GetInt32();
        if (offset < 0 || length < 0 || (long)offset + length > binary.Length)
            throw new InvalidDataException($"GLB {purpose} buffer view exceeds the binary chunk.");
        return binary.AsSpan(offset, length).ToArray();
    }

    /// <summary>Reads one tightly or strided floating-point VEC2 accessor.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="accessorIndex">Accessor index.</param>
    /// <param name="semantic">Attribute semantic used in diagnostics.</param>
    /// <returns>Decoded vectors.</returns>
    private static Vector2[] ReadVector2(JsonElement root, byte[] binary, int accessorIndex, string semantic)
    {
        var view = ResolveAccessor(root, binary, accessorIndex, "VEC2", 5126, semantic);
        var result = new Vector2[view.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var offset = view.Offset + index * view.Stride;
            result[index] = new Vector2(ReadSingle(binary, offset), ReadSingle(binary, offset + 4));
        }
        return result;
    }

    /// <summary>Reads one tightly or strided floating-point VEC3 accessor.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="accessorIndex">Accessor index.</param>
    /// <param name="semantic">Attribute semantic used in diagnostics.</param>
    /// <returns>Decoded vectors.</returns>
    private static Vector3[] ReadVector3(JsonElement root, byte[] binary, int accessorIndex, string semantic)
    {
        var view = ResolveAccessor(root, binary, accessorIndex, "VEC3", 5126, semantic);
        var result = new Vector3[view.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var offset = view.Offset + index * view.Stride;
            result[index] = new Vector3(ReadSingle(binary, offset), ReadSingle(binary, offset + 4),
                ReadSingle(binary, offset + 8));
        }
        return result;
    }

    /// <summary>Reads one tightly or strided floating-point VEC4 accessor.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="accessorIndex">Accessor index.</param>
    /// <param name="semantic">Attribute semantic used in diagnostics.</param>
    /// <returns>Decoded vectors.</returns>
    private static Vector4[] ReadVector4(JsonElement root, byte[] binary, int accessorIndex, string semantic)
    {
        var view = ResolveAccessor(root, binary, accessorIndex, "VEC4", 5126, semantic);
        var result = new Vector4[view.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var offset = view.Offset + index * view.Stride;
            result[index] = new Vector4(ReadSingle(binary, offset), ReadSingle(binary, offset + 4),
                ReadSingle(binary, offset + 8), ReadSingle(binary, offset + 12));
        }
        return result;
    }

    /// <summary>Reads an unsigned scalar index accessor into a uniform 32-bit representation.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="accessorIndex">Accessor index.</param>
    /// <returns>Decoded indices.</returns>
    private static uint[] ReadIndices(JsonElement root, byte[] binary, int accessorIndex)
    {
        var accessors = root.GetProperty("accessors");
        var accessor = accessors[accessorIndex];
        var componentType = accessor.GetProperty("componentType").GetInt32();
        var elementSize = componentType switch { 5121 => 1, 5123 => 2, 5125 => 4,
            _ => throw new InvalidDataException("GLB indices must be unsigned bytes, shorts, or ints.") };
        var view = ResolveAccessor(root, binary, accessorIndex, "SCALAR", componentType, "indices");
        var result = new uint[view.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var offset = view.Offset + index * view.Stride;
            result[index] = elementSize switch
            {
                1 => binary[offset],
                2 => BitConverter.ToUInt16(binary, offset),
                _ => BitConverter.ToUInt32(binary, offset)
            };
        }
        return result;
    }

    /// <summary>Resolves and validates an accessor's binary range.</summary>
    /// <param name="root">glTF JSON root.</param>
    /// <param name="binary">GLB binary chunk.</param>
    /// <param name="accessorIndex">Accessor index.</param>
    /// <param name="type">Required accessor shape.</param>
    /// <param name="componentType">Required component type.</param>
    /// <param name="semantic">Attribute semantic used in diagnostics.</param>
    /// <returns>Validated accessor range.</returns>
    private static AccessorView ResolveAccessor(
        JsonElement root, byte[] binary, int accessorIndex, string type, int componentType,
        string semantic)
    {
        var accessors = root.GetProperty("accessors");
        if ((uint)accessorIndex >= accessors.GetArrayLength())
            throw new InvalidDataException($"GLB {semantic} accessor is out of range.");
        var accessor = accessors[accessorIndex];
        if (accessor.GetProperty("type").GetString() != type ||
            accessor.GetProperty("componentType").GetInt32() != componentType ||
            !accessor.TryGetProperty("bufferView", out var bufferViewIndex))
        {
            throw new InvalidDataException($"GLB {semantic} accessor has an unsupported layout.");
        }
        var componentSize = componentType switch { 5121 => 1, 5123 => 2, 5125 or 5126 => 4,
            _ => throw new InvalidDataException($"GLB {semantic} component type is unsupported.") };
        var components = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4,
            _ => throw new InvalidDataException($"GLB {semantic} type is unsupported.") };
        var elementSize = componentSize * components;
        var bufferViews = root.GetProperty("bufferViews");
        var view = bufferViews[bufferViewIndex.GetInt32()];
        if (view.GetProperty("buffer").GetInt32() != 0)
            throw new InvalidDataException("GLB external buffers are not supported.");
        var offset = (view.TryGetProperty("byteOffset", out var viewOffset) ? viewOffset.GetInt32() : 0)
            + (accessor.TryGetProperty("byteOffset", out var accessorOffset)
                ? accessorOffset.GetInt32() : 0);
        var stride = view.TryGetProperty("byteStride", out var strideElement)
            ? strideElement.GetInt32() : elementSize;
        var count = accessor.GetProperty("count").GetInt32();
        if (offset < 0 || count < 0 || stride < elementSize ||
            (count > 0 && (long)offset + (long)(count - 1) * stride + elementSize > binary.Length))
        {
            throw new InvalidDataException($"GLB {semantic} accessor exceeds the binary chunk.");
        }
        return new AccessorView(offset, count, stride);
    }

    /// <summary>Generates smooth vertex normals from indexed triangles.</summary>
    /// <param name="positions">Object-space positions.</param>
    /// <param name="indices">Triangle-list indices.</param>
    /// <param name="normals">Destination normal array.</param>
    private static void GenerateNormals(Vector3[] positions, uint[] indices, Vector3[] normals)
    {
        for (var index = 0; index < indices.Length; index += 3)
        {
            var first = checked((int)indices[index]);
            var second = checked((int)indices[index + 1]);
            var third = checked((int)indices[index + 2]);
            var face = Vector3.Cross(positions[second] - positions[first],
                positions[third] - positions[first]);
            normals[first] += face;
            normals[second] += face;
            normals[third] += face;
        }
        for (var index = 0; index < normals.Length; index++)
            normals[index] = normals[index].LengthSquared() > 0f
                ? Vector3.Normalize(normals[index]) : Vector3.UnitY;
    }

    /// <summary>Writes one versioned little-endian Nico static-mesh artifact.</summary>
    /// <param name="output">Artifact output stream.</param>
    /// <param name="positions">Object-space positions.</param>
    /// <param name="normals">Object-space normals.</param>
    /// <param name="texCoords">Primary texture coordinates.</param>
    /// <param name="tangents">Tangent vectors and handedness.</param>
    /// <param name="indices">Triangle-list indices.</param>
    /// <param name="materialSlot">Source material slot.</param>
    private static void WriteMesh(
        Stream output, Vector3[] positions, Vector3[] normals, Vector2[] texCoords,
        Vector4[] tangents, uint[] indices, int materialSlot)
    {
        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);
        writer.Write("NMESH001"u8);
        writer.Write(1u);
        writer.Write(checked((uint)positions.Length));
        writer.Write(checked((uint)indices.Length));
        writer.Write(materialSlot);
        for (var index = 0; index < positions.Length; index++)
        {
            Write(writer, positions[index]);
            Write(writer, normals[index]);
            Write(writer, texCoords[index]);
            Write(writer, tangents[index]);
        }
        foreach (var value in indices)
            writer.Write(value);
    }

    /// <summary>Writes a vector.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="value">Vector value.</param>
    private static void Write(BinaryWriter writer, Vector2 value) { writer.Write(value.X); writer.Write(value.Y); }

    /// <summary>Writes a vector.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="value">Vector value.</param>
    private static void Write(BinaryWriter writer, Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }

    /// <summary>Writes a vector.</summary>
    /// <param name="writer">Artifact writer.</param>
    /// <param name="value">Vector value.</param>
    private static void Write(BinaryWriter writer, Vector4 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); writer.Write(value.W); }

    /// <summary>Reads one little-endian single.</summary>
    /// <param name="bytes">Source bytes.</param>
    /// <param name="offset">Byte offset.</param>
    /// <returns>Decoded value.</returns>
    private static float ReadSingle(byte[] bytes, int offset)
    {
        return BitConverter.Int32BitsToSingle(BitConverter.ToInt32(bytes, offset));
    }

    /// <summary>Creates a filesystem-safe stable name.</summary>
    /// <param name="value">Untrusted source name.</param>
    /// <param name="fallback">Fallback when the name is empty.</param>
    /// <returns>Filesystem-safe name.</returns>
    private static string Sanitize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;
        var result = new string(value.Select(character => char.IsLetterOrDigit(character) ||
            character is '-' or '_' ? character : '-').ToArray()).Trim('-');
        return result.Length == 0 ? fallback : result;
    }

    /// <summary>Resolved binary accessor range.</summary>
    private readonly record struct AccessorView(int Offset, int Count, int Stride);
}
