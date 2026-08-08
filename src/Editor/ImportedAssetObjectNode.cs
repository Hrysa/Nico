using Engine.Core;

namespace Editor;

/// <summary>Represents one read-only object discovered inside an imported source asset.</summary>
public sealed class ImportedAssetObjectNode : Node
{
    /// <summary>Gets the stable source-local object key.</summary>
    public string Key { get; }

    /// <summary>Gets the stable object category.</summary>
    public string Kind { get; }

    /// <summary>Gets the physical source file containing this object.</summary>
    public string SourcePath { get; }

    /// <summary>Creates one imported object tree entry.</summary>
    /// <param name="sourcePath">Physical source asset path.</param>
    /// <param name="key">Stable source-local object key.</param>
    /// <param name="kind">Stable object category.</param>
    /// <param name="displayName">Human-readable row label.</param>
    public ImportedAssetObjectNode(
        string sourcePath,
        string key,
        string kind,
        string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        SourcePath = Path.GetFullPath(sourcePath);
        Key = key;
        Kind = kind;
        Name = displayName;
    }
}
