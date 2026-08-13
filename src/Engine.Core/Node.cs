using System.Numerics;

namespace Engine.Core;

/// <summary>
/// Base class for all scene graph nodes. Provides transform, parent/child hierarchy, and name.
/// </summary>
public class Node
{
    private readonly List<Node> _children = new();
    private readonly List<Component> _components = new();
    private Node? _parent;
    private Vector3 _position;
    private Vector3 _rotation;
    private Quaternion _orientation = Quaternion.Identity;
    private Vector3 _scale = Vector3.One;
    private string _name = string.Empty;

    /// <summary>Occurs after authored state on this node changes.</summary>
    public event Action<NodeChangeKind>? Changed;

    /// <summary>Gets or sets the local position relative to the parent.</summary>
    public Vector3 Position
    {
        get => _position;
        set
        {
            if (_position == value)
                return;
            _position = value;
            NotifyTransformChanged();
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
            NotifyTransformChanged();
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
            NotifyTransformChanged();
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
            NotifyTransformChanged();
        }
    }

    /// <summary>Gets or sets the node name for debugging.</summary>
    public string Name
    {
        get => _name;
        set
        {
            value ??= string.Empty;
            if (string.Equals(_name, value, StringComparison.Ordinal))
                return;
            _name = value;
            Changed?.Invoke(NodeChangeKind.Name);
        }
    }

    /// <summary>Gets or sets the first persistent game script attached to this node.</summary>
    /// <remarks>This compatibility facade removes all scripts when assigned null. Use
    /// <see cref="AddComponent"/> and <see cref="Components"/> for multiple components.</remarks>
    public AssetId? ScriptId
    {
        get
        {
            for (var index = 0; index < _components.Count; index++)
            {
                if (_components[index] is ScriptComponent script)
                    return script.ScriptId;
            }
            return null;
        }
        set
        {
            if (value is null)
            {
                for (var index = _components.Count - 1; index >= 0; index--)
                {
                    if (_components[index] is ScriptComponent)
                        RemoveComponent(_components[index]);
                }
                return;
            }
            for (var index = 0; index < _components.Count; index++)
            {
                if (_components[index] is not ScriptComponent script)
                    continue;
                script.ScriptId = value.Value;
                return;
            }
            AddComponent(new ScriptComponent(value.Value));
        }
    }

    /// <summary>Gets the parent node, or null if this is a root node.</summary>
    public Node? Parent => _parent;

    /// <summary>Gets the children of this node.</summary>
    public IReadOnlyList<Node> Children => _children;

    /// <summary>Gets components attached to this node in authored order.</summary>
    public IReadOnlyList<Component> Components => _components;

    /// <summary>Gets whether this node has any children.</summary>
    public bool HasChildren => _children.Count > 0;

    /// <summary>Gets whether this node should expose expandable container behavior.</summary>
    public virtual bool CanHaveChildren => HasChildren;

    /// <summary>
    /// Adds a child node to this node.
    /// </summary>
    /// <param name="child">The node to add as a child.</param>
    public virtual void AddChild(Node child)
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

    /// <summary>Inserts or moves a child at an ordered position.</summary>
    /// <param name="index">Insertion index measured before removing an existing child.</param>
    /// <param name="child">Child node to insert.</param>
    public virtual void InsertChild(int index, Node child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if ((uint)index > (uint)_children.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (ReferenceEquals(child, this))
            throw new InvalidOperationException("A node cannot be its own child.");

        for (var ancestor = this; ancestor is not null; ancestor = ancestor._parent)
        {
            if (ReferenceEquals(ancestor, child))
                throw new InvalidOperationException("Inserting this child would create a scene-graph cycle.");
        }

        if (ReferenceEquals(child._parent, this))
        {
            var previousIndex = _children.IndexOf(child);
            if (previousIndex < index)
                index--;
            if (previousIndex == index)
                return;
            _children.RemoveAt(previousIndex);
            _children.Insert(index, child);
            return;
        }

        if (child._parent != null)
            child._parent.RemoveChild(child);
        child._parent = this;
        _children.Insert(index, child);
    }

    /// <summary>
    /// Removes a child node from this node.
    /// </summary>
    /// <param name="child">The node to remove.</param>
    /// <returns>True if the child was found and removed; otherwise, false.</returns>
    public virtual bool RemoveChild(Node child)
    {
        if (child._parent != this)
            return false;

        child._parent = null;
        return _children.Remove(child);
    }

    /// <summary>
    /// Removes all children from this node.
    /// </summary>
    public virtual void ClearChildren()
    {
        foreach (var child in _children)
            child._parent = null;
        _children.Clear();
    }

    /// <summary>Attaches one component to this node.</summary>
    /// <param name="component">Unattached component to add.</param>
    public void AddComponent(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (ReferenceEquals(component.Owner, this))
            return;
        if (component.Owner is not null)
            throw new InvalidOperationException("A component cannot belong to multiple nodes.");
        component.Owner = this;
        _components.Add(component);
        Changed?.Invoke(NodeChangeKind.Components);
    }

    /// <summary>Removes one component from this node.</summary>
    /// <param name="component">Component to detach.</param>
    /// <returns>True when the component belonged to this node.</returns>
    public bool RemoveComponent(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (!ReferenceEquals(component.Owner, this) || !_components.Remove(component))
            return false;
        component.Owner = null;
        Changed?.Invoke(NodeChangeKind.Components);
        return true;
    }

    /// <summary>Moves one attached component to a requested authored-order index.</summary>
    /// <param name="component">Attached component to move.</param>
    /// <param name="destinationIndex">Insertion index before removal adjustment.</param>
    /// <returns>True when component order changed.</returns>
    public bool MoveComponent(Component component, int destinationIndex)
    {
        ArgumentNullException.ThrowIfNull(component);
        if (!ReferenceEquals(component.Owner, this))
            return false;
        var sourceIndex = _components.IndexOf(component);
        if (sourceIndex < 0)
            return false;
        destinationIndex = Math.Clamp(destinationIndex, 0, _components.Count);
        if (sourceIndex < destinationIndex)
            destinationIndex--;
        if (sourceIndex == destinationIndex)
            return false;
        _components.RemoveAt(sourceIndex);
        _components.Insert(destinationIndex, component);
        Changed?.Invoke(NodeChangeKind.Components);
        return true;
    }

    /// <summary>Returns the first attached component assignable to a requested type.</summary>
    /// <typeparam name="T">Requested component type.</typeparam>
    /// <returns>The first matching component, or null.</returns>
    public T? GetComponent<T>() where T : Component
    {
        for (var index = 0; index < _components.Count; index++)
        {
            if (_components[index] is T component)
                return component;
        }
        return null;
    }

    /// <summary>
    /// Called after a local transform property changes.
    /// </summary>
    protected virtual void OnTransformChanged()
    {
    }

    /// <summary>Publishes a derived node's authored state transition.</summary>
    /// <param name="kind">Changed state category.</param>
    protected void NotifyChanged(NodeChangeKind kind)
    {
        Changed?.Invoke(kind);
    }

    /// <summary>Publishes a change made by one attached component.</summary>
    /// <param name="kind">Component change category.</param>
    internal void NotifyComponentChanged(NodeChangeKind kind)
    {
        NotifyChanged(kind);
    }

    /// <summary>Runs the extension hook and publishes one transform transition.</summary>
    private void NotifyTransformChanged()
    {
        OnTransformChanged();
        NotifyChanged(NodeChangeKind.Transform);
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

/// <summary>Identifies coarse authored state changed on one scene node.</summary>
[Flags]
public enum NodeChangeKind
{
    /// <summary>No state changed.</summary>
    None = 0,
    /// <summary>The display name changed.</summary>
    Name = 1,
    /// <summary>Position, orientation, rotation, or scale changed.</summary>
    Transform = 2,
    /// <summary>Component attachment, configuration, or override data changed.</summary>
    Components = 4,
    /// <summary>An authored value inside an existing component changed.</summary>
    ComponentValues = 8,
    /// <summary>Renderable resource or material state changed.</summary>
    Render = 16
}
