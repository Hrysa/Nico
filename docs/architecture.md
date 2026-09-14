# Nico architecture

Nico is code-first: Rust plugins construct game state and register behavior. This
document defines ownership and dependency rules. [The roadmap](roadmap.md) records phase
outcomes and validation evidence; [TODO](../TODO.md) owns concrete next actions.

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
`nico-launch` for CLI and diagnostics. The game client composes shared gameplay, and
`nico-winit` composes the renderer with its concrete RHI provider. `nico-rhi` has no
dependency on wgpu or its provider. Runtime has no dependency on input, presentation,
native providers, or launch policy. `nico-ecs` does not depend on runtime.

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
| `nico-assets` | Asset/handle identity, owning leases, and optional runtime-owned PNG/GLB loading |
| `nico-launch` | Native CLI, diagnostics, and client/server transport composition |
| `nico-ops` | Dependency-free host control core; optional tool catalogs and game bridge transport |
| `apps/nico-shaderc` | Offline shader compilation outside the Rust build graph |
| `apps/nico-bridge` | CLI entry point for the independent-game MCP bridge |

## Runtime and ECS

`nico_runtime::ecs` is the canonical engine-facing namespace for `World`, `Entity`, and
hecs query/command types. The ECS provider owns entity/component storage; runtime owns
application lifecycle and execution policy. ECS is for simulation state, not universal
storage for windowing, GPU objects, or external services.

`Plugin::build(&self, &mut AppBuilder)` is the composition boundary. The initial stages
are `Startup`, `FixedUpdate`, `Update`, and `Shutdown`. Hosts drive `App::start`,
`tick`, and `shutdown`; runtime does not own a permanent loop. The dedicated server
supplies a paced fixed-rate `AppRunner`. Tests can drive bounded frames and
deterministic time.

Systems query the authoritative world and record structural changes through
`SystemContext::commands`. Successful structural commands flush before the next system;
commands from a failed system are discarded. Direct mutations of existing components and
resources are not rolled back on system failure.

Typed events are bounded broadcast streams with independent `EventReader<T>` cursors.
Successful system event writes become visible to the next scheduled system;
failed-system writes are discarded. Lagging readers receive a missed count when retained
history overflows. Hosts may inject events before a tick. Persistent domain state
belongs in resources/components or external storage, rather than relying on transient
event retention.

Portable services use domain-typed owned requests/completions and bounded queues.
Backends never receive `World`. `AppBuilder::add_service` registers a publication system
in `Update`; its schedule position determines when later consumers see
`ServiceCompletion<T>` events. Cancellation, overload, backend failures, stale
generational entity targets, and shutdown have explicit behavior. Runtime closes
registered channels during shutdown. The boundary selects no async executor; tests can
drive backend endpoints manually.

## Native host and input

`nico-winit` is the concrete native provider. It owns Winit types and the
`ApplicationHandler` callbacks, creates the window on resume, adapts native input, and
drives the runtime and presentation lifecycle. Suspension pauses ticks and resets the
frame-time origin; close and failure paths converge on session shutdown. Desktop
minimization must be validated independently of Winit suspension.

`nico-input` is a headless leaf capability with normalized device identity, connection
state, buttons, axes, persistent vectors, and frame-local motion. Winit supplies
keyboard, pointer, wheel, raw motion, and touch events. Focus loss releases controls. A
native gamepad provider is deferred.

The game client maps `InputState` to semantic commands such as `PlayerCommand`. Shared
gameplay and the server do not depend on physical-input types. The current client
configuration selects title, rendering pipeline, shader paths, and smoke policy. A
provider-neutral host contract requires evidence from a second provider.

See [ADR 0001](decisions/0001-native-client-event-loop.md) for event-loop ownership.

## Presentation and graphics

Presentation may read the authoritative world immutably. It does not own or mutate
gameplay state. Direct queries are allowed; extraction and caching require a
demonstrated need.

Games publish `nico-presentation::Scene2d` and `Scene3d` as runtime resources after
game updates. `Presentation` copies these snapshots through immutable world access;
it never gives the renderer world access. Immutable `Arc<Texture>` and `Arc<Mesh>`
values retain CPU content after store entries are released. Absent scenes produce
empty snapshots. Shutdown releases both retained snapshots.

`nico-render` consumes presentation contracts with default features disabled, keeping
its own dependency path free of runtime. `nico-presentation`'s default `runtime` feature
adds world extraction and lifecycle; its drawing contracts need only `nico-assets`.
World quads use an explicit 2D camera; HUD quads use logical viewport coordinates.
The same quad pipeline draws both. Shared/headless game logic does not depend on assets or presentation.
The native client maps shared positions to visuals in its game-owned extraction system.

`nico-rhi` defines associated provider resource types for capabilities, buffers,
textures, bindings, shaders, pipelines, commands, uploads, passes, and surfaces.
Concrete providers retain native resource ownership without leaking backend types into
unrelated APIs.

`nico-rhi-wgpu` owns the instance, adapter, device, queue, and surface. It handles
resize and recoverable surface outcomes, including zero-size, timeout, occlusion,
outdated/lost surfaces, and suboptimal frames. Unrecoverable failures use RHI error
categories.

`nico-render` selects shaders and pipelines, uploads textures and meshes, records draw
passes, submits commands, and presents. The texture cache identifies immutable allocations and
retires unused entries on drawn frames, retaining its fallback; CPU references are weak.
Providers retain resources referenced by recorded/submitted work when wrappers drop.
The pipelines rebuild if the surface format changes. Native hosts select a pipeline
and supply viewport/DPI values but do not define scene draw calls. The bootstrap
triangle remains available to hosts that do not select a scene pipeline.

`Scene3d` holds a perspective camera and immutable mesh instances. The renderer and
MCP sample controls share validation of stored f32 camera directions.
`MeshRenderPipeline` owns depth, vertex/index caches, per-instance transform uniforms,
and a shared quad renderer for HUD drawing and texture reuse. The mesh pass clears
and depth-tests; the HUD pass loads its color target before one presentation.

The smoke frame limit counts client-session frames, even when GPU acquisition skips
presentation. It is bounded lifecycle coverage, not a successful-GPU-frame counter.

See [ADR 0002](decisions/0002-nico-rhi-wgpu-backend.md) for the GPU boundary and [ADR
0003](decisions/0003-render-pipeline-layer.md) for rendering policy.

## Assets and game construction

Games live under `games/<game>/` with `shared`, `client`, and `server` packages. Shared
Rust plugins own authoritative setup and behavior. The client adds local input and
presentation; the server remains headless.

Each game owns `assets/logic` for authoritative content and `assets/presentation` for
client-only content. Logic assets must not depend on presentation assets. Packaging must
preserve that split when implemented.

`nico-assets` defines `AssetId`, copyable `Handle<T>` identity, and owning
`AssetLease<T>`. Its optional `loading` feature depends on runtime and PNG/glTF decoders;
the default CPU asset API remains dependency-free. `TextureStore` and `MeshStore`
specialize the same runtime resource, `AssetStore<T>`. Each store uses a distinct typed
service completion and one engine-owned worker that reads/decodes files through the
existing service boundary. Runtime systems publish results, reconcile lease release,
and dispatch bounded work.
The host supplies an immutable ID/path catalog. A general importer, dependency resolver,
and packaging pipeline are not present. Usage belongs in [README](../README.md#texture-loading).

Engine shaders live separately under the repository's
`assets/presentation/shaders/` root. `nico-shaderc` compiles Slang into the checked-in
WGSL artifacts outside Cargo's build graph. The host reads selected artifacts
synchronously, waits for GPU initialization, and creates pipelines before its first
redraw. This
direct file read is not the planned service-backed asset load. Backend shader/pipeline
preparation still occurs at runtime.

Authoring project files and metadata stay outside the future shipping runtime contract.
A supported source encoding can itself be a shipping asset: the selected
[texture design](plans/2026-09-14-texture-assets.md) uses PNG directly. Introduce new
data formats when concrete consumers require them. Asset leases retain CPU content
independently of copyable handle identity; an explicit `Arc<Texture>` can pin pixels
for a snapshot beyond store release. GPU resource lifetime remains owned by the renderer;
texture and mesh upload and both consumers are implemented. The
[mesh design](plans/2026-09-14-mesh-assets.md) defines the bounded GLB subset.

## Measurement and profiling requirements

Measurement and profiling remain requirements across all libraries. Experimental
Rust/LLVM XRay is the selected future direction for automatic function profiling without
hand-written spans or per-method attributes. The desired experience is a Unity-style
call hierarchy and timeline with inclusive time, self time, invocation counts, and
frame/thread context.

Profiling implementation is deferred. Do not add profiler code, a prototype, custom
collectors/viewers, per-method instrumentation, or profiling toolchain configuration for
the current work. XRay is not integrated or validated for Nico; platform compatibility,
runtime setup, and coverage investigation can wait until profiling work resumes. See the
[Rust XRay compiler
documentation](https://doc.rust-lang.org/unstable-book/compiler-flags/instrument-xray.html)
for the future integration point.

Existing tracing remains for diagnostics and semantic context. It is not automatic
function capture. Future integration must distinguish recorded invocations from samples
or async polls, execution nesting from async causality, elapsed scope durations from
actual on-CPU time, and CPU submission time from GPU execution. Report coverage limits
and incomplete captures explicitly.

The reported client startup delay remains unmeasured. It is a future profiling case, not
a prerequisite for asset loading. Profiling must preserve authoritative results for
identical input/tick sequences, with controllable overhead and bounded collection.
Profile capture/export and access through MCP are deferred alongside integration.

## AI operation requirements

Client and server operations must support AI tooling through discoverable, structured
interfaces. MCP adapters consume an operation boundary with typed arguments, capability
discovery, request correlation, explicit completion/error results, and timeouts. The
bridge discovers independently launched game instances and exposes their registered
operations. Explicit stop, readiness, and bounded structured diagnostic retrieval are
available. The bridge does not launch game processes. Profile capture/retrieval follows
future XRay integration and is not required for the initial operation baseline.

Process supervision and protocol adapters belong outside the headless runtime. Hosts own
window and device operations. Simulation commands execute at runtime-owned boundaries;
tooling reads owned snapshots and does not receive background mutable world access.
Operational access is enabled by the host and scoped to intended development processes.

The minimal `nico-ops` core is implemented without runtime or provider dependencies.
`control_channel` pairs a cloneable `HostControl` with a single `HostEndpoint`.
Controllers read owned status snapshots and enqueue an idempotent stop signal; one
snapshot and one pending stop are retained. The host publishes lifecycle, completed-step
count, and a final success/failure result. Unexpected endpoint drop reports failure,
while losing all controllers requests orderly stop.

`nico_launch::server::FixedRateServerRunner::with_operations` is the first consumer. It
reports readiness after its first successful host tick, checks stop between ticks, and
uses the stop channel to interrupt its paced wait. The host alone calls App lifecycle
methods. Status may lag a busy host, and terminal host status is not proof that its
process has exited.

The `nico-ops/mcp` feature defines host tool catalogs and handlers using the Rust MCP
SDK types. It does not serve a direct host MCP endpoint. `nico-launch` composes host
control and bridge connections; games supply `ToolExtensions` without owning transport
threads. Extensions cannot replace `status`/`stop` or duplicate names. Handlers validate
arguments and return promptly, reading owned snapshots or queueing host requests without
mutable App access. Engine and game tools are uploaded as one catalog when the host
connects; the bridge does not pre-register host APIs. The default `nico-ops` build has
no external dependencies. Stop results acknowledge delivery, not process exit. Bridge
disconnection does not request host shutdown.

`nico_winit::run_native_client_with_operations` accepts the same optional control
endpoint. `HostEndpoint::set_wakeup` installs a callback that only signals a Winit user
event; stop and last-controller disconnect wake the host even without redraws.
Installation also signals once to cover requests before registration. Callbacks run on
the requesting thread and must return promptly without panicking or holding a
controller. Host endpoint cleanup releases the callback. The default `nico-ops` core
remains dependency-free, and ordinary native client hosting needs no endpoint.

The native host publishes readiness only after App startup and a
`RenderStatus::Presented` outcome. Its `completed_steps` counts successful session
frames, including GPU skips, and can advance while still Starting. Readiness stays
latched across suspension; `HostStatus::active` separately reports host activity.
Optional `HostStatus::graphics` reports successful presentation API calls and the latest
backend-neutral graphics outcome. Skips and failures do not increment the counter; host
activity and readiness remain separate. The snapshot is retained across suspension and
shutdown, and headless servers leave it absent. This is operational reporting, not GPU
timing or proof of display scanout. Stop executes session shutdown on the host thread.
Final success/failure is published after the event loop returns, including loop and
shutdown errors. Blocking initialization or game systems still delay orderly stop.

The preferred development connection is `apps/nico-bridge`, backed by
`nico-ops/src/bridge/`. It serves MCP on stdio and listens for game registrations on
loopback TCP. Games launch independently and attempt bridge connections by default
(`--no-bridge` opts out, `--bridge ADDRESS` overrides the endpoint); `nico-launch`
client/server hosts own adapter threads and retain control handles across connection
loss. Reconnection never restarts gameplay, and bridge disconnect never requests stop.
Game `ToolExtensions` upload schemas and keep handlers inside the game process. The
bridge routes namespaced tools by unique connection instance ID, retains cached schemas
while games are offline, and replaces a game/role catalog when no live incompatible
version exists. Cached definitions outlive game connections but not the bridge process.
Readiness, reported activity, connectivity, and snapshot age are separate. No
disconnected snapshot establishes process exit.

`list_game_tools` and `call_game_tool` provide stable discovery/invocation even when a
client does not react to dynamic MCP catalog notifications. Games validate their tool
arguments and return promptly using owned snapshots or bounded runtime requests. The
minimal game demonstrates a game-owned snapshot plugin ordered after gameplay.
Transport, schemas, and MCP dependencies do not enter the headless runtime.

See [the bridge contract](plans/2026-09-11-mcp-bridge.md) for protocol limits,
heartbeat/deadline behavior, API reconciliation, and local trust scope. Diagnostic
retrieval uses the same registered-tool routing as game operations. New operational
features should expose an automation path alongside human interfaces.

### Diagnostic capture

Native diagnostic capture belongs to `nico-launch`, alongside logging policy. Its
tracing event layer shares the stderr filter and writes bounded owned records into a
process-local history. Native hosts register the read-only `diagnostics` handler when
composing bridge transport. The handler reads that history without App/World access; the
bridge only discovers its schema and routes calls. Retention is 256 events, pages
contain at most eight records, and both field count and text sizes are bounded.
Exclusive cursors and eviction counts distinguish missing history from an empty page.
Capture excludes span timing and is not a profiler.

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
Profiling implementation and broader visual development tools are deferred; phase status
and priorities belong in the roadmap and TODO.

## Window snapshot ownership

Native clients register `window_snapshot` through `nico-launch`. `nico-ops` holds a
bounded request/result slot with process-local IDs; tooling queues requests and reads
owned pixels. `nico-winit` consumes requests at rendering, and the wgpu provider copies
the color target before presentation, waits for bounded GPU completion, removes row
padding, and converts BGRA to RGBA. This adds no runtime dependency to the provider.
Surface COPY_SRC is enabled only when supported; capture errors do not change
presentation success. Launch starts a single background PNG encoding/write job on
retrieval, returns pending while it runs, and retains one local artifact per process.
Encoding never runs on the bridge heartbeat/call thread. New captures are rejected
while the encoder is busy, and teardown joins active work. Shutdown fails pending
capture requests. Usage and bounds belong in
[README](../README.md#window-snapshots-through-mcp).
