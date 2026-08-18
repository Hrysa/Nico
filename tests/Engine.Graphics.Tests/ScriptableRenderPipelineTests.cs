using Engine.Graphics;
using System.Numerics;
using Xunit;

namespace Engine.Graphics.Tests;

public sealed class ScriptableRenderPipelineTests
{
    /// <summary>Verifies the default pipeline exposes the planned stage order.</summary>
    [Fact]
    public void BasicForwardPipeline_ContainsExpectedStages()
    {
        var passes = BasicForwardRenderPipeline.Instance.Passes;

        Assert.Equal(8, passes.Length);
        Assert.Equal(RenderPipelineStage.CameraSetup, passes[0].Stage);
        Assert.IsType<CameraClearRenderPass>(passes[0]);
        Assert.Equal(RenderPipelineStage.Shadows, passes[1].Stage);
        Assert.IsType<DirectionalShadowRenderPass>(passes[1]);
        Assert.Equal(RenderPipelineStage.Shadows, passes[2].Stage);
        Assert.IsType<LocalShadowRenderPass>(passes[2]);
        Assert.Equal(RenderPipelineStage.DepthPrepass, passes[3].Stage);
        Assert.IsType<DepthPrepassRenderPass>(passes[3]);
        Assert.Equal(RenderPipelineStage.Skybox, passes[4].Stage);
        Assert.IsType<SkyboxRenderPass>(passes[4]);
        Assert.Equal(RenderPipelineStage.Opaque, passes[5].Stage);
        Assert.Equal(RenderPipelineStage.Transparent, passes[6].Stage);
        Assert.IsType<ForwardTransparentRenderPass>(passes[6]);
        Assert.Equal(RenderPipelineStage.PostProcess, passes[7].Stage);
        Assert.IsType<OutputPostProcessRenderPass>(passes[7]);
    }

    /// <summary>Schedules an authored environment between depth and opaque rendering.</summary>
    [Fact]
    public void BasicForwardPipeline_EnabledSkybox_RecordsExplicitStage()
    {
        var queue = new RenderQueue
        {
            Skybox = SkyboxRenderSettings.Create(
                new TextureHandle(7), new Vector3(0.8f, 0.9f, 1f), 1.5f, 0.25f)
        };

        BasicForwardRenderPipeline.Instance.Render(
            new RecordingSubmitter(), new RenderViewHandle(1), queue);

        var commands = queue.PipelineCommandSpan;
        Assert.Equal(8, commands.Length);
        Assert.Equal(RenderPipelineCommandKind.DrawSkybox, commands[4].Kind);
        Assert.Equal(RenderPipelineStage.Skybox, commands[4].Stage);
        Assert.Equal(RenderPipelineResource.CameraDepth, commands[4].Reads);
        Assert.Equal(RenderPipelineResource.CameraColor, commands[4].Writes);
        Assert.Equal(RenderPipelineCommandKind.DrawRenderers, commands[5].Kind);
    }

    /// <summary>Records a real depth-only draw before forward color rendering.</summary>
    [Fact]
    public void BasicForwardPipeline_RecordsDepthAndForwardResourceUsage()
    {
        var queue = new RenderQueue();

        BasicForwardRenderPipeline.Instance.Render(
            new RecordingSubmitter(), new RenderViewHandle(1), queue);

        var commands = queue.PipelineCommandSpan;
        Assert.Equal(7, commands.Length);
        Assert.Equal(RenderPipelineCommandKind.ClearCamera, commands[0].Kind);
        Assert.Equal(RenderPipelineResource.CameraColor | RenderPipelineResource.CameraDepth,
            commands[0].Writes);
        Assert.Equal(RenderPipelineCommandKind.LocalShadows, commands[2].Kind);
        Assert.Equal(RenderPipelineResource.LocalShadowAtlas, commands[2].Writes);
        Assert.Equal(RenderMaterialPass.DepthOnly, commands[3].MaterialPass);
        Assert.Equal(RenderQueueFilter.Opaque, commands[3].QueueFilter);
        Assert.Equal(RenderPipelineResource.CameraDepth, commands[3].Writes);
        Assert.Equal(RenderMaterialPass.Forward, commands[4].MaterialPass);
        Assert.Equal(RenderPipelineResource.CameraDepth |
            RenderPipelineResource.DirectionalShadowAtlas |
            RenderPipelineResource.LocalShadowAtlas, commands[4].Reads);
        Assert.Equal(RenderPipelineResource.CameraColor | RenderPipelineResource.CameraDepth,
            commands[4].Writes);
        Assert.Equal(RenderPipelineStage.Transparent, commands[5].Stage);
        Assert.Equal(RenderQueueFilter.Transparent, commands[5].QueueFilter);
        Assert.Equal(RenderPipelineResource.CameraColor | RenderPipelineResource.CameraDepth |
            RenderPipelineResource.DirectionalShadowAtlas |
            RenderPipelineResource.LocalShadowAtlas, commands[5].Reads);
        Assert.Equal(RenderPipelineResource.CameraColor, commands[5].Writes);
        Assert.Equal(RenderPipelineCommandKind.ApplyPostProcess, commands[6].Kind);
        Assert.Equal(RenderPipelineResource.CameraColor, commands[6].Reads);
        Assert.Equal(RenderPipelineResource.PresentedColor, commands[6].Writes);
    }

    /// <summary>Applies validated directional shadow settings at the SRP boundary.</summary>
    [Fact]
    public void DirectionalShadowPass_Render_RecordsExplicitBackendWork()
    {
        var settings = DirectionalShadowSettings.Create(80f, 1f, 2f, 0.7f);
        var pipeline = new RenderPipeline(
            new CameraClearRenderPass(), new DirectionalShadowRenderPass(settings),
            new ForwardOpaqueRenderPass(), new OutputPostProcessRenderPass());
        var submitter = new RecordingSubmitter();
        var queue = new RenderQueue();

        pipeline.Render(submitter, new RenderViewHandle(1), queue);

        var commands = queue.PipelineCommandSpan;
        Assert.Equal(4, commands.Length);
        Assert.Equal(RenderPipelineCommandKind.DirectionalShadows, commands[1].Kind);
        Assert.Equal(RenderPipelineStage.Shadows, commands[1].Stage);
        Assert.Equal(settings, commands[1].DirectionalShadows);
        Assert.Equal(RenderPipelineCommandKind.DrawRenderers, commands[2].Kind);
        Assert.Equal(RenderPipelineStage.Opaque, commands[2].Stage);
        Assert.Equal(RenderQueueFilter.Opaque | RenderQueueFilter.AlphaTest,
            commands[2].QueueFilter);
        Assert.Equal(RenderMaterialPass.Forward, commands[2].MaterialPass);
        Assert.Equal(RenderPipelineResource.CameraDepth |
            RenderPipelineResource.DirectionalShadowAtlas |
            RenderPipelineResource.LocalShadowAtlas, commands[2].Reads);
        Assert.Equal(RenderPipelineResource.CameraColor | RenderPipelineResource.CameraDepth,
            commands[2].Writes);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DirectionalShadowSettings.Create(0f, 1f, 2f, 1f));
    }

    /// <summary>Runs setup, execution, and cleanup in configured pass order.</summary>
    [Fact]
    public void Render_CustomPasses_RunCompleteLifecycleInOrder()
    {
        var events = new List<string>();
        var pipeline = new RenderPipeline(
            new RecordingPass(RenderPipelineStage.CameraSetup, "camera", events,
                clearCamera: true),
            new RecordingPass(RenderPipelineStage.Shadows, "shadow", events),
            new RecordingPass(RenderPipelineStage.Opaque, "opaque", events, drawOpaque: true),
            new RecordingPass(RenderPipelineStage.PostProcess, "post", events,
                grayscaleStrength: 1f));
        var submitter = new RecordingSubmitter();

        pipeline.Render(submitter, new RenderViewHandle(1), new RenderQueue());

        Assert.Equal([
            "camera.setup", "camera.execute", "camera.cleanup",
            "shadow.setup", "shadow.execute", "shadow.cleanup",
            "opaque.setup", "opaque.execute", "opaque.cleanup",
            "post.setup", "post.execute", "post.cleanup"
        ], events);
        Assert.Equal(1, submitter.SubmissionCount);
        Assert.Equal(1f, submitter.LastOutput.GrayscaleStrength);
    }

    /// <summary>Rejects configurations that never produce the required scene submission.</summary>
    [Fact]
    public void Render_WithoutOpaqueSubmission_Throws()
    {
        var pipeline = new RenderPipeline(
            new EmptyRenderPipelinePass(RenderPipelineStage.PostProcess));

        Assert.Throws<InvalidOperationException>(() => pipeline.Render(
            new RecordingSubmitter(), new RenderViewHandle(1), new RenderQueue()));
    }

    /// <summary>Rejects pass arrays that contradict semantic SRP stage ordering.</summary>
    [Fact]
    public void Constructor_OutOfOrderStages_Throws()
    {
        Assert.Throws<ArgumentException>(() => new RenderPipeline(
            new ForwardOpaqueRenderPass(), new DirectionalShadowRenderPass()));
    }

    /// <summary>Allows multiple explicit draw commands for future multi-stage pipelines.</summary>
    [Fact]
    public void Render_WithTwoDrawingPasses_RecordsBothCommands()
    {
        var pipeline = new RenderPipeline(
            new CameraClearRenderPass(), new ForwardOpaqueRenderPass(),
            new ForwardOpaqueRenderPass(), new OutputPostProcessRenderPass());
        var queue = new RenderQueue();

        pipeline.Render(new RecordingSubmitter(), new RenderViewHandle(1), queue);

        Assert.Equal(4, queue.PipelineCommandSpan.Length);
    }

    /// <summary>Compiles every write hazard in the built-in pass sequence.</summary>
    [Fact]
    public void BasicForwardPipeline_CompilesResourceHazards()
    {
        var queue = new RenderQueue();

        BasicForwardRenderPipeline.Instance.Render(
            new RecordingSubmitter(), new RenderViewHandle(1), queue);

        Assert.Equal([
            new RenderPipelineBarrier(3, RenderPipelineResource.CameraDepth,
                RenderPipelineStage.CameraSetup, RenderPipelineStage.DepthPrepass),
            new RenderPipelineBarrier(4, RenderPipelineResource.CameraColor,
                RenderPipelineStage.CameraSetup, RenderPipelineStage.Opaque),
            new RenderPipelineBarrier(4, RenderPipelineResource.CameraDepth,
                RenderPipelineStage.DepthPrepass, RenderPipelineStage.Opaque),
            new RenderPipelineBarrier(4, RenderPipelineResource.DirectionalShadowAtlas,
                RenderPipelineStage.Shadows, RenderPipelineStage.Opaque),
            new RenderPipelineBarrier(4, RenderPipelineResource.LocalShadowAtlas,
                RenderPipelineStage.Shadows, RenderPipelineStage.Opaque),
            new RenderPipelineBarrier(5, RenderPipelineResource.CameraColor,
                RenderPipelineStage.Opaque, RenderPipelineStage.Transparent),
            new RenderPipelineBarrier(5, RenderPipelineResource.CameraDepth,
                RenderPipelineStage.Opaque, RenderPipelineStage.Transparent),
            new RenderPipelineBarrier(6, RenderPipelineResource.CameraColor,
                RenderPipelineStage.Transparent, RenderPipelineStage.PostProcess)
        ], queue.PipelineBarrierSpan.ToArray());
    }

    /// <summary>Rejects rendering that relies on an implicit backend camera clear.</summary>
    [Fact]
    public void Render_WithoutCameraInitialization_Throws()
    {
        var pipeline = new RenderPipeline(new ForwardOpaqueRenderPass());

        Assert.Throws<InvalidOperationException>(() => pipeline.Render(
            new RecordingSubmitter(), new RenderViewHandle(1), new RenderQueue()));
    }

    /// <summary>Rejects ambiguous repeated initialization of camera attachments.</summary>
    [Fact]
    public void Render_WithRepeatedCameraInitialization_Throws()
    {
        var pipeline = new RenderPipeline(
            new CameraClearRenderPass(), new CameraClearRenderPass(),
            new ForwardOpaqueRenderPass());

        Assert.Throws<InvalidOperationException>(() => pipeline.Render(
            new RecordingSubmitter(), new RenderViewHandle(1), new RenderQueue()));
    }

    /// <summary>Keeps steady-state default-pipeline execution free of managed allocations.</summary>
    [Fact]
    public void BasicForwardPipeline_Render_DoesNotAllocate()
    {
        var pipeline = BasicForwardRenderPipeline.Instance;
        var submitter = new RecordingSubmitter();
        var view = new RenderViewHandle(1);
        var queue = new RenderQueue();
        pipeline.Render(submitter, view, queue);
        var before = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 1_000; index++)
            pipeline.Render(submitter, view, queue);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Validates and packs grayscale output strength for the texture shader.</summary>
    [Fact]
    public void RenderOutputSettings_Create_PacksTextureConstants()
    {
        var output = RenderOutputSettings.Create(0.75f);

        var constants = TexturePushConstants.Create(new PushConstants
        {
            Model = Matrix4x4.Identity,
            View = Matrix4x4.Identity,
            Projection = Matrix4x4.Identity
        }, output);

        Assert.Equal(new Vector4(0.75f, 0f, 0f, 0f), constants.OutputEffects);
        Assert.Throws<ArgumentOutOfRangeException>(() => RenderOutputSettings.Create(-0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => RenderOutputSettings.Create(1.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => RenderOutputSettings.Create(float.NaN));
    }

    private sealed class RecordingSubmitter : IRenderQueueSubmitter
    {
        /// <summary>Gets the number of queues received.</summary>
        public int SubmissionCount { get; private set; }

        /// <summary>Gets output settings visible at the deferred submission boundary.</summary>
        public RenderOutputSettings LastOutput { get; private set; }

        /// <inheritdoc/>
        public void Submit(RenderViewHandle view, RenderQueue renderQueue)
        {
            SubmissionCount++;
            LastOutput = renderQueue.Output;
        }
    }

    private sealed class RecordingPass : RenderPipelinePass
    {
        private readonly string _name;
        private readonly List<string> _events;
        private readonly bool _clearCamera;
        private readonly bool _drawOpaque;
        private readonly float _grayscaleStrength;

        /// <summary>Creates one lifecycle-recording test pass.</summary>
        /// <param name="stage">Semantic stage.</param>
        /// <param name="name">Event prefix.</param>
        /// <param name="events">Destination event list.</param>
        /// <param name="clearCamera">Whether execution initializes camera attachments.</param>
        /// <param name="drawOpaque">Whether execution schedules opaque scene geometry.</param>
        /// <param name="grayscaleStrength">Output strength written during execution.</param>
        public RecordingPass(RenderPipelineStage stage, string name,
            List<string> events, bool clearCamera = false, bool drawOpaque = false,
            float grayscaleStrength = 0f) : base(stage)
        {
            _name = name;
            _events = events;
            _clearCamera = clearCamera;
            _drawOpaque = drawOpaque;
            _grayscaleStrength = grayscaleStrength;
        }

        /// <inheritdoc/>
        public override void Setup(ref RenderPipelineContext context) =>
            _events.Add($"{_name}.setup");

        /// <inheritdoc/>
        public override void Execute(ref RenderPipelineContext context)
        {
            _events.Add($"{_name}.execute");
            if (_clearCamera)
                context.ClearCamera();
            if (_drawOpaque)
            {
                context.DrawRenderers(
                    RenderQueueFilter.Opaque,
                    RenderMaterialPass.Forward,
                    RenderPipelineResource.None,
                    RenderPipelineResource.CameraColor | RenderPipelineResource.CameraDepth);
            }
            if (_grayscaleStrength > 0f)
                context.ApplyPostProcess(RenderOutputSettings.Create(_grayscaleStrength));
        }

        /// <inheritdoc/>
        public override void Cleanup(ref RenderPipelineContext context) =>
            _events.Add($"{_name}.cleanup");
    }
}
