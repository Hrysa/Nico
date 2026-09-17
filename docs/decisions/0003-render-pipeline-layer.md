# ADR 0003: Rendering policy lives above the RHI

- Status: accepted
- Date: 2026-09-03
- Updated: 2026-09-10

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
issue scene draw calls. The game client selects the native host;
`nico-winit` composes graphics because its event loop creates the window and
surface. It instantiates the provider and renderer and forwards resize and redraw
events.

The bootstrap pipeline is concrete evidence for the boundary, not a complete
production renderer. Materials, visibility, render-world extraction, and a
render graph will be introduced only with real consumers.

## Consequences

Renderer behavior can be reused by another RHI provider without copying it into
that backend. A backend can also initialize without the bootstrap shader. The
render layer remains independent of Winit, wgpu, and authoritative runtime code.

## Implementation status (2026-09-10)

The bootstrap triangle does not consume authoritative entity positions.
`nico-presentation` currently provides a null world-facing lifecycle alongside
the host-driven GPU path. Connecting visible geometry to game state is future
work, not a capability of the bootstrap pipeline.

The [measurement requirements](../architecture.md#measurement-and-profiling-requirements)
apply separately to renderer policy and provider work. Profiling implementation
is deferred to future experimental XRay integration. When measured, shader/pipeline
creation and frame recording/submission costs must not be labeled as GPU execution
time. Planned AI operations expose rendering diagnostics through host/tooling
boundaries; the renderer does not own an MCP transport.

## Implementation update (2026-09-14)

`QuadRenderPipeline` now draws world sprites and HUD quads from immutable
`nico-presentation::Scene2d` snapshots. The presentation crate separates its optional
runtime lifecycle from drawing contracts; the renderer depends only on those contracts,
asset CPU data, and RHI. The minimal client extracts shared positions and pins loaded
textures in its snapshot. Uploads, texture bindings, straight-alpha blending, and draw
ordering remain renderer policy; providers retain resource uses through submitted work.
The bootstrap path remains available. Current scope and validation belong in
[roadmap phase 4](../roadmap.md#4-display-the-game-world).

## Implementation update: 2026-09-14, 3D consumer

`MeshRenderPipeline` now consumes immutable `Scene3d` snapshots using indexed geometry,
perspective uniforms, depth testing, and unlit alpha-cutoff textures. It owns a shared
`QuadRenderPipeline` for texture uploads and the final HUD pass. Provider contracts
remain backend-neutral; runtime ownership stays outside the renderer. The bounded
asset subset is documented in the [mesh design](../plans/2026-09-14-mesh-assets.md).


## 2026-09-17 implementation update: separate scene and UI passes

`Scene2d` now owns only camera-dependent world quads; `UiScene` independently owns
screen-space HUD/UI quads. Games publish both through the existing immutable
presentation boundary. The shared canvas records separate Scene2D and UI passes,
sharing its texture cache and per-frame vertex buffer. PBR opaque/masked and
transparent scene passes precede them. The current ownership and limitations live
in the [architecture](../architecture.md#presentation-and-graphics).
