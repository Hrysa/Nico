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

        Assert.Equal(5, passes.Length);
        Assert.Equal(RenderPipelineStage.Shadows, passes[0].Stage);
        Assert.Equal(RenderPipelineStage.DepthPrepass, passes[1].Stage);
        Assert.Equal(RenderPipelineStage.Opaque, passes[2].Stage);
        Assert.Equal(RenderPipelineStage.Transparent, passes[3].Stage);
        Assert.Equal(RenderPipelineStage.PostProcess, passes[4].Stage);
    }

    /// <summary>Runs setup, execution, and cleanup in configured pass order.</summary>
    [Fact]
    public void Render_CustomPasses_RunCompleteLifecycleInOrder()
    {
        var events = new List<string>();
        var pipeline = new RenderPipeline(
            new RecordingPass(RenderPipelineStage.Shadows, "shadow", events),
            new RecordingPass(RenderPipelineStage.Opaque, "opaque", events, submit: true),
            new RecordingPass(RenderPipelineStage.PostProcess, "post", events,
                grayscaleStrength: 1f));
        var submitter = new RecordingSubmitter();

        pipeline.Render(submitter, new RenderViewHandle(1), new RenderQueue());

        Assert.Equal([
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

    /// <summary>Rejects a second submission from a malformed custom pipeline.</summary>
    [Fact]
    public void Render_WithTwoSubmittingPasses_Throws()
    {
        var pipeline = new RenderPipeline(
            new ForwardOpaqueRenderPass(), new ForwardOpaqueRenderPass());

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
        private readonly bool _submit;
        private readonly float _grayscaleStrength;

        /// <summary>Creates one lifecycle-recording test pass.</summary>
        /// <param name="stage">Semantic stage.</param>
        /// <param name="name">Event prefix.</param>
        /// <param name="events">Destination event list.</param>
        /// <param name="submit">Whether execution submits the scene.</param>
        /// <param name="grayscaleStrength">Output strength written during execution.</param>
        public RecordingPass(RenderPipelineStage stage, string name,
            List<string> events, bool submit = false,
            float grayscaleStrength = 0f) : base(stage)
        {
            _name = name;
            _events = events;
            _submit = submit;
            _grayscaleStrength = grayscaleStrength;
        }

        /// <inheritdoc/>
        public override void Setup(ref RenderPipelineContext context) =>
            _events.Add($"{_name}.setup");

        /// <inheritdoc/>
        public override void Execute(ref RenderPipelineContext context)
        {
            _events.Add($"{_name}.execute");
            if (_submit)
                context.SubmitScene();
            if (_grayscaleStrength > 0f)
                context.Queue.Output = RenderOutputSettings.Create(_grayscaleStrength);
        }

        /// <inheritdoc/>
        public override void Cleanup(ref RenderPipelineContext context) =>
            _events.Add($"{_name}.cleanup");
    }
}
