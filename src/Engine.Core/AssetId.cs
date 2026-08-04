using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Engine.Core;

/// <summary>Identifies one persistent project asset independently of its location.</summary>
[JsonConverter(typeof(AssetIdJsonConverter))]
public readonly record struct AssetId
{
    /// <summary>Gets the underlying UUID value.</summary>
    public Guid Value { get; }

    /// <summary>Creates an asset identifier from a non-empty UUID.</summary>
    /// <param name="value">Underlying UUID value.</param>
    public AssetId(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("An asset identifier cannot be empty.", nameof(value));
        Value = value;
    }

    /// <summary>Generates a time-ordered UUIDv7 asset identifier.</summary>
    /// <returns>A new non-empty asset identifier.</returns>
    public static AssetId New()
    {
        return new AssetId(Guid.CreateVersion7());
    }

    /// <summary>Parses a canonical hyphenated UUID asset identifier.</summary>
    /// <param name="text">Canonical UUID text.</param>
    /// <returns>The parsed asset identifier.</returns>
    public static AssetId Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!TryParse(text, out var id))
            throw new FormatException($"'{text}' is not a valid asset identifier.");
        return id;
    }

    /// <summary>Attempts to parse a canonical hyphenated UUID asset identifier.</summary>
    /// <param name="text">Canonical UUID text.</param>
    /// <param name="id">Parsed identifier when successful.</param>
    /// <returns>True when the value is a non-empty canonical UUID.</returns>
    public static bool TryParse(
        string? text,
        [NotNullWhen(true)] out AssetId id)
    {
        if (Guid.TryParseExact(text, "D", out var value) && value != Guid.Empty)
        {
            id = new AssetId(value);
            return true;
        }
        id = default;
        return false;
    }

    /// <summary>Formats this identifier as a canonical lowercase hyphenated UUID.</summary>
    /// <returns>The canonical UUID text.</returns>
    public override string ToString()
    {
        return Value.ToString("D");
    }
}

/// <summary>Serializes asset identifiers as canonical JSON strings.</summary>
public sealed class AssetIdJsonConverter : JsonConverter<AssetId>
{
    /// <inheritdoc/>
    public override AssetId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String ||
            !AssetId.TryParse(reader.GetString(), out var id))
        {
            throw new JsonException("An asset identifier must be a non-empty canonical UUID string.");
        }
        return id;
    }

    /// <inheritdoc/>
    public override void Write(
        Utf8JsonWriter writer,
        AssetId value,
        JsonSerializerOptions options)
    {
        if (value.Value == Guid.Empty)
            throw new JsonException("An asset identifier cannot be empty.");
        writer.WriteStringValue(value.ToString());
    }
}
