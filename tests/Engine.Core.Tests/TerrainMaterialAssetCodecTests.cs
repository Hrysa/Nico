using System.Numerics;
using Engine.Core;
using Xunit;

namespace Engine.Core.Tests;

public sealed class TerrainMaterialAssetCodecTests
{
    /// <summary>Round-trips all terrain-layer PBR factors and texture references.</summary>
    [Fact]
    public void SaveLoad_Layer_PreservesProperties()
    {
        var layer = new TerrainLayerAsset
        {
            BaseColor = new Vector4(0.2f, 0.4f, 0.6f, 0.8f),
            BaseColorTexture = new AssetReference(AssetId.New(), "base"),
            NormalTexture = new AssetReference(AssetId.New(), "normal"),
            MetallicRoughnessTexture = new AssetReference(AssetId.New(), "metal-rough"),
            Metallic = 0.35f,
            Roughness = 0.65f,
            Tiling = new Vector2(8f, 12f)
        };
        using var stream = new MemoryStream();

        TerrainMaterialAssetCodec.SaveLayer(stream, layer);
        stream.Position = 0;
        var actual = TerrainMaterialAssetCodec.LoadLayer(stream);

        Assert.Equal(layer.BaseColor, actual.BaseColor);
        Assert.Equal(layer.BaseColorTexture, actual.BaseColorTexture);
        Assert.Equal(layer.NormalTexture, actual.NormalTexture);
        Assert.Equal(layer.MetallicRoughnessTexture, actual.MetallicRoughnessTexture);
        Assert.Equal(layer.Metallic, actual.Metallic);
        Assert.Equal(layer.Roughness, actual.Roughness);
        Assert.Equal(layer.Tiling, actual.Tiling);
    }

    /// <summary>Round-trips ordered layers and normalized RGBA paint weights.</summary>
    [Fact]
    public void SaveLoad_Material_PreservesLayersAndWeights()
    {
        var first = new AssetReference(AssetId.New(), "main");
        var second = new AssetReference(AssetId.New(), "main");
        var material = new TerrainMaterialAsset(2, 2);
        material.Layers.Add(first);
        material.Layers.Add(second);
        material.UpdateWeights([
            255, 0, 0, 0, 64, 191, 0, 0,
            128, 127, 0, 0, 0, 255, 0, 0
        ]);
        using var stream = new MemoryStream();

        TerrainMaterialAssetCodec.SaveMaterial(stream, material);
        stream.Position = 0;
        var actual = TerrainMaterialAssetCodec.LoadMaterial(stream);

        Assert.Equal(2, actual.Width);
        Assert.Equal(2, actual.Depth);
        Assert.Equal([first, second], actual.Layers);
        Assert.Equal(material.CopyWeights(), actual.CopyWeights());
        Assert.Equal(1f, actual.GetWeight(1, 0, 0) + actual.GetWeight(1, 0, 1) +
            actual.GetWeight(1, 0, 2) + actual.GetWeight(1, 0, 3), 5);
    }
}
