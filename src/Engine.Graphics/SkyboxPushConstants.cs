using System.Numerics;
using System.Runtime.InteropServices;

namespace Engine.Graphics;

/// <summary>Stores camera reconstruction and appearance values for skybox rendering.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct SkyboxPushConstants
{
    /// <summary>Gets or sets inverse camera view-projection used to reconstruct world rays.</summary>
    public Matrix4x4 InverseViewProjection;

    /// <summary>Gets or sets RGB tint and brightness intensity.</summary>
    public Vector4 TintIntensity;

    /// <summary>Gets or sets vertical rotation in X with reserved padding.</summary>
    public Vector4 Rotation;

    /// <summary>Creates constants for one camera and skybox submission.</summary>
    /// <param name="camera">Submitted camera matrices.</param>
    /// <param name="settings">Submitted skybox appearance.</param>
    /// <returns>Shader-compatible constants.</returns>
    public static SkyboxPushConstants Create(
        RenderCameraData camera,
        SkyboxRenderSettings settings)
    {
        if (!camera.IsValid || !Matrix4x4.Invert(
                camera.View * camera.Projection, out var inverseViewProjection))
            throw new ArgumentException("A valid invertible camera is required.", nameof(camera));
        if (!settings.IsEnabled)
            throw new ArgumentException("Enabled skybox settings are required.", nameof(settings));
        return new SkyboxPushConstants
        {
            InverseViewProjection = inverseViewProjection,
            TintIntensity = new Vector4(settings.Tint, settings.Intensity),
            Rotation = new Vector4(settings.Rotation, 0f, 0f, 0f)
        };
    }
}
