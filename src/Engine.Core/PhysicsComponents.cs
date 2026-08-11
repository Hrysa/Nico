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

/// <summary>Defines shared collision placement, material, trigger, and filtering properties.</summary>
public abstract class ColliderComponent : Component
{
    private Vector3 _center;
    private bool _isTrigger;
    private uint _collisionLayer = 1u;
    private uint _collisionMask = uint.MaxValue;
    private float _friction = 0.5f;
    private float _restitution;

    /// <summary>Gets or sets the collider center in node-local coordinates.</summary>
    public Vector3 Center
    {
        get => _center;
        set
        {
            if (!IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_center == value)
                return;
            _center = value;
            NotifyValueChanged();
        }
    }

    /// <summary>Gets or sets whether contacts report overlap without applying response.</summary>
    public bool IsTrigger
    {
        get => _isTrigger;
        set
        {
            if (_isTrigger == value)
                return;
            _isTrigger = value;
            NotifyValueChanged();
        }
    }

    /// <summary>Gets or sets the single collision layer bit occupied by this collider.</summary>
    public uint CollisionLayer
    {
        get => _collisionLayer;
        set
        {
            if (value == 0u || (value & (value - 1u)) != 0u)
                throw new ArgumentOutOfRangeException(nameof(value),
                    "A collider must occupy exactly one collision layer bit.");
            if (_collisionLayer == value)
                return;
            _collisionLayer = value;
            NotifyValueChanged();
        }
    }

    /// <summary>Gets or sets the layer mask this collider is allowed to contact.</summary>
    public uint CollisionMask
    {
        get => _collisionMask;
        set
        {
            if (_collisionMask == value)
                return;
            _collisionMask = value;
            NotifyValueChanged();
        }
    }

    /// <summary>Gets or sets tangential contact friction from zero through one.</summary>
    public float Friction
    {
        get => _friction;
        set
        {
            if (!float.IsFinite(value) || value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_friction == value)
                return;
            _friction = value;
            NotifyValueChanged();
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
            if (_restitution == value)
                return;
            _restitution = value;
            NotifyValueChanged();
        }
    }

    /// <summary>Checks whether all three vector components are finite and positive.</summary>
    /// <param name="value">Candidate dimensions.</param>
    /// <returns>True when every component is valid.</returns>
    protected static bool IsPositiveFinite(Vector3 value) =>
        float.IsFinite(value.X) && value.X > 0f &&
        float.IsFinite(value.Y) && value.Y > 0f &&
        float.IsFinite(value.Z) && value.Z > 0f;

    /// <summary>Checks whether every vector component is finite.</summary>
    /// <param name="value">Candidate vector.</param><returns>True when finite.</returns>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    /// <summary>Validates a positive finite scalar dimension.</summary>
    /// <param name="value">Candidate dimension.</param>
    /// <param name="parameterName">Public property name used by the exception.</param>
    /// <returns>The validated value.</returns>
    protected static float ValidatePositive(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value <= 0f)
            throw new ArgumentOutOfRangeException(parameterName);
        return value;
    }
}

/// <summary>Defines an oriented box collider using full local dimensions.</summary>
public sealed class BoxColliderComponent : ColliderComponent
{
    private Vector3 _size = Vector3.One;

    /// <summary>Gets or sets full local box dimensions.</summary>
    public Vector3 Size
    {
        get => _size;
        set
        {
            if (!IsPositiveFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_size == value)
                return;
            _size = value;
            NotifyValueChanged();
        }
    }
}

/// <summary>Defines a sphere collider.</summary>
public sealed class SphereColliderComponent : ColliderComponent
{
    private float _radius = 0.5f;

    /// <summary>Gets or sets local sphere radius.</summary>
    public float Radius
    {
        get => _radius;
        set
        {
            value = ValidatePositive(value, nameof(value));
            if (_radius == value)
                return;
            _radius = value;
            NotifyValueChanged();
        }
    }
}

/// <summary>Defines a Y-axis capsule collider whose height includes both caps.</summary>
public sealed class CapsuleColliderComponent : ColliderComponent
{
    private float _radius = 0.5f;
    private float _height = 2f;

    /// <summary>Gets or sets local capsule radius.</summary>
    public float Radius
    {
        get => _radius;
        set
        {
            value = ValidatePositive(value, nameof(value));
            if (_radius == value)
                return;
            _radius = value;
            NotifyValueChanged();
        }
    }

    /// <summary>Gets or sets full local capsule height.</summary>
    public float Height
    {
        get => _height;
        set
        {
            value = ValidatePositive(value, nameof(value));
            if (_height == value)
                return;
            _height = value;
            NotifyValueChanged();
        }
    }
}

/// <summary>Defines a Y-axis cylinder collider.</summary>
public sealed class CylinderColliderComponent : ColliderComponent
{
    private float _radius = 0.5f;
    private float _height = 1f;

    /// <summary>Gets or sets local cylinder radius.</summary>
    public float Radius
    {
        get => _radius;
        set
        {
            value = ValidatePositive(value, nameof(value));
            if (_radius == value)
                return;
            _radius = value;
            NotifyValueChanged();
        }
    }

    /// <summary>Gets or sets full local cylinder height.</summary>
    public float Height
    {
        get => _height;
        set
        {
            value = ValidatePositive(value, nameof(value));
            if (_height == value)
                return;
            _height = value;
            NotifyValueChanged();
        }
    }
}

/// <summary>Defines a finite thin XZ plane collider.</summary>
public sealed class PlaneColliderComponent : ColliderComponent
{
    private Vector2 _size = new(100_000f);

    /// <summary>Gets or sets full local XZ dimensions.</summary>
    public Vector2 Size
    {
        get => _size;
        set
        {
            if (!float.IsFinite(value.X) || value.X <= 0f ||
                !float.IsFinite(value.Y) || value.Y <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_size == value)
                return;
            _size = value;
            NotifyValueChanged();
        }
    }
}

/// <summary>Defines explicit static triangle collision geometry.</summary>
public sealed class MeshColliderComponent : ColliderComponent
{
    private AssetReference? _mesh;

    /// <summary>Gets or sets the required collision-mesh asset reference.</summary>
    public AssetReference? Mesh
    {
        get => _mesh;
        set
        {
            if (_mesh == value)
                return;
            _mesh = value;
            NotifyValueChanged();
        }
    }
}

/// <summary>Defines an explicit static heightfield terrain collider.</summary>
public sealed class TerrainColliderComponent : ColliderComponent
{
    private Vector2 _horizontalSize = new(100f);
    private float _heightScale = 10f;
    private AssetReference? _terrainData;

    /// <summary>Gets or sets the required terrain-data or heightmap asset reference.</summary>
    public AssetReference? TerrainData
    {
        get => _terrainData;
        set
        {
            if (_terrainData == value)
                return;
            _terrainData = value;
            NotifyValueChanged();
        }
    }

    /// <summary>Gets or sets the full horizontal XZ dimensions.</summary>
    public Vector2 HorizontalSize
    {
        get => _horizontalSize;
        set
        {
            if (!float.IsFinite(value.X) || value.X <= 0f ||
                !float.IsFinite(value.Y) || value.Y <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (_horizontalSize == value)
                return;
            _horizontalSize = value;
            NotifyValueChanged();
        }
    }

    /// <summary>Gets or sets the vertical range represented by normalized terrain samples.</summary>
    public float HeightScale
    {
        get => _heightScale;
        set
        {
            value = ValidatePositive(value, nameof(value));
            if (_heightScale == value)
                return;
            _heightScale = value;
            NotifyValueChanged();
        }
    }
}
