# Nico core roadmap

This roadmap owns phase goals, scope, status, completion criteria, and validation
evidence. Concrete next actions belong only in [TODO.md](../TODO.md); ownership and
contracts belong in [architecture.md](architecture.md).

## Final goal

Build a Rust game engine that supports both 2D and 3D games and can support a complete,
shippable client/server game. Both dimensions have focused rendering samples and the
local arena prototype is implemented; a shippable release remains planned. Game rules run
in a shared headless core. The client displays the game and accepts player
input. AI tools can inspect, test, and explicitly stop independently launched clients
and servers through a separate MCP bridge.

Use one small **reference game** to prove the engine works from development to release.
The game is the test case for engine capabilities. Its initial scope is a third-person
arena ARPG, with platform, player-count, and content targets described
in phase 5. The reference game's scope does not narrow
the engine's support to one dimension; focused samples validate both dimensions.

## The path at a glance

| Phase | Capability gained | Status |
| --- | --- | --- |
| 0. Run game logic | Simulate game state without a window | Implemented |
| 1. Run a native client | Open a window, receive input, and draw | Core implemented; validation remains |
| 2. Control clients and servers with AI | Discover independent games, inspect readiness, call game tools, and stop | Implemented; Windows end-to-end verified |
| 3. Load game assets | Request usable game content by asset identity | PNG and mesh-only GLB paths implemented |
| 4. Display the game world | Show loaded content moving with game state | 2D and 3D samples with shared HUD implemented |
| 5. Make a playable local game | Move, collide, complete an objective, and restart | Native arena approved in initial playtest; detailed balance evidence remains |
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

**Windows validation (2026-09-15, Vulkan, NVIDIA GeForce GTX 1660):** Engine-owned
MCP window controls drove logical sizes 800x600, 1100x700, 640x480, and 1280x720,
maximize/restore, and minimize/restore in a test-owned arena client. Observed window
state confirmed each transition. Each visible transition was followed by at least
ten additional successful presentations, observed over 0.31–0.35 seconds in the
recorded run. This is a polling interval including MCP overhead, not individual frame
timing or GPU execution time. Minimize reported zero extent, lost focus, released
pointer capture, and cancelled a queued gameplay movement lease with `focus_lost`.
Restore resumed rendering, and both hosts exited successfully after `stop`.
That run recorded 1,846 successful presentations and no host failure; diagnostics
contained the existing OBS Vulkan hook warning. Desktop minimization does not prove
Winit suspension behavior. Physical held-key release, close-button interaction,
individual frame timing, injected GPU failures, and macOS remain unverified.
The latest full arena regression is recorded under [phase 5](#validation-evidence).

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

**Status:** PNG and mesh-only GLB loading are implemented through `nico-assets`'
optional `loading` feature. Both use the same typed asset-store lifecycle. The headless
texture example and native samples consume shipping assets; see the
[texture design](plans/2026-09-14-texture-assets.md) and
[mesh design](plans/2026-09-14-mesh-assets.md).

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
using `target/texture-validation`, with texture lifecycle/decoder and mesh validation coverage.
The headless `load_texture` example loaded the checked-in 2x2 PNG (16 RGBA8 bytes),
consumed it, verified release, and joined the worker. Tests cover shared leases,
publication boundaries, cancellation/reacquisition, explicit retry, bounded requests,
backend loss, snapshot retention, shutdown, PNG variants, malformed input, and limits.
Workspace all-feature/all-target Clippy passed with warnings denied; formatting and
documentation link targets passed checks. The default CPU asset configuration also type-checks without runtime dependencies. This headless validation did not exercise GPU upload
or visual sample behavior; phase 4 records rendering evidence. Non-Windows loader
execution remains unverified.

## 4. Display the game world

**Result:** The screen reflects actual game entities and their positions.

**Status:** Paired 2D and 3D rendering samples are implemented. They establish the
rendering foundation; the reference game's initial scope is selected in phase 5,
with scenario rules recorded in its design. Concrete next actions belong in
[TODO](../TODO.md#next-reference-game-balance-evidence).

**Scope delivered:** Both samples follow shared entity positions through immutable
presentation snapshots. The 2D world sprite and fixed HUD icon share one transparent
PNG and quad pipeline. The 3D sample draws a mesh-only GLB cube with perspective,
depth testing, and an opaque labeled UV checker, then draws the same transparent HUD.
The mesh and HUD paths share texture-loading and GPU-cache functionality. Missing
assets use fallbacks. MCP exposes owned sample state, bounded edits with per-command
outcomes, and window-content PNG capture using background encoding.

UI layout, text, panel clipping, quad rotation, lighting, imported glTF materials/scenes,
skinning, animation, and sorted transparent meshes remain outside this scope. The
mesh shader supports alpha cutoff; the current cube fixture is opaque to make its
geometry and UV orientation easier to inspect. Usage belongs in
[README](../README.md#2d-world-and-hud-sample), and ownership in
[architecture](architecture.md#presentation-and-graphics).

**Done when:** Input moves a game entity and its loaded visual moves with it.
Creating/removing entities updates the screen correctly. Shared game logic continues
to run headlessly without presentation assets. Focused 2D and 3D samples demonstrate
these behaviors; the reference game may use either or both.

### Validation evidence (2026-09-14)

Native and GPU observations below used Windows/Vulkan, NVIDIA GeForce GTX 1660,
driver 591.86. They do not establish other-platform support, window-transition
behavior, or physical display scanout. Interactive lifecycle checks remain in phase 1.

| Check | Observed result |
| --- | --- |
| Portable regressions | Shared movement, entity creation/removal, command boundaries and outcome history, asset failure/release, and invalid geometry passed. Camera validation rejects the f64-to-f32 boundary case before application; retry covers either or both textures failing. |
| Quad GPU readback | Orientation, transparency, half-alpha HUD layering, camera movement with fixed HUD, removal, fallback, and resource release after submission passed. |
| Mesh GPU readback | Depth occlusion, perspective size, camera movement with fixed HUD, removal, and resource lifetime passed. |
| Window capture | MCP returned 1920x1080 PNGs; a yaw edit changed mesh pixels while the HUD region stayed byte-identical. Invalid/expired IDs were rejected. A separate GPU test verified 13-pixel-wide padded rows and BGRA conversion. |
| Six-face checker inspection | All six faces showed readable A1-D4 labels and expected corner order. An independent ray/UV comparison of 11,031 sampled mesh pixels had no mismatches above one channel value; the HUD region stayed byte-identical. |
| Background encoder | Gated-worker tests passed responsive pending polls, overlap rejection, retained failures, and joined shutdown. Encoding and file writes run outside the bridge call/heartbeat thread. |
| Native smoke, both modes | Sample controls, release/reload of textures, PNG capture/retrieval, status calls while polling, independent bridge/game restarts, and orderly shutdown passed. The 3D scenario also confirmed camera-boundary rejection leaves the host running. Only the existing OBS Vulkan hook API-version warning appeared. |
| Workspace and boundaries | All-feature tests, all-target Clippy with warnings denied, formatting, generated-shader checks, and documentation links passed. The standalone renderer dependency tree excludes runtime/providers; presentation contracts build without their runtime feature. |

The initial 3D validation used the transparent PNG on the cube. The opaque checker
replaced that fixture after visual review; alpha-cutoff support remains implemented.

## 5. Make a playable local game

**Result:** A player can complete a small game loop on one machine.

**Status (2026-09-15):** The three-wave arena prototype, shared headless simulation,
MCP adapter, and native hosts are implemented. The user approved the engine
extraction build while playing. Detailed human win/loss/restart outcomes, encounter
duration, and balance assessment remain outstanding; see [TODO](../TODO.md#next-reference-game-balance-evidence).

**Scope:** One hero with melee and dodge clears three waves of grunt/brute enemies
in one compact 3D arena. The game has telegraphed attacks, health, intermissions,
victory, defeat, and restart. Windows and solo play come first; macOS is unverified.
Roughly five minutes is a pacing target, not a measured human encounter duration.
Equipment, loot, and progression follow balance assessment. Detailed rules and
acceptance cases belong in the [reference-game design](plans/2026-09-15-reference-game.md).
The earlier collect-and-escape proposal was replaced by this scenario on 2026-09-15.

**Done when:** A person can start, play, win or lose, and restart. Headless tests
exercise the same movement, collision, combat, outcomes, and reset rules.

**Dependency for phase 6:** Both current hosts run independent solo simulations.
Two-player co-op requires an authoritative server and synchronization model;
cross-platform floating-point repeatability remains unverified. Fixed steps alone
do not establish determinism across platforms.

### Implementation and engine boundaries

Shared gameplay owns combat, waves, spawns, authored arena geometry, and semantic
commands applied at fixed boundaries. Following feedback about wave-two attacks,
dodge gained recovery cancellation and a nine-tick input buffer; monster strikes
are spaced by at least 30 ticks and the last 12 windup ticks have stronger warnings.

Reusable camera control, quaternion coordinate helpers, bitmap text, procedural
meshes, spatial queries, input accumulation, command bookkeeping, and snapshot
publication now live in existing engine layers. Native pointer/window operations
and the common `--background` option are engine-owned. The rendering sample
finalizes queued commands on shutdown. The [architecture](architecture.md) owns
these contracts; the [resolved boundary review](reviews/2026-09-15-game-engine-boundaries.md)
records the follow-up findings and resolutions.

### Validation evidence

**Automated checks (2026-09-15, Windows):** Workspace all-feature tests, strict
all-feature/all-target Clippy, formatting, and whitespace checks passed after the
boundary fixes. Coverage includes combat timing/outcomes, wave transitions, fixed
input accumulation, camera collision, quaternion transforms, FIFO ordering,
overload, stale publications, and queued shutdown cancellation. The Winit suspension
callback shares its tested suspension routine; OS-originated suspension remains a
separate native validation requirement.

**Scripted balance baseline:** The headless `combat_assessment` after the
responsiveness update produced the following results on the tested build:

| Policy | Outcome | Simulation ticks / seconds | Health |
| --- | --- | --- | --- |
| Idle | Lost on wave 1 | 550 / 9.167 | 0 |
| Rush nearest monster | Lost on wave 3 | 1461 / 24.350 | 0 |
| React to windup and dodge sideways | Won all waves | 3019 / 50.317 | 100 |

Before that update, rush lost on wave 2. This comparison demonstrates a scripted
completion path and improved scripted survivability, not human difficulty or pacing.

**Latest native run (2026-09-15, Windows / NVIDIA GeForce GTX 1660 / Vulkan):**
`target/arena-boundary-review-evidence/report.json` records a passed
`combat_and_window_lifecycle` scenario with 7,126 successful client presentations.
Both hosts cleared all waves at 100 health: server tick 2896, client tick 3050.
Buffered dodges began at the recovery boundary. Defeat/restart, camera operations,
resize/maximize/minimize/restore, pointer capture/release while minimized,
focus-loss command cancellation, snapshot capture, and orderly process exits passed.
The orbit capture was visually inspected. Earlier focus automation timed out in
`target/arena-fairness-evidence`; later full runs passed. This does not guarantee
that desktop focus requests will always be granted.

Both 2D and 3D private-bridge sample scenarios passed with shared `--background`
and publication metadata checks. The 3D scenario includes roll and vertical camera
commands, continued rendering, capture, reconnect, and independent shutdown.
The explicit GPU depth/perspective/shared-HUD regression passed during the preceding
quaternion update. These are functional checks, not CPU/GPU performance measurements
or proof of display scanout. Native artifacts are generated under ignored `target/`.

**Human acceptance:** The user reported "lgtm while playing" for the preceding
engine-extraction build. No issues or tuning requests were reported. Specific wave
outcomes, duration, and manual lifecycle results were not supplied; this closes that
refactor's acceptance check but does not complete phase 5 balance validation.

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
| Before expanding beyond paired samples (selected) | Third-person arena ARPG; Windows first; one arena; solo followed by two-player co-op | Bounds further content and rendering requirements |
| Before phase 5 (selected) | Melee combat and dodge with kinematic floor movement; monster behavior, arena, and tests specified in the reference-game design | Gives gameplay a concrete completion test |
| Before phase 6 | Two-player co-op selected; network conditions and synchronization model remain open | Determines replication and latency handling |
| Before phase 7 | Required media features, devices, languages, accessibility cases | Makes the player-experience scope finite |
| Before phase 9 | Target hardware and measurable performance budgets | Makes performance acceptance testable |
| Before phase 10 | Distribution channel and final support matrix | Determines packaging and release validation |
