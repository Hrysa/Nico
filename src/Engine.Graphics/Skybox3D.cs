using System.Numerics;
using Engine.Core;

namespace Engine.Graphics;

/// <summary>Defines an equirectangular texture environment rendered behind scene geometry.</summary>
public sealed class Skybox3D : Node3D
{
    /// <summary>Gets or sets whether this skybox contributes to scene rendering.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Gets or sets the imported equirectangular texture.</summary>
    public AssetReference? Texture { get; set; }

    /// <summary>Gets or sets the nonnegative linear RGB texture multiplier.</summary>
    public Vector3 Tint
    {
        get;
        set
        {
            if (!IsFinite(value) || value.X < 0f || value.Y < 0f || value.Z < 0f)
                throw new ArgumentOutOfRangeException(nameof(value));
            field = value;
        }
    } = Vector3.One;

    /// <summary>Gets or sets the nonnegative linear brightness multiplier.</summary>
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

    /// <summary>Checks whether every vector component is finite.</summary>
    /// <param name="value">Vector to validate.</param>
    /// <returns>True when every component is finite.</returns>
    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
