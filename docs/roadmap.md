# Nico roadmap

Build a Rust engine for 2D and 3D games, with a shippable client/server reference game.
This roadmap tracks current status, unfinished goals, and completion rules.
[TODO](../TODO.md) owns tasks; [architecture](architecture.md) owns technical rules; [README](../README.md) owns usage.

Status was checked against source and manifests on 2026-09-28.
Dated evidence describes earlier tested builds, not fresh checks of every current path.
Completed work stays in the table; detailed test histories are omitted.

## Current state

| Phase | Current result | Status or remaining limit |
| --- | --- | --- |
| 0. Run game logic | Headless ECS, stages, events, and services | Implemented |
| 1. Run a native client | Winit, input, RHI, and wgpu | Core implemented; platform checks remain |
| 2. Control hosts with AI | Independent hosts, MCP bridge, diagnostics, and stop | Implemented; Windows baseline checked 2026-09-11 |
| 3. Load game assets | Extensible PNG, static GLB, and model GLB import | Implemented; source-independent packaging remains |
| 4. Display game state | Arena rendering and HUD | Implemented; standalone sample removed; native smoothness checks remain |
| 5. Complete a local game | Three-wave Arena with restart | Earlier balance accepted; human pacing was not measured |
| 6. Play over a network | Authoritative world, two clients, and persistence | Windows local milestone checked 2026-09-17; Internet conditions unverified |
| 7. Complete the player experience | Animation, imported characters, Meadow, and PBR | Art, effects, audio, settings, and input work remain |
| 8. Make development repeatable | CLI, project scenes, and bridge tools | Editor and automatic CLI caching removed 2026-09-28 |
| 9. Validate performance | Selected measurements exist | Deep profiling and agreed budgets remain deferred |
| 10. Ship the game | No release package accepted | Planned |

The default world uses an authoritative server.
The `--arena` option keeps client and server simulations independent.
Character names are local development identities, not authenticated accounts.
Resource limits do not prove player capacity or broad hardware support.

### Scene startup migration — 2026-09-29

Both Arena hosts now load composed `SceneDefinition` assets before gameplay starts.
Scenes own camera settings, lighting, world references, collider placements, spawns, and scenery placement.
The retained authoring adapter edits that same scene. Server startup skips client component creation.

On macOS arm64, workspace checking passed with all targets and features.
Focused tests across scene, authoring, and four Arena packages passed: 137 tests, three ignored measurements.
Coverage includes scene selection, role filtering, owned entity cleanup, camera settings, lights, and invalid references.
Clippy passed for those packages with the existing `chunks_exact_to_as_chunks` lint allowed.
Strict Clippy remains blocked by that existing lint in rendering and grass import code.
Native rendering and user-observed gameplay were not checked for this migration.

### Default splash scene — 2026-09-29

The default client scene displays NICO for one second, then shows Meadow loading progress.
CPU asset preparation runs through the engine batch loader. The native window stays open during the scene switch.
Headless startup follows the same target without a splash delay.
The `scene_loading` tool exposes phase, asset counts, and failures.

On macOS arm64, workspace checking passed with all targets and features.
Focused tests cover splash timing, background success and failure, server target selection, and runtime replacement cleanup.
Clippy passed with the existing `chunks_exact_to_as_chunks` lint allowed.
The isolated native splash test passed with client PID 82385 and private server and bridge ports.
Captures showed splash, loading, preparation, and connected Meadow gameplay in the same client process.
Asset progress reached 24 of 24 files. Host status recorded 437 successful presentation calls before shutdown.
Captures and status snapshots were separate samples. Desktop visibility and user-observed approval were not verified.
The test stopped its own hosts and closed its window.

Scene activation now keeps one `App` root and replaces only scene-owned worlds, systems, events, and local clocks.
The root owns background loading, persistent resources, and application timing; cleanup closes scene services before releasing scene state.
On 2026-09-29, Windows workspace tests passed using `target/bridge-validation`; strict workspace Clippy and formatting also passed.
Runtime coverage includes root persistence, scene cleanup, failed preparation, failed activation, and cleanup hooks that return errors.
The isolated splash test passed with client PID 32888. Scene generations advanced from 1 to 2 without resetting application frames.
The client log contained one runtime startup and one final shutdown; status recorded 779 successful presentation calls.
Inspected captures showed splash and connected Meadow. The test stopped its own hosts and closed its window.
Snapshots and captures were separate samples; desktop visibility and user-observed smoothness remain unverified.

## 1. Run a native client

**Remaining goal:** Complete native lifecycle checks on supported platforms.

Windows/Vulkan checks on 2026-09-15 passed automated window transitions, presentation recovery, input cancellation, and orderly exits.
Physical input, close-button behavior, OS suspension, restore timing, and GPU failure recovery still need checks.
Matching macOS lifecycle checks remain open.

**Complete when:** Each supported platform passes those checks with rendering and process exit recorded separately.
Session-frame counts alone do not prove successful presentation.

## 4. Display the game world

The standalone sample game was removed on 2026-09-28.
Engine rendering remains; Arena and character preview are the native consumers.

### TrueType text — 2026-09-30

Splash, Meadow, Arena, and character preview now use bundled Noto Sans through the shared `TextFont` API.
The hardcoded bitmap alphabet has been removed. Shaping, glyph rasterization, and native pixel density use the existing quad renderer.
Font handles share CPU caches across scene changes. Ownership, budgets, and script limits are documented in [architecture](architecture.md#text).

On Windows, 119 focused tests passed across presentation control, native hosting, Arena client, and character preview.
One existing startup measurement remained ignored. Eight text tests cover input rejection, shaping, whitespace, density, cache reuse, and eviction.
Workspace checking and strict Clippy passed with all targets and features. Formatting and diff checks also passed.

The isolated splash test passed with client PID 28284 and recorded 436 successful presentation calls.
Inspected captures showed splash, loading labels, and connected Meadow text through scene generations 1 and 2.
The character preview check passed with PID 3012; its capture showed readable multiline text.
Arena captures from PID 13232 showed its HUD and controls. The broader combat smoke failed during server wave two.
Its automated server hero died before victory, so that run does not establish a complete combat regression pass.
All test processes stopped and their windows closed. Captures and status snapshots were separate samples.
Desktop visibility, user-observed acceptance, macOS rendering, and additional script fallback fonts were not verified.

**Remaining goal:** Verify native camera smoothness after rendering changes.

On 2026-09-29, Meadow's default camera changed to 0.30 radians pitch, 5.5 metres distance, and 1.35 metres target height.
Vertical FOV remains 1.0 radian. Two client scene tests passed on Windows.
The projection test places an unobstructed, upright 1.8 metre hero within 25–30% of screen height.
Its feet fall within 69–71% from the top. Animated rendering and user-observed framing remain unverified.

GPU tests confirm that scenery reentry no longer causes repeated uploads.
Earlier offscreen measurements supported the debug dependency and driver-validation changes.
Neither result proves native frame-time improvement or user-observed smoothness.

**Complete when:** Rebuilt world clients pass repeated camera rotation and scenery reentry checks on named hardware.

## 7. Complete the player experience

**Remaining goal:** Finish character art, grounded shadows, combat effects, audio, settings, and clear loading/error flows.
Required devices, languages, and accessibility support still need a defined scope.

**Complete when:** The agreed player journey passes visual, audio, settings, and input checks.
Restart, disconnect, and focus loss must leave controls in a valid state.

### Client character milestone

Animation, retargeting, attachments, imported enemies, and GPU skinning are implemented.
Windows/Vulkan checks on 2026-09-16 covered an imported-hero encounter; the user confirmed watching all waves and victory.
This does not establish full art quality, exact contacts, or the earlier crowd timing claims.
Native enemy captures also predate removal of visual injury playback.

On 2026-09-29, humanoid capsules and sector-to-body hit tests passed focused macOS checks.
These covered 43 shared gameplay tests, three asset definition tests, and eight world client tests.
Workspace checks passed for all targets. The debug overlay now uses capsule dimensions.
Native rendering and user-observed combat feel remain unverified for this change.

**Remaining:** Resolve source/license questions and check reference poses, floor contact, strike timing, and replacement character assets.

### Meadow scenery milestone

Imported scenery, grass, paths, and distant visuals are implemented; walkable ground remains flat.
The last recorded texture correction lacked a fresh native capture, and user-observed acceptance remained open.

**Remaining:** Inspect the current rendered world and record visual limits.

Meadow Watch is implemented and tested in simulation.
Native checks covered acceptance and saved progress, but not the full kill-to-reward sequence.

## 8. Make development repeatable

**Remaining goal:** Make fresh-checkout builds, content changes, and reference tests repeatable.

On 2026-09-29, Windows checks passed 40 `nico-ops` tests and six bridge process tests for shared daemon support.
Coverage includes concurrent startup, frontend exit, stable game IDs, daemon crash recovery, idle shutdown, and occupied-port failure.
A lost mutation returned an uncertain result and was not replayed.
The Windows job smoke test killed the first frontend's job while the second frontend retained access to the same daemon.
The CIM fallback passed with a state-directory path containing spaces. Workspace type checks and focused Clippy checks passed.
The tested executable is under `target/bridge-validation/debug`; a running old bridge blocked replacement under `target/debug`.
Unix process lifetime and live Codex reconnection after this upgrade remain unverified. No user game was stopped.

The integrated editor and Play launcher were removed on 2026-09-28.
CLI hosts reuse cached imports and require restart after content edits.
Project scenes, authoring libraries, explicit cache/watch APIs, and local MCP operations remain.
Earlier editor results do not describe current CLI loading.

On 2026-09-29, Windows checks passed seven scene tests and six Arena project tests after excluding generated caches from content scans.
Arena loaded the local 220 MiB source tree despite 539 MiB of old caches inside `assets`.
The content byte limit was then removed. Seven scene tests passed, including hashing a source larger than 512 MiB.
The server build passed in `target/bridge-validation`; Windows blocked replacement of the default executable.
Formatting checks passed. No live server or visual session was tested.

Recorded removal checks passed Windows workspace and focused package checks.
They did not include native visual gameplay.
Editor RPC, credential grants, and the editor capture downloader were removed on 2026-09-28.
MCP window capture remains available; the bridge wire protocol is now version 3.
On macOS, workspace type checks and 65 bridge and host tests passed after this removal.
These include snapshot and PNG transfer tests; no native GPU capture was performed for this removal.

**Complete when:** A fresh checkout builds content and runs documented reference tests.
Content changes reach the game through a clear workflow, and failures identify their source.
Tests must clean up only their own processes.

### Common mesh instancing (delivered 2026-09-21)

Instancing, GPU culling, foliage fields, and bounded streaming are implemented without LOD.
Recorded Vulkan and DX12 graphics checks passed on the tested build.
Frame-time parity with expanded geometry remains unproven; further optimization is deferred.
Removed editor checks do not prove current CLI startup or user-observed smoothness.

On 2026-09-29, a startup check found idle frames between completed worker batches.
Streaming now collects finished jobs before starting the next batch, while preserving eviction checks.
An isolated grass/shrub check used GTX 1660, Vulkan, a debug build, and an 840-by-764 offscreen target.
At 16 ms frame pacing, all 64 chunks became resident in 17 frames, previously 32.
Elapsed time fell from 683 ms to 473 ms; 33 ms pacing fell from 1104 ms to 606 ms.
These are single trials with a GPU completion wait each frame, excluding asset loading and pipeline creation.
Shared mesh uploads totaled 652 bytes; instance and visibility source uploads totaled 20,988,560 bytes.
Neither run deferred uploads because of the upload budget.
Render submission took about 375–390 ms across startup; this elapsed scope does not isolate CPU or GPU execution.
These results do not establish native window startup time or user-observed smoothness.
Repeat with `NICO_MEASUREMENT_STARTUP_MS=16` and the ignored `gpu_arena_instance_path_measurement` test in `nico-rhi-wgpu`.

A follow-up packing check used the same camera and 60 visible batches containing 145,976 records.
Five debug trials of the production packing function took 259–264 ms, including output allocation and release.
Copying prepared buffers took 1.2–1.7 ms for the same 16,349,312 bytes.
A matching offscreen startup run uploaded exactly that record size and spent 367 ms inside rendering calls.
The standalone packing cost was about 71% of that rendering scope; these were separate runs, not nested timings.
This supports packing as a major debug-build cost, without establishing release performance or exact in-frame attribution.
Repeat with `cargo test -p nico-render arena_startup_packing_measurement -- --ignored --nocapture`.

Packing now assembles fixed arrays and appends each record once, using safe byte casts on little-endian hosts.
With unchanged debug settings, five trials took 18.4–18.8 ms, compared with 259–264 ms before this change.
The matching startup check reduced render-call time from 367 ms to 120 ms and total elapsed time to 277 ms.
It still took 17 frames at 16 ms pacing and uploaded the same record bytes.
Byte compatibility tests cover full and compact layouts, mirrored affine transforms, foliage parameters, and appended records.
All 27 renderer tests and the offscreen startup check passed; native-window smoothness remains unverified.

A later Windows debug startup check separated work after file-import progress finished.
Preparation took 2.69 seconds: environment binding used 1.24 seconds and final content verification used 1.14 seconds.
Ground generation now reuses axis distances, skips unaffected obstacle rows, and appends complete pixels.
It uses a temporary 4 MiB shade buffer; every Meadow pixel matches the previous generator.
Ground generation fell from 1.19 seconds to 0.31 seconds with unchanged debug settings.
The matching preparation run fell to 1.83 seconds; final content verification still used 1.15 seconds.
These single-run elapsed measurements exclude window creation, GPU preparation, and scene activation.
Repeat with the ignored client `startup_asset_preparation_measurement` and presentation `landscape_preparation_measurement` tests.

On 2026-09-29, normal Arena startup stopped calculating and rechecking whole-project content hashes.
Definitions still receive parsing and validation. Required resources and cached payloads receive byte-hash checks before cache reuse.
A regression test covers changed bytes with matching metadata and a corrupted cached payload.
One debug CPU measurement fell from 3.85 seconds to 2.11 seconds, excluding window creation and GPU setup.
Post-import preparation fell from 1.83 seconds to 0.68 seconds; verified resource loading took 1.35 seconds.
The isolated Windows native test passed with client PID 28176 and 515 successful presentation calls.
Client and server `scene_info` reported Meadow, validated definitions, and no calculated content revision.
Rendered captures showed preparation without a completed import bar, followed by connected Meadow gameplay.
Captures and MCP state were separate samples. Desktop visibility and user-observed smoothness were not verified.
Workspace tests with all features, strict all-target Clippy, formatting, and whitespace checks passed.
Bridge lifecycle tests required execution outside the sandbox to create their private Windows daemons.
The default debug client was rebuilt. The isolated server build passed; a running process locked the default server executable.

## 9. Validate performance

**Status:** Deferred.
XRay is planned but neither integrated nor validated.
Selected past measurements do not explain current end-to-end startup or prove general performance.

**Before starting:** Choose target hardware, workloads, and startup, frame, tick, loading, and memory budgets.

**Complete when:** Supported measurements show workloads meet agreed budgets without changing authoritative results.
Keep elapsed time, CPU execution, and GPU execution distinct.
Do not add profiler code, instrumentation, collectors, viewers, toolchain changes, or compatibility work now.

## 10. Ship the game

**Status:** Planned.
Packaging may proceed while profiling is deferred; release acceptance still needs agreed performance checks.

**Before release:** Choose the distribution channel, platform support, hardware limits, and acceptance checks.
Resolve licenses, clean-machine setup, asset paths, persistence upgrades, and player/server instructions.

**Complete when:** Repeatable packages pass local and multiplayer tests on supported targets.
Long sessions, reconnects, upgrades, and failure recovery must meet agreed reliability limits.
AI operations must remain usable within their intended scope.
