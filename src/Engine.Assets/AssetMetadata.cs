using System.Text.Json;
using Engine.Core;

namespace Engine.Assets;

/// <summary>Contains authoritative engine-managed metadata for one source asset.</summary>
/// <param name="Version">Metadata schema version.</param>
/// <param name="Id">Persistent asset identity.</param>
/// <param name="Importer">Stable importer identifier.</param>
/// <param name="Settings">Importer-owned JSON settings object.</param>
public sealed record AssetMetadata(
    int Version,
    AssetId Id,
    string Importer,
    JsonElement Settings);
