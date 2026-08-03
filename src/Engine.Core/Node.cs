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
    private Quaternion _orientation = Quaternion.Identity;
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

    /// <summary>Gets or sets the local rotation as Euler angles in radians.</summary>
    /// <remarks>This is a presentation and serialization facade over <see cref="Orientation"/>.</remarks>
    public virtual Vector3 Rotation
    {
        get => _rotation;
        set
        {
            if (_rotation == value)
                return;
            _rotation = value;
            _orientation = Quaternion.CreateFromRotationMatrix(CreateEulerRotation(value));
            OnTransformChanged();
        }
    }

    /// <summary>Gets or sets the authoritative local quaternion orientation.</summary>
    public Quaternion Orientation
    {
        get => _orientation;
        set
        {
            var lengthSquared = value.LengthSquared();
            if (!float.IsFinite(lengthSquared) || lengthSquared <= float.Epsilon)
                throw new ArgumentOutOfRangeException(nameof(value));
            var normalized = Quaternion.Normalize(value);
            if (MathF.Abs(Quaternion.Dot(_orientation, normalized)) >= 0.9999999f)
                return;
            _orientation = normalized;
            _rotation = ExtractEuler(Matrix4x4.CreateFromQuaternion(normalized));
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

    /// <summary>Gets or sets the assembly-qualified game script type attached to this node.</summary>
    public string? ScriptType { get; set; }

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

    /// <summary>Creates the engine's row-vector Rz * Ry * Rx Euler rotation matrix.</summary>
    /// <param name="euler">Euler angles in radians.</param>
    /// <returns>The equivalent rotation matrix.</returns>
    private static Matrix4x4 CreateEulerRotation(Vector3 euler)
    {
        return Matrix4x4.CreateRotationZ(euler.Z)
             * Matrix4x4.CreateRotationY(euler.Y)
             * Matrix4x4.CreateRotationX(euler.X);
    }

    /// <summary>Extracts a canonical Euler presentation from a rotation matrix.</summary>
    /// <param name="rotation">Normalized row-vector rotation matrix.</param>
    /// <returns>Euler angles with Y in the range [-PI/2, PI/2].</returns>
    private static Vector3 ExtractEuler(Matrix4x4 rotation)
    {
        const float singularityThreshold = 0.99999f;
        var sinY = Math.Clamp(rotation.M31, -1f, 1f);
        var y = MathF.Asin(sinY);
        if (MathF.Abs(sinY) >= singularityThreshold)
            return new Vector3(MathF.Atan2(rotation.M23, rotation.M22), y, 0f);
        return new Vector3(
            MathF.Atan2(-rotation.M32, rotation.M33),
            y,
            MathF.Atan2(-rotation.M21, rotation.M11));
    }
}
