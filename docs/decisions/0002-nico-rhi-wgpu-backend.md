# ADR 0002: Nico owns its RHI with a wgpu backend

- Status: accepted
- Date: 2026-09-03
- Updated: 2026-09-10

## Context

The Winit client previously observed window size but used a no-device
presentation boundary. It never painted the native client area, so newly exposed
pixels after window growth had undefined platform-specific contents. The first
visible frame now needs a real graphics surface without exposing a selected GPU
library to presentation consumers or authoritative runtime code.

Nico also needs room to evaluate lower-level native backends later. Shader
development uses Slang with offline-generated backend artifacts; the safe first
backend accepts WGSL.

## Decision

Nico owns a small backend-neutral `nico-rhi` crate. Its first implementation is
the concrete `nico-rhi-wgpu` provider. Backend types remain private to that
provider and do not enter `nico-presentation`, `nico-runtime`, shared gameplay,
or the public native-client configuration.

The core RHI contract covers:

- adapter identity, API selection, and portable limits;
- buffers, textures, texture views, samplers, and queue uploads;
- shader modules for offline artifacts;
- binding layouts, bind groups, pipeline layouts, and graphics/compute pipelines;
- transfer commands and render/compute passes;
- surface acquisition, resize, submit, and present;
- explicit zero-size, timeout, and occlusion acquisition outcomes; and
- stable unrecoverable error categories.

The traits use associated resource types. Concrete providers therefore retain
static dispatch and native handle ownership rather than routing commands through
a central dynamic handle map. Optional features are not exposed until their
complete lifecycle exists; CPU buffer mapping, binding arrays, immediate data,
and a render graph remain deferred.

`nico-rhi-wgpu` owns the instance, adapter, device, queue, surface, and surface
configuration. It reconfigures non-zero resizes, skips zero-sized surfaces,
retries outdated surfaces, recreates lost surfaces, and reconfigures after a
suboptimal frame. Device-loss and uncaptured backend failures become stable RHI
errors instead of implicit panics. Winit supplies owned display and window
handles and continues to own the event loop.

The authoritative bootstrap Slang source and generated artifacts live under the
independent `assets/presentation/shaders/` root. The standalone `nico-shaderc`
executable compiles them outside Cargo's crate build graph. `nico-rhi` owns
artifact and entry-point contracts, `nico-render` selects and uses them, and
`nico-rhi-wgpu` translates runtime-loaded WGSL bytes. Shader edits therefore do
not rebuild or relink Rust crates. Future native providers may consume SPIR-V,
DXIL, or Metal libraries. Reflection and asset-backed shader packaging remain
deferred. Do not add a render graph or higher-level material API until visible
consumers establish their requirements.

## Consequences

The native client paints every acquired surface image and window growth no
longer depends on compositor background behavior. The runtime and server remain
headless. The additional RHI boundary lets Nico measure or replace wgpu without
changing game-facing presentation contracts, but it does not remove wgpu's own
validation, state-tracking, or shader-translation costs.

The Winit host currently waits on asynchronous device initialization using
`pollster::block_on` during its resume callback. It also reads the shader and
creates the bootstrap pipeline before requesting the first redraw. The RHI
does not select an async executor.

## Implementation status (2026-09-10)

Offline Slang compilation produces WGSL, while backend shader/pipeline
preparation still occurs at runtime. The reported startup delay has not been
profiled. Profiling is deferred to future experimental XRay integration; instance,
adapter/device, surface, shader, pipeline, and first-presentation costs remain
candidates to measure before choosing an optimization.

The runtime file read is a bootstrap path, not the planned service-backed asset
loader. Reflection and asset-backed shader packaging remain deferred. CPU call
durations must be distinguished from GPU execution measurements. See
[the measurement requirements](../architecture.md#measurement-and-profiling-requirements).

Surface behavior has automated coverage and a recorded bounded Windows GPU
smoke run. Interactive Windows/macOS validation remains in
[TODO](../../TODO.md#outstanding-host-validation).
