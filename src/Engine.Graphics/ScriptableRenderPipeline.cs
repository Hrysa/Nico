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
    /// <summary>Initializes camera attachments and per-view state.</summary>
    CameraSetup,
    /// <summary>Produces light-space visibility for shadowed lights.</summary>
    Shadows,
    /// <summary>Produces camera-space depth before color rendering.</summary>
    DepthPrepass,
    /// <summary>Renders the camera environment behind scene geometry.</summary>
    Skybox,
    /// <summary>Renders opaque surfaces with forward lighting.</summary>
    Opaque,
    /// <summary>Renders blended surfaces after opaque geometry.</summary>
    Transparent,
    /// <summary>Transforms the completed color target before presentation.</summary>
    PostProcess
}

/// <summary>Identifies backend work explicitly scheduled by a render-pipeline pass.</summary>
public enum RenderPipelineCommandKind
{
    /// <summary>Clears and initializes the active camera color and depth targets.</summary>
    ClearCamera,
    /// <summary>Renders directional-light shadow casters into the shadow atlas.</summary>
    DirectionalShadows,
    /// <summary>Renders point- and spot-light casters into the local-shadow atlas.</summary>
    LocalShadows,
    /// <summary>Draws a filtered subset of scene geometry with a material pass.</summary>
    DrawRenderers,
    /// <summary>Draws the submitted equirectangular camera environment.</summary>
    DrawSkybox,
    /// <summary>Transforms camera color into the view's presentation output.</summary>
    ApplyPostProcess
}

/// <summary>Identifies the material shader pass requested for filtered geometry.</summary>
public enum RenderMaterialPass
{
    /// <summary>Writes camera depth without evaluating surface color.</summary>
    DepthOnly,
    /// <summary>Evaluates the built-in forward-lit surface shader.</summary>
    Forward
}

/// <summary>Filters scene draws by authored surface behavior.</summary>
[Flags]
public enum RenderQueueFilter
{
    /// <summary>Selects no scene draws.</summary>
    None = 0,
    /// <summary>Selects solid surfaces.</summary>
    Opaque = 1 << 0,
    /// <summary>Selects cutout surfaces that still write depth.</summary>
    AlphaTest = 1 << 1,
    /// <summary>Selects blended surfaces.</summary>
    Transparent = 1 << 2,
    /// <summary>Selects non-world overlay geometry.</summary>
    Overlay = 1 << 3
}

/// <summary>Names render resources read or written by SRP commands.</summary>
[Flags]
public enum RenderPipelineResource
{
    /// <summary>No render resource.</summary>
    None = 0,
    /// <summary>The active camera color target.</summary>
    CameraColor = 1 << 0,
    /// <summary>The active camera depth target.</summary>
    CameraDepth = 1 << 1,
    /// <summary>The active view's directional-shadow atlas.</summary>
    DirectionalShadowAtlas = 1 << 2,
    /// <summary>The active view's point- and spot-light shadow atlas.</summary>
    LocalShadowAtlas = 1 << 3,
    /// <summary>The final color consumed while presenting the rendered view.</summary>
    PresentedColor = 1 << 4
}

/// <summary>Describes one renderer-independent unit of GPU work scheduled by the SRP.</summary>
/// <param name="Stage">Semantic pipeline stage that authored the command.</param>
/// <param name="Kind">Backend operation to execute.</param>
/// <param name="QueueFilter">Scene surface classes selected by a draw command.</param>
/// <param name="MaterialPass">Material shader pass selected by a draw command.</param>
/// <param name="Reads">Render resources read by the command.</param>
/// <param name="Writes">Render resources written by the command.</param>
/// <param name="DirectionalShadows">Settings used by a directional-shadow operation.</param>
/// <param name="LocalShadows">Settings used by a local-shadow operation.</param>
/// <param name="Output">Presentation transform used by a post-process operation.</param>
public readonly record struct RenderPipelineCommand(
    RenderPipelineStage Stage,
    RenderPipelineCommandKind Kind,
    RenderQueueFilter QueueFilter = RenderQueueFilter.None,
    RenderMaterialPass MaterialPass = default,
    RenderPipelineResource Reads = RenderPipelineResource.None,
    RenderPipelineResource Writes = RenderPipelineResource.None,
    DirectionalShadowSettings DirectionalShadows = default,
    LocalShadowSettings LocalShadows = default,
    RenderOutputSettings Output = default);

/// <summary>Describes one resource hazard resolved between sequential SRP commands.</summary>
/// <param name="BeforeCommandIndex">Command index that requires the dependency.</param>
/// <param name="Resources">Resources whose prior access must complete.</param>
/// <param name="SourceStage">Stage that last accessed the resources.</param>
/// <param name="DestinationStage">Stage beginning the next access.</param>
public readonly record struct RenderPipelineBarrier(
    int BeforeCommandIndex,
    RenderPipelineResource Resources,
    RenderPipelineStage SourceStage,
    RenderPipelineStage DestinationStage);

/// <summary>Configures one directional-light shadow-map pass.</summary>
public readonly record struct DirectionalShadowSettings
{
    /// <summary>Creates validated directional shadow settings.</summary>
    /// <param name="maxDistance">World-space shadow coverage around the camera.</param>
    /// <param name="depthBias">Constant raster depth bias.</param>
    /// <param name="slopeBias">Slope-scaled raster depth bias.</param>
    /// <param name="strength">Direct-light shadow attenuation from zero to one.</param>
    /// <param name="cascadeCount">Number of frustum-fitted cascades.</param>
    /// <param name="splitLambda">Blend between uniform and logarithmic splits.</param>
    /// <param name="cascadeBlend">Fraction of each cascade blended into the next.</param>
    /// <param name="normalBias">Receiver normal offset measured in shadow texels.</param>
    private DirectionalShadowSettings(
        float maxDistance,
        float depthBias,
        float slopeBias,
        float strength,
        int cascadeCount,
        float splitLambda,
        float cascadeBlend,
        float normalBias)
    {
        MaxDistance = maxDistance;
        DepthBias = depthBias;
        SlopeBias = slopeBias;
        Strength = strength;
        CascadeCount = cascadeCount;
        SplitLambda = splitLambda;
        CascadeBlend = cascadeBlend;
        NormalBias = normalBias;
    }

    /// <summary>Gets whether this configuration requests shadow rendering.</summary>
    public bool IsEnabled => Strength > 0f;

    /// <summary>Gets world-space shadow coverage around the camera.</summary>
    public float MaxDistance { get; }

    /// <summary>Gets constant raster depth bias.</summary>
    public float DepthBias { get; }

    /// <summary>Gets slope-scaled raster depth bias.</summary>
    public float SlopeBias { get; }

    /// <summary>Gets direct-light shadow attenuation from zero to one.</summary>
    public float Strength { get; }

    /// <summary>Gets the number of frustum-fitted cascades.</summary>
    public int CascadeCount { get; }

    /// <summary>Gets the blend between uniform zero and logarithmic one split placement.</summary>
    public float SplitLambda { get; }

    /// <summary>Gets the fractional transition band between adjacent cascades.</summary>
    public float CascadeBlend { get; }

    /// <summary>Gets receiver normal offset measured in cascade texels.</summary>
    public float NormalBias { get; }

    /// <summary>Gets disabled shadow rendering.</summary>
    public static DirectionalShadowSettings None { get; } = default;

    /// <summary>Gets balanced defaults for the built-in forward pipeline.</summary>
    public static DirectionalShadowSettings Default { get; } =
        new(50f, 1.25f, 1.75f, 1f, 3, 0.7f, 0.1f, 1.5f);

    /// <summary>Creates validated directional shadow settings.</summary>
    /// <param name="maxDistance">World-space shadow coverage around the camera.</param>
    /// <param name="depthBias">Constant raster depth bias.</param>
    /// <param name="slopeBias">Slope-scaled raster depth bias.</param>
    /// <param name="strength">Direct-light shadow attenuation from zero to one.</param>
    /// <param name="cascadeCount">Cascade count from one through four.</param>
    /// <param name="splitLambda">Blend between uniform zero and logarithmic one splits.</param>
    /// <param name="cascadeBlend">Fractional transition width from zero through 0.3.</param>
    /// <param name="normalBias">Receiver normal offset measured in cascade texels.</param>
    /// <returns>Validated shadow settings.</returns>
    public static DirectionalShadowSettings Create(
        float maxDistance,
        float depthBias,
        float slopeBias,
        float strength,
        int cascadeCount = 3,
        float splitLambda = 0.7f,
        float cascadeBlend = 0.1f,
        float normalBias = 1.5f)
    {
        if (!float.IsFinite(maxDistance) || maxDistance <= 0f)
            throw new ArgumentOutOfRangeException(nameof(maxDistance));
        if (!float.IsFinite(depthBias) || depthBias < 0f)
            throw new ArgumentOutOfRangeException(nameof(depthBias));
        if (!float.IsFinite(slopeBias) || slopeBias < 0f)
            throw new ArgumentOutOfRangeException(nameof(slopeBias));
        if (!float.IsFinite(strength) || strength < 0f || strength > 1f)
            throw new ArgumentOutOfRangeException(nameof(strength));
        if (cascadeCount is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(cascadeCount));
        if (!float.IsFinite(splitLambda) || splitLambda < 0f || splitLambda > 1f)
            throw new ArgumentOutOfRangeException(nameof(splitLambda));
        if (!float.IsFinite(cascadeBlend) || cascadeBlend < 0f || cascadeBlend > 0.3f)
            throw new ArgumentOutOfRangeException(nameof(cascadeBlend));
        if (!float.IsFinite(normalBias) || normalBias < 0f || normalBias > 8f)
            throw new ArgumentOutOfRangeException(nameof(normalBias));
        return new DirectionalShadowSettings(maxDistance, depthBias, slopeBias, strength,
            cascadeCount, splitLambda, cascadeBlend, normalBias);
    }
}

/// <summary>Configures point- and spot-light shadow-map rendering.</summary>
public readonly record struct LocalShadowSettings
{
    /// <summary>Creates validated local-shadow settings.</summary>
    /// <param name="depthBias">Constant raster depth bias.</param>
    /// <param name="slopeBias">Slope-scaled raster depth bias.</param>
    /// <param name="normalBias">Receiver normal offset relative to light range.</param>
    /// <param name="strength">Direct-light shadow attenuation from zero to one.</param>
    private LocalShadowSettings(
        float depthBias,
        float slopeBias,
        float normalBias,
        float strength)
    {
        DepthBias = depthBias;
        SlopeBias = slopeBias;
        NormalBias = normalBias;
        Strength = strength;
    }

    /// <summary>Gets constant raster depth bias.</summary>
    public float DepthBias { get; }

    /// <summary>Gets slope-scaled raster depth bias.</summary>
    public float SlopeBias { get; }

    /// <summary>Gets receiver normal offset relative to light range.</summary>
    public float NormalBias { get; }

    /// <summary>Gets the authored shadow strength.</summary>
    public float Strength { get; }

    /// <summary>Gets whether these settings request local-shadow rendering.</summary>
    public bool IsEnabled => Strength > 0f;

    /// <summary>Gets balanced built-in local-shadow settings.</summary>
    public static LocalShadowSettings Default { get; } = Create(1f, 2f, 0.002f, 1f);

    /// <summary>Creates validated local-shadow settings.</summary>
    /// <param name="depthBias">Constant raster depth bias.</param>
    /// <param name="slopeBias">Slope-scaled raster depth bias.</param>
    /// <param name="normalBias">Receiver normal offset relative to light range.</param>
    /// <param name="strength">Direct-light shadow attenuation from zero to one.</param>
    /// <returns>Validated settings.</returns>
    public static LocalShadowSettings Create(
        float depthBias,
        float slopeBias,
        float normalBias,
        float strength)
    {
        if (!float.IsFinite(depthBias) || depthBias < 0f || depthBias > 16f)
            throw new ArgumentOutOfRangeException(nameof(depthBias));
        if (!float.IsFinite(slopeBias) || slopeBias < 0f || slopeBias > 16f)
            throw new ArgumentOutOfRangeException(nameof(slopeBias));
        if (!float.IsFinite(normalBias) || normalBias < 0f || normalBias > 0.1f)
            throw new ArgumentOutOfRangeException(nameof(normalBias));
        if (!float.IsFinite(strength) || strength < 0f || strength > 1f)
            throw new ArgumentOutOfRangeException(nameof(strength));
        return new LocalShadowSettings(depthBias, slopeBias, normalBias, strength);
    }
}

/// <summary>Provides backend-independent state shared by one pipeline execution.</summary>
public struct RenderPipelineContext
{
    private RenderPipelineStage _activeStage;

    /// <summary>Creates a context for one render view and queue.</summary>
    /// <param name="view">Destination render view.</param>
    /// <param name="queue">Prepared scene render queue.</param>
    internal RenderPipelineContext(
        RenderViewHandle view,
        RenderQueue queue)
    {
        View = view;
        Queue = queue;
        _activeStage = default;
    }

    /// <summary>Gets the destination render view.</summary>
    public RenderViewHandle View { get; }

    /// <summary>Gets the mutable queue prepared for this render.</summary>
    public RenderQueue Queue { get; }

    /// <summary>Schedules explicit initialization of camera color and depth attachments.</summary>
    public void ClearCamera()
    {
        Queue.AddPipelineCommand(new RenderPipelineCommand(
            _activeStage,
            RenderPipelineCommandKind.ClearCamera,
            Writes: RenderPipelineResource.CameraColor |
                RenderPipelineResource.CameraDepth));
    }

    /// <summary>Schedules directional shadow rendering as explicit backend work.</summary>
    /// <param name="settings">Validated directional-shadow configuration.</param>
    public void RenderDirectionalShadows(DirectionalShadowSettings settings)
    {
        if (!settings.IsEnabled)
            throw new ArgumentException("Directional shadow rendering requires enabled settings.",
                nameof(settings));
        Queue.AddPipelineCommand(new RenderPipelineCommand(
            _activeStage,
            RenderPipelineCommandKind.DirectionalShadows,
            Writes: RenderPipelineResource.DirectionalShadowAtlas,
            DirectionalShadows: settings));
    }

    /// <summary>Schedules point- and spot-light shadow rendering as explicit backend work.</summary>
    /// <param name="settings">Validated local-shadow configuration.</param>
    public void RenderLocalShadows(LocalShadowSettings settings)
    {
        if (!settings.IsEnabled)
            throw new ArgumentException("Local shadow rendering requires enabled settings.",
                nameof(settings));
        Queue.AddPipelineCommand(new RenderPipelineCommand(
            _activeStage,
            RenderPipelineCommandKind.LocalShadows,
            Writes: RenderPipelineResource.LocalShadowAtlas,
            LocalShadows: settings));
    }

    /// <summary>Schedules a filtered material pass as explicit backend work.</summary>
    /// <param name="filter">Surface classes included by the draw.</param>
    /// <param name="materialPass">Material shader pass used for matching geometry.</param>
    /// <param name="reads">Render resources read by the pass.</param>
    /// <param name="writes">Render resources written by the pass.</param>
    public void DrawRenderers(
        RenderQueueFilter filter,
        RenderMaterialPass materialPass,
        RenderPipelineResource reads,
        RenderPipelineResource writes)
    {
        if (filter == RenderQueueFilter.None)
            throw new ArgumentException("A renderer draw requires at least one queue class.",
                nameof(filter));
        Queue.AddPipelineCommand(new RenderPipelineCommand(
            _activeStage, RenderPipelineCommandKind.DrawRenderers,
            filter, materialPass, reads, writes));
    }

    /// <summary>Schedules the submitted environment behind camera-depth geometry.</summary>
    public void DrawSkybox()
    {
        if (!Queue.Skybox.IsEnabled)
            return;
        Queue.AddPipelineCommand(new RenderPipelineCommand(
            _activeStage,
            RenderPipelineCommandKind.DrawSkybox,
            Reads: RenderPipelineResource.CameraDepth,
            Writes: RenderPipelineResource.CameraColor));
    }

    /// <summary>Schedules the final camera-color transform for presentation.</summary>
    /// <param name="settings">Validated output-effect settings.</param>
    public void ApplyPostProcess(RenderOutputSettings settings)
    {
        Queue.Output = settings;
        Queue.AddPipelineCommand(new RenderPipelineCommand(
            _activeStage,
            RenderPipelineCommandKind.ApplyPostProcess,
            Reads: RenderPipelineResource.CameraColor,
            Writes: RenderPipelineResource.PresentedColor,
            Output: settings));
    }

    /// <summary>Sets the semantic stage assigned to work authored by the next pass.</summary>
    /// <param name="stage">Active pass stage.</param>
    internal void BeginPass(RenderPipelineStage stage) => _activeStage = stage;
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
            if (index > 0 && _passes[index].Stage < _passes[index - 1].Stage)
            {
                throw new ArgumentException(
                    "Render pipeline stages must be configured in semantic execution order.",
                    nameof(passes));
            }
        }
    }

    /// <summary>Gets an allocation-free view of configured passes.</summary>
    public ReadOnlySpan<RenderPipelinePass> Passes => _passes;

    /// <summary>Executes every configured pass and submits its recorded GPU work.</summary>
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
        queue.ClearPipelineCommands();
        var context = new RenderPipelineContext(view, queue);
        for (var index = 0; index < _passes.Length; index++)
        {
            var pass = _passes[index];
            context.BeginPass(pass.Stage);
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
        if (!queue.HasDrawPipelineCommand)
            throw new InvalidOperationException(
                "The render pipeline completed without scheduling scene geometry.");
        queue.CompilePipelineDependencies();
        queue.SortTransparentBackToFront();
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

/// <summary>Explicitly initializes the active camera attachments.</summary>
public sealed class CameraClearRenderPass : RenderPipelinePass
{
    /// <summary>Creates the built-in camera initialization pass.</summary>
    public CameraClearRenderPass() : base(RenderPipelineStage.CameraSetup)
    {
    }

    /// <inheritdoc/>
    public override void Execute(ref RenderPipelineContext context) => context.ClearCamera();
}

/// <summary>Schedules opaque geometry for the built-in forward renderer.</summary>
public sealed class ForwardOpaqueRenderPass : RenderPipelinePass
{
    /// <summary>Creates the basic forward opaque stage.</summary>
    public ForwardOpaqueRenderPass() : base(RenderPipelineStage.Opaque)
    {
    }

    /// <inheritdoc/>
    public override void Execute(ref RenderPipelineContext context) => context.DrawRenderers(
        RenderQueueFilter.Opaque | RenderQueueFilter.AlphaTest,
        RenderMaterialPass.Forward,
        RenderPipelineResource.CameraDepth |
            RenderPipelineResource.DirectionalShadowAtlas |
            RenderPipelineResource.LocalShadowAtlas,
        RenderPipelineResource.CameraColor | RenderPipelineResource.CameraDepth);
}

/// <summary>Schedules solid and cutout geometry into the active camera depth target.</summary>
public sealed class DepthPrepassRenderPass : RenderPipelinePass
{
    /// <summary>Creates the built-in camera depth prepass.</summary>
    public DepthPrepassRenderPass() : base(RenderPipelineStage.DepthPrepass)
    {
    }

    /// <inheritdoc/>
    public override void Execute(ref RenderPipelineContext context) => context.DrawRenderers(
        RenderQueueFilter.Opaque,
        RenderMaterialPass.DepthOnly,
        RenderPipelineResource.None,
        RenderPipelineResource.CameraDepth);
}

/// <summary>Schedules the active equirectangular environment behind scene geometry.</summary>
public sealed class SkyboxRenderPass : RenderPipelinePass
{
    /// <summary>Creates the built-in skybox stage.</summary>
    public SkyboxRenderPass() : base(RenderPipelineStage.Skybox)
    {
    }

    /// <inheritdoc/>
    public override void Execute(ref RenderPipelineContext context) => context.DrawSkybox();
}

/// <summary>Schedules blended geometry after solid forward rendering.</summary>
public sealed class ForwardTransparentRenderPass : RenderPipelinePass
{
    /// <summary>Creates the built-in forward transparent stage.</summary>
    public ForwardTransparentRenderPass() : base(RenderPipelineStage.Transparent)
    {
    }

    /// <inheritdoc/>
    public override void Execute(ref RenderPipelineContext context) => context.DrawRenderers(
        RenderQueueFilter.Transparent,
        RenderMaterialPass.Forward,
        RenderPipelineResource.CameraColor | RenderPipelineResource.CameraDepth |
            RenderPipelineResource.DirectionalShadowAtlas |
            RenderPipelineResource.LocalShadowAtlas,
        RenderPipelineResource.CameraColor);
}

/// <summary>Requests a directional shadow map before opaque forward rendering.</summary>
public sealed class DirectionalShadowRenderPass : RenderPipelinePass
{
    private readonly DirectionalShadowSettings _settings;

    /// <summary>Creates a shadow pass using the built-in settings.</summary>
    public DirectionalShadowRenderPass() : this(DirectionalShadowSettings.Default)
    {
    }

    /// <summary>Creates a shadow pass using explicit settings.</summary>
    /// <param name="settings">Validated shadow-map settings.</param>
    public DirectionalShadowRenderPass(DirectionalShadowSettings settings)
        : base(RenderPipelineStage.Shadows)
    {
        if (!settings.IsEnabled)
            throw new ArgumentException("A shadow pass requires enabled settings.", nameof(settings));
        _settings = settings;
    }

    /// <inheritdoc/>
    public override void Execute(ref RenderPipelineContext context) =>
        context.RenderDirectionalShadows(_settings);
}

/// <summary>Requests local-light shadow maps before forward rendering.</summary>
public sealed class LocalShadowRenderPass : RenderPipelinePass
{
    private readonly LocalShadowSettings _settings;

    /// <summary>Creates a local-shadow pass using the built-in settings.</summary>
    public LocalShadowRenderPass() : this(LocalShadowSettings.Default)
    {
    }

    /// <summary>Creates a local-shadow pass using explicit settings.</summary>
    /// <param name="settings">Validated local-shadow settings.</param>
    public LocalShadowRenderPass(LocalShadowSettings settings)
        : base(RenderPipelineStage.Shadows)
    {
        if (!settings.IsEnabled)
            throw new ArgumentException("A local-shadow pass requires enabled settings.",
                nameof(settings));
        _settings = settings;
    }

    /// <inheritdoc/>
    public override void Execute(ref RenderPipelineContext context) =>
        context.RenderLocalShadows(_settings);
}

/// <summary>Transforms completed camera color into presentation-ready output.</summary>
public sealed class OutputPostProcessRenderPass : RenderPipelinePass
{
    private readonly RenderOutputSettings _settings;

    /// <summary>Creates a presentation pass with no optional output effect.</summary>
    public OutputPostProcessRenderPass() : this(RenderOutputSettings.None)
    {
    }

    /// <summary>Creates a presentation pass with explicit output settings.</summary>
    /// <param name="settings">Validated output effects.</param>
    public OutputPostProcessRenderPass(RenderOutputSettings settings)
        : base(RenderPipelineStage.PostProcess)
    {
        _settings = settings;
    }

    /// <inheritdoc/>
    public override void Execute(ref RenderPipelineContext context) =>
        context.ApplyPostProcess(_settings);
}

/// <summary>Provides the engine's initial configurable forward render pipeline.</summary>
public sealed class BasicForwardRenderPipeline : RenderPipeline
{
    /// <summary>Gets the shared stateless default pipeline.</summary>
    public static BasicForwardRenderPipeline Instance { get; } = new();

    /// <summary>Creates shadow, depth, opaque, transparent, and post-process stages.</summary>
    public BasicForwardRenderPipeline()
        : base(
            new CameraClearRenderPass(),
            new DirectionalShadowRenderPass(),
            new LocalShadowRenderPass(),
            new DepthPrepassRenderPass(),
            new SkyboxRenderPass(),
            new ForwardOpaqueRenderPass(),
            new ForwardTransparentRenderPass(),
            new OutputPostProcessRenderPass())
    {
    }
}
