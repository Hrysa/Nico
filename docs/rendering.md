# Scriptable Rendering

## Pipeline contract

`RenderPipeline` is backend-independent and contains an immutable ordered array of `RenderPipelinePass` objects. Every frame receives a `RenderPipelineContext` containing the target view and prepared `RenderQueue`.

A pipeline must submit the scene exactly once. Normally it keeps `ForwardOpaqueRenderPass`; configuration-only passes run before or after it. `BasicForwardRenderPipeline.Instance` contains the default stage order:

1. Shadows (empty extension point)
2. DepthPrepass (empty extension point)
3. Opaque (`ForwardOpaqueRenderPass`)
4. Transparent (empty extension point)
5. PostProcess (empty extension point)

The stage value describes semantic intent; execution order is the order supplied to the pipeline constructor.

## Game-project pass

A gameplay assembly can define a pass without referencing Silk.NET:

```csharp
using Engine.Graphics;

public sealed class GrayscalePass : RenderPipelinePass
{
    public GrayscalePass() : base(RenderPipelineStage.PostProcess)
    {
    }

    public bool Enabled { get; set; }

    public override void Execute(ref RenderPipelineContext context)
    {
        context.Queue.Output = RenderOutputSettings.Create(Enabled ? 1f : 0f);
    }
}
```

Install it through `Scene.Rendering` from a script. Preserve the existing passes so the scene still submits:

```csharp
private RenderPipeline? _previous;
private RenderPipeline? _installed;

public override void OnReady()
{
    _previous = Scene.Rendering.RenderPipeline;
    var existing = _previous.Passes;
    var passes = new RenderPipelinePass[existing.Length + 1];
    existing.CopyTo(passes);
    passes[^1] = new GrayscalePass { Enabled = true };
    _installed = new RenderPipeline(passes);
    Scene.Rendering.RenderPipeline = _installed;
}

public override void OnDestroy()
{
    if (_previous is not null &&
        ReferenceEquals(Scene.Rendering.RenderPipeline, _installed))
        Scene.Rendering.RenderPipeline = _previous;
}
```

Restoring only when the installed instance is still active avoids overwriting a pipeline installed later by another system.

## Current limitation

The public pass layer currently configures one forward scene submission and presentation output. Empty shadow, depth, transparent, and post-process stages do not expose command buffers, render targets, temporary textures, or backend draw APIs. A pass that needs new GPU work requires corresponding backend-independent renderer contracts and a Silk implementation; game code must not call Vulkan directly.
