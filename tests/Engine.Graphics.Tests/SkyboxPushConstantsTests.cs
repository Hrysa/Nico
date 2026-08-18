using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

public sealed class SkyboxPushConstantsTests
{
    /// <summary>Creates shader values from the submitted camera and appearance.</summary>
    [Fact]
    public void Create_ValidCamera_PreservesInverseAndAppearance()
    {
        var view = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        var projection = Matrix4x4.CreateScale(2f, 3f, 4f);
        var camera = RenderCameraData.Create(view, projection);
        var settings = SkyboxRenderSettings.Create(
            new TextureHandle(2), new Vector3(0.5f, 0.75f, 1f), 2f, 0.4f);

        var constants = SkyboxPushConstants.Create(camera, settings);

        Assert.True(Matrix4x4.Invert(view * projection, out var expectedInverse));
        Assert.Equal(expectedInverse, constants.InverseViewProjection);
        Assert.Equal(new Vector4(0.5f, 0.75f, 1f, 2f), constants.TintIntensity);
        Assert.Equal(new Vector4(0.4f, 0f, 0f, 0f), constants.Rotation);
    }

    /// <summary>Rejects camera state that cannot reconstruct world-space rays.</summary>
    [Fact]
    public void Create_InvalidCamera_Throws()
    {
        var settings = SkyboxRenderSettings.Create(
            new TextureHandle(2), Vector3.One, 1f, 0f);

        Assert.Throws<ArgumentException>(() =>
            SkyboxPushConstants.Create(default, settings));
    }
}
