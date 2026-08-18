using System.Numerics;
using Engine.Graphics;
using Xunit;

namespace Engine.Graphics.Tests;

/// <summary>Tests retained mesh submissions.</summary>
public sealed class RenderQueueTests
{
    /// <summary>Verifies a queue stores a mesh identity instead of copying vertex arrays.</summary>
    [Fact]
    public void Add_ValidMesh_StoresHandleAndTransforms()
    {
        var queue = new RenderQueue();
        var handle = new MeshHandle(42);
        var pushConstants = new PushConstants { Model = Matrix4x4.Identity };

        queue.Add(handle, pushConstants);

        var command = Assert.Single(queue.Commands);
        Assert.Equal(handle, command.Mesh);
        Assert.Equal(Matrix4x4.Identity, command.PushConstants.Model);
    }

    /// <summary>Verifies the invalid default handle cannot enter a render queue.</summary>
    [Fact]
    public void Add_DefaultMesh_Throws()
    {
        var queue = new RenderQueue();

        Assert.Throws<ArgumentException>(() => queue.Add(default, default));
    }

    /// <summary>Preserves explicit shadow-caster participation on retained draws.</summary>
    [Fact]
    public void Add_NonCaster_StoresShadowParticipation()
    {
        var queue = new RenderQueue();

        queue.Add(new MeshHandle(42), default, castsShadows: false);

        Assert.False(Assert.Single(queue.Commands).CastsShadows);
    }

    /// <summary>Preserves authored surface classification for SRP queue filtering.</summary>
    [Fact]
    public void Add_Transparent_StoresSurfaceType()
    {
        var queue = new RenderQueue();

        queue.Add(new MeshHandle(42), default,
            surfaceType: RenderSurfaceType.Transparent);

        Assert.Equal(RenderSurfaceType.Transparent,
            Assert.Single(queue.Commands).SurfaceType);
    }

    /// <summary>Stores the palette identity required by a skinned draw.</summary>
    [Fact]
    public void AddSkinned_ValidHandles_StoresPalette()
    {
        var queue = new RenderQueue();
        var mesh = new MeshHandle(42);
        var palette = new SkinPaletteHandle(7);

        queue.AddSkinned(mesh, palette, default);

        var command = Assert.Single(queue.Commands);
        Assert.Equal(mesh, command.Mesh);
        Assert.Equal(palette, command.SkinPalette);
    }

    /// <summary>Clears explicit view-dependent camera state with the frame queue.</summary>
    [Fact]
    public void Clear_ResetsRenderCamera()
    {
        var queue = new RenderQueue
        {
            Camera = RenderCameraData.Create(Matrix4x4.Identity, Matrix4x4.Identity)
        };

        queue.Clear();

        Assert.False(queue.Camera.IsValid);
    }

    /// <summary>Clears the optional environment together with other per-frame state.</summary>
    [Fact]
    public void Clear_ResetsSkybox()
    {
        var queue = new RenderQueue
        {
            Skybox = SkyboxRenderSettings.Create(
                new TextureHandle(5), Vector3.One, 1f, 0f)
        };

        queue.Clear();

        Assert.False(queue.Skybox.IsEnabled);
    }

    /// <summary>Rejects invalid appearance values at the renderer boundary.</summary>
    [Fact]
    public void SkyboxSettings_InvalidValues_Throw()
    {
        Assert.Throws<ArgumentException>(() =>
            SkyboxRenderSettings.Create(default, Vector3.One, 1f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SkyboxRenderSettings.Create(new TextureHandle(1), -Vector3.One, 1f, 0f));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SkyboxRenderSettings.Create(new TextureHandle(1), Vector3.One, -1f, 0f));
    }

    /// <summary>Clears SRP-authored backend work together with frame draw state.</summary>
    [Fact]
    public void Clear_RemovesPipelineCommands()
    {
        var queue = new RenderQueue();
        BasicForwardRenderPipeline.Instance.Render(
            new RecordingSubmitter(), new RenderViewHandle(1), queue);

        queue.Clear();

        Assert.True(queue.PipelineCommandSpan.IsEmpty);
    }

    /// <summary>Verifies repeated command enumeration through the hot-path span does not allocate.</summary>
    [Fact]
    public void CommandSpan_RepeatedEnumerationDoesNotAllocate()
    {
        var queue = new RenderQueue();
        queue.Add(new MeshHandle(42), default);
        var warmup = queue.CommandSpan[0].Mesh.Value;
        var allocationStart = GC.GetAllocatedBytesForCurrentThread();

        ulong sum = 0;
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            foreach (var command in queue.CommandSpan)
                sum += command.Mesh.Value;
        }

        var allocationEnd = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(42UL, warmup);
        Assert.Equal(420_000UL, sum);
        Assert.Equal(allocationStart, allocationEnd);
    }

    /// <summary>Sorts transparent surfaces far-to-near without moving opaque slots.</summary>
    [Fact]
    public void PipelineRender_TransparentDraws_SortsBackToFrontStably()
    {
        var queue = new RenderQueue
        {
            Camera = RenderCameraData.Create(Matrix4x4.Identity, Matrix4x4.Identity)
        };
        queue.Add(new MeshHandle(1), CreateTranslatedConstants(1f),
            surfaceType: RenderSurfaceType.Transparent);
        queue.Add(new MeshHandle(2), CreateTranslatedConstants(50f));
        queue.Add(new MeshHandle(3), CreateTranslatedConstants(10f),
            surfaceType: RenderSurfaceType.Transparent);

        BasicForwardRenderPipeline.Instance.Render(
            new RecordingSubmitter(), new RenderViewHandle(1), queue);

        Assert.Equal(new MeshHandle(3), queue.CommandSpan[0].Mesh);
        Assert.Equal(new MeshHandle(2), queue.CommandSpan[1].Mesh);
        Assert.Equal(new MeshHandle(1), queue.CommandSpan[2].Mesh);
    }

    /// <summary>Creates identity camera constants with one translated model.</summary>
    /// <param name="distance">World-space X translation.</param>
    /// <returns>Translated render constants.</returns>
    private static PushConstants CreateTranslatedConstants(float distance) => new()
    {
        Model = Matrix4x4.CreateTranslation(distance, 0f, 0f),
        View = Matrix4x4.Identity,
        Projection = Matrix4x4.Identity
    };

    private sealed class RecordingSubmitter : IRenderQueueSubmitter
    {
        /// <inheritdoc/>
        public void Submit(RenderViewHandle view, RenderQueue renderQueue)
        {
        }
    }
}
