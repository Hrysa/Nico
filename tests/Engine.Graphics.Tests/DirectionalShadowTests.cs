using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

public sealed class DirectionalShadowTests
{
    /// <summary>Builds monotonic practical splits bounded by the authored shadow distance.</summary>
    [Fact]
    public void Calculate_DefaultPerspectiveCamera_ProducesThreeBoundedCascades()
    {
        var camera = CreateCamera(Vector3.Zero);

        var cascades = DirectionalShadowCascadeCalculator.Calculate(
            camera, Vector3.Normalize(new Vector3(1f, 2f, 1f)),
            DirectionalShadowSettings.Default, 2048);

        Assert.Equal(3, cascades.Count);
        Assert.True(cascades.SplitDistances.X > 0.1f);
        Assert.True(cascades.SplitDistances.Y > cascades.SplitDistances.X);
        Assert.True(cascades.SplitDistances.Z > cascades.SplitDistances.Y);
        Assert.Equal(50f, cascades.SplitDistances.Z, 3);
        Assert.True(cascades.WorldTexelSizes.X < cascades.WorldTexelSizes.Y);
        Assert.True(cascades.WorldTexelSizes.Y < cascades.WorldTexelSizes.Z);
    }

    /// <summary>Snaps every cascade projection to the shadow texel lattice.</summary>
    [Fact]
    public void Calculate_CascadeMatrices_HaveTexelAlignedWorldOrigin()
    {
        const int resolution = 2048;
        var cascades = DirectionalShadowCascadeCalculator.Calculate(
            CreateCamera(new Vector3(3.17f, 2.41f, 8.63f)),
            Vector3.Normalize(new Vector3(-0.4f, 1f, 0.3f)),
            DirectionalShadowSettings.Default, resolution);

        for (var index = 0; index < cascades.Count; index++)
        {
            var clip = Vector4.Transform(new Vector4(0f, 0f, 0f, 1f),
                cascades.GetMatrix(index));
            var texelX = clip.X / clip.W * resolution * 0.5f;
            var texelY = clip.Y / clip.W * resolution * 0.5f;
            Assert.InRange(MathF.Abs(texelX - MathF.Round(texelX)), 0f, 0.001f);
            Assert.InRange(MathF.Abs(texelY - MathF.Round(texelY)), 0f, 0.001f);
        }
    }

    /// <summary>Keeps every camera-frustum slice inside its fitted light projection.</summary>
    [Fact]
    public void Calculate_FrustumSliceCorners_FitEveryCascade()
    {
        var camera = CreateCamera(new Vector3(2f, 3f, 7f));
        var cascades = DirectionalShadowCascadeCalculator.Calculate(
            camera, Vector3.Normalize(new Vector3(0.4f, 1f, -0.2f)),
            DirectionalShadowSettings.Default, 2048);
        Matrix4x4.Invert(camera.View * camera.Projection, out var inverseViewProjection);
        Span<Vector3> nearCorners = stackalloc Vector3[4];
        Span<Vector3> farCorners = stackalloc Vector3[4];
        UnprojectCorners(inverseViewProjection, nearCorners, farCorners);
        const float cameraNear = 0.1f;
        const float cameraFar = 500f;
        var sliceNear = cameraNear;

        for (var cascadeIndex = 0; cascadeIndex < cascades.Count; cascadeIndex++)
        {
            var sliceFar = GetComponent(cascades.SplitDistances, cascadeIndex);
            for (var cornerIndex = 0; cornerIndex < 4; cornerIndex++)
            {
                var edge = farCorners[cornerIndex] - nearCorners[cornerIndex];
                AssertInsideClip(nearCorners[cornerIndex] + edge *
                    ((sliceNear - cameraNear) / (cameraFar - cameraNear)),
                    cascades.GetMatrix(cascadeIndex));
                AssertInsideClip(nearCorners[cornerIndex] + edge *
                    ((sliceFar - cameraNear) / (cameraFar - cameraNear)),
                    cascades.GetMatrix(cascadeIndex));
            }
            sliceNear = sliceFar;
        }
    }

    /// <summary>Keeps repeated cascade calculation allocation-free after warmup.</summary>
    [Fact]
    public void Calculate_RepeatedFrames_DoesNotAllocate()
    {
        var camera = CreateCamera(Vector3.Zero);
        var direction = Vector3.Normalize(new Vector3(1f, 2f, 1f));
        _ = DirectionalShadowCascadeCalculator.Calculate(
            camera, direction, DirectionalShadowSettings.Default, 2048);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 1_000; index++)
        {
            _ = DirectionalShadowCascadeCalculator.Calculate(
                camera, direction, DirectionalShadowSettings.Default, 2048);
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    /// <summary>Rejects invalid cascade quality settings at the SRP boundary.</summary>
    [Fact]
    public void Create_InvalidCascadeSettings_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DirectionalShadowSettings.Create(50f, 1f, 1f, 1f, cascadeCount: 5));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DirectionalShadowSettings.Create(50f, 1f, 1f, 1f, splitLambda: 1.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DirectionalShadowSettings.Create(50f, 1f, 1f, 1f, cascadeBlend: 0.4f));
    }

    /// <summary>Creates explicit camera state for a translated perspective view.</summary>
    /// <param name="position">Camera world position.</param>
    /// <returns>Validated render camera.</returns>
    private static RenderCameraData CreateCamera(Vector3 position)
    {
        var view = Matrix4x4.CreateLookAt(position, position - Vector3.UnitZ, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, 16f / 9f, 0.1f, 500f);
        projection.M22 = -projection.M22;
        return RenderCameraData.Create(view, projection);
    }

    /// <summary>Unprojects four Vulkan near and far corners for test verification.</summary>
    /// <param name="inverseViewProjection">Clip-to-world transform.</param>
    /// <param name="nearCorners">Near-plane destination.</param>
    /// <param name="farCorners">Far-plane destination.</param>
    private static void UnprojectCorners(
        Matrix4x4 inverseViewProjection,
        Span<Vector3> nearCorners,
        Span<Vector3> farCorners)
    {
        var index = 0;
        for (var y = -1; y <= 1; y += 2)
        {
            for (var x = -1; x <= 1; x += 2)
            {
                nearCorners[index] = Unproject(new Vector3(x, y, 0f), inverseViewProjection);
                farCorners[index] = Unproject(new Vector3(x, y, 1f), inverseViewProjection);
                index++;
            }
        }
    }

    /// <summary>Unprojects one normalized-device coordinate.</summary>
    /// <param name="point">Vulkan normalized-device coordinate.</param>
    /// <param name="inverseViewProjection">Clip-to-world transform.</param>
    /// <returns>World-space coordinate.</returns>
    private static Vector3 Unproject(Vector3 point, Matrix4x4 inverseViewProjection)
    {
        var homogeneous = Vector4.Transform(new Vector4(point, 1f), inverseViewProjection);
        return new Vector3(homogeneous.X, homogeneous.Y, homogeneous.Z) / homogeneous.W;
    }

    /// <summary>Asserts one world point lies within a cascade clip volume.</summary>
    /// <param name="world">World-space point.</param>
    /// <param name="matrix">World-to-cascade transform.</param>
    private static void AssertInsideClip(Vector3 world, Matrix4x4 matrix)
    {
        var clip = Vector4.Transform(new Vector4(world, 1f), matrix);
        var projected = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
        Assert.InRange(projected.X, -1.001f, 1.001f);
        Assert.InRange(projected.Y, -1.001f, 1.001f);
        Assert.InRange(projected.Z, -0.001f, 1.001f);
    }

    /// <summary>Reads one vector component by index.</summary>
    /// <param name="value">Source vector.</param>
    /// <param name="index">Component index.</param>
    /// <returns>Selected component.</returns>
    private static float GetComponent(Vector4 value, int index) => index switch
    {
        0 => value.X,
        1 => value.Y,
        2 => value.Z,
        3 => value.W,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
}
