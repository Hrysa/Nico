using System.Numerics;

namespace Engine.Graphics;

/// <summary>Provides editable sun-like lighting for a 3D scene.</summary>
public sealed class DirectionalLight3D : Node3D
{
    private Vector3 _color = Vector3.One;
    private float _intensity = 1f;
    private float _ambientIntensity = 0.2f;

    /// <summary>Gets or sets whether this light contributes to scene rendering.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Gets or sets linear RGB light color.</summary>
    public Vector3 Color
    {
        get => _color;
        set
        {
            if (!IsFinite(value) || value.X < 0f || value.Y < 0f || value.Z < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _color = value;
        }
    }

    /// <summary>Gets or sets direct-light intensity.</summary>
    public float Intensity
    {
        get => _intensity;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _intensity = value;
        }
    }

    /// <summary>Gets or sets omnidirectional ambient intensity.</summary>
    public float AmbientIntensity
    {
        get => _ambientIntensity;
        set
        {
            if (!float.IsFinite(value) || value < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            _ambientIntensity = value;
        }
    }

    /// <summary>Gets the normalized world direction from a surface toward this light.</summary>
    /// <returns>World-space direction used by Lambert shading.</returns>
    public Vector3 GetDirectionToLight()
    {
        if (!Matrix4x4.Decompose(GetModelMatrix(), out _, out var rotation, out _))
            return Vector3.UnitZ;
        var direction = Vector3.Transform(Vector3.UnitZ, rotation);
        return Vector3.Normalize(direction);
    }

    /// <summary>Checks whether every vector component is finite.</summary>
    /// <param name="value">Vector to validate.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
