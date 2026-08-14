using System.Numerics;

namespace Engine.Graphics;

/// <summary>Provides validated properties shared by authored 3D light nodes.</summary>
public abstract class Light3D : Node3D
{
    /// <summary>Gets or sets whether this light contributes to scene rendering.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Gets or sets whether this light may request shadow rendering.</summary>
    public bool CastsShadows { get; set; }

    /// <summary>Gets or sets linear RGB light color.</summary>
    public Vector3 Color
    {
        get;
        set
        {
            if (!IsFinite(value) || value.X < 0f || value.Y < 0f || value.Z < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = Vector3.One;

    /// <summary>Gets or sets direct-light intensity.</summary>
    public float Intensity
    {
        get;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = 1f;

    /// <summary>Gets the normalized direction in which a local negative-Z light emits.</summary>
    /// <returns>World-space emission direction.</returns>
    public Vector3 GetEmissionDirection()
    {
        if (!Matrix4x4.Decompose(GetModelMatrix(), out _, out var rotation, out _))
            return -Vector3.UnitZ;
        return Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, rotation));
    }

    /// <summary>Checks whether every vector component is finite.</summary>
    /// <param name="value">Vector to validate.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
