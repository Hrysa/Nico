using System.Numerics;
using Engine.Core;

namespace Engine.Graphics;

/// <summary>Contains renderer-independent editable standard-material values.</summary>
public sealed class MaterialProperties
{
    private Vector4 _baseColor = new(0.8f, 0.8f, 0.8f, 1f);
    private float _metallic;
    private float _roughness = 0.5f;
    private bool _doubleSided;
    private AssetReference? _baseColorTexture;

    /// <summary>Occurs after an editable material value changes.</summary>
    public event Action? Changed;

    /// <summary>Gets an independent copy of the built-in default material values.</summary>
    public static MaterialProperties Default => new();

    /// <summary>Gets or sets the linear base-color multiplier.</summary>
    public Vector4 BaseColor
    {
        get => _baseColor;
        set
        {
            if (_baseColor == value)
                return;
            _baseColor = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Gets or sets metallic response from zero through one.</summary>
    public float Metallic
    {
        get => _metallic;
        set
        {
            if (_metallic == value)
                return;
            _metallic = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Gets or sets surface roughness from zero through one.</summary>
    public float Roughness
    {
        get => _roughness;
        set
        {
            if (_roughness == value)
                return;
            _roughness = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Gets or sets whether back-face culling is disabled.</summary>
    public bool DoubleSided
    {
        get => _doubleSided;
        set
        {
            if (_doubleSided == value)
                return;
            _doubleSided = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Gets or sets the optional persistent base-color texture.</summary>
    public AssetReference? BaseColorTexture
    {
        get => _baseColorTexture;
        set
        {
            if (_baseColorTexture == value)
                return;
            _baseColorTexture = value;
            Changed?.Invoke();
        }
    }

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
