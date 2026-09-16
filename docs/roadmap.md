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
| 3. Load game assets | Request usable game content by asset identity | Extensible PNG, static-mesh GLB, and model GLB import implemented |
| 4. Display the game world | Show loaded content moving with game state | 2D and 3D samples with shared HUD implemented |
| 5. Make a playable local game | Move, collide, complete an objective, and restart | Local arena and current balance accepted; Rapier integration validated on Windows |
| 6. Play over a network | Two clients play together on one authoritative server | Planned |
| 7. Complete the player experience | Add the required visuals, sound, menus, and settings | Animation pipeline implemented; imported-hero encounter verified; broader experience pending |
| 8. Make development repeatable | Rebuild content, inspect state, and automate playtests | Planned |
| 9. Validate performance | Profile representative workloads and meet defined budgets | Deferred |
| 10. Ship the game | Run release packages on supported machines | Planned |

**Priority update (2026-09-16):** Client humanoid models, skeletal animation, and
supporting presentation work in phase 7 precede phase 6 networking. Phase numbers
remain stable for existing references. Two-player co-op remains planned after this
client work. Platform lifecycle validation can continue
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

**Status:** PNG, static-mesh GLB, and bounded model GLB import use the public
`nico-assets` importer interface and typed asset-store lifecycle. Optional decoder
and runtime features compose through the compatibility `loading` feature. The headless
texture example and native samples consume shipping assets; see the
[texture design](plans/2026-09-14-texture-assets.md) and
[mesh design](plans/2026-09-14-mesh-assets.md).

**Import extensibility (2026-09-16):** Public `AssetImporter` and typed
`ImportRegistry<T>` support userland importers, multiple formats per output type,
per-asset settings/budgets, and custom asset types. PNG/static-GLB use that same
interface. Runtime-free import and independently selectable decoder features
preserve existing loading convenience and lifecycle behavior. Generic model
import and initial CPU humanoid conversion are now implemented, with a standalone
native GPU preview. Playback transitions and optional imported arena hero rendering are implemented. The
[import contract](plans/2026-09-16-extensible-asset-import.md) defines current bounds.

**Extension validation (2026-09-16, Windows):** Workspace all-feature tests and
strict all-feature/all-target Clippy passed using `target/asset-import-review`.
The final asset suite passed 19 unit tests, nine public-API integration tests, and
one executable documentation example. External implementations exercise an engine
texture type and a custom text type, explicit importer selection, per-entry settings,
registration rejection, source/output limits, structured errors, retry, cancellation,
stale completion, worker panic, and joined shutdown. Base, PNG-only, GLB-only, and
runtime-only feature configurations passed tests and strict package Clippy; the
base normal dependency tree is empty. This change did not run GPU/native window
validation and does not establish character import or cross-platform behavior.

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
[TODO](../TODO.md#next-client-humanoid-models-and-animation).

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
extraction build while playing and subsequently accepted the current combat balance.
No tuning changes are requested. Detailed encounter-duration measurements were not
supplied; they are not a blocker for the accepted prototype. Next major scope is
[client character presentation](../TODO.md#next-client-humanoid-models-and-animation).

**Scope:** One hero with melee and dodge clears three waves of grunt/brute enemies
in one compact 3D arena. The game has telegraphed attacks, health, intermissions,
victory, defeat, and restart. Windows and solo play come first; macOS is unverified.
Roughly five minutes is a pacing target, not a measured human encounter duration.
Equipment, loot, and progression are later content work. Detailed rules and
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

**Boundary-refactor native run (2026-09-15, Windows / NVIDIA GeForce GTX 1660 / Vulkan):**
`target/arena-boundary-review-evidence/report.json` records a passed
`combat_and_window_lifecycle` scenario with 7,126 successful client presentations.
Both hosts cleared all waves at 100 health: server tick 2896, client tick 3050.
Buffered dodges began at the recovery boundary. Defeat/restart, camera operations,
resize/maximize/minimize/restore, pointer capture/release while minimized,
focus-loss command cancellation, snapshot capture, and orderly process exits passed.
The orbit capture was visually inspected. Earlier focus automation timed out in
`target/arena-fairness-evidence`; later full runs passed. This does not guarantee
that desktop focus requests will always be granted.

**Renewable MCP movement (2026-09-16):** `game_move_hold` and
`game_move_release` add continuous, named movement leases to both arena hosts.
Renewals and direction changes apply at fixed boundaries; expiry is measured in
simulation ticks. The [README](../README.md) owns usage and cancellation rules.
All 45 shared and 17 client tests passed, including renewal continuity, steering,
expiry, stale edits, bounded slots and cancellation. The bridge registration test
also routed start/renew/release successfully; strict Clippy passed for the arena
packages and bridge. In a Windows debug client with the imported character,
one lease remained active for 272 consecutive ticks across eight renewals and a
direction change. Explicit release stopped movement; an unrenewed hold expired
after exactly 30 ticks. This verifies command continuity, not frame smoothness or
wall-clock expiry during suspension.

The initial hold check used command/state responses and did not establish what the
user saw on the desktop. A follow-up used the user's confirmed client (PID 3628),
ran a 30-second movement sequence, and inspected six GPU captures while renewing
the hold. The images show running poses, changing direction, and changing arena
wall/floor positions. State observations reported corresponding movement and a
running animation; those reads were after capture completion, not frame-exact
capture metadata. This establishes rendered movement in that follow-up, not desktop
scanout or an explanation for the user's earlier idle-window report.

Both 2D and 3D private-bridge sample scenarios passed with shared `--background`
and publication metadata checks. The 3D scenario includes roll and vertical camera
commands, continued rendering, capture, reconnect, and independent shutdown.
The explicit GPU depth/perspective/shared-HUD regression passed during the preceding
quaternion update. These are functional checks, not CPU/GPU performance measurements
or proof of display scanout. Native artifacts are generated under ignored `target/`.

**Human acceptance:** The user reported "lgtm while playing" for the preceding
engine-extraction build. No issues or tuning requests were reported. Specific wave
outcomes, duration, and manual lifecycle results were not supplied; this closes that
refactor's acceptance check. The user subsequently reported that everything looked
fine and accepted the current balance, closing the tuning task without requesting
additional measurement. This does not establish the five-minute pacing target.

### Rapier integration validation

The [physics integration](plans/2026-09-15-physics.md) adds a thin `nico-physics`
wrapper around Rapier 3D, with f64 poses, private provider handles, fixed/dynamic/
kinematic bodies, character movement, queries, collision filtering, and owned
contact observations. The optional ECS/runtime adapter manages component changes,
entity removal, fixed stepping, and shutdown. The arena uses a persistent physics
world for its existing planar movement; the unused handwritten slide solver was
removed. Camera queries and combat rules retain their existing ownership.

On Windows (2026-09-15), workspace all-feature tests and strict all-target Clippy
passed after resolving both findings from the
[integration review](reviews/2026-09-15-rapier-integration.md). All 13 physics tests
passed, covering dynamics, slope movement, pushing, contacts, filtering, query
freshness, ECS lifecycle, bounded output, invalid input, and provider failure.
Regressions verify trajectory equivalence with/without lookups and kinematic
sensor entry/exit through direct physics and runtime events. All 10 runtime-free
physics tests also passed. No performance measurements or cross-platform
determinism claims are made.

The initial integration's bounded falling-box example settled its half-unit box at
Y=0.49993 after 180 ticks. Its headless assessment loses at tick 550 for idle and tick 1461
for rush; reactive play wins at tick 2893 (48.217 simulation seconds) with 100 health.
Compared with the earlier baseline, exact paths and completion ticks can change
with the collision implementation; attack/dodge timing and wave rules are unchanged.

The corrected native build passed the complete combat/window lifecycle harness on
Windows / GTX 1660 / Vulkan
(`target/arena-rapier-review-fixes/report.json`), recording 7,226 successful
presentations. Server and client cleared all three waves at ticks 2879 and 3232,
both with 100 health. Buffered dodges, defeat/restart, camera/capture, window
transitions, focus-loss cancellation, and orderly exits passed. The orbit capture
was visually inspected. This arena run validates the migrated movement path;
the focused engine regressions exercise the two review failures.

## 6. Play over a network

**Status (2026-09-16):** Planned after the client character and presentation work
in phase 7, following the user's priority change.

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

### Client character milestone

**Status (2026-09-16):** Generic GLB model/skin/clip import and CPU animation with
humanoid conversion and a standalone GPU-skinned preview are implemented ahead of
networking. Compiled mappings and reusable pose buffers remove per-frame geometry
rebuilding. Playback transitions are implemented. Fresh Windows/GTX 1660 Vulkan
validation covered workspace/all-feature checks, four GPU tests, a two-character
preview, and an imported-hero encounter completing waves 1–3 on both hosts, followed
by defeat/restart and orderly shutdown. The user confirmed watching all three waves
and victory. The [fresh review](reviews/2026-09-16-uncommitted-mcp-review.md) records
build/scenario evidence and limits. Earlier 16/64-character cadence measurements
and visual contact calibration have not been revalidated. Broader character-art
acceptance remains part of phase 7.

**Game-owned hero assets (2026-09-16):** The arena now loads its model and six
selected clips from `games/arena-arpg/assets/presentation/characters/hero/` by
default. `--procedural-hero` selects the original visuals; paired path overrides
remain available for experiments. The selected files match the original inputs
byte-for-byte. Asset provenance and outstanding redistribution terms remain in the
[source record](plans/2026-09-16-character-assets.md).
The updated client passed 20 package tests, strict Clippy and formatting. A final
Windows/GTX 1660 Vulkan MCP run with no asset overrides completed all three waves,
defeat/restart and shutdown on both hosts; client victory was captured and inspected.
The [review record](reviews/2026-09-16-uncommitted-mcp-review.md#game-asset-location-update)
also preserves two preceding failed automation attempts and the focus-cancellation
handling adjustment. These results do not claim a user-watched demonstration.

The dated investigation notes below retain their original chronology. Earlier MCP
measurements and visual claims are historical, not fresh validation authority;
the status paragraph above supersedes their implementation-status statements.

**Asset inspection (2026-09-16):** The supplied Ch03 character and 64 RPG clip
files were structurally inspected. The clips share a skinned mesh and skeleton,
but that skeleton differs from Ch03's Mixamo rig. CPU retargeting now bridges the
body mappings; clip visual suitability and animation source/license remain
unresolved. A native attack-pose preview was captured as recorded below; full clip
acceptance is outstanding. Findings and candidate roles are recorded in the
[asset selection](plans/2026-09-16-character-assets.md).

**Scope:** One rigged hero establishes reusable skeleton and clip assets, pose
evaluation, skinned rendering, clip transitions, and weapon attachment. Idle,
locomotion, melee, dodge, hit reaction, and death follow gameplay snapshots.
In-place clips preserve simulation-owned movement, collision, damage, and action
timing; visual reactions must not introduce gameplay interruptions. The server
continues to run without presentation assets. Engine layers own reusable animation
capabilities; the game owns character content and state-to-animation mapping.

**Humanoid implementation (2026-09-16):** A canonical body mapping and CPU animation
conversion layer supports the supplied Mixamo and RPG skeletons.
Shared bone roles and a reference pose allow motion transfer while each model
retains its original skinning hierarchy and bind matrices. Explicit profiles and
mapping overrides cover source differences; arbitrary formats and automatic
recognition of every rig are not assumed. Details belong in the
[model/animation contract](plans/2026-09-16-model-animation.md).

The extensible, runtime-free import prerequisite is implemented: userland can
register new formats and asset types. Generic skeleton import and userland
humanoid profiles now consume that interface. The
[import design](plans/2026-09-16-extensible-asset-import.md) defines the implemented
boundary and links to the implemented CPU consumers.

**CPU validation (2026-09-16, Windows):** Workspace all-feature tests and strict
all-feature/all-target Clippy passed using `target/asset-import-review`. Four new
model-import regressions cover valid bundles, malformed ranges/hierarchies/bindings,
limits, and image/material handling. Five animation regressions cover time origins,
loop/clamp/STEP/slerp, reference axes and poses, limb proportions, root policies,
mapping failures/index overrides, and mesh-local skin matrix composition.

Local Ch03 (65 skin joints) and all 64 RPG GLBs (53 skin joints each) imported.
The headless retarget example mapped 22 body roles on both rigs and evaluated nine
poses per clip: 576 poses and 9,411,840 target vertex evaluations, all finite.
Structured results are in ignored
`target/character-import-evidence/retarget.jsonl`. Materials explicitly fall back
from optional specular/IOR extensions. This is CPU numerical validation, not native
rendering, visual animation acceptance, a performance measurement, or proof of
cross-platform behavior. No supplied asset binaries were added to shipping roots.

**Native preview validation (2026-09-16, Windows, GTX 1660/Vulkan):**
`nico-character-preview` rendered locally supplied Ch03 with RPG
`Unarmed-Attack-L1` retargeting. MCP discovery, state inspection, seek/pause at
0.25 seconds, and 1920x1080 GPU-readback capture succeeded; host status reported
360 successful presentation API calls at the recorded check. The captured image
shows a textured, deformed character. The session subsequently stopped cleanly
at 1,332 steps and the process exited with code 0. The capture is retained in
ignored `target/character-preview-evidence/attack-025.png`.

Four focused preview regressions cover independent ECS instance state, selected
scene/CPU deformation/reference pose, seek/loop and rejected controls, and malformed
control arguments. Package tests, strict all-target Clippy, and formatting passed.
This does not establish GPU skinning, complete animation quality, PBR fidelity,
physical input coverage, or performance. Usage belongs in the
[README](../README.md#native-character-preview).

**Preview frame inspection (2026-09-16):** The HUD and `preview_state.frame_timing`
now report wall-clock Update cadence over windows of at least one second, including
average/minimum/maximum intervals and sample count. Two deterministic tests cover
elapsed-weighted FPS, zero intervals, window reset, and stalls; all six preview
unit tests and strict package all-target Clippy passed. This is a frame-cadence
readout, not CPU/GPU profiling. This addition has not had a separate native capture.

**Playback-rate investigation (2026-09-16):** A read-only MCP observation of the
running preview at about 28 Updates/second measured 1.698 seconds of clip-clock
advance over 1.697 seconds of wall time, including a loop wrap, at speed 1.
A regression verifies equal playback position and deformed pose after three
seconds divided into 30, 60, or 144 Updates/second. It passes in debug and release;
all seven preview tests and strict package Clippy pass. The reported release-only
speed difference is not reproduced by these checks; the two launch configurations
and live release playback still need comparison. No playback behavior was changed.

**GPU skinning foundation (2026-09-16, Windows, GTX 1660/Vulkan):** The native
preview now retains shared geometry and updates joint palettes. Compiled humanoid
rigs retain model ownership; pose sampling, blending and retargeting have reusable
buffers with failure-safe publication. A real-GPU regression matches weighted
skinning against CPU reference pixels for shared geometry with independent palettes.
Native Ch03/RPG `Unarmed-Attack-L1` debug preview measured 58.34 Updates/second,
17.14 ms mean and 17.65 ms maximum over a 59-interval window. This differs from the
earlier ~28 FPS CPU preview, but is not a controlled CPU/GPU timing breakdown or a
crowd-capacity claim. The host reported 7,879 presentations before orderly MCP stop;
process exit was 0. Capture: ignored `target/character-preview-evidence/gpu-skinning.png`.
Workspace all-feature tests, strict all-target/all-feature Clippy, all four opt-in
GPU regressions, generated shader checks, and formatting passed. Full production
acceptance remains open in the linked plan.

**Done when:** The native arena displays the rigged hero with correct bind pose,
joint deformation, weapon attachment, and readable transitions synchronized with
combat. Import and sampling regressions cover malformed data and boundary times;
native captures and MCP inspection demonstrate transitions, restart, and asset
lifecycle behavior. Validation evidence must identify the tested asset and platform.

Following client work applies the pipeline to enemies and adds the materials,
lighting, shadows, combat effects/audio, and settings needed by the arena. Advanced
inverse kinematics, ragdolls, and animation-driven root motion are outside the
initial character milestone. Humanoid motion conversion is now included; further
retargeting quality features follow demonstrated needs. Concrete actions belong in
[TODO](../TODO.md#next-client-humanoid-models-and-animation).

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


## Historical animation progress notes (2026-09-16)

These chronological notes preserve earlier implementation reports and the original
acceptance conclusion. Pending-work statements describe their point in time. The
earlier MCP evidence was rejected as validation authority; measurements and visual
claims here are not current acceptance. See the
[client character milestone](#client-character-milestone) for current status.

**Playback and shared model presentation (2026-09-16):** The elapsed-time player
supports looping/one-shot completion, crossfades, interruption, pause, seek, speed,
and retargeted clips. Named attachment transforms and reusable `ModelVisual`
assembly are implemented. Focused checks passed: 15 animation, 9 presentation-control,
and 7 preview tests, plus strict Clippy for those packages. These cover transition
continuity, independent clocks/palettes, socket hierarchy and instance placement,
invalid inputs, and snapshot resource retention/release.

On Windows/GTX 1660/Vulkan, the two-file Ch03 idle/attack debug preview accepted MCP
clip/fade/looping controls and held the attack endpoint with `finished=true`.
A later endpoint sample reported 58.56 Updates/second, mean 17.08 ms, max 17.79 ms;
this is wall-clock cadence, not CPU execution time or a crowd benchmark. The native
capture is retained locally at `target/character-preview-evidence/playback-once.png`.
That capture predates the updated completion HUD and shared-renderer extraction.
The updated extraction/`--once` build subsequently completed a 1,200-frame bounded
native run and exited 0; its capture request did not produce a retained image before
shutdown. Physical A/D clip switching has not been manually verified. Arena state
selection, visible weapon attachment, crowd update policy, and the final workspace
acceptance audit remain incomplete.


**Imported arena hero (2026-09-16, Windows, GTX 1660/Vulkan):** An opt-in local
Mixamo/RPG hero now replaces the procedural hero body while retaining the arena,
enemies, combat telegraphs, HUD, and authoritative simulation. The named right-hand
socket drives a visible blade through a full-affine palette. MCP state reported
idle/run/attack/dodge/hit/death, and completed move/attack/dodge/restart command
outcomes were checked. Native capture inspection confirmed the hero and attached
blade in the arena (`target/character-preview-evidence/arena-dodge.png` and
`arena-death.png`, local ignored evidence). Source clip suitability, weapon grip,
and exact strike-contact calibration are not established by these captures.

The native session stopped through MCP after 8,365 reported presentation successes;
the process exited 0. Sixteen client and 41 shared-game tests pass, alongside
strict client Clippy. Focused regressions
cover repeated/skipped snapshots, action identity, normalized action duration, hit
precedence, terminal death completion, and run/wave reset. The imported-hero path
remains client-only; enemies are procedural. Later sections record crowd, animated-bounds, resource/failure and workspace
acceptance.


**Animated bounds and draw visibility (2026-09-16):** `ModelVisual` now retains
per-joint influence boxes and evaluates current-pose bounds without per-vertex CPU
skinning. Preview and imported arena hero extraction use conservative frustum tests;
the arena unions the blade into hero bounds. Render and visibility share
`Camera3d::view_projection`. Invalid visibility inputs stay visible. This culls draws
only; CPU pose evaluation frequency is unchanged and crowd policy remains open.

Forty-four tests passed across presentation-control, render, preview, and arena
client, with strict Clippy for those packages. Bounds regressions compare weighted
vertices across 100 affine poses, including inverse binds, unequal/negative scales,
shear, near-normalized weights, rigid geometry, and selected-scene exclusion.
Frustum tests cover plane crossings, camera-enclosing boxes, behind-camera and
near/far rejection, plus invalid-input fallback.

The Windows/GTX 1660/Vulkan debug preview exposed changing finite bounds at MCP
seeks 0, 0.25, and 0.6 seconds, retaining its visible model draw. Native capture
`target/character-preview-evidence/animated-bounds.png` was inspected. One cadence
sample was 58.52 Updates/second (mean 17.09 ms, max 17.51 ms), not a CPU profile or
crowd benchmark. The session stopped via MCP after 5,490 reported presentation
successes and exited 0. Native offscreen/re-entry and reduced-rate crowd tests remain
required, along with weapon calibration and the final production audit.


**Bounded crowd preview (2026-09-16, Windows, GTX 1660/Vulkan):** The preview
supports 1..64 ECS characters sharing immutable assets and retaining independent
players, positions and palette snapshots. The aggregate model draw limit is 256.
CLI and MCP pose caps hold displayed poses and consume accumulated elapsed time;
selection, per-instance placement, camera target, sampled time/pending time and
visible/evaluated counts are discoverable through the bridge. Twelve preview tests
pass, including capped/full timing parity at 30/60/144 host rates, pause/completion,
placement/re-entry, input limits, and despawn/snapshot asset release. Strict preview
Clippy passes.

Five approximately one-second cadence windows were sampled per case at 1920x1080:

| Build and workload | Pose evaluation | Observed Update FPS range |
| --- | --- | --- |
| Debug, 16 visible Ch03/RPG attack instances | Every Update | 55.19–56.55 |
| Same debug session, same 16 instances | 15 Hz cap | 58.38–58.72 |
| Release, 64 visible instances | Every Update | 58.39–58.61 |

These are wall-clock Update intervals, including renderer/waits; they do not measure
CPU execution, GPU completion, or general crowd capacity. The capped session sampled
3–5 instances on the inspected frames while preserving elapsed playback. MCP tests
paused/seeked instance 0 independently, moved it offscreen (16 to 15 draws), moved
the camera away (zero draws), and restored the grid (16 draws). All command outcomes
were checked. Captures `target/character-preview-evidence/crowd-16.png` and
`crowd-64-release.png` were inspected. Both sessions stopped through MCP and exited
0 (6,255 and 9,179 reported presentation successes respectively).

`cargo test --workspace --all-features --target-dir target/asset-import-review`
and strict workspace/all-target/all-feature Clippy passed. The initial default-target
attempt encountered the running bridge executable's Windows file lock; isolated
validation succeeded while leaving the bridge running. Shader artifacts are current,
and all four explicit real-GPU tests passed, including weighted skinning versus the
CPU reference and shared-geometry independent palettes. Evidence logs are
`target/character-preview-evidence/production-workspace-tests.log`,
`production-clippy.log`, and `production-gpu-tests.log`. The gate-closure entry below records subsequent contact calibration and the final
requirement audit.


**Original production animation gate-closure claim (2026-09-16; superseded validation):** The
[acceptance review](reviews/2026-09-16-production-animation.md) maps all six production
implementation gates to code, tests and native evidence. The arena now uses the
right-hand attack; its inspected peak-forward marker at 49/120 of the source clip
maps to the authoritative active-phase boundary. A full-affine local +Y attachment
and load-time weapon-length solve align the blade tip with the two-metre hero reach.
Native MCP observed contact at 0.326666647 seconds with length 1.159424782 metres;
`arena-calibrated-contact.png` records an inspected active-phase frame. That session
stopped after 3,812 presentation successes and exited 0.

`update_at` preserves fades under an external action clock; a regression covers
late observations at contact without delayed blending. Seventeen arena, sixteen
animation and thirteen preview tests pass. The final workspace/all-feature test and
strict Clippy runs also pass. A missing-file native launch exited 1 before host
startup. Preview diagnostic labels/extensions are now bounded for bridge publication.
This was the original completion conclusion. Current validation is limited to the
[client character milestone](#client-character-milestone); phase-7 art, licensing,
lighting/effects and broader character-content acceptance remain separate work.
