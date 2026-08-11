namespace Engine.Graphics;

/// <summary>Accepts a completed render queue without exposing a graphics backend.</summary>
public interface IRenderQueueSubmitter
{
    /// <summary>Submits an ordered render queue to a render view.</summary>
    /// <param name="view">Render view receiving the queue.</param>
    /// <param name="renderQueue">Commands to enqueue for the current frame.</param>
    void Submit(RenderViewHandle view, RenderQueue renderQueue);
}

/// <summary>Allows an active scene to replace its game-view render pipeline.</summary>
public interface ISceneRenderingService
{
    /// <summary>Gets or sets the pipeline used for subsequent game-view frames.</summary>
    RenderPipeline RenderPipeline { get; set; }
}

/// <summary>Describes renderer-independent effects applied while presenting a render view.</summary>
public readonly record struct RenderOutputSettings
{
    /// <summary>Creates output settings after caller validation.</summary>
    /// <param name="grayscaleStrength">Validated grayscale blend.</param>
    private RenderOutputSettings(float grayscaleStrength)
    {
        GrayscaleStrength = grayscaleStrength;
    }

    /// <summary>Gets the blend from original color at zero to grayscale at one.</summary>
    public float GrayscaleStrength { get; }

    /// <summary>Gets output with no presentation effects.</summary>
    public static RenderOutputSettings None { get; } = new(0f);

    /// <summary>Creates validated output settings.</summary>
    /// <param name="grayscaleStrength">Blend from original color at zero to grayscale at one.</param>
    /// <returns>Validated settings.</returns>
    public static RenderOutputSettings Create(float grayscaleStrength)
    {
        if (!float.IsFinite(grayscaleStrength) || grayscaleStrength < 0f ||
            grayscaleStrength > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(grayscaleStrength),
                "Grayscale strength must be finite and between zero and one.");
        }
        return new RenderOutputSettings(grayscaleStrength);
    }
}

/// <summary>Identifies the role and ordering intent of a render-pipeline pass.</summary>
public enum RenderPipelineStage
{
    /// <summary>Produces light-space visibility for shadowed lights.</summary>
    Shadows,
    /// <summary>Produces camera-space depth before color rendering.</summary>
    DepthPrepass,
    /// <summary>Renders opaque surfaces with forward lighting.</summary>
    Opaque,
    /// <summary>Renders blended surfaces after opaque geometry.</summary>
    Transparent,
    /// <summary>Transforms the completed color target before presentation.</summary>
    PostProcess
}

/// <summary>Provides backend-independent state shared by one pipeline execution.</summary>
public struct RenderPipelineContext
{
    private bool _submitted;

    /// <summary>Creates a context for one render view and queue.</summary>
    /// <param name="view">Destination render view.</param>
    /// <param name="queue">Prepared scene render queue.</param>
    internal RenderPipelineContext(
        RenderViewHandle view,
        RenderQueue queue)
    {
        View = view;
        Queue = queue;
    }

    /// <summary>Gets the destination render view.</summary>
    public RenderViewHandle View { get; }

    /// <summary>Gets the mutable queue prepared for this render.</summary>
    public RenderQueue Queue { get; }

    /// <summary>Gets whether a pass has submitted the scene queue.</summary>
    public readonly bool IsSubmitted => _submitted;

    /// <summary>Requests scene submission after every pass has finished configuring output.</summary>
    public void SubmitScene()
    {
        if (_submitted)
            throw new InvalidOperationException(
                "A render pipeline may submit its scene queue only once.");
        _submitted = true;
    }
}

/// <summary>Defines one extensible stage in a renderer-independent render pipeline.</summary>
public abstract class RenderPipelinePass
{
    /// <summary>Creates a pass with a stable semantic stage.</summary>
    /// <param name="stage">Role performed by the pass.</param>
    protected RenderPipelinePass(RenderPipelineStage stage)
    {
        Stage = stage;
    }

    /// <summary>Gets the semantic stage represented by this pass.</summary>
    public RenderPipelineStage Stage { get; }

    /// <summary>Prepares transient state immediately before execution.</summary>
    /// <param name="context">Current render context.</param>
    public virtual void Setup(ref RenderPipelineContext context)
    {
    }

    /// <summary>Executes this pipeline stage.</summary>
    /// <param name="context">Current render context.</param>
    public abstract void Execute(ref RenderPipelineContext context);

    /// <summary>Releases transient state immediately after execution.</summary>
    /// <param name="context">Current render context.</param>
    public virtual void Cleanup(ref RenderPipelineContext context)
    {
    }
}

/// <summary>Runs an immutable ordered set of scriptable render passes.</summary>
public class RenderPipeline
{
    private readonly RenderPipelinePass[] _passes;

    /// <summary>Creates a pipeline from passes in execution order.</summary>
    /// <param name="passes">Passes copied into immutable pipeline configuration.</param>
    public RenderPipeline(params RenderPipelinePass[] passes)
    {
        ArgumentNullException.ThrowIfNull(passes);
        if (passes.Length == 0)
            throw new ArgumentException("A render pipeline requires at least one pass.",
                nameof(passes));
        _passes = (RenderPipelinePass[])passes.Clone();
        for (var index = 0; index < _passes.Length; index++)
        {
            if (_passes[index] is null)
                throw new ArgumentException("Render pipeline passes cannot be null.",
                    nameof(passes));
        }
    }

    /// <summary>Gets an allocation-free view of configured passes.</summary>
    public ReadOnlySpan<RenderPipelinePass> Passes => _passes;

    /// <summary>Executes every configured pass and requires one scene submission.</summary>
    /// <param name="submitter">Backend submission boundary.</param>
    /// <param name="view">Destination render view.</param>
    /// <param name="queue">Prepared render queue.</param>
    public void Render(
        IRenderQueueSubmitter submitter,
        RenderViewHandle view,
        RenderQueue queue)
    {
        ArgumentNullException.ThrowIfNull(submitter);
        ArgumentNullException.ThrowIfNull(queue);
        if (!view.IsValid)
            throw new ArgumentException("A valid render view is required.", nameof(view));
        var context = new RenderPipelineContext(view, queue);
        for (var index = 0; index < _passes.Length; index++)
        {
            var pass = _passes[index];
            pass.Setup(ref context);
            try
            {
                pass.Execute(ref context);
            }
            finally
            {
                pass.Cleanup(ref context);
            }
        }
        if (!context.IsSubmitted)
            throw new InvalidOperationException(
                "The render pipeline completed without submitting the scene queue.");
        submitter.Submit(view, queue);
    }
}

/// <summary>Marks a planned pipeline stage whose first implementation has no GPU work.</summary>
public sealed class EmptyRenderPipelinePass : RenderPipelinePass
{
    /// <summary>Creates an empty extension point for one stage.</summary>
    /// <param name="stage">Future stage represented by this pass.</param>
    public EmptyRenderPipelinePass(RenderPipelineStage stage) : base(stage)
    {
    }

    /// <inheritdoc/>
    public override void Execute(ref RenderPipelineContext context)
    {
    }
}

/// <summary>Submits the prepared queue to the existing forward renderer.</summary>
public sealed class ForwardOpaqueRenderPass : RenderPipelinePass
{
    /// <summary>Creates the basic forward opaque stage.</summary>
    public ForwardOpaqueRenderPass() : base(RenderPipelineStage.Opaque)
    {
    }

    /// <inheritdoc/>
    public override void Execute(ref RenderPipelineContext context) => context.SubmitScene();
}

/// <summary>Provides the engine's initial configurable forward render pipeline.</summary>
public sealed class BasicForwardRenderPipeline : RenderPipeline
{
    /// <summary>Gets the shared stateless default pipeline.</summary>
    public static BasicForwardRenderPipeline Instance { get; } = new();

    /// <summary>Creates shadow, depth, opaque, transparent, and post-process stages.</summary>
    public BasicForwardRenderPipeline()
        : base(
            new EmptyRenderPipelinePass(RenderPipelineStage.Shadows),
            new EmptyRenderPipelinePass(RenderPipelineStage.DepthPrepass),
            new ForwardOpaqueRenderPass(),
            new EmptyRenderPipelinePass(RenderPipelineStage.Transparent),
            new EmptyRenderPipelinePass(RenderPipelineStage.PostProcess))
    {
    }
}
