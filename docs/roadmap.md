# Nico core roadmap

This roadmap owns phase goals, scope, status, completion criteria, and validation
evidence. Concrete next actions belong only in [TODO.md](../TODO.md); ownership and
contracts belong in [architecture.md](architecture.md).

## Final goal

Build a Rust game engine that supports both 2D and 3D games and can support a complete,
shippable client/server game. Both dimensions have focused rendering samples; a complete game remains planned. Game
rules run in a shared headless core. The client displays the game and accepts player
input. AI tools can inspect, test, and explicitly stop independently launched clients
and servers through a separate MCP bridge.

Use one small **reference game** to prove the engine works from development to release.
The game is the test case for engine capabilities. Its genre, initial 2D/3D scope,
player count, and target platforms still need to be defined. Choosing a first sample
does not narrow the engine's support to one dimension; focused samples must also
validate the other dimension.

## The path at a glance

| Phase | Capability gained | Status |
| --- | --- | --- |
| 0. Run game logic | Simulate game state without a window | Implemented |
| 1. Run a native client | Open a window, receive input, and draw | Core implemented; validation remains |
| 2. Control clients and servers with AI | Discover independent games, inspect readiness, call game tools, and stop | Implemented; Windows end-to-end verified |
| 3. Load game assets | Request usable game content by asset identity | PNG and mesh-only GLB paths implemented |
| 4. Display the game world | Show loaded content moving with game state | 2D and 3D samples with shared HUD implemented |
| 5. Make a playable local game | Move, collide, complete an objective, and restart | Planned |
| 6. Play over a network | Two clients play together on one authoritative server | Planned |
| 7. Complete the player experience | Add the required visuals, sound, menus, and settings | Planned |
| 8. Make development repeatable | Rebuild content, inspect state, and automate playtests | Planned |
| 9. Validate performance | Profile representative workloads and meet defined budgets | Deferred |
| 10. Ship the game | Run release packages on supported machines | Planned |

Work proceeds in this order by default. Platform lifecycle validation can continue
alongside later phases. Basic UI, importing, and automation appear when first needed;
phases 7 and 8 complete those workflows. Measurement and regression testing apply
throughout, even while deep profiling remains deferred. Phase 10 packaging work may
proceed while phase 9 is deferred; final release acceptance still requires the agreed
performance budgets to be validated.

## 0. Run game logic

**Result:** Shared Rust gameplay runs without graphics or a native window.

**Already implemented:**

- ECS entities, components, and resources hold game state.
- The runtime starts the game, executes ordered systems and fixed simulation
  steps, and shuts down.
- Typed events carry messages between systems.
- Typed services accept background work and return results to the runtime thread.
- Hosts supply time, allowing repeatable headless tests.

**Evidence:** Existing regression tests cover known command/tick sequences and startup,
failure, and shutdown behavior. Later phases must preserve this foundation.

## 1. Run a native client

**Result:** A native window receives player input and renders successfully.

**Already implemented:** Winit hosting, normalized input, the GPU abstraction and wgpu
backend, bootstrap triangle rendering, and offline shader compilation.

**Status:** Core implemented; platform lifecycle validation remains. The concrete checks
are tracked in [TODO: native host validation](../TODO.md#native-host-validation).

**Done when:** The lifecycle checks pass on the recorded environments. The current smoke
counter counts client-session frames, so successful rendering must also be observed.
This phase's triangle still does not display gameplay state.

## 2. Control clients and servers with AI

**Result:** AI tools discover independently started hosts, inspect their state, retrieve
diagnostics, invoke game operations, and request orderly shutdown.

**Status:** Implemented; the operation baseline and extended Windows smoke scenario are
verified. Automatic tool exposure was not observed in the tested Codex session; the
fixed discovery/invocation fallback works. This is a session-specific result, not a
claim about all MCP clients.

**Scope delivered:** A separate bridge discovers uploaded host/game tool catalogs,
routes calls by connection instance ID, and caches schemas for its process lifetime.
Native hosts provide status, explicit stop, bounded diagnostics, and client graphics
reports. The minimal game supplies an owned `game_state` snapshot. Games reconnect
without restarting gameplay, and bridge disconnect never requests host shutdown.

**Done when:** An automated MCP scenario discovers independently started hosts, observes
readiness, retrieves diagnostics, invokes game tools, and verifies explicit shutdown.
Game and bridge restart scenarios preserve the independent lifecycle.

### Validation evidence (2026-09-11)

| Check | Observed result |
| --- | --- |
| Workspace checks | Tests, Clippy, formatting, and dependency-free/optional-feature checks passed. |
| Protocol and process tests | Registration, schema replacement, malformed input, bounds, overload, timeouts, heartbeat expiry, old instance IDs, and bridge restart behavior passed. |
| Native smoke | Windows/Vulkan, NVIDIA GeForce GTX 1660: both hosts became ready, game calls succeeded, independent restarts/stops passed, and final status was retained. Six catalog-change notifications were received. |
| Codex tool discovery | Starting from five fixed tools and no registrations, server tools appeared in `list_game_tools` but not the session's callable catalog, including after an additional 15-second wait. Fallback status and game calls succeeded. The Codex UI itself was not inspected. |
| Live server diagnostics | Three startup events with typed fields; exclusive pagination, empty final page, invalid-limit rejection, and disconnected-instance errors passed. |
| Live client diagnostics | Seven events with device/backend and 1920x1080 window fields; pagination, empty-page polling, invalid-limit rejection, and game-state retrieval passed. The server was disconnected during this separate check. |
| Live client graphics | Routed status showed presentations advancing from 1,057 to 1,557 with `presented`, readiness true, and no failure. The older running bridge omitted graphics from cached instance status; routed status returned the new fields. |
| Extended native smoke | Diagnostics on both roles, identical retained history after bridge restart, updated bridge graphics snapshots before/after shutdown, independent server restart, and final status passed. Six catalog-change notifications were received. |

The Vulkan loader reported the existing OBS hook API-version warning. Test processes
were cleaned up; live checks left user-owned games running and issued no stop calls.
Eviction, truncation, skipped frames, graphics failures, and retained terminal reports
are covered by regression tests; the live checks do not establish every failure path.
Interactive platform lifecycle coverage remains separate under phase 1.

Usage and current schemas are described in
[README](../README.md#agent-access-through-mcp). Ownership belongs in
[architecture](architecture.md#ai-operation-requirements); protocol limits and
reconciliation rules belong in the [bridge contract](plans/2026-09-11-mcp-bridge.md).
The earlier supervisor proposal is superseded. Additional game commands and persistent
schema caching are conditional extensions tracked in TODO; profiling remains deferred.

## 3. Load game assets

**Result:** Game code requests an asset and receives usable content or a clear error.

**Status:** The first texture path is implemented through `nico-assets`' optional
`loading` feature. A headless consumer loads the checked-in sample PNG, consumes CPU
pixels, releases ownership, and shuts down. Mesh-only GLB loading now shares the
same typed asset-store lifecycle; see the [mesh design](plans/2026-09-14-mesh-assets.md).

**Scope:** A typed runtime asset with explicit loading, ready, and failed states, handle
resolution, ownership, and release behavior. Background byte loading uses the service
boundary; publication belongs to the runtime thread. Repeated requests, invalid files,
cancellation, and shutdown have defined outcomes. Offline conversion and dependency
metadata are limited to what the selected asset requires.

**Selected design (2026-09-14):** Load and decode shipping PNG files directly in the
background. Copyable handles remain identity; owning reference-counted leases retain
content, with release reconciled at runtime boundaries. The
[texture asset design](plans/2026-09-14-texture-assets.md) records lifecycle rules and
implementation bounds. No intermediate raw-pixel shipping format is introduced.

**Done when:** A sample loads a real game asset through this path, consumes it, and
releases it. Failure tests produce explicit results without publishing incomplete
content. Shipping content can load without its authoring source files.

**Handoff to phase 4:** A loaded asset is available to the renderer. Asset loading alone
does not yet put an object on screen.

**Validation evidence (2026-09-14, Windows):** The workspace all-feature tests passed
using `target/texture-validation`, including 14 new texture lifecycle/decoder tests.
The headless `load_texture` example loaded the checked-in 2x2 PNG (16 RGBA8 bytes),
consumed it, verified release, and joined the worker. Tests cover shared leases,
publication boundaries, cancellation/reacquisition, explicit retry, bounded requests,
backend loss, snapshot retention, shutdown, PNG variants, malformed input, and limits.
Workspace all-feature/all-target Clippy passed with warnings denied; formatting and
documentation link targets passed checks. The default identity-only feature
configuration also type-checks. This headless validation did not exercise GPU upload
or visual sample behavior; phase 4 records rendering evidence. Non-Windows loader
execution remains unverified.

## 4. Display the game world

**Result:** The screen reflects actual game entities and their positions.

**Scope:** Spatial conventions and a camera connect shared entity state to loaded
visuals through the immutable presentation boundary. Geometry, textures, and shaders
support the selected reference-game content. Entity creation, movement, removal, missing
assets, and rendering-resource lifetime have defined visible behavior.

**Selected approach:** Build paired samples sequentially. First, a 2D sample displays
one movable world sprite and one fixed HUD icon using the same texture. Then a 3D
sample displays one textured mesh and reuses the HUD. Shared 2D drawing functionality
serves world sprites and HUD elements, with world-camera and viewport transforms
respectively. UI layout and interaction can extend that foundation when needed.
Both samples are implemented. These samples do not
require choosing the full reference game's genre first.

**2D scope delivered (2026-09-14):** Shared quad drawing consumes immutable world/HUD
snapshots, uploads sRGB RGBA8 textures, blends straight alpha, and uses camera-relative
world or logical viewport coordinates. The minimal client displays a loaded sprite
following shared entity positions and a fixed HUD icon; both share the texture and
pipeline. Missing content uses a fallback. Game-owned MCP tools expose owned sample
state and bounded runtime-boundary edits. UI layout, text, panel clipping, and quad rotation remain outside this slice.

**GPU evidence (2026-09-14, Windows/Vulkan, NVIDIA GTX 1660, driver 591.86):** The
opt-in offscreen readback test passed orientation, transparency, half-alpha HUD
layering, camera movement with fixed HUD placement, removal, fallback, and resource
release after submission. This validates rendered pixels on this adapter, not display
scanout or platform window lifecycle. Portable regression tests cover extraction from
shared movement, entity creation/removal, command boundaries, asset failure/release,
and invalid geometry. Broader platform validation remains in phase 1.

**Native evidence (same Windows/Vulkan environment):** Live bridge calls observed CPU
texture readiness, applied position/camera edits, hidden sprites with the HUD retained,
texture release and reload, and successful presentation counts advancing from 19,151
to 21,469 without a reported render failure. MCP stop was accepted and the test process
exited successfully. The extended native smoke also passed the sample edits plus its
existing independent lifecycle/restart checks. Only the existing OBS Vulkan hook
API-version warning appeared in retrieved diagnostics. These checks do not establish
interactive resize/minimize behavior or physical display scanout.

Workspace all-feature tests, all-target Clippy with warnings denied, formatting,
generated-shader checks, and relative documentation link checks passed. Final client
tests and the isolated native smoke passed after adding bounded per-command outcome
history. The renderer dependency tree contains no runtime/provider dependencies when
built on its own, and presentation contracts type-check without their runtime feature.

**3D scope and evidence (2026-09-14):** The client selects `--sample 2d|3d`.
A mesh-only GLB cube follows the same shared positions, with perspective, depth testing,
and an unlit textured alpha-cutoff material. The HUD reuses the quad renderer and GPU
texture cache. Typed texture and mesh stores share loading, lease, retry, and shutdown
logic. Tests cover malformed GLB/accessor bounds and concurrent store result isolation.
On Windows/Vulkan, NVIDIA GTX 1660, driver 591.86, both offscreen GPU tests passed,
including depth occlusion, perspective size, camera movement with fixed HUD, removal,
and resource lifetime. Both native smoke modes passed structured sample controls and
independent host lifecycle checks. These observations do not validate other platforms,
window transitions, or display scanout. Full glTF scenes, imported materials, lighting,
skinning, animation, and general transparent mesh sorting are deferred. The
[mesh design](plans/2026-09-14-mesh-assets.md) defines the implemented subset.

**Window snapshot evidence (2026-09-14, same Windows/Vulkan environment):** The new
client-host `window_snapshot` tool captured two 1920x1080 PNGs through live bridge MCP.
Visual inspection showed the loaded cube, perspective, alpha-cutout texture, and fixed
HUD. A yaw edit changed mesh pixels while the HUD region remained byte-identical.
Invalid and expired IDs were rejected, presentations continued without a reported
failure, and the test client exited after MCP stop. An opt-in GPU regression passed
13-pixel-wide padded-row readback and BGRA conversion. Updated native smoke tests
passed PNG capture/retrieval in both 2D and 3D, alongside their existing lifecycle
checks. This validates rendered window
content, not desktop borders, physical scanout, or other platforms.

**Opaque checker validation (2026-09-14, Windows/Vulkan, GTX 1660):** The cube now
loads a separate opaque 128x128 A1¨CD4 checker; the HUD retains the transparent fixture.
MCP captures of all six faces showed readable labels and the expected corner order.
An independent ray/UV comparison of 11,031 sampled mesh pixels had no mismatches
above one channel value; the HUD region was byte-identical across all six captures.
Client regressions, workspace all-target/all-feature Clippy, and the updated 3D native
smoke passed, including release/reload of both textures. This replaces the earlier
cutout cube as the default sample; alpha-cutoff shader support is unchanged.

**Review fixes (2026-09-14):** PNG encoding/file writes now run on one bounded
background worker, leaving bridge polling and heartbeats responsive. Gated-worker
regressions cover pending polls, overlap rejection, retained failures, and joined
shutdown. MCP and rendering share validation of stored f32 camera directions; the
`x=0.0001,y=0,z=0` boundary is rejected before application. Texture retry targets only
failed entries, with regressions for either or both textures failing. Workspace
all-feature tests, all-target Clippy, and both native smoke modes passed. The Windows
3D smoke also verifies camera rejection leaves the host running and status calls work
while polling snapshot completion.

**Done when:** Input moves a game entity and its loaded visual moves with it.
Creating/removing entities updates the screen correctly. The same game logic continues
to run headlessly without presentation assets. Focused 2D and 3D samples demonstrate
these behaviors; the reference game may use either or both.

## 5. Make a playable local game

**Result:** A player can complete a small game loop on one machine.

**Scope:** One reference scenario has a starting state, player actions, an objective,
success/failure, and restart. Shared gameplay owns movement, interactions, and the
required collision/physics behavior at simulation boundaries. Feedback and basic UI make
the scenario playable; automation uses the same semantic commands.

**Done when:** A person can start, play, win or lose, and restart. A headless test can
exercise the same rules and check movement, collision, outcomes, and reset.

**Dependency for phase 6:** The chosen physics implementation's repeatability limits are
known; fixed simulation steps alone do not prove cross-platform determinism.

## 6. Play over a network

**Result:** At least two clients play the reference scenario on one server that owns the
authoritative game state.

**Scope:** Player/session identity, replicated objects, validated commands, and
initial/incremental state exchange support the chosen synchronization model.
Interpolation, prediction, and correction are included only where that model needs them.
Version compatibility, disconnects, and rejoining have explicit policies. An automated
multi-client scenario provides integration evidence.

**Done when:** Two clients complete the scenario and agree on server-owned outcomes.
Tests cover join/leave, server shutdown, invalid inputs, and simulated adverse network
conditions. Queues and request processing remain bounded.

## 7. Complete the player experience

**Result:** The reference game has a complete player journey from launch to exit.

**Scope:** A finite set of visual, animation, lighting, effects, and audio capabilities
supports the reference game. Menus, HUD, loading/error states, and connection feedback
cover the player journey. Settings, input devices/rebinding, localization, and
accessibility follow the agreed requirements. Relevant settings and actions remain
accessible to automation.

**Done when:** A player can configure the game, enter a session, understand its state,
finish, and exit. Required visual/audio and accessibility checks pass; restart,
disconnect, and focus loss leave input, sound, and UI in a valid state.

## 8. Make development repeatable

**Result:** Developers and AI tools can reliably change content and test the game.

**Scope:** Reproducible imports, dependency diagnostics, selective rebuilds, and useful
content reload form a documented authoring workflow. Reload failures have defined
behavior, and caching requires measurement. Owned state inspection, controlled game
actions, and automated content/local/multiplayer tests produce structured results and
clean up test processes. Fresh-checkout prerequisites and commands are documented.

**Done when:** A fresh checkout can build its content and run the reference tests. A
content change reaches the running game through a documented workflow, and failures
identify the affected content or operation.

A visual editor or scene format is a separate decision based on actual authoring needs.
Engine tools grow from the workflows above.

## 9. Validate performance

**Result:** Performance is measured against explicit targets on known hardware.

**Status:** Deferred. XRay is the future direction and has not been integrated or
validated. This phase does not start profiling work now. The reported startup delay
remains unmeasured.

**Scope when resumed:** Representative workloads and target hardware determine startup,
frame, tick, loading, and memory budgets. Experimental Rust/LLVM XRay is the intended
route to automatic function capture, nested calls, inclusive/self time, invocation
counts, and frame/thread context without per-method annotations. Compatibility and
capture coverage remain unverified. Controlled capture/export and AI retrieval must
disclose overhead and incomplete results. Optimization is supported by equivalent
before/after measurements.

**Done when:** Supported captures give interpretable results and representative
workloads meet the agreed budgets. Optimizations preserve gameplay results. Elapsed
duration, actual CPU execution, and GPU execution are reported distinctly. If XRay
proves unsuitable, record the evidence and revisit the profiling decision.

No profiler code, per-method instrumentation, custom collector/viewer, toolchain
configuration, or compatibility investigation is required now.

## 10. Ship the game

**Result:** Versioned client/server release packages run on supported machines.

**Scope:** A supported platform/hardware matrix and acceptance criteria govern versioned
packages, reproducible builds, and clean-machine validation. Runtime assets have no
developer-local path dependencies; persistence and migration are included if the game
needs them. Long sessions, repeated joins/restarts, updates, and failure recovery meet
release expectations. Distribution includes licenses, player/server instructions, and
known limitations.

**Done when:** Release packages pass the complete local and multiplayer scenarios on
supported targets, satisfy the agreed performance and reliability criteria, and are
distributed through the chosen channel. Builds are reproducible from documented inputs,
and AI operations remain usable for their intended workflows.

## Rules shared by every phase

- Keep game rules headless and reusable between client and server. Presentation
  reads game state; background services and tooling never mutate it directly.
- Keep host control and MCP lifecycle in engine crates. Games compose those
  capabilities and may add game-specific tools.
- Keep authoritative logic assets independent of client-only presentation assets.
- Test new behavior, failure, shutdown, and timing paths. Record visual/platform
  validation separately from automated tests.
- Measure before optimizing. Deep profiling remains deferred as described above.
- Define new APIs and crates when real consumers need them. Keep detailed ownership
  rules in [architecture.md](architecture.md) and implementation tasks in TODO.

## Phase dependencies

Decisions below constrain phase scope. Schedule their concrete resolution in TODO when
the dependent phase approaches.

| When | Decision | Why it matters |
| --- | --- | --- |
| Phase 3 entry (selected) | Paired samples: texture loading, 2D sprite and HUD first, then a textured 3D mesh | Establishes the first consumers and implementation order for both dimensions |
| Before expanding beyond paired samples | Reference game: core mechanic, platforms, content scale | Bounds further content and rendering requirements |
| Before phase 5 | Exact playable scenario and collision/physics needs | Gives gameplay a concrete completion test |
| Before phase 6 | Player count, network conditions, synchronization model | Determines replication and latency handling |
| Before phase 7 | Required media features, devices, languages, accessibility cases | Makes the player-experience scope finite |
| Before phase 9 | Target hardware and measurable performance budgets | Makes performance acceptance testable |
| Before phase 10 | Distribution channel and final support matrix | Determines packaging and release validation |
