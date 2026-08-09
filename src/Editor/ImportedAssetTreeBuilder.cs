using Engine.Assets;
using Engine.Core;

namespace Editor;

/// <summary>Builds editor-only hierarchy rows for objects contained inside imported assets.</summary>
public static class ImportedAssetTreeBuilder
{
    /// <summary>Adds categorized imported-object hierarchies beneath a source file.</summary>
    /// <param name="source">Physical source file node.</param>
    /// <param name="objects">Imported object descriptions.</param>
    public static void AddObjects(
        FileSystemNode source,
        IReadOnlyList<AssetImportObject>? objects)
    {
        AddObjects(source, null, objects, null);
    }

    /// <summary>Adds categorized imported objects with draggable artifact-backed rows.</summary>
    /// <param name="source">Physical source file node.</param>
    /// <param name="assetId">Persistent identity of the source asset.</param>
    /// <param name="objects">Imported object descriptions.</param>
    /// <param name="artifacts">Published artifacts represented by object rows.</param>
    public static void AddObjects(
        FileSystemNode source,
        AssetId? assetId,
        IReadOnlyList<AssetImportObject>? objects,
        IReadOnlyList<AssetArtifact>? artifacts)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (objects is null || objects.Count == 0)
            return;
        AddKind(source, assetId, objects, artifacts, "node", "Nodes");
        AddKind(source, assetId, objects, artifacts, "skeleton", "Skeletons");
        AddKind(source, assetId, objects, artifacts, "animation", "Animations");
    }

    /// <summary>Adds one categorized object hierarchy.</summary>
    /// <param name="source">Physical source file node.</param>
    /// <param name="assetId">Optional persistent source identity.</param>
    /// <param name="objects">All imported object descriptions.</param>
    /// <param name="artifacts">Optional published artifacts represented by rows.</param>
    /// <param name="kind">Object category to add.</param>
    /// <param name="groupName">Human-readable category label.</param>
    private static void AddKind(
        FileSystemNode source,
        AssetId? assetId,
        IReadOnlyList<AssetImportObject> objects,
        IReadOnlyList<AssetArtifact>? artifacts,
        string kind,
        string groupName)
    {
        var entries = new List<AssetImportObject>();
        for (var index = 0; index < objects.Count; index++)
        {
            if (string.Equals(objects[index].Kind, kind, StringComparison.Ordinal))
                entries.Add(objects[index]);
        }
        if (entries.Count == 0)
            return;
        var group = new Node { Name = groupName };
        source.AddChild(group);
        var nodes = new Dictionary<string, Node>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            AssetArtifact? artifact = null;
            if (entry.ArtifactKey is not null && artifacts is not null)
            {
                for (var artifactIndex = 0; artifactIndex < artifacts.Count; artifactIndex++)
                {
                    if (artifacts[artifactIndex].Key == entry.ArtifactKey)
                    {
                        artifact = artifacts[artifactIndex];
                        break;
                    }
                }
            }
            nodes.Add(entry.Key, artifact is not null && assetId is { } id
                ? new ImportedSubAssetNode(source.FullPath,
                    new AssetReference(id, artifact.Key), artifact.ContentType, entry.Name)
                : new ImportedAssetObjectNode(
                    source.FullPath, entry.Key, entry.Kind, entry.Name));
        }
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var node = nodes[entry.Key];
            if (entry.ParentKey is not null && nodes.TryGetValue(entry.ParentKey, out var parent))
                parent.AddChild(node);
            else
                group.AddChild(node);
        }
    }
}
