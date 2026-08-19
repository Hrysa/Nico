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
    /// <summary>Gets or sets the body's simulation mode.</summary>
    public RigidBodyMotionType MotionType { get; set; } = RigidBodyMotionType.Dynamic;

    /// <summary>Gets or sets mass in kilograms for dynamic bodies.</summary>
    public float Mass
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = 1f;

    /// <summary>Gets or sets world-space linear velocity in units per second.</summary>
    public Vector3 LinearVelocity { get; set; }

    /// <summary>Gets or sets whether world gravity affects this body.</summary>
    public bool UseGravity { get; set; } = true;

    /// <summary>Gets or sets the multiplier applied to world gravity.</summary>
    public float GravityScale
    {
        get;
        set
        {
            if (!float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = 1f;

    /// <summary>Gets or sets linear velocity loss per second.</summary>
    public float LinearDamping
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = 0.05f;
}

/// <summary>Defines shared collision placement, material, trigger, and filtering properties.</summary>
public abstract class ColliderComponent : Component
{
    /// <summary>Gets or sets the collider center in node-local coordinates.</summary>
    public Vector3 Center
    {
        get;
        set
        {
            if (!IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    }

    /// <summary>Gets or sets whether contacts report overlap without applying response.</summary>
    public bool IsTrigger
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    }

    /// <summary>Gets or sets the single collision layer bit occupied by this collider.</summary>
    public uint CollisionLayer
    {
        get;
        set
        {
            if (value == 0u || (value & (value - 1u)) != 0u)
                throw new ArgumentOutOfRangeException(nameof(value),
                    "A collider must occupy exactly one collision layer bit.");
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    } = 1u;

    /// <summary>Gets or sets the layer mask this collider is allowed to contact.</summary>
    public uint CollisionMask
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    } = uint.MaxValue;

    /// <summary>Gets or sets tangential contact friction from zero through one.</summary>
    public float Friction
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    } = 0.5f;

    /// <summary>Gets or sets normal bounce from zero through one.</summary>
    public float Restitution
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
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
    /// <summary>Gets or sets full local box dimensions.</summary>
    public Vector3 Size
    {
        get;
        set
        {
            if (!IsPositiveFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    } = Vector3.One;
}

/// <summary>Defines a sphere collider.</summary>
public sealed class SphereColliderComponent : ColliderComponent
{
    /// <summary>Gets or sets local sphere radius.</summary>
    public float Radius
    {
        get;
        set
        {
            value = ValidatePositive(value, nameof(value));
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    } = 0.5f;
}

/// <summary>Defines a Y-axis capsule collider whose height includes both caps.</summary>
public sealed class CapsuleColliderComponent : ColliderComponent
{
    /// <summary>Gets or sets local capsule radius.</summary>
    public float Radius
    {
        get;
        set
        {
            value = ValidatePositive(value, nameof(value));
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    } = 0.5f;

    /// <summary>Gets or sets full local capsule height.</summary>
    public float Height
    {
        get;
        set
        {
            value = ValidatePositive(value, nameof(value));
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    } = 2f;
}

/// <summary>Defines a Y-axis cylinder collider.</summary>
public sealed class CylinderColliderComponent : ColliderComponent
{
    /// <summary>Gets or sets local cylinder radius.</summary>
    public float Radius
    {
        get;
        set
        {
            value = ValidatePositive(value, nameof(value));
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    } = 0.5f;

    /// <summary>Gets or sets full local cylinder height.</summary>
    public float Height
    {
        get;
        set
        {
            value = ValidatePositive(value, nameof(value));
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    } = 1f;
}

/// <summary>Defines a finite thin XZ plane collider.</summary>
public sealed class PlaneColliderComponent : ColliderComponent
{
    /// <summary>Gets or sets full local XZ dimensions.</summary>
    public Vector2 Size
    {
        get;
        set
        {
            if (!float.IsFinite(value.X) || value.X <= 0f ||
                !float.IsFinite(value.Y) || value.Y <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    } = new(100_000f);
}

/// <summary>Defines explicit static triangle collision geometry.</summary>
public sealed class MeshColliderComponent : ColliderComponent
{
    /// <summary>Gets or sets the required collision-mesh asset reference.</summary>
    public AssetReference? Mesh
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    }
}

/// <summary>Defines an explicit static heightfield terrain collider.</summary>
public sealed class TerrainColliderComponent : ColliderComponent
{
    /// <summary>Gets or sets the required terrain-data or heightmap asset reference.</summary>
    public AssetReference? TerrainData
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    }

    /// <summary>Gets or sets the full horizontal XZ dimensions.</summary>
    public Vector2 HorizontalSize
    {
        get;
        set
        {
            if (!float.IsFinite(value.X) || value.X <= 0f ||
                !float.IsFinite(value.Y) || value.Y <= 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    } = new(100f);

    /// <summary>Gets or sets the vertical range represented by normalized terrain samples.</summary>
    public float HeightScale
    {
        get;
        set
        {
            value = ValidatePositive(value, nameof(value));
            if (field == value)
                return;
            field = value;
            NotifyValueChanged();
        }
    } = 10f;
}
