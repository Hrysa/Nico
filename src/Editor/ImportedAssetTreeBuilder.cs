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
        ArgumentNullException.ThrowIfNull(source);
        if (objects is null || objects.Count == 0)
            return;
        AddKind(source, objects, "node", "Nodes");
        AddKind(source, objects, "skeleton", "Skeletons");
        AddKind(source, objects, "animation", "Animations");
    }

    /// <summary>Adds one categorized object hierarchy.</summary>
    /// <param name="source">Physical source file node.</param>
    /// <param name="objects">All imported object descriptions.</param>
    /// <param name="kind">Object category to add.</param>
    /// <param name="groupName">Human-readable category label.</param>
    private static void AddKind(
        FileSystemNode source,
        IReadOnlyList<AssetImportObject> objects,
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
        var nodes = new Dictionary<string, ImportedAssetObjectNode>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            nodes.Add(entry.Key, new ImportedAssetObjectNode(
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
