# Nico

Nico is an experimental Rust 2024 game engine with a headless runtime, shared
client/server gameplay, a third-person arena ARPG prototype, and textured 2D/3D
rendering samples. Native clients use Winit and Nico's RHI with its wgpu backend.

The core native client host implementation is complete. Automated Windows window
transitions and rendering recovery are verified in the recorded environment; manual
input/close-button and macOS validation remain. Native gamepad integration and
Slang reflection/asset-backed shaders are deferred.

AI tooling connects to **`nico-bridge`**, a separate MCP process. You launch game
clients and servers independently; they connect to `127.0.0.1:47631` by default and
register host and game-defined tools and reconnect automatically. The bridge never
launches games, and losing its connection does not stop gameplay. It caches uploaded
schemas for its process lifetime and reports connection state separately from host
activity. Native hosts expose bounded structured diagnostic history through registered
tools.

Profiling remains a cross-library requirement, with experimental Rust/LLVM XRay chosen
as the future direction for automatic function capture and a Unity-style call hierarchy,
timings, and invocation counts. Profiling implementation, compatibility investigation,
and toolchain changes are deferred. XRay is not integrated or validated for Nico, and no
custom profiler is planned now.

## Run and validate

The arena ARPG prototype has a third-person native client and a headless server,
with shared melee, dodge, monster pursuit, collision, and win/loss/restart rules.
Its camera uses engine orbit and collision mechanics; the game supplies the hero
target, tuning, and arena geometry. Camera and mesh orientations use normalized
quaternions; orbit controls retain yaw/pitch limits. See the
[architecture](docs/architecture.md#assets-and-game-construction) for engine ownership.
Clear three waves: three grunts, then two grunts and one brute, then one grunt and
two brutes. Purple brutes move slowly and hit farther, with a longer windup.
Between waves, a three-second countdown resets positions and restores 40 health
(up to 100). Clearing the final wave wins.
Run from the repository root:

```text
cargo run -p arena-arpg-client
cargo run -p arena-arpg-server
```

Click the viewport to capture the pointer. WASD moves relative to the camera,
mouse motion looks around, left click attacks, Space dodges, R restarts, and Escape
releases the pointer. The capture click does not attack. Dodge can cancel sword recovery; a press up to
150 ms before readiness is buffered. Earlier presses are ignored. Monster strikes
are spaced at least half a second apart. Pale-gold pulses and an overhead warning
mark the final 200 ms of windup. The shared client option `--background` opens the
window without requesting focus and is available to both arena and minimal clients.
Focus loss releases input; simulation continues. Client and server run independent solo encounters;
multiplayer synchronization is not implemented. The server requires 60 Hz.

Both hosts attempt the default bridge connection; `--no-bridge` disables it and
`--bridge ADDRESS` overrides it. The client supports `--smoke-frames N`. Visuals
use procedural meshes, textures, and bitmap text; no external game assets or
skeletal animation are required. Attack sectors show full reach with a growing windup
fill and a yellow active strike; the HUD shows dodge cooldown progress. Rendering
uses the repository's generated shaders.
See [phase 5](docs/roadmap.md#5-make-a-playable-local-game) for validation limits.

### Arena operations

With `arena-arpg-shared`'s `tools` feature enabled, call
`arena_arpg_shared::tools::register(builder.add_plugin(ArenaPlugin))` and pass the
returned `ToolExtensions` to the engine host. Use `FIXED_STEP` for the runtime.
Registration adds game handlers and snapshot publication; engine hosts own the bridge
connection. Both arena hosts compose this adapter, registered as `arena_arpg`;
the minimal-game rendering samples keep their separate tools.

| Tool | Purpose |
| --- | --- |
| `game_state` | Read run/wave/countdown, actor type and combat stats, health, snapshot age, and action phases. |
| `game_move` | Queue world-space movement for 1..120 simulation ticks; zero direction waits. |
| `game_attack` | Request melee facing yaw radians, with zero along +Z. |
| `game_dodge` | Request a dodge along a nonzero world-space direction. |
| `game_restart` | Reset the current run and cancel old actions. |
| `game_command` | Poll acceptance/execution outcome by command ID. |
| `client_state` (client only) | Read camera, draw counts, and window fields from the last published frame, with age and closed state. |
| `client_control` (client only) | Queue `camera` with yaw/pitch. |

For camera controls, poll `client_state.last_applied_command`. Pointer capture uses
engine `window_control` with `{"action":"pointer_capture","value":true}`; poll
`window_state` with its `request_id` for completion and observed capture state.
Capture requires an enabled, active, focused window; release also works while inactive.
Shutdown marks the view `closed` and reports any `cancelled_command_id`.

Discover connected instances and original schemas through `list_game_tools` before
using `call_game_tool`. Mutations require the current `run_id`; accepted means queued.
Combat completion means the action started, not that damage landed. A buffered
dodge stays `running` until execution, with a null start tick and zero applied ticks.
`game_state.buffered_dodge` exposes its stored direction; newer human combat input,
focus loss, wave clear, restart, defeat, and shutdown cancel unexecuted dodges. Movement counts
lease ticks even while combat blocks locomotion. Wave clears cancel remaining lease
ticks with `wave_cleared`; intermission rejects movement/combat with `intermission`,
while restart remains available. Actor slots are reused each wave; use run ID, wave,
and actor ID together. Poll outcomes before inspecting the
corresponding run/tick; do not blindly retry a timed-out mutation. History retains
128 terminal outcomes and survives run reset and bridge reconnect, not process exit.

Validate handlers and real bridge routing with:

```text
cargo test -p arena-arpg-shared --features tools
cargo test -p nico-bridge --test registration arena_tools_route
cargo build -p arena-arpg-client -p arena-arpg-server -p nico-bridge --target-dir target/texture-validation
python apps/nico-bridge/tests/arena_native_smoke.py --bin-dir target/texture-validation/debug
```

The native test opens a client window, uses a private bridge port, and cleans up
only its own processes. It requests focus on that window for capture/focus-loss checks,
resizes, maximizes, minimizes, restores, verifies presentations resume, and stops both
hosts. It saves screenshots and a report under `target/arena-native-evidence`.
Use `--combat-only` to test gameplay, buffered dodges, captures, and orderly stop
without requesting focus or exercising window transitions; the report records this
reduced scope explicitly.

Compare three deterministic combat policies without graphics:

```text
cargo run -p arena-arpg-shared --example combat_assessment
```

The CSV output records idle, rush, and telegraph-reactive outcomes, simulation duration,
health, reached wave, and action counts. These are scripted comparisons, not human playtest results.

### Native rendering samples

Run from the repository root with a Rust toolchain supporting edition 2024:

```text
cargo run -p minimal-game-client
cargo run -p minimal-game-client -- --smoke-frames 3
cargo run -p minimal-game-server
```

The client runs until the window closes. The server runs at a configured 60 Hz by
default; Ctrl+C terminates the process. The server runner supports orderly shutdown when
runtime code requests exit or the registered MCP `stop` tool receives a request.

The client maps WASD and arrow keys to shared gameplay movement commands; the world
sprite follows the entity while the HUD icon stays at the top-left. Both share the same
PNG and quad pipeline. The input model
supports gamepads, but there is no native gamepad provider.

The smoke limit bounds client-session frames. It does not count only successful GPU
presentations, so it is not proof that every requested frame reached the display. See
[the validation checklist](TODO.md#native-host-validation).

```text
cargo check --workspace
cargo test --workspace
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
```

The extended native smoke test opens a temporary client window and exercises its own
client/server/bridge processes on an isolated port:

```text
cargo build -p nico-bridge -p minimal-game-client -p minimal-game-server --target-dir target/bridge-validation
python apps/nico-bridge/tests/native_smoke.py --bin-dir target/bridge-validation/debug
```

It cleans up only its test processes. Recorded results belong in the
[roadmap](docs/roadmap.md#2-control-clients-and-servers-with-ai).

Both executables accept `--log-level <off|error|warn|info|debug|trace>`. Diagnostics go
to stderr. An explicit level overrides `RUST_LOG`; the default is `info`. The server
also accepts `--tick-rate <TICKS_PER_SECOND>`:

```text
cargo run -p minimal-game-server -- --tick-rate 30 --log-level debug
cargo run -p minimal-game-client -- --log-level trace
```

For targeted diagnostics in PowerShell:

```powershell
$env:RUST_LOG = "nico_runtime=trace"
cargo run -p minimal-game-client
Remove-Item Env:RUST_LOG
```

## Minimal host control

The engine's `nico_launch::server::FixedRateServerRunner` accepts an optional
`nico_ops::HostEndpoint`. Another thread can retain its `HostControl` to read status or
request stop:

```rust,ignore
let (control, operations) = nico_ops::control_channel();
let runner = FixedRateServerRunner::new(step).with_operations(operations);
// Move `runner` to the host and retain `control` in an adapter.
let snapshot = control.status();
control.request_stop()?;
```

Run the complete [controlled server
example](games/minimal-game/server/examples/controlled.rs):

```text
cargo run -p minimal-game-server --example controlled
```

It starts the real server, observes readiness after one successful host tick, requests
stop from another thread, and prints the final status. Repeated stop requests coalesce.
Stop and last-controller disconnect wake the server's paced wait; shutdown still runs on
the host thread. Final success/failure remains readable after the host endpoint closes.

Snapshots are cached host reports; stop acceptance is distinct from completion, and host
completion does not imply operating-system process exit.

The native client accepts the same endpoint through
`nico_winit::run_native_client_with_operations(app, config, map_input, endpoint)`. Stop
and last-controller disconnect wake the Winit loop, including without redraws; shutdown
stays on the host thread. Readiness requires the first successful GPU presentation.
`completed_steps` counts session frames, including skipped GPU presentations, and can
advance before readiness. Readiness stays latched during suspension until shutdown
begins; `active` separately reports host activity. `graphics.presented_frames`
separately counts successful presentation API calls. A blocked startup or game callback
delays stop.

Run the [controlled client
example](games/minimal-game/client/examples/controlled_client.rs) to open a window,
observe GPU readiness, request stop, and print final status:

```text
cargo run -p minimal-game-client --example controlled_client
```

This in-process example has a 15-second readiness deadline and requests orderly stop on
timeout. Use the bridge below for MCP access to independently launched clients and
servers.

## Agent access through MCP

### Connect the bridge

Build the bridge and game executables:

```text
cargo build -p nico-bridge -p minimal-game-client -p minimal-game-server
```

Register **only the bridge** with Codex. Remove any old `nico-client` and `nico-server`
executable entries first; those entries either fail or launch a game as part of
connecting MCP.

```powershell
codex mcp remove nico-client
codex mcp remove nico-server
codex mcp add nico-bridge -- E:\repos\Nico\target\debug\nico-bridge.exe
```

Adjust the executable path for your build output. Equivalent Codex configuration:

```toml
[mcp_servers.nico-bridge]
command = "E:/repos/Nico/target/debug/nico-bridge.exe"
args = ["--listen", "127.0.0.1:47631"]
```

Codex may launch this bridge; it starts no game processes. Reload/restart the MCP
connection after changing configuration. The bridge's stdout carries MCP only. Its
loopback TCP listener accepts game registrations, not MCP HTTP requests. Use matching
`--listen` and `--bridge` addresses if running multiple bridges.

Start games yourself, in separate terminals:

```text
cargo run -p minimal-game-server
cargo run -p minimal-game-client
```

Games can start before the bridge and reconnect when it becomes available. Launches with
`--no-bridge` disable connection attempts. The default connection retries in the
background, so games also run normally when the bridge is unavailable. Use `--bridge
ADDRESS` to override the default endpoint. On Windows, stop and exit a game before
rebuilding its executable; restarting a game does not require restarting the bridge.

### Discover and call tools

The bridge always exposes these tools:

| Tool | Arguments | Result |
| --- | --- | --- |
| `bridge_status` | `{}` | Connected instance count and cache counts |
| `list_instances` | `{}` | Connected and retained disconnected instances |
| `instance_status` | `instance_id` | Cached host state, activity, readiness, PID, connection state, and snapshot age |
| `list_game_tools` | `{}` | Cached game/role API versions and original tool schemas |
| `call_game_tool` | `instance_id`, `tool_name`, `arguments` | Routed game tool result or structured error |

On connection, both minimal-game hosts upload `status`, `stop`, `diagnostics`, and the
game-owned `game_state` tool. The bridge advertises names such as
`minimal_game.server.game_state` and `minimal_game.client.stop`, each taking
`instance_id` and an `arguments` object. For example, after selecting a connected server
ID from `list_instances`:

```json
{
  "name": "minimal_game.server.game_state",
  "arguments": {"instance_id": "<connected-instance-id>", "arguments": {}}
}
```

If your MCP client does not refresh dynamically advertised tools, use the fixed fallback
with the original game tool name:

```json
{
  "name": "call_game_tool",
  "arguments": {
    "instance_id": "<connected-instance-id>",
    "tool_name": "game_state",
    "arguments": {}
  }
}
```

Schemas stay discoverable after game disconnection until the bridge exits. A fresh
bridge starts with an empty game catalog and learns it again from registrations. A
reconnect gets a new instance ID; old IDs never route to replacement connections.
`connected` means a live registration connection, while `host.active` is the last host
activity report. A disconnected snapshot is historical and does not prove that the
process exited. Client readiness requires successful GPU presentation; server readiness
requires a successful tick. Readiness can stay latched during client suspension, while
`host.active` becomes false.

Game state tools include `snapshot_sequence`, `snapshot_age_ms`, and `closed`.
Age measures time since publication; reading a snapshot does not refresh it.
Arena `client_state` window fields describe its last published frame; use
`window_state` for current host observations. The rendering sample records
cancellation outcomes for queued commands when it shuts down.

Calling a registered `stop` tool explicitly requests shutdown of that instance.
Acceptance does not prove completion or OS process exit. Bridge/Codex disconnect never
requests game shutdown. Calls have a five-second deadline; after a timeout or connection
loss, a mutation may already have executed and should not be retried blindly. Offline
calls return `instance_unavailable`.

For game extensions, see the [minimal-game
tools](games/minimal-game/shared/src/tools.rs) and [operation
ownership](docs/architecture.md#ai-operation-requirements).

The bridge is for trusted local development processes: it binds only to loopback and has
no authentication. Limits, compatibility rules, and verification scope are documented in
the [bridge plan](docs/plans/2026-09-11-mcp-bridge.md).

### Graphics status

The registered client `status` tool includes `graphics.presented_frames` and
`graphics.last_outcome`. Successful calls to the presentation API increment the counter;
zero-sized, timed-out, or occluded frames do not. Outcomes are `not_attempted`,
`presented`, `zero_sized`, `timeout`, `occluded`, `initialization_failed`, or
`render_failed`. Error details remain in host failure status and diagnostics. These
counts are independent of `completed_steps` and are retained across suspension and
shutdown; they do not prove monitor scanout or GPU completion. Servers report `graphics:
null`. Updated bridges retain this optional graphics report in cached host snapshots
too. Rebuild and reload older bridge binaries to expose these fields in
`instance_status`.

### Diagnostics

Native hosts also register `diagnostics`. Invoke it through `call_game_tool`:

```json
{"instance_id":"<connected-instance-id>","tool_name":"diagnostics","arguments":{"after":0,"limit":8}}
```

The response contains `records`, `next_cursor`, `oldest_cursor`, `latest_cursor`,
`dropped`, `has_more`, and `capacity`. Start with `after: 0`, then pass `next_cursor` as
`after` to read subsequent pages. `dropped` counts records evicted since your cursor; it
does not count events excluded by the logging filter. Invalid or future cursors return
`invalid_arguments`. Cursors are process-local: retain them across bridge reconnects to
the same process and reset to zero after a game restart.

Capture uses the same `--log-level`/`RUST_LOG` filter as stderr. Each native process
retains up to 256 tracing events, with eight records per page. Events have UTC Unix
millisecond timestamps, levels, targets, typed fields, and a `truncated` flag. Capture
limits each event to eight fields, 1,024 total field-value text bytes, 64-byte field
names, and a 128-byte target. Span lifecycle output and span timing are not captured.
Custom hosts must call `init_logging` to install capture. History survives bridge
reconnects but is lost on game exit; the bridge caches schemas and host status, not
diagnostic records. Retrieve diagnostics while the instance is connected. A game
extension named `diagnostics` conflicts with the native host tool and causes host setup
to fail.

Client and server executables do not serve MCP directly. Their host and game tools are
uploaded on bridge connection; `--mcp-stdio` is not supported.

## Shader workflow

Shader sources and generated WGSL live under
[`assets/presentation/shaders`](assets/presentation/shaders). The bootstrap path loads
`bootstrap.wgsl`; the 2D sample loads `quads.wgsl`; the 3D sample loads `meshes.wgsl`
and `quads.wgsl` for its HUD.

Keep all three generated WGSL artifacts committed. Normal Cargo builds and native
launches do not require Slang or regenerate shaders. `nico-shaderc` processes all
three sources, and `--check` detects stale generated output.

To regenerate or check the artifacts, make `slangc` available through `PATH`,
`NICO_SLANGC`, or the tool's `--slangc <PATH>` argument:

```text
cargo run -p nico-shaderc
cargo run -p nico-shaderc -- --check
```

The tool searches upward from its working directory for the shader root. Use `--root
<PROJECT>` to specify it explicitly. Shader compilation is separate from Cargo builds;
changing the generated artifact does not relink Rust crates.

The client reads the WGSL file synchronously and waits for GPU initialization and
pipeline creation before its first redraw. This is a direct bootstrap file load, not a
general asset loader. Offline WGSL generation still leaves backend shader and pipeline
preparation at runtime. The reported roughly one-second startup delay has not been
profiled; its cause remains unconfirmed.

## Texture loading

Enable `nico-assets`' optional `loading` feature for native background PNG loading.
`TextureStore::install` registers an immutable `AssetId` to relative-path catalog,
starts one worker, and installs runtime publication and shutdown systems. Register it
before texture consumers. Acquire an `AssetLease<Texture>` through the store; cloned
leases share a load, while copyable `Handle<Texture>` values retain identity only.
Inspect `TextureState` for loading, CPU-ready content, or a structured failure. Failed
loads require explicit retry; dropping the last lease permits release on the next update.

Run the headless sample, which loads the checked-in PNG, consumes its dimensions/pixels,
releases it, and joins the worker:

```sh
cargo run -p nico-assets --features loading --example load_texture
cargo test -p nico-assets --features loading
```

The decoder normalizes static PNGs to RGBA8 with straight alpha. Color bytes are
interpreted as sRGB; ICC/gamma conversion is not implemented. Defaults cap resident
entries at 64, input at 16 MiB per file, dimensions at 4096, and decoded buffers at
64 MiB each. The decoder also receives a 64 MiB internal allocation limit; these are
separate bounds, not a single process-memory budget. Animated PNG is rejected.
Shutdown joins active local I/O/decoding, so cancellation is not an immediate interrupt.
Content paths are trusted host configuration, not a filesystem sandbox.

GPU upload and both 2D and 3D samples with a shared HUD are implemented.
Ready CPU pixels are pinned with an `Arc` in immutable presentation snapshots. See the
[texture design](docs/plans/2026-09-14-texture-assets.md) for lifecycle details.

## 2D world and HUD sample

The native client loads `textures/sample.png` from
`games/minimal-game/assets/presentation`; `--asset-root PATH` overrides that directory.
The original 2x2 fixture intentionally makes texture orientation and transparency visible.
Missing/loading/failed textures draw a magenta/dark checkerboard. Shader artifacts are
compiled from `quads.slang` with the existing `nico-shaderc` command.

World coordinates use X right and Y up, with the camera centered in the viewport and
128 logical pixels per world unit. The one-unit sprite follows shared `Position` state.
The HUD icon is 48 logical pixels square and centered at (48,48) from the top-left,
independent of the world camera. DPI scales both drawing paths. World quads draw in
list order before HUD quads, using nearest sampling and straight-alpha blending.
The pipeline supports at most 4096 quads per frame. Text, clipping panels, rotation,
and UI layout/interaction are not implemented.

After discovering a client through the bridge, use `list_game_tools` and `call_game_tool`:

| Game tool | Operation |
| --- | --- |
| `sample_state` | Read positions, camera, draw counts, CPU texture state, and last applied command ID |
| `sample_control` | Queue `set_position` or `set_camera` with `x`/`y`; `set_sprite_visible` or `set_texture_enabled` with boolean `value`; or `retry_texture` |

Controls have a 32-request bound and apply at Update. `set_position` is a validation
teleport; keyboard input still uses shared gameplay movement. Accepted command IDs
must be reconciled with `last_applied_command` and the matching entry in
`command_results` (the latest 32 outcomes). An older outcome may have been evicted;
absence is not success. `command_error` describes only the most recently applied edit. Acceptance does
not mean the change was applied or rendered. CPU texture readiness is separate from
host successful presentation counts. Timed-out edits must not be retried blindly.

The native smoke test exercises these controls. Opt-in GPU pixel validation runs with:

```sh
cargo test -p nico-rhi-wgpu gpu_ -- --ignored --nocapture
```

These tests need a graphics adapter; portable workspace tests leave them ignored.
They check rendered offscreen pixels, not window scanout.

## 3D mesh and shared HUD sample

```sh
cargo run -p minimal-game-client -- --sample 3d
```

The default `--sample 2d` draws a sprite; `3d` loads `meshes/cube.glb`, mapping
shared positions to world XY at Z=0. The perspective camera looks toward the origin.
The cube uses an opaque 128x128 UV checker (`textures/uv-checker.png`), with A1–D4
labels and colored corners. The HUD keeps the transparent 2x2 PNG. Meshes use depth
testing and an unlit texture with alpha cutoff 0.5. The fixed HUD
shares the quad renderer and texture cache; it draws after meshes without depth testing.
Missing meshes use a tetrahedron, and missing textures use the checkerboard.

`MeshStore` and `TextureStore` specialize the same `AssetStore<T>` lifecycle.
Mesh-only GLB accepts one indexed triangle primitive with float positions and UVs,
embedded geometry, and identity node transforms. Materials, external buffers, scenes
with multiple nodes, skins, animations, and sparse accessors are unsupported. Defaults
limit a mesh to 250,000 vertices and 750,000 indices; drawing caps instances at 256.
See the [mesh contract](docs/plans/2026-09-14-mesh-assets.md) for bounds and ownership.

The registered `sample_state` includes mode, mesh readiness, counts, camera and yaw.
In 3D mode, `sample_control` also accepts `set_mesh_enabled` with boolean `value`,
`retry_mesh`, `set_camera3d` with `x`/`y`/`z`, and `set_mesh_yaw` with `radians`.
`set_camera_orientation` accepts a finite unit quaternion with `x`/`y`/`z`/`w`,
allowing vertical views and roll. `sample_state.camera3d_orientation` reports XYZW;
`set_camera3d` restores origin targeting. Arena `client_state` also reports camera
`orientation` in XYZW order alongside its game-specific orbit controls.
These use the same queued command IDs and outcome history as the 2D controls.
`checker_state`, `checker_error`, and `checker_resident` expose the cube texture
separately from the HUD texture. `set_texture_enabled` and `retry_texture` apply to
both textures in 3D mode. Retry queues only failed textures and reports `NotFailed`
when neither texture has failed.

After building the bridge and hosts into an isolated target directory, validate each
mode with `python apps/nico-bridge/tests/native_smoke.py --bin-dir
 target/texture-validation/debug --sample 2d` or `--sample 3d` (on one command line).

## Window controls through MCP

Native clients register `window_control` and `window_state`; discover the connected
instance and schemas first. Examples of `window_control` arguments:

- `{"action":"resize","width":800,"height":600}` uses logical pixels, bounded to
  widths 320..3840 and heights 240..2160.
- `{"action":"maximize"}`, `{"action":"minimize"}`, or `{"action":"restore"}`.
  Restore clears both minimization and maximization.
- `{"action":"focus"}` requests foreground activation of this client window.

Acceptance returns a `request_id`. Poll `window_state` with that ID for
`pending`/`applied`/`failed`; an empty object reads just the observed window state.
Verify actual size, focus, minimized/maximized state, and snapshot age: `applied`
means the platform call returned, and the window manager may ignore it. Minimized
is null when the platform cannot report it. Pointer capture is reported separately.
Only one request and its latest outcome are retained; a new request expires the old
ID. Requests wake the native event loop even when rendering is stopped. No timeout
implicitly cancels an operation; reconcile by ID before issuing another mutation.
Shutdown cancels pending work. Use the existing `stop` tool for orderly shutdown.

## Window snapshots through MCP

Discover the connected client and its tool schema, then call `window_snapshot` with
`{}`. Poll with `{"request_id": 1}` (using the returned ID) until `ready` or `failed`.
A ready result includes physical pixel dimensions and an absolute local PNG path.
Native client hosts register this tool; servers do not expose it.

Captures include rendered content and HUD before presentation, excluding desktop
borders and other windows. They establish GPU readback completion, not display scanout.
Only one request/result and one PNG file are retained per process. A newer request
expires the previous ID, and its completed PNG overwrites the previous file. Copy the
PNG first if you need both. Files live in the host's system temporary directory.

Requests require an active, ready host. Polling reports timeout after five seconds
if no capture completes. Unsupported surface copies/formats, skipped rendering, GPU
failures, and shutdown produce errors. Dimensions are limited to 4096x4096 with
RGBA8/BGRA8 surface formats. On-demand readback can pause rendering for the two-second
GPU wait. The first retrieval of captured pixels starts a background PNG encoder;
polls remain `pending` until it finishes. Encoding may take longer than the five-second
capture deadline, while bridge calls and heartbeats continue. Only one encoding job
runs per host; new captures return `snapshot_encoder_busy` until it finishes. Failures
are retained, and host shutdown joins active encoding/file I/O. This is diagnostic
capture, not continuous video recording or profiling.

## Workspace

| Location | Responsibility |
| --- | --- |
| `crates/nico-ecs` | World, resources, and hecs entity/component storage |
| `crates/nico-runtime` | Lifecycle, scheduling, fixed time, events, and services |
| `crates/nico-input` | Provider-neutral device state and frame-to-fixed-step accumulation |
| `crates/nico-presentation` | Immutable 2D/3D draw snapshots and optional runtime lifecycle |
| `crates/nico-presentation-control` | Camera control, coordinate helpers, and cached bitmap text |
| `crates/nico-spatial` | Headless sphere/box queries and bounded circle sliding |
| `crates/nico-render` | Bootstrap, textured quad and mesh pipelines, uploads, and frame recording |
| `crates/nico-rhi` | Backend-neutral GPU contracts |
| `crates/nico-rhi-wgpu` | Concrete wgpu resources, device, and surface recovery |
| `crates/nico-winit` | Native event loop, input adaptation, client coordination, and optional in-process host control |
| `crates/nico-assets` | Asset identity, leases, procedural meshes, and optional PNG/GLB loading |
| `crates/nico-launch` | Native CLI, diagnostics, and optional client/server transport composition |
| `crates/nico-ops` | Host control, command bookkeeping, publication, optional tool catalogs and bridge transport |
| `apps/nico-bridge` | MCP entry point for independently launched game instances |
| `apps/nico-shaderc` | Standalone offline shader compiler tool |
| `assets/presentation/shaders` | Engine shader sources and committed WGSL artifacts |
| `games/minimal-game` | Shared gameplay, client/server executables, and game asset roots |
| `games/arena-arpg` | Shared arena combat and native client/server executables |

See [architecture](docs/architecture.md) for dependency direction and contracts, and
[AGENTS.md](AGENTS.md) for contribution rules. Physics, audio, UI, and broader devtools
have no placeholder crates.

## Documentation

- [Architecture](docs/architecture.md): ownership, runtime contracts, and required tooling boundaries.
- [Roadmap](docs/roadmap.md): phase goals, scope, status, completion criteria, and validation evidence.
- [TODO](TODO.md): concrete next actions and outstanding validation.
- [Review guide](docs/review.md): checks for proposed changes.
- [ADR 0001](docs/decisions/0001-native-client-event-loop.md): native event-loop ownership.
- [ADR 0002](docs/decisions/0002-nico-rhi-wgpu-backend.md): RHI and wgpu provider.
- [ADR 0003](docs/decisions/0003-render-pipeline-layer.md): rendering policy.
- [Repository guidelines](AGENTS.md): contributor and agent instructions.
- [Game asset roots](games/minimal-game/assets/README.md): logic and presentation ownership.
