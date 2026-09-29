# Nico architecture

This document defines ownership, data flow, and lifecycle rules.
[Roadmap](roadmap.md) tracks progress; [TODO](../TODO.md) lists tasks.
[README](../README.md) owns commands and usage.
Detailed API behavior and numeric limits belong beside their source code.

## Dependency direction

Runtime depends on ECS, never on presentation, native providers, physics, or launch policy.
ECS never depends on runtime.
The native host combines runtime, input, presentation, rendering, and the GPU provider.
Rendering uses RHI contracts; wgpu implements them.
Presentation and physics offer optional runtime adapters.
Rendering disables presentation's runtime feature on its own dependency path.
Provider types stay inside their integration boundaries.

| Owner | Main responsibility |
| --- | --- |
| [nico-ecs](../crates/nico-ecs/src/lib.rs) | Entity, component, resource, and query storage |
| [nico-runtime](../crates/nico-runtime/src/lib.rs) | App lifecycle, stages, time, events, and service results |
| [nico-input](../crates/nico-input/src/lib.rs) | Device state and input across fixed steps |
| [nico-presentation](../crates/nico-presentation/src/lib.rs) | Immutable scene data and optional world extraction |
| [nico-presentation-control](../crates/nico-presentation-control/src/lib.rs) | Camera, coordinates, text, model views, and instance streaming |
| [nico-spatial](../crates/nico-spatial/src/lib.rs) | Conservative camera queries |
| [nico-animation](../crates/nico-animation/src/lib.rs) | CPU poses, playback, skin matrices, and retargeting |
| [nico-assets](../crates/nico-assets/src/lib.rs) | Asset identity, leases, imports, definition parsing, models, and optional loading |
| [nico-definition-derive](../crates/nico-definition-derive/src/lib.rs) | Generates parse and load methods for validated definitions |
| [nico-scene](../crates/nico-scene/src/lib.rs) | Project files, saved scenes, and ECS creation |
| [nico-authoring](../crates/nico-authoring/src/lib.rs) | Game adapter contracts without UI |
| [nico-physics](../crates/nico-physics/src/lib.rs) | Rapier bodies, colliders, queries, and optional runtime integration |
| [nico-render](../crates/nico-render/src/lib.rs) | Draw policy, pipelines, uploads, submission, and presentation |
| [nico-rhi](../crates/nico-rhi/src/lib.rs) | Backend-neutral GPU contracts |
| [nico-rhi-wgpu](../crates/nico-rhi-wgpu/src/lib.rs) | Wgpu resources, surfaces, and recovery |
| [nico-winit](../crates/nico-winit/src/lib.rs) | Native window, input, and client lifecycle |
| [nico-launch](../crates/nico-launch/src/lib.rs) | CLI, logging, diagnostics, and host setup |
| [nico-ops](../crates/nico-ops/src/lib.rs) | Host control, bounded commands, snapshots, and optional bridge transport |
| [nico-net](../crates/nico-net/src/lib.rs) | Bounded game TCP transport, without gameplay or runtime dependencies |
| [apps/nico-mcp-bridge](../apps/nico-mcp-bridge/src/main.rs) | MCP entry point for independently launched games |
| [apps/nico-shaderc](../apps/nico-shaderc/src/main.rs) | Offline shader compilation |

## Runtime and ECS

Rust plugins register state and systems through `AppBuilder`.
Hosts drive Startup, FixedUpdate, Update, and Shutdown; runtime owns no permanent loop.
ECS holds simulation state, not windows or GPU resources.

Successful structural commands and event writes become visible before the next system.
Failed systems discard those queued writes.
Direct component and resource changes are not rolled back.
Events have independent readers, bounded history, and missed-event counts.

Services exchange owned requests and results through bounded queues.
Workers never receive the world.
Runtime publishes results at owned boundaries and rejects stale entity targets.
Cancellation, overload, failure, and shutdown must have explicit outcomes.
Shutdown closes registered channels and finalizes pending work.

## Native host and input

Winit owns windows, native callbacks, device input, and client lifecycle.
Suspension pauses ticks and resets frame timing.
Focus loss releases held input.
Desktop minimization and OS suspension require separate checks.

Games map physical input into semantic commands.
Shared gameplay has no physical key bindings.
Held movement survives catch-up ticks; action edges are consumed once.
Games own bindings, camera tuning, pointer policy, and input cancellation rules.

Only the native event thread calls window APIs.
Window requests wake that thread and work independently of redraws or game ticks.
Applied means the platform call returned; observed state confirms its effect.
Teardown cancels pending work.
Capture requires enabled policy, focus, and an active window; release remains allowed while inactive.

## Multiplayer world ownership

Default world mode uses one authoritative server.
The `--arena` option runs independent solo simulations on each host.
Game networking is separate from MCP.
The engine owns TCP framing and bounded I/O; games own protocol and session rules.

Servers own movement validation, combat, monsters, loot, quests, and saved progress.
Clients predict local movement and interpolate remote state.
Transport submission never proves server success.
Stable network and asset IDs remain separate from generational ECS IDs.

Current networking is loopback-only; character names are development identities, not authentication.
One server owns each save directory.
Failed loads must not overwrite records; failed saves retain state for retry.
Orderly shutdown saves state before closing connections.
Resource limits do not prove measured player capacity.

## Presentation and graphics

Presentation reads gameplay immutably and publishes owned scene snapshots.
Renderers receive snapshots, never world access.
Scene references retain CPU assets until released.
Games own visual layout and animation choices.
Arena maps action progress to animation time using authoritative action ticks.
Dodge bindings may set a clip-relative `end_seconds` to exclude settling motion after the recovered pose.
Bindings without this endpoint use the full clip. Clip loading rejects endpoints beyond the source duration.

Rendering owns pipelines, uploads, draw order, submission, and presentation.
RHI defines GPU contracts; providers own native resources and recovery.
Invalid frame data must fail before submission.
Recoverable surface outcomes remain distinct from fatal errors.
Presentation API success proves neither GPU completion nor display scanout.

Mesh, material, and image uploads remain while CPU sources have owners, including offscreen assets.
Canvas textures retire when unused by the drawn frame.
Recorded GPU work retains required resources after public wrappers drop.
Surface format changes rebuild pipelines.

Opaque and masked geometry precede blended draws; UI follows world rendering.
Transparency sorting is per draw, so intersecting surfaces can still show ordering errors.
Cameras use unit quaternions and zero-to-one depth.
Detailed material and shader rules belong in [rendering code](../crates/nico-render/src/meshes.rs).

### Instancing and streaming

Immutable batches own placement data, bounds, IDs, and shared prototypes.
GPU serialization assembles fixed float arrays and copies whole records on little-endian hosts.
Big-endian hosts retain explicit conversion; full and compact shader layouts stay unchanged.
Current instancing supports opaque or masked static meshes.
Culling stays conservative and uses maximum foliage deformation bounds.
GPU compaction can change tied-depth draw order.

GPU cache keys become valid only after submission.
Allocation failure must preserve live residents.
Reusing shared GPU ranges must not overwrite live neighbors.
Residency, upload pacing, and CPU copies have separate budgets.

Presentation-control owns bounded streaming workers and publication.
Each update applies evictions, collects finished jobs, then fills available worker slots.
Game providers decode placement data without changing simulation state.
Owner and generation checks reject stale results.
Cancellation does not release worker capacity before thread exit.
Generic owner drop may detach unfinished workers; explicit shutdown confirms exit.
Arena's bounded chunk providers instead cancel and join on replacement or destruction.

Visual field changes apply atomically at the render boundary.
Failed requests retain previous fields.
GPU observations carry their own view identity and age.
Readback failure affects diagnostics only; separately sampled values are not one frame.

## Assets and game construction

Games own shared, client, and server packages.
Authoritative assets belong in assets/logic; client-only content belongs in assets/presentation.
Logic assets never depend on presentation assets.
Games own combat rules, character bindings, equipment, and content licenses.
The optional `nico-assets` definition contract parses bounded TOML, then calls the explicit `DefinitionValidation` implementation.
Its derive provides common parse and load methods. Games keep their own schema rules and extra loading checks.

Handles identify assets; leases and scene references retain them.
Importers own format rules and settings.
They receive bounded bytes and cooperative cancellation, never world or GPU access.
User import code is trusted and cannot be forcibly interrupted.
Asset workers publish through runtime-owned boundaries.

Native file and embedded texture loads reuse persistent `ImportCache` data in every build profile.
Sources, importer versions, settings, and budgets determine whether cached data can be reused.
The cache sits beside the nearest project manifest, above `assets`, or beside a standalone source.
Embedded bytes without an owner file import directly.
Asset batches own up to four CPU workers and 1024 jobs. Results retain input order when collected.
Polling does not block. Cancellation is cooperative; dropping a batch cancels queued work and joins workers.
Workers return owned values. GPU work and runtime publication stay on their owning threads.
Arena starts one dependency graph from `nico.project.toml`, using `default_scene` and its character catalog.
Project asset references stay under the fixed `assets` folder.
Content hashes include the selected scene path, manifest, and `assets` folder.
Snapshots include the manifest and project assets.
Content hashes and snapshots skip generated `.nico` cache directories, including caches inside `assets` from older layouts.
Content hashing and snapshots have no total byte limit. Both stream file bytes through a 64 KiB buffer.
`assets/logic` holds authoritative content; `assets/presentation` holds client content.
Game readers resolve definition references; engine workers load unique GLB and PNG sources and their embedded textures.
Traversal rejects cycles and missing files before loading. One fixed progress total counts source files, including their embedded dependencies.
The synchronous graph call waits; its asynchronous form supports polling and cancellation of child workers.
Project `[authoring]` metadata selects the retained adapter. It replaces the removed `[editor]` section.
Worker cache counts return through the caller's progress scope.
Texture batches reserve their total RGBA budget before decoding and share identical images.
Runtime asset stores keep their existing worker and update-boundary publication.
Persistent import storage and GPU resource reuse are separate.

### Authored scenes

`nico-scene` owns `SceneDefinition`, entity identities, component registration, and prepared scene creation.
Each entity contains named, typed components. Registrations declare shared, client, or server ownership.
Preparation checks schemas, component values, asset paths, and entity references before creating any runtime entities.
Hosts instantiate supported components at startup. Shutdown removes only entities owned by that scene.
Servers validate presentation references without importing meshes or creating camera and light components.

Arena loads its selected scene through `ProjectContent` on both hosts.
Scene components reference reusable world rules, character catalogs, item definitions, and a scenery library.
Transforms, collider boxes, spawns, quest markers, camera controls, and lights belong to scene entities.
The game compiles authoritative components into its existing zone snapshot for networking and simulation.
The client reads camera and light components when building its presentation state.
The retained authoring adapter edits and saves this same scene.

Arena gameplay scenes require one orbit camera, one directional light, and one ambient light.
Actor spawns and quest markers are planar. Collider boxes remain axis-aligned.
Scenery supports Y rotation and authored height. Unsupported transforms fail validation.
Solo scenes retain the fixed arena bounds and three initial spawn kinds required by the wave rules.
Scene components configure startup; live component edits do not automatically rebuild game resources.

The default splash scene contains a client `arena.splash` component with title, duration, and target scene.
It waits one second of active runtime time before starting Meadow preparation.
`nico-assets::Batch` owns the worker. Progress observers publish owned counts through shared state.
Workers prepare owned CPU data; runtime composition and network setup happen on the window thread.
Asset cancellation is cooperative. Closing the host joins remaining work after its next cancellation check.
Import completion changes the label to preparation while character and scenery data finish.
Loading failures keep the splash visible. No partial gameplay runtime starts.

`nico-winit::NativeScene` keeps the same window and renderer when changing runtimes.
A host frame boundary shuts down the old runtime, installs the new runtime, and resets input state.
`nico-launch::ClientHost::run_scenes` replaces bridge registration with the new tools and loaded content revision.
Bridge reconnects use new instance IDs. Host frame counts remain continuous across scene changes.
Host readiness can describe a rendered splash. `scene_loading` reports gameplay asset loading separately.
Its loaded phase does not prove server connection, GPU completion, or desktop visibility.
Headless hosts and the retained world authoring adapter follow the splash target without showing it.
Splash targets must be gameplay scenes; self-references and splash chains are rejected.

Offline Slang compilation produces checked-in WGSL outside Cargo's build graph.
Hosts load shaders and prepare pipelines before their first redraw.
This startup path is separate from runtime asset services.

Animation owns CPU poses, playback, attachments, and retargeting without runtime or renderer dependencies.
Actors share immutable assets but own playback state.
Game snapshots choose actions; visual timing never changes authoritative damage or movement.
Bounds describe displayed poses, include attachments separately, and keep invalid cases visible.

## Physics

Physics hides Rapier types behind owned descriptors and local body IDs.
Its optional runtime adapter runs at FixedUpdate.
Intent producers run before physics; result consumers run afterward.
Entity removal releases the matching body and collider.

Queries must see current geometry without consuming pending dynamics changes.
Contacts describe touching pairs, not game damage.
Games own movement policy, collision rules, and hit tests.
Humanoid definitions use upright capsules with total height and radius. Their lower ends rest on the ground plane.
World authority, client prediction, and solo Arena share collider dimensions and floor movement checks.
Planar movement is swept again after sliding to prevent capsule overlap while preserving tangent movement.
Attack sectors intersect circular capsule footprints, including the arc and both finite side edges.
The initial hurtbox shares movement dimensions. Combat has no vertical hit test or animated sword collision.
The world debug overlay draws these capsules and the unchanged attack sectors from client presentation state.
Solo Arena owns its physics world directly for shared headless and runtime stepping.
Single-threaded stepping and enhanced determinism do not prove cross-platform repeatability.

## AI operation requirements

Codex connects only to nico-mcp-bridge.
Users launch games independently; the bridge never launches or owns them.
Bridge and Codex disconnect never stop games.
Game reconnects preserve gameplay and receive new instance IDs.
Retry diagnostics use trace-level logging.

Each MCP connection owns a stdio frontend. A shared daemon owns game registrations, cached catalogs, and command routing.
Both implementations live in `nico-ops`; the executable only selects the mode and configuration.
Frontends connect through separate MCP sessions on a discovered loopback endpoint.
An endpoint token checks local discovery. This is not a remote access boundary.
The state directory belongs to the local user. Do not share it with untrusted users.
An OS-held startup lock prevents concurrent frontend launches from starting duplicate daemons.
A second lock protects daemon ownership for its full lifetime. Stale endpoint files are checked before use.
Windows startup requests process-job breakaway and hides the console. A CIM process broker handles jobs that forbid breakaway.
Unix startup uses a separate process group and detached standard streams; service-manager process policies still apply.
Frontends never stop an existing process to free a port.
Each daemon accepts up to 32 frontend sessions and 32 game connections.
Readiness and MCP initialization have timeouts. Endpoint files are limited to 4 KiB.
The daemon exits after 30 idle seconds only when no frontend or game connection remains.
Frontend loss preserves the daemon and game IDs. Daemon loss triggers frontend startup coordination and game reconnection.
The frontend reports lost calls as uncertain outcomes. It never replays them after reconnecting.
Tool-list changes reach each frontend separately. Mutating the same game still requires coordination between users.

Engine hosts own transport, service threads, and lifecycle tools.
Keep built-in status, stop, and native diagnostics names.
Games register extra tools without replacing built-ins.
Handlers read owned snapshots or queue bounded work.
Simulation changes occur only at runtime-owned boundaries.

Discover live instances before calling tools.
Each instance keeps its own catalog and API version.
Cached schemas do not prove availability.
Use `list_game_tools` and `call_game_tool` when dynamic tool refresh is unavailable.
Timed-out mutations may have run; never retry them blindly.

Connection, activity, readiness, snapshot age, and session progress remain separate.
Stop acceptance proves neither completed shutdown nor process exit.
Pending commands need final outcomes during shutdown.
Snapshot reads never refresh publication time.
Bridge adapters retain control handles across reconnects.
Blocking startup or game systems can delay orderly stop.

Local MCP enforces separate inspection, capture, mutation, and stop permissions.
Release hosts require explicit debug opt-in; local release MCP remains inspection-only.
The editor RPC endpoint, session workers, credential grants, and capture downloader have been removed.
Bridge protocol version 3 removes editor call origins; older hosts and bridges must be rebuilt together.
Window capture still uses host snapshot controls, native GPU readback, and MCP PNG tools.

Launch owns bounded diagnostics and capture encoding.
Diagnostic cursors are process-local; history survives reconnects, not process exit.
Truncation and eviction remain visible.
Window capture runs through native rendering; PNG encoding runs off the bridge thread.
Capture failure does not change presentation success.
Shutdown fails pending captures and joins active encoding work.

Visual checks must identify the exact client and PID.
Inspect captures while actions run and keep separate state samples clearly labeled.
User-watched success requires user confirmation.
Announce when automated control starts and stops.

## Measurement and extension rules

Measure suspected bottlenecks before optimizing.
Keep elapsed time, CPU execution, GPU execution, and display timing distinct.
Preserve authoritative results for the same inputs and ticks.

XRay is planned but neither integrated nor validated.
Profiling implementation and compatibility work remain deferred.
Do not add profiler code, per-method instrumentation, collectors, viewers, or toolchain changes now.

Add contracts only for real consumers and clear ownership boundaries.
Keep failure and shutdown rules with the owning API.
The editor and Play launcher were removed; scene and authoring libraries remain.
Saved content reaches CLI hosts after restart.
Current features and open work belong in README, roadmap, and TODO.
