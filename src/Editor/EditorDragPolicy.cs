using Engine.Core;

namespace Editor;

/// <summary>Identifies one editor tree item and the tree that originated its drag.</summary>
/// <param name="Item">Dragged hierarchy or filesystem node.</param>
/// <param name="FromHierarchy">Whether the source belongs to the scene hierarchy.</param>
public sealed record EditorTreeDragData(Node Item, bool FromHierarchy);

/// <summary>Defines which editor tree items may participate in asset and scene drag operations.</summary>
public static class EditorDragPolicy
{
    /// <summary>Returns whether a File System row supports any drag operation.</summary>
    /// <param name="source">Candidate File System tree item.</param>
    /// <returns>True for physical entries and runtime imported sub-assets.</returns>
    public static bool CanStartFileSystemDrag(Node source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source is FileSystemNode or ImportedSubAssetNode;
    }

    /// <summary>Returns whether a File System row can instantiate a scene node.</summary>
    /// <param name="source">Candidate dragged item.</param>
    /// <returns>True only for GLB sources and imported static or skinned meshes.</returns>
    public static bool CanInstantiateInHierarchy(Node source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source switch
        {
            ImportedSubAssetNode imported =>
                imported.ContentType is "nico/static-mesh" or "nico/skinned-mesh",
            FileSystemNode file => !file.IsDirectory &&
                Path.GetExtension(file.FullPath).Equals(".glb",
                    StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
