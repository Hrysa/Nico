using System.Numerics;

namespace Engine.Core;

/// <summary>Identifies how a rigid body participates in simulation.</summary>
public enum RigidBodyMotionType
{
    /// <summary>The body never moves and has infinite mass.</summary>
    Static,
    /// <summary>The body is integrated and responds to forces and collisions.</summary>
    Dynamic,
    /// <summary>The body is moved by game code and pushes dynamic bodies.</summary>
    Kinematic
}

/// <summary>Provides linear physical motion for a node.</summary>
public sealed class RigidBodyComponent : Component
{
    private float _mass = 1f;
    private float _gravityScale = 1f;
    private float _linearDamping = 0.05f;

    /// <summary>Gets or sets the body's simulation mode.</summary>
    public RigidBodyMotionType MotionType { get; set; } = RigidBodyMotionType.Dynamic;

    /// <summary>Gets or sets mass in kilograms for dynamic bodies.</summary>
    public float Mass
    {
        get => _mass;
        set
        {
            if (!float.IsFinite(value) || value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _mass = value;
        }
    }

    /// <summary>Gets or sets world-space linear velocity in units per second.</summary>
    public Vector3 LinearVelocity { get; set; }

    /// <summary>Gets or sets whether world gravity affects this body.</summary>
    public bool UseGravity { get; set; } = true;

    /// <summary>Gets or sets the multiplier applied to world gravity.</summary>
    public float GravityScale
    {
        get => _gravityScale;
        set
        {
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            _gravityScale = value;
        }
    }

    /// <summary>Gets or sets linear velocity loss per second.</summary>
    public float LinearDamping
    {
        get => _linearDamping;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _linearDamping = value;
        }
    }
}

/// <summary>Identifies the primitive collision geometry attached to a node.</summary>
public enum ColliderShape
{
    /// <summary>Axis-aligned or oriented box.</summary>
    Box,
    /// <summary>Uniform sphere.</summary>
    Sphere,
    /// <summary>Y-axis capsule.</summary>
    Capsule,
    /// <summary>Y-axis cylinder.</summary>
    Cylinder,
    /// <summary>Infinite local XZ plane with local positive-Y normal.</summary>
    Plane,
    /// <summary>Static triangle mesh resolved from an imported mesh artifact.</summary>
    Mesh
}

/// <summary>Defines primitive collision geometry and contact material values.</summary>
public sealed class ColliderComponent : Component
{
    private Vector3 _size = Vector3.One;
    private float _radius = 0.5f;
    private float _height = 1f;
    private float _friction = 0.5f;
    private float _restitution;

    /// <summary>Gets or sets the primitive collision shape.</summary>
    public ColliderShape Shape { get; set; } = ColliderShape.Box;

    /// <summary>Gets or sets the imported triangle mesh used by mesh colliders.</summary>
    public AssetReference? Mesh { get; set; }

    /// <summary>Gets or sets the collider center in node-local coordinates.</summary>
    public Vector3 Center { get; set; }

    /// <summary>Gets or sets full local box dimensions.</summary>
    public Vector3 Size
    {
        get => _size;
        set
        {
            if (!IsPositiveFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            _size = value;
        }
    }

    /// <summary>Gets or sets local sphere, capsule, or cylinder radius.</summary>
    public float Radius
    {
        get => _radius;
        set
        {
            if (!float.IsFinite(value) || value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _radius = value;
        }
    }

    /// <summary>Gets or sets full local capsule or cylinder height.</summary>
    public float Height
    {
        get => _height;
        set
        {
            if (!float.IsFinite(value) || value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _height = value;
        }
    }

    /// <summary>Gets or sets whether contacts report overlap without applying response.</summary>
    public bool IsTrigger { get; set; }

    /// <summary>Gets or sets tangential contact friction from zero through one.</summary>
    public float Friction
    {
        get => _friction;
        set
        {
            if (!float.IsFinite(value) || value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _friction = value;
        }
    }

    /// <summary>Gets or sets normal bounce from zero through one.</summary>
    public float Restitution
    {
        get => _restitution;
        set
        {
            if (!float.IsFinite(value) || value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _restitution = value;
        }
    }

    /// <summary>Checks whether all three vector components are finite and positive.</summary>
    /// <param name="value">Candidate dimensions.</param>
    /// <returns>True when every component is valid.</returns>
    private static bool IsPositiveFinite(Vector3 value) =>
        float.IsFinite(value.X) && value.X > 0f &&
        float.IsFinite(value.Y) && value.Y > 0f &&
        float.IsFinite(value.Z) && value.Z > 0f;
}
