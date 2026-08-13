using System.Numerics;
using System.Text;

namespace Engine.Core;

/// <summary>Contains persistent renderer-independent standard-material asset data.</summary>
public sealed class StandardMaterialAsset
{
    /// <summary>Gets or sets the linear base-color multiplier.</summary>
    public Vector4 BaseColor { get; set; } = new(0.8f, 0.8f, 0.8f, 1f);

    /// <summary>Gets or sets the optional persistent base-color texture.</summary>
    public AssetReference? BaseColorTexture { get; set; }

    /// <summary>Gets or sets the optional persistent normal map texture.</summary>
    public AssetReference? NormalTexture { get; set; }

    /// <summary>Gets or sets the optional persistent metallic-roughness texture.</summary>
    public AssetReference? MetallicRoughnessTexture { get; set; }

    /// <summary>Gets or sets metallic response in the range zero through one.</summary>
    public float Metallic { get; set; }

    /// <summary>Gets or sets surface roughness in the range zero through one.</summary>
    public float Roughness { get; set; } = 0.5f;

    /// <summary>Gets or sets whether back-face culling is disabled.</summary>
    public bool DoubleSided { get; set; }

    /// <summary>Creates an independent material-data copy.</summary>
    /// <returns>A material containing the same persistent values.</returns>
    public StandardMaterialAsset Clone()
    {
        return new StandardMaterialAsset
        {
            BaseColor = BaseColor,
            BaseColorTexture = BaseColorTexture,
            NormalTexture = NormalTexture,
            MetallicRoughnessTexture = MetallicRoughnessTexture,
            Metallic = Metallic,
            Roughness = Roughness,
            DoubleSided = DoubleSided
        };
    }
}

/// <summary>Owns the single binary contract for standard-material source and runtime artifacts.</summary>
public static class StandardMaterialAssetCodec
{
    private static ReadOnlySpan<byte> Magic => "NMATL002"u8;

    /// <summary>Writes one standard-material artifact.</summary>
    /// <param name="stream">Writable artifact stream.</param>
    /// <param name="material">Persistent material data.</param>
    public static void Save(Stream stream, StandardMaterialAsset material)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(material);
        Validate(material);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(material.BaseColor.X);
        writer.Write(material.BaseColor.Y);
        writer.Write(material.BaseColor.Z);
        writer.Write(material.BaseColor.W);
        writer.Write(material.Metallic);
        writer.Write(material.Roughness);
        writer.Write(material.DoubleSided);
        writer.Write(material.BaseColorTexture.HasValue);
        if (material.BaseColorTexture is { } texture)
        {
            writer.Write(texture.Asset.Value.ToByteArray());
            writer.Write(texture.SubAsset ?? string.Empty);
        }
        writer.Write(material.NormalTexture.HasValue);
        if (material.NormalTexture is { } normalTexture)
        {
            writer.Write(normalTexture.Asset.Value.ToByteArray());
            writer.Write(normalTexture.SubAsset ?? string.Empty);
        }
        writer.Write(material.MetallicRoughnessTexture.HasValue);
        if (material.MetallicRoughnessTexture is { } roughnessTexture)
        {
            writer.Write(roughnessTexture.Asset.Value.ToByteArray());
            writer.Write(roughnessTexture.SubAsset ?? string.Empty);
        }
    }

    /// <summary>Reads one standard-material artifact.</summary>
    /// <param name="stream">Readable artifact stream.</param>
    /// <returns>Decoded persistent material data.</returns>
    public static StandardMaterialAsset Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
                throw new InvalidDataException("Standard material artifact has an invalid signature.");
            var material = new StandardMaterialAsset
            {
                BaseColor = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                    reader.ReadSingle()),
                Metallic = reader.ReadSingle(),
                Roughness = reader.ReadSingle(),
                DoubleSided = reader.ReadBoolean()
            };
            if (reader.ReadBoolean())
            {
                var bytes = reader.ReadBytes(16);
                if (bytes.Length != 16)
                    throw new InvalidDataException("Material texture asset ID is truncated.");
                var guid = new Guid(bytes);
                if (guid == Guid.Empty)
                    throw new InvalidDataException("Material texture asset ID is empty.");
                var subAsset = reader.ReadString();
                material.BaseColorTexture = new AssetReference(new AssetId(guid),
                    string.IsNullOrEmpty(subAsset) ? null : subAsset);
            }
            material.NormalTexture = ReadOptionalTextureReference(reader, stream);
            material.MetallicRoughnessTexture = ReadOptionalTextureReference(reader, stream);
            if (stream.CanSeek && stream.Position != stream.Length)
                throw new InvalidDataException("Standard material artifact payload length is invalid.");
            Validate(material);
            return material;
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException)
        {
            throw new InvalidDataException("Standard material artifact payload is invalid.", exception);
        }
    }

    /// <summary>Validates the persistent standard-material contract.</summary>
    /// <param name="material">Material data to validate.</param>
    private static void Validate(StandardMaterialAsset material)
    {
        if (!IsUnit(material.BaseColor.X) || !IsUnit(material.BaseColor.Y) ||
            !IsUnit(material.BaseColor.Z) || !IsUnit(material.BaseColor.W))
            throw new InvalidDataException("Material base color must contain finite values from zero through one.");
        if (!IsUnit(material.Metallic))
            throw new InvalidDataException("Material metallic must be a finite value from zero through one.");
        if (!IsUnit(material.Roughness))
            throw new InvalidDataException("Material roughness must be a finite value from zero through one.");
    }

    /// <summary>Checks one normalized material scalar.</summary>
    /// <param name="value">Candidate scalar.</param>
    /// <returns>True when finite and normalized.</returns>
    private static bool IsUnit(float value) => float.IsFinite(value) && value is >= 0f and <= 1f;

    /// <summary>Reads an optional texture asset reference from the input stream.</summary>
    /// <param name="reader">Stream reader for serialized values.</param>
    /// <param name="stream">Underlying stream used for end-of-payload checks.</param>
    /// <returns>The decoded reference, or null when omitted.</returns>
    private static AssetReference? ReadOptionalTextureReference(
        BinaryReader reader,
        Stream stream)
    {
        if (!stream.CanSeek || stream.Position == stream.Length)
            return null;
        if (!reader.ReadBoolean())
            return null;

        var bytes = reader.ReadBytes(16);
        if (bytes.Length != 16)
            throw new InvalidDataException("Material texture asset ID is truncated.");
        var guid = new Guid(bytes);
        if (guid == Guid.Empty)
            throw new InvalidDataException("Material texture asset ID is empty.");

        var subAsset = reader.ReadString();
        return new AssetReference(new AssetId(guid),
            string.IsNullOrEmpty(subAsset) ? null : subAsset);
    }
}
