using System.Numerics;
using System.Text;

namespace Engine.Core;

/// <summary>Contains one tileable PBR surface used by terrain materials.</summary>
public sealed class TerrainLayerAsset
{
    /// <summary>Gets or sets the linear base-color multiplier.</summary>
    public Vector4 BaseColor { get; set; } = Vector4.One;

    /// <summary>Gets or sets the optional base-color texture.</summary>
    public AssetReference? BaseColorTexture { get; set; }

    /// <summary>Gets or sets the optional tangent-space normal texture.</summary>
    public AssetReference? NormalTexture { get; set; }

    /// <summary>Gets or sets the optional glTF-style metallic-roughness texture.</summary>
    public AssetReference? MetallicRoughnessTexture { get; set; }

    /// <summary>Gets or sets the metallic factor.</summary>
    public float Metallic { get; set; }

    /// <summary>Gets or sets the roughness factor.</summary>
    public float Roughness { get; set; } = 0.8f;

    /// <summary>Gets or sets texture repetitions across terrain UV space.</summary>
    public Vector2 Tiling { get; set; } = new(8f, 8f);

    /// <summary>Creates an independent layer copy.</summary>
    /// <returns>A layer containing the same authored values.</returns>
    public TerrainLayerAsset Clone() => new()
    {
        BaseColor = BaseColor,
        BaseColorTexture = BaseColorTexture,
        NormalTexture = NormalTexture,
        MetallicRoughnessTexture = MetallicRoughnessTexture,
        Metallic = Metallic,
        Roughness = Roughness,
        Tiling = Tiling
    };
}

/// <summary>Contains four ordered terrain layers and their RGBA paint weights.</summary>
public sealed class TerrainMaterialAsset
{
    /// <summary>Maximum number of paintable layers represented by one RGBA map.</summary>
    public const int MaximumLayers = 4;

    private byte[] _weights;

    /// <summary>Creates a terrain material with a fully weighted first channel.</summary>
    /// <param name="width">Weight-map columns.</param>
    /// <param name="depth">Weight-map rows.</param>
    /// <param name="layers">Ordered terrain-layer references.</param>
    /// <param name="weights">Optional tightly packed RGBA8 weights.</param>
    public TerrainMaterialAsset(
        int width = 2,
        int depth = 2,
        IReadOnlyList<AssetReference>? layers = null,
        byte[]? weights = null)
    {
        if (width < 2)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (depth < 2)
            throw new ArgumentOutOfRangeException(nameof(depth));
        if (layers is { Count: > MaximumLayers })
            throw new ArgumentException("Terrain materials support at most four layers.", nameof(layers));
        Width = width;
        Depth = depth;
        Layers = layers is null ? [] : [.. layers];
        var byteCount = checked(width * depth * MaximumLayers);
        if (weights is not null && weights.Length != byteCount)
            throw new ArgumentException("Weight count must equal width times depth times four.",
                nameof(weights));
        _weights = weights is null ? CreateDefaultWeights(width, depth) : (byte[])weights.Clone();
        NormalizeAllWeights();
    }

    /// <summary>Gets the number of weight-map columns.</summary>
    public int Width { get; private set; }

    /// <summary>Gets the number of weight-map rows.</summary>
    public int Depth { get; private set; }

    /// <summary>Gets ordered terrain-layer asset references.</summary>
    public List<AssetReference> Layers { get; }

    /// <summary>Gets one layer weight as a normalized scalar.</summary>
    /// <param name="x">Weight-map column.</param>
    /// <param name="z">Weight-map row.</param>
    /// <param name="layer">Layer channel.</param>
    /// <returns>Normalized paint weight.</returns>
    public float GetWeight(int x, int z, int layer)
    {
        ValidateCoordinate(x, z, layer);
        return _weights[(z * Width + x) * MaximumLayers + layer] / 255f;
    }

    /// <summary>Copies the complete tightly packed RGBA8 paint payload.</summary>
    /// <returns>An independently owned byte array.</returns>
    public byte[] CopyWeights() => (byte[])_weights.Clone();

    /// <summary>Replaces all paint weights while preserving dimensions.</summary>
    /// <param name="weights">Tightly packed RGBA8 replacement payload.</param>
    public void UpdateWeights(ReadOnlySpan<byte> weights)
    {
        if (weights.Length != _weights.Length)
            throw new ArgumentException("Weight count must equal width times depth times four.",
                nameof(weights));
        weights.CopyTo(_weights);
        NormalizeAllWeights();
    }

    /// <summary>Resizes the paint grid using nearest-neighbor weight preservation.</summary>
    /// <param name="width">New weight-map columns.</param>
    /// <param name="depth">New weight-map rows.</param>
    public void Resize(int width, int depth)
    {
        if (width < 2)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (depth < 2)
            throw new ArgumentOutOfRangeException(nameof(depth));
        if (width == Width && depth == Depth)
            return;
        var resized = new byte[checked(width * depth * MaximumLayers)];
        for (var z = 0; z < depth; z++)
        {
            var sourceZ = (int)MathF.Round(z / (float)(depth - 1) * (Depth - 1));
            for (var x = 0; x < width; x++)
            {
                var sourceX = (int)MathF.Round(x / (float)(width - 1) * (Width - 1));
                var source = (sourceZ * Width + sourceX) * MaximumLayers;
                var target = (z * width + x) * MaximumLayers;
                for (var layer = 0; layer < MaximumLayers; layer++)
                    resized[target + layer] = _weights[source + layer];
            }
        }
        Width = width;
        Depth = depth;
        _weights = resized;
    }

    /// <summary>Validates a paint-map coordinate.</summary>
    /// <param name="x">Weight-map column.</param>
    /// <param name="z">Weight-map row.</param>
    /// <param name="layer">Layer channel.</param>
    private void ValidateCoordinate(int x, int z, int layer)
    {
        if ((uint)x >= (uint)Width)
            throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)z >= (uint)Depth)
            throw new ArgumentOutOfRangeException(nameof(z));
        if ((uint)layer >= MaximumLayers)
            throw new ArgumentOutOfRangeException(nameof(layer));
    }

    /// <summary>Creates an opaque first layer for a new weight grid.</summary>
    /// <param name="width">Weight-map columns.</param>
    /// <param name="depth">Weight-map rows.</param>
    /// <returns>Tightly packed default weights.</returns>
    private static byte[] CreateDefaultWeights(int width, int depth)
    {
        var weights = new byte[checked(width * depth * MaximumLayers)];
        for (var index = 0; index < width * depth; index++)
            weights[index * MaximumLayers] = byte.MaxValue;
        return weights;
    }

    /// <summary>Normalizes every texel to a stable sum of 255.</summary>
    private void NormalizeAllWeights()
    {
        for (var index = 0; index < Width * Depth; index++)
        {
            var offset = index * MaximumLayers;
            var sum = _weights[offset] + _weights[offset + 1] +
                _weights[offset + 2] + _weights[offset + 3];
            if (sum == 0)
            {
                _weights[offset] = byte.MaxValue;
                continue;
            }
            var assigned = 0;
            for (var layer = 0; layer < MaximumLayers - 1; layer++)
            {
                var normalized = (int)MathF.Round(_weights[offset + layer] * 255f / sum);
                _weights[offset + layer] = (byte)Math.Clamp(normalized, 0, 255 - assigned);
                assigned += _weights[offset + layer];
            }
            _weights[offset + MaximumLayers - 1] = (byte)(255 - assigned);
        }
    }
}

/// <summary>Serializes terrain layers and terrain materials.</summary>
public static class TerrainMaterialAssetCodec
{
    private static ReadOnlySpan<byte> LayerMagic => "NTLAY001"u8;
    private static ReadOnlySpan<byte> MaterialMagic => "NTMAT001"u8;

    /// <summary>Writes one terrain-layer asset.</summary>
    /// <param name="stream">Writable source or artifact stream.</param>
    /// <param name="layer">Layer values to write.</param>
    public static void SaveLayer(Stream stream, TerrainLayerAsset layer)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(layer);
        ValidateLayer(layer);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(LayerMagic);
        Write(writer, layer.BaseColor);
        writer.Write(layer.Metallic);
        writer.Write(layer.Roughness);
        writer.Write(layer.Tiling.X);
        writer.Write(layer.Tiling.Y);
        Write(writer, layer.BaseColorTexture);
        Write(writer, layer.NormalTexture);
        Write(writer, layer.MetallicRoughnessTexture);
    }

    /// <summary>Reads one terrain-layer asset.</summary>
    /// <param name="stream">Readable source or artifact stream.</param>
    /// <returns>Decoded terrain layer.</returns>
    public static TerrainLayerAsset LoadLayer(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        RequireMagic(reader, LayerMagic, "terrain layer");
        var layer = new TerrainLayerAsset
        {
            BaseColor = new Vector4(reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle()),
            Metallic = reader.ReadSingle(),
            Roughness = reader.ReadSingle(),
            Tiling = new Vector2(reader.ReadSingle(), reader.ReadSingle()),
            BaseColorTexture = ReadReference(reader),
            NormalTexture = ReadReference(reader),
            MetallicRoughnessTexture = ReadReference(reader)
        };
        RequireEnd(stream, "Terrain layer");
        ValidateLayer(layer);
        return layer;
    }

    /// <summary>Writes one terrain-material asset.</summary>
    /// <param name="stream">Writable source or artifact stream.</param>
    /// <param name="material">Material values and paint weights.</param>
    public static void SaveMaterial(Stream stream, TerrainMaterialAsset material)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(material);
        if (material.Layers.Count > TerrainMaterialAsset.MaximumLayers)
            throw new InvalidDataException("Terrain materials support at most four layers.");
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(MaterialMagic);
        writer.Write(material.Width);
        writer.Write(material.Depth);
        writer.Write(material.Layers.Count);
        for (var index = 0; index < material.Layers.Count; index++)
            WriteRequired(writer, material.Layers[index]);
        writer.Write(material.CopyWeights());
    }

    /// <summary>Reads one terrain-material asset.</summary>
    /// <param name="stream">Readable source or artifact stream.</param>
    /// <returns>Decoded terrain material.</returns>
    public static TerrainMaterialAsset LoadMaterial(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        RequireMagic(reader, MaterialMagic, "terrain material");
        var width = reader.ReadInt32();
        var depth = reader.ReadInt32();
        var layerCount = reader.ReadInt32();
        if (width < 2 || depth < 2 || layerCount is < 0 or > TerrainMaterialAsset.MaximumLayers)
            throw new InvalidDataException("Terrain material dimensions or layer count are invalid.");
        var layers = new AssetReference[layerCount];
        for (var index = 0; index < layers.Length; index++)
            layers[index] = ReadRequiredReference(reader);
        var byteCount = checked(width * depth * TerrainMaterialAsset.MaximumLayers);
        var weights = reader.ReadBytes(byteCount);
        if (weights.Length != byteCount)
            throw new InvalidDataException("Terrain material paint weights are truncated.");
        RequireEnd(stream, "Terrain material");
        return new TerrainMaterialAsset(width, depth, layers, weights);
    }

    /// <summary>Validates finite normalized layer values.</summary>
    /// <param name="layer">Candidate layer.</param>
    private static void ValidateLayer(TerrainLayerAsset layer)
    {
        if (!IsUnit(layer.BaseColor.X) || !IsUnit(layer.BaseColor.Y) ||
            !IsUnit(layer.BaseColor.Z) || !IsUnit(layer.BaseColor.W) ||
            !IsUnit(layer.Metallic) || !IsUnit(layer.Roughness) ||
            !float.IsFinite(layer.Tiling.X) || !float.IsFinite(layer.Tiling.Y) ||
            layer.Tiling.X <= 0f || layer.Tiling.Y <= 0f)
            throw new InvalidDataException("Terrain layer values are outside their authored ranges.");
    }

    /// <summary>Checks one finite normalized scalar.</summary>
    /// <param name="value">Candidate scalar.</param>
    /// <returns>True when the value is in zero through one.</returns>
    private static bool IsUnit(float value) => float.IsFinite(value) && value is >= 0f and <= 1f;

    /// <summary>Writes one vector.</summary>
    /// <param name="writer">Destination writer.</param>
    /// <param name="value">Vector value.</param>
    private static void Write(BinaryWriter writer, Vector4 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
    }

    /// <summary>Writes one optional asset reference.</summary>
    /// <param name="writer">Destination writer.</param>
    /// <param name="reference">Optional reference.</param>
    private static void Write(BinaryWriter writer, AssetReference? reference)
    {
        writer.Write(reference.HasValue);
        if (reference is { } value)
            WriteRequired(writer, value);
    }

    /// <summary>Writes one required asset reference.</summary>
    /// <param name="writer">Destination writer.</param>
    /// <param name="reference">Required reference.</param>
    private static void WriteRequired(BinaryWriter writer, AssetReference reference)
    {
        if (reference.Asset.Value == Guid.Empty)
            throw new InvalidDataException("Terrain material references cannot use an empty asset ID.");
        writer.Write(reference.Asset.Value.ToByteArray());
        writer.Write(reference.SubAsset ?? string.Empty);
    }

    /// <summary>Reads one optional asset reference.</summary>
    /// <param name="reader">Source reader.</param>
    /// <returns>Decoded optional reference.</returns>
    private static AssetReference? ReadReference(BinaryReader reader) =>
        reader.ReadBoolean() ? ReadRequiredReference(reader) : null;

    /// <summary>Reads one required asset reference.</summary>
    /// <param name="reader">Source reader.</param>
    /// <returns>Decoded reference.</returns>
    private static AssetReference ReadRequiredReference(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16)
            throw new InvalidDataException("Terrain material asset reference is invalid.");
        var guid = new Guid(bytes);
        if (guid == Guid.Empty)
            throw new InvalidDataException("Terrain material asset reference is invalid.");
        var subAsset = reader.ReadString();
        return new AssetReference(new AssetId(guid), string.IsNullOrEmpty(subAsset) ? null : subAsset);
    }

    /// <summary>Validates an artifact signature.</summary>
    /// <param name="reader">Source reader.</param>
    /// <param name="magic">Expected signature.</param>
    /// <param name="kind">Asset kind used in diagnostics.</param>
    private static void RequireMagic(BinaryReader reader, ReadOnlySpan<byte> magic, string kind)
    {
        if (!reader.ReadBytes(magic.Length).AsSpan().SequenceEqual(magic))
            throw new InvalidDataException($"The {kind} artifact has an invalid signature.");
    }

    /// <summary>Rejects trailing payload bytes.</summary>
    /// <param name="stream">Underlying stream.</param>
    /// <param name="kind">Asset kind used in diagnostics.</param>
    private static void RequireEnd(Stream stream, string kind)
    {
        if (!stream.CanSeek || stream.Position != stream.Length)
            throw new InvalidDataException($"{kind} artifact payload length is invalid.");
    }
}
