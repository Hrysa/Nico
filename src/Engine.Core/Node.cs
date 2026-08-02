using System.Numerics;

namespace Engine.Core;

/// <summary>
/// Base class for all scene graph nodes. Provides transform, parent/child hierarchy, and name.
/// </summary>
public class Node
{
    private readonly List<Node> _children = new();
    private Node? _parent;
    private Vector3 _position;
    private Vector3 _rotation;
    private Vector3 _scale = Vector3.One;

    /// <summary>Gets or sets the local position relative to the parent.</summary>
    public Vector3 Position
    {
        get => _position;
        set
        {
            if (_position == value)
                return;
            _position = value;
            OnTransformChanged();
        }
    }

    /// <summary>Gets or sets the local rotation (Euler angles in radians).</summary>
    public Vector3 Rotation
    {
        get => _rotation;
        set
        {
            if (_rotation == value)
                return;
            _rotation = value;
            OnTransformChanged();
        }
    }

    /// <summary>Gets or sets the local scale.</summary>
    public Vector3 Scale
    {
        get => _scale;
        set
        {
            if (_scale == value)
                return;
            _scale = value;
            OnTransformChanged();
        }
    }

    /// <summary>Gets or sets the node name for debugging.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets the parent node, or null if this is a root node.</summary>
    public Node? Parent => _parent;

    /// <summary>Gets the children of this node.</summary>
    public IReadOnlyList<Node> Children => _children;

    /// <summary>Gets whether this node has any children.</summary>
    public bool HasChildren => _children.Count > 0;

    /// <summary>Gets whether this node should expose expandable container behavior.</summary>
    public virtual bool CanHaveChildren => HasChildren;

    /// <summary>
    /// Adds a child node to this node.
    /// </summary>
    /// <param name="child">The node to add as a child.</param>
    public void AddChild(Node child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (ReferenceEquals(child, this))
            throw new InvalidOperationException("A node cannot be its own child.");

        for (var ancestor = this; ancestor is not null; ancestor = ancestor._parent)
        {
            if (ReferenceEquals(ancestor, child))
                throw new InvalidOperationException("Adding this child would create a scene-graph cycle.");
        }

        if (ReferenceEquals(child._parent, this))
            return;

        if (child._parent != null)
            child._parent.RemoveChild(child);

        child._parent = this;
        _children.Add(child);
    }

    /// <summary>
    /// Removes a child node from this node.
    /// </summary>
    /// <param name="child">The node to remove.</param>
    /// <returns>True if the child was found and removed; otherwise, false.</returns>
    public bool RemoveChild(Node child)
    {
        if (child._parent != this)
            return false;

        child._parent = null;
        return _children.Remove(child);
    }

    /// <summary>
    /// Removes all children from this node.
    /// </summary>
    public void ClearChildren()
    {
        foreach (var child in _children)
            child._parent = null;
        _children.Clear();
    }

    /// <summary>
    /// Called after a local transform property changes.
    /// </summary>
    protected virtual void OnTransformChanged()
    {
    }
}
