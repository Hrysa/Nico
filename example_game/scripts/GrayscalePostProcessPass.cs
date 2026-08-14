using Engine.Graphics;

namespace ExampleGame;

/// <summary>Applies an optional full-view grayscale presentation effect.</summary>
public sealed class GrayscalePostProcessPass : RenderPipelinePass
{
    /// <summary>Creates the example game's grayscale post-process stage.</summary>
    public GrayscalePostProcessPass() : base(RenderPipelineStage.PostProcess)
    {
    }

    /// <summary>Gets or sets whether the grayscale effect is active.</summary>
    public bool Enabled { get; set; }

    /// <inheritdoc/>
    public override void Execute(ref RenderPipelineContext context)
    {
        context.ApplyPostProcess(RenderOutputSettings.Create(Enabled ? 1f : 0f));
    }
}
