using System.Numerics;
using Engine.Core;
using Xunit;

namespace Engine.Core.Tests;

public sealed class StandardMaterialAssetCodecTests
{
    /// <summary>Round-trips all PBR factors and texture references.</summary>
    [Fact]
    public void SaveLoad_PbrMaterial_PreservesProperties()
    {
        var material = new StandardMaterialAsset
        {
            BaseColor = new Vector4(0.2f, 0.4f, 0.6f, 0.8f),
            BaseColorTexture = new AssetReference(AssetId.New(), "base"),
            NormalTexture = new AssetReference(AssetId.New(), "normal"),
            MetallicRoughnessTexture = new AssetReference(AssetId.New(), "metal-rough"),
            Metallic = 0.35f,
            Roughness = 0.65f,
            DoubleSided = true
        };
        using var stream = new MemoryStream();

        StandardMaterialAssetCodec.Save(stream, material);
        stream.Position = 0;
        var actual = StandardMaterialAssetCodec.Load(stream);

        Assert.Equal(material.BaseColor, actual.BaseColor);
        Assert.Equal(material.BaseColorTexture, actual.BaseColorTexture);
        Assert.Equal(material.NormalTexture, actual.NormalTexture);
        Assert.Equal(material.MetallicRoughnessTexture, actual.MetallicRoughnessTexture);
        Assert.Equal(material.Metallic, actual.Metallic);
        Assert.Equal(material.Roughness, actual.Roughness);
        Assert.Equal(material.DoubleSided, actual.DoubleSided);
    }

    /// <summary>Rejects obsolete material payloads instead of maintaining a hidden compatibility path.</summary>
    [Fact]
    public void Load_LegacySignature_RejectsPayload()
    {
        using var stream = new MemoryStream("NMATL001"u8.ToArray());

        Assert.Throws<InvalidDataException>(() => StandardMaterialAssetCodec.Load(stream));
    }

    /// <summary>Rejects non-finite or out-of-contract authored values.</summary>
    [Fact]
    public void Save_InvalidNormalizedValue_RejectsMaterial()
    {
        using var stream = new MemoryStream();
        var material = new StandardMaterialAsset
        {
            BaseColor = new Vector4(1f, 1f, float.NaN, 1f)
        };

        Assert.Throws<InvalidDataException>(() =>
            StandardMaterialAssetCodec.Save(stream, material));
    }
}
