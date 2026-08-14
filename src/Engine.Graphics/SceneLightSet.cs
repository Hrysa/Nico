using System.Numerics;
using System.Runtime.InteropServices;
using Engine.Core;

namespace Engine.Graphics;

/// <summary>Identifies the shader behavior of one visible light.</summary>
public enum SceneLightType
{
    /// <summary>Infinite light described only by direction.</summary>
    Directional,
    /// <summary>Omnidirectional finite-range light.</summary>
    Point,
    /// <summary>Finite-range cone light.</summary>
    Spot
}

/// <summary>Contains renderer-independent data for one enabled scene light.</summary>
/// <param name="Type">Light shader behavior.</param>
/// <param name="Position">World-space position for local lights.</param>
/// <param name="Direction">Direction toward a directional light or emission direction for a spot.</param>
/// <param name="Color">Linear RGB light color.</param>
/// <param name="Intensity">Direct-light multiplier.</param>
/// <param name="Range">Finite local-light range.</param>
/// <param name="InnerConeCosine">Spotlight inner-cone cosine.</param>
/// <param name="OuterConeCosine">Spotlight outer-cone cosine.</param>
/// <param name="CastsShadows">Whether the light may request shadow rendering.</param>
/// <param name="ShadowIndex">Local-shadow atlas slot, or negative one.</param>
public readonly record struct SceneLight(
    SceneLightType Type,
    Vector3 Position,
    Vector3 Direction,
    Vector3 Color,
    float Intensity,
    float Range,
    float InnerConeCosine,
    float OuterConeCosine,
    bool CastsShadows,
    int ShadowIndex);

/// <summary>Collects enabled lights for one camera submission without per-frame allocations.</summary>
public sealed class SceneLightSet
{
    /// <summary>Maximum number of lights accepted by the built-in forward path.</summary>
    public const int MaximumLights = 64;

    /// <summary>Maximum number of local lights owning shadow-atlas rows.</summary>
    public const int MaximumShadowedLocalLights = 4;

    private readonly List<SceneLight> _lights = new(MaximumLights);
    private int _shadowedLocalCount;

    /// <summary>Gets collected lights as an allocation-free span.</summary>
    public ReadOnlySpan<SceneLight> Lights => CollectionsMarshal.AsSpan(_lights);

    /// <summary>Gets linear RGB ambient light color.</summary>
    public Vector3 AmbientColor { get; private set; } = Vector3.One;

    /// <summary>Gets ambient-light intensity.</summary>
    public float AmbientIntensity { get; private set; }

    /// <summary>Gets the primary directional-light index, or negative one.</summary>
    public int MainDirectionalIndex { get; private set; } = -1;

    /// <summary>Gets the number of collected lights.</summary>
    public int Count => _lights.Count;

    /// <summary>Collects all enabled supported lights from a scene hierarchy.</summary>
    /// <param name="root">Scene hierarchy root.</param>
    public void Resolve(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Clear();
        Collect(root);
    }

    /// <summary>Replaces this set with another set's current values.</summary>
    /// <param name="source">Source light set.</param>
    public void CopyFrom(SceneLightSet source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _lights.Clear();
        foreach (var light in source.Lights)
            _lights.Add(light);
        AmbientColor = source.AmbientColor;
        AmbientIntensity = source.AmbientIntensity;
        MainDirectionalIndex = source.MainDirectionalIndex;
        _shadowedLocalCount = source._shadowedLocalCount;
    }

    /// <summary>Clears all per-view lighting state for queue reuse.</summary>
    public void Clear()
    {
        _lights.Clear();
        AmbientColor = Vector3.One;
        AmbientIntensity = 0f;
        MainDirectionalIndex = -1;
        _shadowedLocalCount = 0;
    }

    /// <summary>Sets one directional light for previews without a scene hierarchy.</summary>
    /// <param name="directionToLight">Normalized direction from surfaces toward the light.</param>
    /// <param name="color">Linear RGB light and ambient color.</param>
    /// <param name="intensity">Direct-light multiplier.</param>
    /// <param name="ambientIntensity">Ambient-light multiplier.</param>
    internal void SetDirectional(
        Vector3 directionToLight,
        Vector3 color,
        float intensity,
        float ambientIntensity)
    {
        Clear();
        if (intensity > 0f)
        {
            _lights.Add(new SceneLight(
                SceneLightType.Directional,
                Vector3.Zero,
                Vector3.Normalize(directionToLight),
                color,
                intensity,
                0f,
                1f,
                1f,
                false,
                -1));
            MainDirectionalIndex = 0;
        }
        AmbientColor = color;
        AmbientIntensity = ambientIntensity;
    }

    /// <summary>Recursively collects supported light nodes without iterator allocation.</summary>
    /// <param name="node">Current scene node.</param>
    private void Collect(Node node)
    {
        if (_lights.Count < MaximumLights && node is Light3D { IsEnabled: true } light)
            Add(light);
        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
            Collect(children[index]);
    }

    /// <summary>Converts one authored light node into renderer-independent data.</summary>
    /// <param name="light">Enabled authored light.</param>
    private void Add(Light3D light)
    {
        var localShadowIndex = -1;
        if (light.CastsShadows && light.Intensity > 0f &&
            (light is PointLight3D or SpotLight3D) &&
            _shadowedLocalCount < MaximumShadowedLocalLights)
        {
            localShadowIndex = _shadowedLocalCount++;
        }
        SceneLight sceneLight;
        switch (light)
        {
            case DirectionalLight3D directional:
                sceneLight = new SceneLight(
                    SceneLightType.Directional,
                    Vector3.Zero,
                    directional.GetDirectionToLight(),
                    directional.Color,
                    directional.Intensity,
                    0f,
                    1f,
                    1f,
                    directional.CastsShadows,
                    -1);
                if (MainDirectionalIndex < 0)
                {
                    MainDirectionalIndex = _lights.Count;
                    AmbientColor = directional.Color;
                    AmbientIntensity = directional.AmbientIntensity;
                }
                break;
            case PointLight3D point:
                sceneLight = new SceneLight(
                    SceneLightType.Point,
                    point.GetWorldPosition(),
                    Vector3.Zero,
                    point.Color,
                    point.Intensity,
                    point.Range,
                    1f,
                    1f,
                    point.CastsShadows,
                    localShadowIndex);
                break;
            case SpotLight3D spot:
                sceneLight = new SceneLight(
                    SceneLightType.Spot,
                    spot.GetWorldPosition(),
                    spot.GetEmissionDirection(),
                    spot.Color,
                    spot.Intensity,
                    spot.Range,
                    MathF.Cos(spot.InnerAngle * MathF.PI / 180f),
                    MathF.Cos(spot.OuterAngle * MathF.PI / 180f),
                    spot.CastsShadows,
                    localShadowIndex);
                break;
            default:
                return;
        }
        _lights.Add(sceneLight);
    }
}
