using System.Text.Json;
using Engine.Core;
using Xunit;

namespace Engine.Core.Tests;

public class AssetIdTests
{
    /// <summary>Verifies generated asset identities use non-empty UUIDv7 values.</summary>
    [Fact]
    public void New_CreatesUuidVersion7()
    {
        var id = AssetId.New();

        Assert.NotEqual(Guid.Empty, id.Value);
        Assert.Equal(7, id.Value.Version);
    }

    /// <summary>Verifies asset identity text has one canonical round-trippable representation.</summary>
    [Fact]
    public void ToString_Parse_RoundTripsCanonicalValue()
    {
        var id = AssetId.New();

        var text = id.ToString();
        var parsed = AssetId.Parse(text);

        Assert.Equal(36, text.Length);
        Assert.Equal(text.ToLowerInvariant(), text);
        Assert.Equal(id, parsed);
    }

    /// <summary>Verifies empty and noncanonical UUID text cannot become an asset identity.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-an-id")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("0197f48ffcd37a54a9c39bbdd68f9f42")]
    public void TryParse_InvalidValue_ReturnsFalse(string text)
    {
        Assert.False(AssetId.TryParse(text, out _));
    }

    /// <summary>Verifies JSON stores an asset identity as one canonical string.</summary>
    [Fact]
    public void JsonSerialization_RoundTripsCanonicalString()
    {
        var id = AssetId.New();

        var json = JsonSerializer.Serialize(id);
        var restored = JsonSerializer.Deserialize<AssetId>(json);

        Assert.Equal($"\"{id}\"", json);
        Assert.Equal(id, restored);
    }

    /// <summary>Verifies default asset identities cannot be serialized as persistent references.</summary>
    [Fact]
    public void JsonSerialization_DefaultId_Throws()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(default(AssetId)));
    }

    /// <summary>Verifies sub-assets retain the source identity and stable imported key.</summary>
    [Fact]
    public void AssetReference_WithSubAsset_PreservesBothParts()
    {
        var id = AssetId.New();

        var reference = new AssetReference(id, "animation/Walk");

        Assert.Equal(id, reference.Asset);
        Assert.Equal("animation/Walk", reference.SubAsset);
        Assert.Equal($"{id}#animation/Walk", reference.ToString());
    }
}
