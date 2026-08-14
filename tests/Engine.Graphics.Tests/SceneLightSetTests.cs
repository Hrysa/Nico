using System.Numerics;
using Engine.Core;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>Tests renderer-independent per-view light collection.</summary>
public sealed class SceneLightSetTests
{
    /// <summary>Collects directional, point, and spot lights in hierarchy order.</summary>
    [Fact]
    public void Resolve_MixedLights_ProducesTypedGpuReadyData()
    {
        var root = new Node3D();
        root.AddChild(new DirectionalLight3D
        {
            Color = new Vector3(1f, 0.8f, 0.6f),
            Intensity = 2f,
            AmbientIntensity = 0.15f
        });
        root.AddChild(new PointLight3D
        {
            Position = new Vector3(2f, 3f, 4f),
            Range = 12f,
            Intensity = 4f
        });
        root.AddChild(new SpotLight3D
        {
            Position = new Vector3(5f, 6f, 7f),
            InnerAngle = 20f,
            OuterAngle = 40f,
            Range = 18f
        });
        var lights = new SceneLightSet();

        lights.Resolve(root);

        Assert.Equal(3, lights.Count);
        Assert.Equal(SceneLightType.Directional, lights.Lights[0].Type);
        Assert.Equal(SceneLightType.Point, lights.Lights[1].Type);
        Assert.Equal(new Vector3(2f, 3f, 4f), lights.Lights[1].Position);
        Assert.Equal(12f, lights.Lights[1].Range);
        Assert.Equal(SceneLightType.Spot, lights.Lights[2].Type);
        Assert.True(lights.Lights[2].InnerConeCosine >
            lights.Lights[2].OuterConeCosine);
        Assert.Equal(0, lights.MainDirectionalIndex);
        Assert.Equal(0.15f, lights.AmbientIntensity);
    }

    /// <summary>Ignores disabled lights and clears previous results on reuse.</summary>
    [Fact]
    public void Resolve_DisabledLights_ReusesAndClearsCollection()
    {
        var root = new Node3D();
        root.AddChild(new PointLight3D { IsEnabled = false });
        var lights = new SceneLightSet();
        lights.Resolve(new DirectionalLight3D());

        lights.Resolve(root);

        Assert.Equal(0, lights.Count);
        Assert.Equal(-1, lights.MainDirectionalIndex);
        Assert.Equal(0f, lights.AmbientIntensity);
    }

    /// <summary>Keeps repeated hierarchy collection free of managed allocations.</summary>
    [Fact]
    public void Resolve_RepeatedCollection_DoesNotAllocate()
    {
        var root = new Node3D();
        root.AddChild(new DirectionalLight3D());
        root.AddChild(new PointLight3D());
        root.AddChild(new SpotLight3D());
        var lights = new SceneLightSet();
        lights.Resolve(root);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 1_000; index++)
            lights.Resolve(root);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Assigns stable atlas rows only to the supported number of local shadow lights.</summary>
    [Fact]
    public void Resolve_ShadowedLocalLights_AssignsBoundedSlots()
    {
        var root = new Node3D();
        for (var index = 0; index < SceneLightSet.MaximumShadowedLocalLights + 1; index++)
        {
            root.AddChild(new PointLight3D
            {
                Position = new Vector3(index, 0f, 0f),
                CastsShadows = true
            });
        }
        var lights = new SceneLightSet();

        lights.Resolve(root);

        for (var index = 0; index < SceneLightSet.MaximumShadowedLocalLights; index++)
            Assert.Equal(index, lights.Lights[index].ShadowIndex);
        Assert.Equal(-1, lights.Lights[^1].ShadowIndex);
    }

    /// <summary>Builds one spotlight face and six finite point-light faces.</summary>
    [Fact]
    public void LocalShadowMatrixCalculator_BuildsExpectedFaceCounts()
    {
        var root = new Node3D();
        root.AddChild(new PointLight3D
        {
            Position = new Vector3(1f, 2f, 3f),
            Range = 12f,
            CastsShadows = true
        });
        root.AddChild(new SpotLight3D
        {
            Position = new Vector3(3f, 4f, 5f),
            Range = 18f,
            CastsShadows = true
        });
        var lights = new SceneLightSet();
        lights.Resolve(root);

        var point = LocalShadowMatrixCalculator.CalculatePoint(lights.Lights[0]);
        var spot = LocalShadowMatrixCalculator.CalculateSpot(lights.Lights[1]);

        Assert.Equal(6, point.Count);
        Assert.Equal(1, spot.Count);
        Assert.True(Matrix4x4.Invert(point.GetMatrix(5), out _));
        Assert.True(Matrix4x4.Invert(spot.GetMatrix(0), out _));
    }
}
