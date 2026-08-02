using Engine.Core;

namespace Engine.Scripting;

/// <summary>
/// Provides game scripts with controlled access to an active scene graph.
/// </summary>
public sealed class SceneContext
{
    /// <summary>Gets the synthetic root of the active scene.</summary>
    public Node Root { get; }

    /// <summary>
    /// Creates a scene context for a root node.
    /// </summary>
    /// <param name="root">Synthetic scene root.</param>
    public SceneContext(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = root;
    }

    /// <summary>
    /// Finds the first node with an exact name using depth-first traversal.
    /// </summary>
    /// <param name="name">Node name to find.</param>
    /// <returns>The matching node, or null when none exists.</returns>
    public Node? FindNode(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Enumerate(Root).FirstOrDefault(node =>
            string.Equals(node.Name, name, StringComparison.Ordinal));
    }

    /// <summary>
    /// Finds the first node of a requested type with an exact name.
    /// </summary>
    /// <typeparam name="TNode">Required node type.</typeparam>
    /// <param name="name">Node name to find.</param>
    /// <returns>The matching typed node, or null when none exists.</returns>
    public TNode? FindNode<TNode>(string name) where TNode : Node
    {
        return FindNode(name) as TNode;
    }

    /// <summary>
    /// Creates a named node that a script can attach to the scene graph.
    /// </summary>
    /// <typeparam name="TNode">Node type with a public parameterless constructor.</typeparam>
    /// <param name="name">Name assigned to the new node.</param>
    /// <returns>The new unattached node.</returns>
    public TNode CreateNode<TNode>(string name) where TNode : Node, new()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new TNode { Name = name };
    }

    /// <summary>
    /// Enumerates a node and all descendants depth first.
    /// </summary>
    /// <param name="root">Subtree root.</param>
    /// <returns>The subtree nodes.</returns>
    internal static IEnumerable<Node> Enumerate(Node root)
    {
        yield return root;
        foreach (var child in root.Children)
        foreach (var descendant in Enumerate(child))
            yield return descendant;
    }
}
