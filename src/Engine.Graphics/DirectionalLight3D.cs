using System.Numerics;

namespace Engine.Graphics;

/// <summary>Provides editable sun-like lighting for a 3D scene.</summary>
public sealed class DirectionalLight3D : Light3D
{
    private float _ambientIntensity = 0.2f;

    /// <summary>Creates a directional light that casts shadows by default.</summary>
    public DirectionalLight3D()
    {
        CastsShadows = true;
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

}
