# ADR 0003: Rendering policy lives above the RHI

- Status: accepted
- Date: 2026-09-03

## Context

The first wgpu provider initially created a bootstrap shader and graphics
pipeline, recorded a clear and triangle pass, submitted it, and presented the
surface. Those operations proved the RHI, but keeping them in the provider made
backend initialization depend on one visible effect and required every future
provider to duplicate renderer policy.

## Decision

`nico-render` is the backend-neutral rendering-policy layer above `nico-rhi`.
Its first `BootstrapRenderPipeline` owns the shader module, pipeline state,
surface-outcome handling, command recording, submission, and presentation. It
rebuilds its graphics pipeline if surface recovery selects a different target
format.

`nico-rhi` continues to define GPU resource and command contracts.
`nico-rhi-wgpu` implements those contracts and owns native device, queue, and
surface recovery only. It does not select shaders, create scene pipelines, or
issue scene draw calls. `nico-winit` remains the composition root because the
native event loop creates the window and surface; it instantiates both layers
and forwards resize and redraw events.

The bootstrap pipeline is concrete evidence for the boundary, not a complete
production renderer. Materials, visibility, render-world extraction, and a
render graph will be introduced only with real consumers.

## Consequences

Renderer behavior can be reused by another RHI provider without copying it into
that backend. A backend can also initialize without the bootstrap shader. The
render layer remains independent of Winit, wgpu, and authoritative runtime code.
