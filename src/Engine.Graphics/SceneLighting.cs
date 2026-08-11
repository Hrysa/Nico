using System.Numerics;
using Engine.Core;

namespace Engine.Graphics;

/// <summary>Contains the single directional and ambient light evaluated by basic forward shading.</summary>
/// <param name="DirectionToLight">Normalized world direction toward the light.</param>
/// <param name="Color">Linear RGB light color.</param>
/// <param name="Intensity">Direct-light multiplier.</param>
/// <param name="AmbientIntensity">Ambient-light multiplier.</param>
public readonly record struct SceneLighting(
    Vector3 DirectionToLight,
    Vector3 Color,
    float Intensity,
    float AmbientIntensity)
{
    /// <summary>Gets lighting with no direct or ambient contribution.</summary>
    public static SceneLighting None { get; } = new(
        Vector3.UnitZ, Vector3.One, 0f, 0f);

    /// <summary>Finds the first enabled directional light in hierarchy order.</summary>
    /// <param name="root">Scene hierarchy root.</param>
    /// <returns>Authored lighting, or no lighting when no enabled light exists.</returns>
    public static SceneLighting Resolve(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var light = FindFirstEnabled(root);
        if (light is null)
            return None;
        return new SceneLighting(
            light.GetDirectionToLight(), light.Color,
            light.Intensity, light.AmbientIntensity);
    }

    /// <summary>Recursively finds the first enabled directional light without allocations.</summary>
    /// <param name="node">Subtree root.</param>
    /// <returns>The first enabled light, or null.</returns>
    private static DirectionalLight3D? FindFirstEnabled(Node node)
    {
        if (node is DirectionalLight3D light)
        {
            if (light.IsEnabled)
                return light;
        }
        var children = node.Children;
        for (var index = 0; index < children.Count; index++)
        {
            var result = FindFirstEnabled(children[index]);
            if (result is not null)
                return result;
        }
        return null;
    }
}
