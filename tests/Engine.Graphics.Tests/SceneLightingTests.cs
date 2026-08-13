using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

public sealed class SceneLightingTests
{
    /// <summary>Produces no contribution when a scene has no authored light.</summary>
    [Fact]
    public void Resolve_NoLight_ReturnsNone()
    {
        Assert.Equal(SceneLighting.None, SceneLighting.Resolve(new Node3D()));
    }

    /// <summary>Produces no contribution when every authored light is disabled.</summary>
    [Fact]
    public void Resolve_OnlyDisabledLights_ReturnsNone()
    {
        var root = new Node3D();
        root.AddChild(new DirectionalLight3D { IsEnabled = false });

        Assert.Equal(SceneLighting.None, SceneLighting.Resolve(root));
    }

    /// <summary>Resolves the first enabled light with its world rotation and authored values.</summary>
    [Fact]
    public void Resolve_EnabledDirectionalLight_ReturnsAuthoredLighting()
    {
        var root = new Node3D
        {
            Rotation = new Vector3(0f, MathF.PI * 0.5f, 0f),
            Scale = new Vector3(1f, 5f, 0.25f)
        };
        var disabled = new DirectionalLight3D { IsEnabled = false };
        var light = new DirectionalLight3D
        {
            Color = new Vector3(1f, 0.5f, 0.25f),
            Intensity = 2f,
            AmbientIntensity = 0.1f
        };
        root.AddChild(disabled);
        root.AddChild(light);

        var actual = SceneLighting.Resolve(root);

        Assert.Equal(light.Color, actual.Color);
        Assert.Equal(2f, actual.Intensity);
        Assert.Equal(0.1f, actual.AmbientIntensity);
        Assert.InRange(Vector3.Distance(Vector3.UnitX, actual.DirectionToLight), 0f, 0.00001f);
    }

    /// <summary>Packs resolved lighting after the three renderer-independent matrices.</summary>
    [Fact]
    public void ModelPushConstants_Create_PreservesTransformsAndLighting()
    {
        var transforms = new PushConstants
        {
            Model = Matrix4x4.CreateTranslation(1f, 2f, 3f),
            View = Matrix4x4.CreateRotationY(0.5f),
            Projection = Matrix4x4.CreateScale(2f)
        };
        var lighting = new SceneLighting(Vector3.UnitY,
            new Vector3(0.2f, 0.4f, 0.6f), 3f, 0.25f);

        var actual = ModelPushConstants.Create(transforms, lighting, 0.35f, 0.65f);

        Assert.Equal(transforms.Model, actual.Model);
        Assert.Equal(transforms.View, actual.View);
        Assert.Equal(transforms.Projection, actual.Projection);
        Assert.Equal(new Vector4(0f, 1f, 0f, 3f), actual.LightDirectionIntensity);
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 0.25f), actual.LightColorAmbient);
        Assert.Equal(new Vector4(Matrix4x4.Invert(transforms.View, out var inverseView)
            ? inverseView.Translation : Vector3.Zero, 0.35f), actual.CameraPositionMetallic);
        Assert.Equal(new Vector4(0.65f, 0f, 0f, 0f), actual.MaterialFactors);
    }
}
