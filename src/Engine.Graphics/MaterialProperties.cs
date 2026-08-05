using System.Numerics;
using Engine.Core;

namespace Engine.Graphics;

/// <summary>Contains renderer-independent editable standard-material values.</summary>
public sealed class MaterialProperties
{
    /// <summary>Gets an independent copy of the built-in default material values.</summary>
    public static MaterialProperties Default => new();

    /// <summary>Gets or sets the linear base-color multiplier.</summary>
    public Vector4 BaseColor { get; set; } = new(0.8f, 0.8f, 0.8f, 1f);

    /// <summary>Gets or sets metallic response from zero through one.</summary>
    public float Metallic { get; set; }

    /// <summary>Gets or sets surface roughness from zero through one.</summary>
    public float Roughness { get; set; } = 0.5f;

    /// <summary>Gets or sets whether back-face culling is disabled.</summary>
    public bool DoubleSided { get; set; }

    /// <summary>Gets or sets the optional persistent base-color texture.</summary>
    public AssetReference? BaseColorTexture { get; set; }

    /// <summary>Creates an independent editable copy.</summary>
    /// <returns>A material containing the same values.</returns>
    public MaterialProperties Clone()
    {
        return new MaterialProperties
        {
            BaseColor = BaseColor,
            Metallic = Metallic,
            Roughness = Roughness,
            DoubleSided = DoubleSided,
            BaseColorTexture = BaseColorTexture
        };
    }
}
