using System.Text.Json.Serialization;

namespace Engine.Core;

/// <summary>References an asset or one stable imported output within that asset.</summary>
public readonly record struct AssetReference
{
    /// <summary>Gets the persistent source asset identifier.</summary>
    public AssetId Asset { get; }

    /// <summary>Gets the optional importer-defined stable sub-asset key.</summary>
    public string? SubAsset { get; }

    /// <summary>Creates a persistent asset reference.</summary>
    /// <param name="asset">Persistent source asset identifier.</param>
    /// <param name="subAsset">Optional importer-defined stable sub-asset key.</param>
    [JsonConstructor]
    public AssetReference(AssetId asset, string? subAsset = null)
    {
        if (asset.Value == Guid.Empty)
            throw new ArgumentException("An asset reference requires a valid asset identifier.", nameof(asset));
        if (subAsset is not null && string.IsNullOrWhiteSpace(subAsset))
            throw new ArgumentException("A sub-asset key cannot be empty or whitespace.", nameof(subAsset));
        Asset = asset;
        SubAsset = subAsset;
    }

    /// <summary>Formats the asset ID followed by an optional sub-asset fragment.</summary>
    /// <returns>A stable diagnostic representation of this reference.</returns>
    public override string ToString()
    {
        return SubAsset is null ? Asset.ToString() : $"{Asset}#{SubAsset}";
    }
}
