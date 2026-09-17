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
game client -> nico-presentation-control -> nico-presentation (contracts only)
                                       -> nico-spatial
shared gameplay -> nico-physics -> Rapier 3D
                              -> nico-runtime (optional adapter) -> nico-ecs
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
| `nico-input` | Provider-neutral device state and frame-to-fixed-step accumulation |
| `nico-presentation` | Immutable world-facing presentation lifecycle |
| `nico-presentation-control` | Camera control, coordinate helpers, bitmap text caching/layout, and quad construction |
| `nico-render` | Shader/pipeline selection, frame recording, submission, and presentation |
| `nico-rhi` | Backend-neutral GPU resource, command, and surface contracts |
| `nico-rhi-wgpu` | Native GPU resources and surface recovery using wgpu |
| `nico-winit` | Native window lifecycle, input adaptation, and client-session coordination |
| `nico-assets` | Asset identity, leases, procedural meshes, and optional runtime-owned PNG/GLB loading |
| `nico-animation` | Runtime-free CPU poses, skin matrices, humanoid profiles and retargeting |
| `nico-spatial` | Conservative camera sphere/box queries |
| `nico-physics` | Rapier body/collider ownership, queries, character movement, and optional fixed-step ECS adapter |
| `nico-launch` | Native CLI, diagnostics, and client/server transport composition |
| `nico-ops` | Host control, command bookkeeping, owned publication; optional tool catalogs and bridge transport |
| `nico-net` | Bounded framed native game transport, connection attempts and peer lifetime; no gameplay or runtime dependencies |
| `apps/nico-shaderc` | Offline shader compilation outside the Rust build graph |
| `apps/nico-bridge` | CLI entry point for the independent-game MCP bridge |

## Multiplayer world ownership

The reference game's default native mode is a persistent outdoor world; `--arena`
selects the independent combat test. `nico-net` owns nonblocking TCP framing and
bounded I/O/queues. A short-lived engine connection worker avoids blocking the
client while joining; the game polls established connections at fixed boundaries.
Game transport is independent of MCP. `nico-ops` and `nico-launch` continue to own
bridge transport and host lifecycle; losing the bridge does not stop simulation.

`arena-arpg-shared::open_world` owns a hecs-backed world with independent identity,
position, combat, player, monster and loot components. Stable object IDs map to
generational ECS entities; reconnect creates a new object ID. Immutable character
definitions are shared by reference. The game owns action/dodge rules, AI, loot,
inventory, progression, session protocol and persistence; these are not engine
components or inheritance hierarchies.

The Meadow quest stores bounded stage/count progress in each character record.
Zone content owns the stationary warden and camp area; sequenced talk input is
validated at the simulation boundary. Client rendering and prompts consume owned
zone/snapshot values. See the [quest contract](plans/2026-09-17-meadow-quest.md).

The authoritative server simulates at 60 Hz and consumes at most one sequenced
input per player per tick. Movement is bounded and collision constrained. Each
input describes one tick; missing inputs do not renew movement. Queues are bounded
to 64 inputs. The native client limits its lead to eight unacknowledged inputs,
retaining unsent button edges and queued tool actions under backpressure.

At 20 Hz the server publishes a full nearby-object snapshot for each player, using
a 32-metre distance filter. Inventory and XP are sent only to their owner. Clients
replace their interest set from each snapshot, reconcile predicted local movement
against acknowledged input, and interpolate continuous remote samples. Respawns,
teleports and action boundaries use the newest complete state. Clients never
determine authoritative damage, loot ownership, health or progression.

The current bounded zone supports at most 16 sessions and 256 objects; these are
resource bounds, not measured population-capacity claims. The protocol is versioned,
strict JSON over framed TCP and restricted to loopback. Local character names are
development identities, not authentication. The character store holds one exclusive
directory lock and replaces synced temporary records on save. Failed loads reject
that character's join without overwriting data; failed saves are surfaced and retain
the authoritative record for retry. The runtime saves on periodic fixed boundaries,
disconnect and orderly shutdown. Monsters are not persisted.

World tools enqueue bounded commands or read owned publications. `world_state`
includes entities, sessions, per-player interest views and spawn results.
`world_client_state` separates connection state, prediction, acknowledgement,
snapshot age and command history. `submitted` means queued to transport, not a
successful server action. Final shutdown publications cancel pending commands.

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

`Scene3d` holds a perspective camera and immutable mesh instances. Both store
orientation as a finite normalized `Quaternion` (the shared glam quaternion type,
XYZW order), mapping local axes into world space. Camera local forward is -Z and
local up is +Y. The renderer inverts the camera pose directly, so vertical views
and roll do not require a fixed world-up vector. Invalid/non-unit orientations are
rejected. `Camera3d::looking_at` is an optional targeting helper and rejects coincident
targets or an up vector parallel to the view; these constraints do not apply to
explicit quaternion poses.
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

`arena-arpg-shared` owns a bounded four-actor combat simulation as a runtime resource.
Actor slots are reused across three waves; identity includes run and wave. Shared
`CharacterCatalog` definitions drive authoritative attacks, presentation reach, and
tool timing. Actor roles index a shared immutable catalog; mutable instance state
remains separate. Native hosts load `.char.toml` assets at startup, while the client
also resolves `.char-vis.toml` models, sockets, poses, animation settings and procedural
parts. Both schemas compose engine `core` and game `arena` sections.
`nico-assets::character` (optional `character` feature) owns runtime-free identity,
collision, model, profile, pose, socket and named clip descriptors. The game owns
health/movement/combat rules, procedural body/equipment composition and action
bindings. Core validation does not require arena actions; the client resolves
bindings into indexed playback settings before use. Serde composition introduces
no inheritance, field flattening, type registry or automatic ECS spawning.
See the [character definition contract](plans/2026-09-17-character-definitions.md).
Intermissions, roster replacement, position reset, and health recovery run at fixed
boundaries; run time persists through wave transitions.
`ArenaPlugin` consumes game-owned `TickInput` events only at fixed boundaries and
requires the exported 60 Hz `FIXED_STEP`. Inputs describe one tick; held movement
must be resubmitted. Latest input wins at a boundary, except a valid current-run
restart takes priority. `Arena` exposes immutable snapshots and `StepReport` records
the latest semantic rejection/reset result. `InputFocusLost` clears pending intent
without undoing a combat action already started. Dodge buffering lives in the shared
simulation so human and MCP input use the same readiness rules. The adapter retains
a buffered dodge's command until actual action start or cancellation. Shared state
also reserves spaced monster strike times; rendering only reads the resulting phases.

The optional `tools` feature registers the arena's game catalog through `nico-ops`.
It stores one movement lease, one combat request, a reserved restart request, and
128 terminal outcomes. Runtime boundaries arbitrate human input, apply requests,
and publish owned snapshots with outcomes atomically under a process-local mutex.
This lock covers bounded four-actor work; no I/O or external callbacks run under it.
Shutdown closes acceptance and retains final snapshots/outcomes. Engine hosts still
own all service threads and transport; the default shared crate depends on the
headless runtime and physics integration. Detailed rules belong in the
[reference-game design](plans/2026-09-15-reference-game.md).

`arena-arpg-client` maps frame input into fixed-step intent before `ArenaPlugin`,
retaining movement through catch-up ticks while consuming action edges once. Run
reset and wave transitions suppress held movement until release. Game-owned camera tuning and procedural
visual extraction publish `Scene3d`/`Scene2d` after simulation. Camera boom collision
uses the perimeter geometry; text, health, attack telegraphs, and poses use existing
mesh/quad contracts. The client adds bounded camera commands and an owned
view snapshot. The server composes the same shared rules at 60 Hz; neither host
currently replicates state to the other.

`nico-presentation-control` is the common home for stateful presentation controllers.
Its `camera` module owns reusable orbit math, angle bounds, and distance restoration.
It uses headless `nico-spatial` queries and presentation contracts without the
optional runtime lifecycle. Games supply targets, tuning, geometry, and
collision filtering through a query callback; the arena wrapper selects the hero
pivot and perimeter. Expanded-box sweeps are conservative at corners, and a configured
minimum boom distance can overlap nearby geometry. Shared game-authored wall dimensions
drive mesh construction, camera queries, and authoritative collision; actor centers
stop their configured collision radius inside the wall inner face (11.4 units with
the shipped 0.4-metre radius). Orbit yaw/pitch
remain input coordinates for game limits; the controller derives a normalized
quaternion for its boom and published camera, with no independent mutable Euler pose.
The arena's planar actor-facing angles remain gameplay data and are converted to
quaternions when publishing mesh instances.

The `coordinates` module supplies quaternion local-to-world point transforms,
floor-relative direction rotation, and cylindrical billboarding. A billboard view
parallel to its up axis returns `None`; the caller chooses a stable fallback.
The arena retains axis bindings, actor poses, body proportions, and health-bar layout.

`nico-assets::procedural` builds validated boxes and XZ sectors/arcs. UVs, sizes,
angles, and tessellation counts are supplied by callers; palette and attack range
remain game data. `nico-presentation-control::text` supplies a cached bootstrap 5x7
ASCII font, text measurement, line breaks, and quad construction. It is not a full
font-shaping or UI system. The arena owns HUD layout and scales it at small sizes.

`nico-input::fixed` accumulates frame deltas/edges and persists held axes across
catch-up ticks. Edges coalesce until consumed. Game bindings and focus/run/wave
cancellation policy remain in the arena adapter.

`nico-ops::commands::CommandBook` owns bounded request lanes, checked monotonic IDs,
closed acceptance, and configurable terminal-history retention. Arena gameplay,
camera tools, and engine window tools use it. The caller owns synchronization and
must finalize pending work on close; command completion semantics and arbitration
remain with the appropriate game or native host.

`FifoCommands` adds submission ordering over that bookkeeping for the rendering
sample. Closing drains queued requests into caller-defined cancellation outcomes;
work already popped is finalized by the runtime owner. The sample publishes those
outcomes in its final closed snapshot before releasing its presentation resources.

`nico-ops::publication::Publication` stores an owned payload, monotonic sequence,
publication time, and closed state. Reads calculate age without refreshing it, and
closing retains the last payload and its timestamp. Games embed it under their
existing locks, preserving atomic arena snapshot/outcome publication. JSON state
tools expose `snapshot_sequence`, `snapshot_age_ms`, and `closed`. Arena presentation
is constructed outside its tooling lock; `client_state` window fields describe the
last published frame. Use the engine `window_state` tool for current host observations.

`nico-launch::client` owns the shared `--background` option and maps it to native
initial-focus configuration. Both clients inherit it; games still choose titles and
pointer-capture policy.

`nico-winit` owns OS pointer capture. Engine `window_control` requests wake the
native event loop and complete independently of redraw or simulation. Capture
requires the configured capture policy and an active, focused window; release is
allowed while inactive. Operational snapshots read host-owned capture state directly;
`NativeWindowState` is its runtime mirror, published before input dispatch. Capture clicks
are consumed; Escape releases capture. Focus loss/suspension releases input and
publishes `WindowFocusLost`, which the game maps to its semantic input cancellation.
Actual capture and request acceptance remain distinct; platform APIs stay in Winit.

Each game owns `assets/logic` for authoritative content and `assets/presentation` for
client-only content. Logic assets must not depend on presentation assets. Packaging must
preserve that split when implemented.

`nico-assets` defines `AssetId`, copyable `Handle<T>` identity, and owning
`AssetLease<T>`. Its default CPU assets and public import interface are dependency-free.
`png-import` and `gltf-import` independently enable built-in importers;
`runtime-loading` enables the runtime adapter. `loading` enables all three for
existing consumers. `TextureStore` and `MeshStore`
specialize the same runtime resource, `AssetStore<T>`. Each store uses a distinct typed
service completion and one engine-owned worker that reads/decodes files through the
existing service boundary. Runtime systems publish results, reconcile lease release,
and dispatch bounded work.
The host supplies an immutable catalog selecting importer, source, typed settings,
and budgets per asset ID. `AssetImporter` is implemented on a decoder rather than
its output type; `ImportRegistry<T>` accepts multiple engine or userland importers
for T. Output types require only `Send + Sync + 'static`. Registration/configuration
errors precede worker startup, and sources/importer descriptors support structured
inspection. Shared source reading is bounded; importers receive primary bytes and
cooperative cancellation/output accounting, without world or GPU access. Worker
loss fails pending entries; user code is not automatically restarted or sandboxed.
Native publication and identity/lease semantics are unchanged.

External dependency reads, a dependency scheduler, and packaging are not implemented.
The static mesh GLB importer remains restricted; the separate `ModelGlbImporter`
produces validated immutable bundles of scene nodes, geometry, skins, clips,
materials, textures, and encoded images. It uses the same public registry and
`AssetStore<Model>` lifecycle. Usage belongs in
[README](../README.md#extending-asset-import); the
[import contract](plans/2026-09-16-extensible-asset-import.md) owns extension details.

`nico-animation` consumes CPU model contracts and glam only. It owns pure pose
sampling, parent-order transform evaluation, mesh-local skin matrices, and canonical
humanoid conversion. Userland supplies bone-name/index profiles, reference-pose
calibration, and basis/root-motion policy; presets cover the supplied Mixamo/RPG
rigs. Source skinning hierarchy and weights are retained. The initial 22 body roles
leave unmapped finger/helper joints at their reference local transforms. Poses
borrow their exact model; cross-model pose reuse is rejected. Games will select
animation states from authoritative snapshots. `AnimationPlayer` owns elapsed-time
playback and transitions independently of runtime scheduling; `Attachment` resolves
a named node once and computes its evaluated affine socket transform.
`nico-presentation-control::model::ModelVisual` owns shared immutable mesh/texture
assembly and produces independent palette snapshots from model-space node matrices.
Callers provide matrices in that exact model node order; this raw matrix boundary
does not validate model identity. Loading, animation selection, and instance placement
remain caller policy. The arena client loads a local Mixamo hero and RPG/Quaternius
clips, selects motion from owned snapshots, and publishes animation observations
through `client_state`. Its named hand attachment uses a one-joint palette to retain
affine transforms; gameplay collision and damage remain unchanged. The [model/animation contract](plans/2026-09-16-model-animation.md)
defines the supported subset and mathematical conventions.

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

## Physics

`nico-physics` owns a Rapier 3D world behind provider-independent descriptors,
world-local body IDs, and owned results. It has no presentation or host dependency.
The default `runtime` feature adds a plugin above the runtime; runtime and ECS never
depend on physics. Standalone simulations can disable that feature and own the world
directly. Coordinates use f64; poses contain unit XYZW quaternions.

`PhysicsPlugin` synchronizes `PhysicsBody` components in stable entity order at each
FixedUpdate, steps using the runtime delta, and writes poses/velocities back. Its
`PhysicsEntities` resource maps generational ECS identities to private provider
bodies. Despawn/component removal removes the corresponding body and collider;
shape/material edits replace them. Kinematic pose edits set motion targets; fixed
and dynamic pose edits teleport. Register intent producers before physics and
result consumers after physics. Shutdown releases physics state without workers.

World mutations become visible to subsequent queries without advancing simulation.
Lookups use a separate collider snapshot and spatial tree, rebuilt lazily after
geometry changes or a dynamics step. Refreshing them never consumes pending
changes in the dynamics world. Queries support body exclusion, collision groups,
and optional sensors. Sensors detect all body-type combinations, including fixed
triggers with kinematic characters; bilateral collision masks still apply. Solid
colliders retain Rapier's default active collision types. Each body
has one collider. Contact observations are capped at 4,096 pairs with a truncation
flag; the runtime emits them as owned `ContactFrame` events through the existing
bounded event bus. They describe touching pairs, not hit damage or contact starts.

The arena uses a persistent world inside its `Arena` resource instead of the ECS
adapter, so headless direct stepping and runtime/MCP stepping share the same path.
It synchronizes live actor spheres and authored wall cuboids, then moves actors in
stable slot order with Rapier's controller. Game rules retain planar movement,
contact margins, collision dimensions, no actor pushing, and combat hit sectors.
The unused handwritten circle-slide solver was removed; conservative camera queries
remain in `nico-spatial`. This does not turn the arena into a gravity-driven game.

The integration enables Rapier's enhanced determinism and uses single-threaded
stepping. Nico cross-platform repeatability remains unverified. Physics profiling
is not enabled. API bounds and deferred features belong in the
[integration design](plans/2026-09-15-physics.md).

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
- Keep physics authoritative and run it at simulation-owned boundaries.
- Use measurements to justify optimization, extraction, and caching.
- Keep protocol/provider types out of unrelated engine-facing APIs.

Parallel scheduling, broader math APIs, advanced physics, audio, UI, production
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

## Native window operation ownership

`nico-launch` registers client-only `window_control`/`window_state` tools. The
`nico-ops` channel holds one pending request/latest outcome and an owned observed
window snapshot with age. Accepted operations wake the native event loop using the
host's existing wake callback. `nico-winit` consumes requests on the event thread,
independently of redraw or application ticks, so minimized clients can be restored.
Platform calls never run on bridge threads. Applied records submission to the window
manager; separately observed window state confirms the effect. Host teardown cancels
pending work and closes the slot. This introduces no runtime or game-owned transport.
Arguments and retention rules belong in [README](../README.md#window-controls-through-mcp).

## Character preview composition

`apps/nico-character-preview` composes the normal native host, imports local model
bundles before startup, and spawns an ECS character through a code prefab function.
Instances share immutable assets and own playback state. Its Update system samples
or retargets a pose using per-instance reusable buffers and publishes immutable
model-space joint palettes with persistent skin geometry through `Scene3d`.
`Mesh` validates joint indices and weights at construction; each `MeshInstance`
provides a palette matching its geometry. The renderer retains geometry uploads
and per-draw palette buffers, writes active joint matrices, and uses a separate
skinned vertex pipeline. Static mesh layout/shading remain unchanged. The optional
native skin shader is configured through `with_skin_shader`. Preview-only base-color
decoding, camera, and clip selection stay in the tool; runtime remains independent
of animation and presentation.

MCP handlers queue bounded commands or read owned publications. Playback changes
apply at Update; shutdown closes the queue with terminal cancellation results,
closes publication, despawns the character, and clears scene resources. Engine
launch/host crates retain transport, capture, and lifecycle ownership. This code
prefab is not a serialized ECS prefab system.


### Animated render visibility

`ModelVisual` caches an influence box for each joint/primitive from vertices with
positive weights. At extraction it transforms these boxes through current joint
palettes and instance placement; the union conservatively contains weighted skin
positions without CPU vertex deformation. Bounds include a numerical margin and
require finite affine transforms. They cover the current pose only. Callers union
attachment geometry separately and keep objects visible if bounds fail.

`Camera3d::view_projection` provides the shared zero-to-one depth matrix for drawing
and frustum tests. `ModelBounds::intersects_clip` rejects only boxes wholly outside
a common frustum plane; invalid input stays visible. The preview and imported arena
hero use this to omit offscreen draws and publish bounds/visibility through MCP.
The preview optionally caps per-instance pose evaluation, holds the displayed pose,
and accumulates elapsed time for the next sample. Camera changes force a fresh pose
before culling; bounds describe displayed geometry, not future motion. Arena pose
evaluation retains its normal rate. Visibility is presentation policy;
it does not change physics, gameplay collision, or camera obstruction geometry.


Arena attack timing uses `AnimationPlayer::update_at` to map snapshot phases to
an authored source contact marker without cancelling crossfades. The same snapshot
continues to own hit tests and movement. The game owns the Ch03 right-finger grip
reference, explicit Quaternius sword profile, palm socket, weapon geometry and
contact marker. `HumanoidRig::from_reference` preserves the equipment finger pose
through body retargeting without changing engine presets. Content regressions
check the authored sword against target-body reach and sampled floor clearance;
these checks never change authoritative combat rules.

World and arena presentation share the same character asset loader and playback
controller. Immutable assets are shared per character type; each visible actor
owns mutable playback state. Models may contain their own skinned weapons or
use an optional attached weapon. The game binds idle, movement, attack, dodge,
and death to owned simulation state. Health loss alone does not create an injury
state or interrupt playback. The [character contract](plans/2026-09-17-character-definitions.md)
owns the schema and inspection fields; source selection and licenses belong in
the game asset notices.
