# Nico architecture

Nico is code-first: Rust plugins construct game state and register behavior.
This document defines ownership and dependency rules. [The roadmap](roadmap.md)
records implementation status, and [TODO](../TODO.md) tracks current work.

## Dependency direction

Arrows represent Rust dependencies toward contracts and the headless core:

```text
game client -> nico-winit -> nico-input
                        -> nico-presentation -> nico-runtime -> nico-ecs
                        -> nico-runtime
                        -> nico-render -> nico-rhi
                        -> nico-rhi-wgpu -> nico-rhi
                                        -> wgpu
game server -> shared gameplay -> nico-runtime
```

These are the core paths, not every manifest edge. Native executables also use
`nico-launch` for CLI and diagnostics. The game client composes shared gameplay,
and `nico-winit` composes the renderer with its concrete RHI provider.
`nico-rhi` has no dependency on wgpu or its provider. Runtime has no dependency
on input, presentation, native providers, or launch policy. `nico-ecs` does not
depend on runtime.

| Owner | Responsibility |
| --- | --- |
| `nico-ecs` | World, resources, entities/components, and hecs query vocabulary |
| `nico-runtime` | Lifecycle, schedules, time, events, and service completion publication |
| `nico-input` | Provider-neutral physical device state |
| `nico-presentation` | Immutable world-facing presentation lifecycle |
| `nico-render` | Shader/pipeline selection, frame recording, submission, and presentation |
| `nico-rhi` | Backend-neutral GPU resource, command, and surface contracts |
| `nico-rhi-wgpu` | Native GPU resources and surface recovery using wgpu |
| `nico-winit` | Native window lifecycle, input adaptation, and client-session coordination |
| `nico-assets` | Stable asset and typed handle identity |
| `nico-launch` | Native CLI parsing and diagnostics initialization |
| `apps/nico-shaderc` | Offline shader compilation outside the Rust build graph |

## Runtime and ECS

`nico_runtime::ecs` is the canonical engine-facing namespace for `World`,
`Entity`, and hecs query/command types. The ECS provider owns entity/component
storage; runtime owns application lifecycle and execution policy. ECS is for
simulation state, not universal storage for windowing, GPU objects, or external
services.

`Plugin::build(&self, &mut AppBuilder)` is the composition boundary. The initial
stages are `Startup`, `FixedUpdate`, `Update`, and `Shutdown`. Hosts drive
`App::start`, `tick`, and `shutdown`; runtime does not own a permanent loop.
The dedicated server supplies a paced fixed-rate `AppRunner`. Tests can drive
bounded frames and deterministic time.

Systems query the authoritative world and record structural changes through
`SystemContext::commands`. Successful structural commands flush before the
next system; commands from a failed system are discarded. Direct mutations of
existing components and resources are not rolled back on system failure.

Typed events are bounded broadcast streams with independent `EventReader<T>`
cursors. Successful system event writes become visible to the next scheduled
system; failed-system writes are discarded. Lagging readers receive a missed
count when retained history overflows. Hosts may inject events before a tick.
Persistent domain state belongs in resources/components or external storage,
rather than relying on transient event retention.

Portable services use domain-typed owned requests/completions and bounded queues.
Backends never receive `World`. `AppBuilder::add_service` registers a publication
system in `Update`; its schedule position determines when later consumers see
`ServiceCompletion<T>` events. Cancellation, overload, backend failures, stale
generational entity targets, and shutdown have explicit behavior. Runtime closes
registered channels during shutdown. The boundary selects no async executor;
tests can drive backend endpoints manually.

## Native host and input

`nico-winit` is the concrete native provider. It owns Winit types and the
`ApplicationHandler` callbacks, creates the window on resume, adapts native
input, and drives the runtime and presentation lifecycle. Suspension pauses
ticks and resets the frame-time origin; close and failure paths converge on
session shutdown. Desktop minimization must be validated independently of
Winit suspension.

`nico-input` is a headless leaf capability with normalized device identity,
connection state, buttons, axes, persistent vectors, and frame-local motion.
Winit supplies keyboard, pointer, wheel, raw motion, and touch events. Focus loss
releases controls. A native gamepad provider is deferred.

The game client maps `InputState` to semantic commands such as `PlayerCommand`.
Shared gameplay and the server do not depend on physical-input types. The
current client configuration selects title, bootstrap shader path, and smoke
policy. A provider-neutral host contract requires evidence from a second provider.

See [ADR 0001](decisions/0001-native-client-event-loop.md) for event-loop ownership.

## Presentation and graphics

Presentation may read the authoritative world immutably. It does not own or
mutate gameplay state. Direct queries are allowed; extraction and caching require
a demonstrated need.

The current `nico-presentation::Presentation` is a null lifecycle implementation:
it accepts a world/frame and counts frames. The Winit host separately drives
`nico-render::BootstrapRenderPipeline`. The triangle uses fixed geometry and
is not connected to game positions.

`nico-rhi` defines associated provider resource types for capabilities, buffers,
textures, bindings, shaders, pipelines, commands, uploads, passes, and surfaces.
Concrete providers retain native resource ownership without leaking backend
types into unrelated APIs.

`nico-rhi-wgpu` owns the instance, adapter, device, queue, and surface. It handles
resize and recoverable surface outcomes, including zero-size, timeout, occlusion,
outdated/lost surfaces, and suboptimal frames. Unrecoverable failures use RHI
error categories.

`nico-render` selects shaders and pipelines, records the bootstrap pass, submits
commands, and presents. It rebuilds the pipeline if the surface format changes.
Native hosts compose and drive these layers but do not define scene draw calls.

The smoke frame limit counts client-session frames, even when GPU acquisition
skips presentation. It is bounded lifecycle coverage, not a successful-GPU-frame
counter.

See [ADR 0002](decisions/0002-nico-rhi-wgpu-backend.md) for the GPU boundary and
[ADR 0003](decisions/0003-render-pipeline-layer.md) for rendering policy.

## Assets and game construction

Games live under `games/<game>/` with `shared`, `client`, and `server` packages.
Shared Rust plugins own authoritative setup and behavior. The client adds local
input and presentation; the server remains headless.

Each game owns `assets/logic` for authoritative content and `assets/presentation`
for client-only content. Logic assets must not depend on presentation assets.
Packaging must preserve that split when implemented.

`nico-assets` defines `AssetId` and `Handle<T>` identity only. A general loader,
manifest, importer, dependency resolver, and packaging pipeline are not present.

Engine bootstrap shaders live separately under the repository's
`assets/presentation/shaders/` root. `nico-shaderc` compiles Slang into the
checked-in WGSL artifact outside Cargo's build graph. The host reads that artifact
synchronously, waits for GPU initialization, and creates the pipeline before its
first redraw. This direct file read is not the planned service-backed asset load.
Backend shader/pipeline preparation still occurs at runtime.

Source files and authoring metadata stay outside the future shipping runtime
contract. Introduce new data formats when concrete consumers require them.

## Measurement and profiling requirements

Every library must support measurement of its meaningful work. Use structured
spans, durations, and counters, extending the existing tracing foundation where
suitable. Hosts select collection, filtering, and export policy. Retention must
be bounded, overflow visible, and overhead controllable.

Measurements use wall-clock time independently of fixed simulation time.
Instrumentation must preserve authoritative results for identical input and
tick sequences. CPU submission durations and GPU execution durations must be
identified separately; unsupported GPU measurements must be reported as such.

The first profiling consumer is the reported client startup delay. Capture window
creation, shader read, graphics instance/adapter/device initialization, surface
configuration, shader/pipeline creation, and time to first successful
presentation. Record build profile, backend, and adapter and compare repeated
launches before choosing an optimization.

Existing runtime stage/system spans and diagnostics do not yet provide complete
cross-library profiling or shared capture/export. The next milestone establishes
that path and extends it across runtime, services, ECS, input, assets, rendering,
and client/server hosts as applicable.

## AI operation requirements

Client and server operations must support AI tooling through discoverable,
structured interfaces. The planned MCP adapter consumes an operation boundary
with typed arguments, capability discovery, request correlation, explicit
completion/error results, and timeouts. Initial operations cover local process
launch/stop, readiness, diagnostics, and profile capture/retrieval.

Process supervision and protocol adapters belong outside the headless runtime.
Hosts own window and device operations. Simulation commands execute at
runtime-owned boundaries; tooling reads owned snapshots and does not receive
background mutable world access. Operational access is enabled by the host and
scoped to intended development processes.

No MCP adapter or shared operation API is implemented yet. Choose transport,
exporter, and package boundaries with the first client and server consumers.
New operational features should expose an automation path alongside human
interfaces.

## Extension rules

- Keep public contracts small and document lifecycle, ownership, and failure.
- Add crates for demonstrated ownership/dependency boundaries, not placeholders.
- Keep stable asset/player/network identities distinct from generational ECS IDs.
- Libraries emit diagnostics; hosts initialize subscribers and exporters.
- Keep physics authoritative if it is introduced for shared collision rules.
- Use measurements to justify optimization, extraction, and caching.
- Keep protocol/provider types out of unrelated engine-facing APIs.

Parallel scheduling, public math representation, physics, audio, UI, production
materials/render graphs, scene/prefab formats, and import caching remain deferred.
Measurement and AI operations are required next work; broader visual development
tools remain future capabilities.
