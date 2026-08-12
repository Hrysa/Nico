using Engine.Core;

namespace Engine.UI;

/// <summary>Identifies where a dragged value would be inserted relative to a tree row.</summary>
public enum TreeViewDropPosition
{
    /// <summary>Insert immediately before the target row.</summary>
    Above,

    /// <summary>Insert as the final child of the target row.</summary>
    Inside,

    /// <summary>Insert immediately after the target row.</summary>
    Below
}

/// <summary>Describes a semantic tree drop resolved from a routed drag position.</summary>
/// <param name="Item">Target row item, or null for empty tree space.</param>
/// <param name="Position">Position relative to the target row.</param>
/// <param name="Parent">Destination parent, or null for the tree root collection.</param>
/// <param name="InsertionIndex">Insertion index within the destination collection.</param>
public readonly record struct TreeViewDropTarget(
    Node? Item,
    TreeViewDropPosition Position,
    Node? Parent,
    int InsertionIndex);
