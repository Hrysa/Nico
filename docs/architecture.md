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
| `nico-launch` | Native CLI, diagnostics, and optional server host/MCP lifecycle |
| `nico-ops` | Dependency-free host control core; optional MCP stdio adapter |
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

Measurement and profiling remain requirements across all libraries. Experimental
Rust/LLVM XRay is the selected future direction for automatic function profiling
without hand-written spans or per-method attributes. The desired experience is
a Unity-style call hierarchy and timeline with inclusive time, self time,
invocation counts, and frame/thread context.

Profiling implementation is deferred. Do not add profiler code, a prototype,
custom collectors/viewers, per-method instrumentation, or profiling toolchain
configuration for the current work. XRay is not integrated or validated for Nico;
platform compatibility, runtime setup, and coverage investigation can wait until
profiling work resumes. See the
[Rust XRay compiler documentation](https://doc.rust-lang.org/unstable-book/compiler-flags/instrument-xray.html)
for the future integration point.

Existing tracing remains for diagnostics and semantic context. It is not
automatic function capture. Future integration must distinguish recorded
invocations from samples or async polls, execution nesting from async causality,
elapsed scope durations from actual on-CPU time, and CPU submission time from GPU
execution. Report coverage limits and incomplete captures explicitly.

The reported client startup delay remains unmeasured. It is a future profiling
case, not a prerequisite for the next milestone. Profiling must preserve
authoritative results for identical input/tick sequences, with controllable
overhead and bounded collection. Profile capture/export and access through MCP
are deferred alongside integration.

## AI operation requirements

Client and server operations must support AI tooling through discoverable,
structured interfaces. MCP adapters consume an operation boundary
with typed arguments, capability discovery, request correlation, explicit
completion/error results, and timeouts. Initial operations cover local process
launch/stop, readiness, and diagnostics. Profile capture/retrieval follows future
XRay integration and is not required for the initial operation baseline.

Process supervision and protocol adapters belong outside the headless runtime.
Hosts own window and device operations. Simulation commands execute at
runtime-owned boundaries; tooling reads owned snapshots and does not receive
background mutable world access. Operational access is enabled by the host and
scoped to intended development processes.

The minimal `nico-ops` core is implemented without runtime or provider dependencies.
`control_channel` pairs a cloneable `HostControl` with a single `HostEndpoint`.
Controllers read owned status snapshots and enqueue an idempotent stop signal;
one snapshot and one pending stop are retained. The host publishes lifecycle,
completed-step count, and a final success/failure result. Unexpected endpoint
drop reports failure, while losing all controllers requests orderly stop.

`nico_launch::server::FixedRateServerRunner::with_operations` is the first consumer. It reports
readiness after its first successful host tick, checks stop between ticks, and
uses the stop channel to interrupt its paced wait. The host alone calls App
lifecycle methods. Status may lag a busy host, and terminal host status is not
proof that its process has exited.

The optional `nico-ops/mcp` feature exposes `status` and `stop` using the official
Rust MCP SDK. `nico-launch/src/server/` owns the runner, MCP arguments, control
channel, dedicated service thread, and final join. `minimal-game-server` delegates
to `ServerHost`, keeping the App on its existing host thread. Games build their
App and may supply `ToolExtensions` through `ServerHost::with_mcp_tools`; they do
not own MCP transport or lifecycle. Extensions cannot replace `status`/`stop` or
duplicate another tool name. Handlers validate arguments and return promptly,
reading owned data or queueing host requests without mutable App access. The default
`nico-ops` build has no external dependencies. Stdout is reserved for MCP and
native launch diagnostics use stderr. Host completion leaves MCP available for
final-status reads; stdin EOF requests host stop and ends the connection. The
engine host joins the adapter before exiting. Stop results acknowledge delivery,
not process exit. Native client integration, process supervision, and structured
diagnostic forwarding remain planned.
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
AI-accessible client/server operations are next. Profiling implementation and
broader visual development tools are deferred.
