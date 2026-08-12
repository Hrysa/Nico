using System.Numerics;
using Engine.Core;
using Xunit;

namespace Engine.Core.Tests;

public sealed class StandardMaterialAssetCodecTests
{
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
