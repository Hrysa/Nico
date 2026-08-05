using Engine.Assets;
using Engine.Core;

namespace Editor;

/// <summary>Represents one read-only imported sub-asset beneath a physical source file.</summary>
public sealed class ImportedSubAssetNode : Node
{
    /// <summary>Gets the persistent source and sub-asset reference.</summary>
    public AssetReference Reference { get; }

    /// <summary>Gets the artifact content type used for typed drag-and-drop.</summary>
    public string ContentType { get; }

    /// <summary>Gets the physical source file containing this imported resource.</summary>
    public string SourcePath { get; }

    /// <summary>Creates an imported sub-asset tree entry.</summary>
    /// <param name="sourcePath">Physical source asset path.</param>
    /// <param name="reference">Persistent imported resource reference.</param>
    /// <param name="contentType">Typed artifact content identifier.</param>
    /// <param name="displayName">Human-readable row label.</param>
    public ImportedSubAssetNode(
        string sourcePath,
        AssetReference reference,
        string contentType,
        string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        SourcePath = Path.GetFullPath(sourcePath);
        Reference = reference;
        ContentType = contentType;
        Name = displayName;
    }
}
