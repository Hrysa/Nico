using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

public sealed class ModelPushConstantTests
{
    /// <summary>Packs object, camera, and material state without duplicating per-view lights.</summary>
    [Fact]
    public void ModelPushConstants_Create_PreservesTransformsAndMaterial()
    {
        var transforms = new PushConstants
        {
            Model = Matrix4x4.CreateTranslation(1f, 2f, 3f),
            View = Matrix4x4.CreateRotationY(0.5f),
            Projection = Matrix4x4.CreateScale(2f)
        };
        var actual = ModelPushConstants.Create(transforms, 0.35f, 0.65f);

        Assert.Equal(transforms.Model, actual.Model);
        Assert.Equal(transforms.View * transforms.Projection, actual.ViewProjection);
        Assert.Equal(new Vector4(0.65f, 0.35f, 0f, 0f), actual.MaterialFactors);
    }
}
