# Nico

Nico is an experimental Rust 2024 game engine with a headless runtime, shared
client/server gameplay, a persistent multiplayer action RPG prototype, and textured 2D/3D
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

Install Git LFS before checking out game assets. For a fresh clone or an existing
checkout, run from the repository root:

```sh
git lfs install --local
git lfs pull
```

[.gitattributes](.gitattributes) tracks models, geometry buffers, Blender sources,
and textures through LFS. glTF JSON, material descriptions, TOML, manifests, and
license notices remain ordinary Git files. `git add` stores matching assets as
LFS pointers while keeping full files in the working tree; the LFS pre-push hook
uploads their contents. The remote must support LFS. Installing LFS alone does
not convert older commits; history migration is a separate operation that changes
commit IDs and requires coordinating any update to a shared remote branch.

The default game is a shared outdoor world with a settlement and monster camp.
Run these commands in separate terminals from the repository root:

```text
cargo run -p arena-arpg-server
cargo run -p arena-arpg-client -- --character alice
cargo run -p arena-arpg-client -- --character bob
```

The server owns movement validation, combat, monsters, loot and progression.
Clients predict local movement and receive nearby entities at 20 Hz. Each character
name must be unique among active connections: use 1..32 lowercase ASCII letters,
digits or underscores. This milestone uses local development identities and
loopback networking, not Internet accounts or MMO-scale infrastructure.

WASD moves, mouse motion aims the camera, left click attacks, Space dodges,
E picks up nearby loot, F equips the iron sword, R respawns after death, and Q
reconnects. Click to capture the pointer; Escape releases it. The server saves
position, health, XP, inventory and equipment every five simulation seconds and
on disconnect/orderly shutdown. Monster state is not persistent.

The game endpoint defaults to `127.0.0.1:47640`: override it with server `--listen`
and client `--server`. Server `--data-dir` defaults to `target/world-data`; choose
a durable directory outside `target` to keep saves across build cleanup. One
server owns each data directory. `--world-asset` and `--item-asset` select the
authored zone and loot definitions. Both game modes require 60 Hz simulation.

Through the bridge, discover the current instances and tool catalog. World servers
expose `world_state` and queued `world_spawn`; clients expose `world_client_state`,
`client_characters`, and queued `world_action` (movement, attack, dodge, pickup,
equip, respawn, reconnect and camera). A submitted client command is not proof of
server success: compare its epoch/sequence with the authoritative acknowledgement
and resulting world state. `last_disconnect`, prediction backlog and snapshot age
are reported separately. Built-in host/window tools remain available.

See the [world milestone](docs/plans/2026-09-17-open-world.md) and
[native checkpoint](docs/reviews/2026-09-17-open-world-native.md) for validation
scope and remaining work.

Run the isolated two-client scenario after building the binaries:

```text
cargo build -p arena-arpg-client -p arena-arpg-server -p nico-bridge --target-dir target/bridge-validation
python apps/nico-bridge/tests/world_native_smoke.py --bin-dir target/bridge-validation/debug
```

It opens two test windows, uses private bridge/game ports and temporary character
saves, and stops only its own processes. Captures, logs and a JSON report go to
`target/world-native-evidence` (override with `--output-dir`). It validates shared
combat, dodge, loot/equipment, death/respawn, reconnect and server restart through
MCP. GPU readbacks and separately sampled state do not establish desktop visibility.

### Standalone arena combat test

The arena ARPG prototype has a third-person native client and a headless server,
with shared melee, dodge, monster pursuit, collision, and win/loss/restart rules.
Its camera uses engine orbit and collision mechanics; the game supplies the hero
target, tuning, and arena geometry. Camera and mesh orientations use normalized
quaternions; orbit controls retain yaw/pitch limits. See the
[architecture](docs/architecture.md#assets-and-game-construction) for engine ownership.
Clear three waves: three grunts, then two grunts and one brute, then one grunt and
two brutes. Puglin brutes move slowly and hit farther, with a longer windup.
Between waves, a three-second countdown resets positions and restores 40 health
(up to 100). Clearing the final wave wins.
Run from the repository root:

```text
cargo run -p arena-arpg-client -- --arena
cargo run -p arena-arpg-server -- --arena
```

Click the viewport to capture the pointer. WASD moves relative to the camera,
mouse motion looks around, left click attacks, Space dodges, R restarts, and Escape
releases the pointer. The capture click does not attack. Dodge can cancel sword recovery; a press up to
150 ms before readiness is buffered. Earlier presses are ignored. Monster strikes
are spaced at least half a second apart. Pale-gold pulses and an overhead warning
mark the final 200 ms of windup. The shared client option `--background` opens the
window without requesting focus and is available to both arena and minimal clients.
Focus loss releases input; simulation continues. In `--arena` mode, client and
server run independent solo encounters; use the default world mode for multiplayer.

Both hosts attempt the default bridge connection; `--no-bridge` disables it and
`--bridge ADDRESS` overrides it. The client supports `--smoke-frames N`. Default
visuals use the game-owned imported hero alongside animated Bestiary monsters,
textures, and bitmap text. `--procedural-hero` skips only the hero's imported model
and animation data. Attack sectors show full reach with a growing windup
fill and a yellow active strike; the HUD shows dodge cooldown progress. Rendering
uses the repository's generated shaders. The [imported hero](#imported-arena-hero)
supports explicit asset-path overrides.
See [phase 5](docs/roadmap.md#5-make-a-playable-local-game) for validation limits.

### Arena operations

With `arena-arpg-shared`'s `tools` feature enabled, call
`arena_arpg_shared::tools::register(builder.add_plugin(ArenaPlugin))` and pass the
returned `ToolExtensions` to the engine host. This unit plugin uses the embedded
catalog; native hosts use `ArenaPlugin::with_characters` with the disk-loaded
catalog. Use `FIXED_STEP` for the runtime.
Registration adds game handlers and snapshot publication; engine hosts own the bridge
connection. Both hosts compose this adapter in `--arena` mode, registered as `arena_arpg`;
the minimal-game rendering samples keep their separate tools.

| Tool | Purpose |
| --- | --- |
| `game_characters` | Inspect the loaded authoritative character catalog. |
| `client_characters` (client only) | Inspect visual definitions and resolved hero model/clip/socket parameters. |
| `game_state` | Read run/wave/countdown, actor type and combat stats, health, snapshot age, and action phases. |
| `game_move` | Queue world-space movement for 1..120 simulation ticks; zero direction waits. |
| `game_move_hold` | Start or renew continuous movement without a release gap; optionally change direction. |
| `game_move_release` | Release a specific movement hold at the next fixed boundary. |
| `game_attack` | Request melee facing yaw radians, with zero along +Z. |
| `game_dodge` | Request a dodge along a nonzero world-space direction. |
| `game_restart` | Reset the current run and cancel old actions. |
| `game_command` | Poll acceptance/execution outcome by command ID. |
| `client_state` (client only) | Read camera, draw counts, and window fields from the last published frame, with age and closed state. |
| `client_control` (client only) | Queue `camera` with yaw/pitch and optional `distance` (0.5..12 metres); omitted distance preserves zoom. |

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

For continuous control, call `game_move_hold` with
`{"run_id":1,"lease_id":0,"x":1,"z":0,"ticks":120}` (using the current run).
The returned `command_id` is the hold's lease ID. Before it expires, call the same
tool with that `lease_id`, a direction, and a fresh 1..120 tick budget. Renewals
return separate command IDs that complete when applied; keep using the original
lease ID. Renew well before expiry, for example every 30 ticks for a 120-tick hold.
Release with `game_move_release` and `{"run_id":1,"lease_id":LEASE_ID}`.
`game_state.movement_hold` exposes the lease, direction, remaining ticks and
pending/running state. Poll each edit's command result to distinguish acceptance
from application. A hold ends `cancelled` with `released` or `lease_expired`;
late edits are rejected with `inactive_lease` and never revive old movement.

Timeouts count **simulation ticks**, including ticks where combat blocks movement;
they are not wall-clock deadlines during suspension. Bridge disconnect does not
release immediately: without renewals the remaining tick budget expires. Manual
movement, focus loss, restart, wave clear, defeat and shutdown also cancel holds.
Finite `game_move` requests and holds share one movement slot. One queued
renew/release edit is allowed at a time; additional requests return `busy`.

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

The test uses the game-owned imported hero by default. Run the three-wave victory,
defeat, and restart checks without window transitions:

```text
python apps/nico-bridge/tests/arena_native_smoke.py --bin-dir target/texture-validation/debug --combat-only --output-dir target/arena-imported-evidence
```

Use `--procedural-hero` to test the original visuals, or supply both
`--character-model` and `--character-animations` to override the game assets.
The report identifies instance IDs,
PIDs, character mode, wave outcomes, and captures with separately sampled state.
Automated results do not establish desktop visibility or user-observed success.

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

## Extending asset import

Game and external Rust crates can implement `nico_assets::import::AssetImporter`
for an existing CPU asset type such as `Texture`, or for their own `Send + Sync`
output type. Register implementations in `ImportRegistry<T>`, then configure each
asset with an explicit importer token, relative source path, typed settings, and
`ImportBudget`. Multiple importers may produce the same type; overlapping file
extensions do not select or override an importer automatically.

The [public example](crates/nico-assets/src/import.rs) demonstrates importing text
without a runtime. `ImportRegistry::import_bytes` uses supplied bytes; for background
file loading, pass the registry to `AssetStore::install_with_importers` before
consumer systems. `source`/`sources` expose configured provenance, and import
failures carry importer identity, category, diagnostic code, bounded text, and a
truncation flag. Loading remains explicitly retried after failure.

| Feature | Capability |
| --- | --- |
| Default | Dependency-free CPU assets, importer interface, typed registry, offline byte import |
| `png-import` | `PngImporter` and `PngSettings`, without runtime |
| `gltf-import` | Static mesh and generic model GLB importers, without runtime |
| `runtime-loading` | Background `AssetStore<T>` loading, without built-in decoders |
| `loading` | Compatibility combination of all three optional features |

Importers receive bytes and cooperative cancellation/budget controls, never the
world or GPU. Userland code is trusted; output accounting is cooperative and
shutdown waits for active decoding. External source dependencies and hot reload
remain future work. See the
[import contract](docs/plans/2026-09-16-extensible-asset-import.md).

## Model and humanoid animation

`ModelGlbImporter` loads an immutable `Model` bundle containing node hierarchies,
meshes, skins/inverse bind matrices, STEP/LINEAR clips, core materials, and embedded
PNG/JPEG bytes. Images remain encoded. Its supported subset and configurable
bounds are described in the [model contract](docs/plans/2026-09-16-model-animation.md).
The static mesh importer and static rendering behavior remain compatible.

`nico-animation` evaluates CPU poses and skin matrices. Its humanoid layer maps
22 body roles through userland profiles, with Mixamo/RPG presets, explicit indexed
overrides for duplicate names, reference-pose corrections, proportional translation,
and in-place/preserved root-motion policies. Unmapped finger/helper joints retain
their reference local poses. Humanoid rigs retain an `Arc<Model>` and compile their
mapping/reference corrections once. `PoseBuffer` and `HumanoidWorkspace` provide
reusable per-instance evaluation storage; failures preserve the previous valid pose.
The native preview uses GPU skinning. `AnimationPlayer` supports looping, one-shot
completion, crossfades, and continuous interruption. Named `Attachment` sockets
follow evaluated joint poses. The arena can use a local imported hero. Bounded
crowd preview and update policies are implemented. The
[fresh review](docs/reviews/2026-09-16-uncommitted-mcp-review.md) records current
validation, including the imported-hero three-wave victory; earlier 16/64-character
measurements have not been revalidated. The
[production plan](docs/plans/2026-09-16-production-animation.md) defines the scope.

The headless examples emit JSON and accept local assets without starting a game:

```sh
cargo run -p nico-assets --features gltf-import --example inspect_model -- games/arena-arpg/assets/presentation/characters/hero/model.glb
cargo run -p nico-animation --example inspect_retarget -- games/arena-arpg/assets/presentation/characters/hero/model.glb games/arena-arpg/assets/presentation/characters/hero/animations/RPG-Character@Unarmed-Idle.glb
```

These paths refer to the selected files in the arena asset folder. The examples
explicitly permit core-material fallback for optional specular/IOR extensions.
CPU deformation checks do not establish native visual quality.

### Character definitions

Hero, grunt and brute each have a `<name>.char.toml` logic asset under
`games/arena-arpg/assets/logic/characters/` and a `<name>.char-vis.toml` visual asset
under `games/arena-arpg/assets/presentation/characters/`. Edit logic for health,
movement, collision radius, attacks and dodge; edit visuals for model selection,
rigs, grip, sockets, weapon geometry, animation timing and procedural body parts.
Both files use explicit `[core]` and `[arena]` sections. Core descriptors come from
`nico-assets::character`; arena rules remain game-owned. Visual
`core.animations.<name>` entries select clips, and `arena.animations.<action>`
bindings select a library entry and its playback/timing settings.
Both hosts load logic; only the client loads presentation. Restart after editing.

Use `--logic-characters <directory>` on either host and `--visual-characters
<directory>` on the client to select another catalog. The three filenames remain
`hero`, `grunt`, and `brute`; matching IDs link the files. Through the bridge,
`game_characters` and `client_characters` inspect the loaded content. The
[format specification](docs/plans/2026-09-17-character-definitions.md) defines units,
validation, supported content and ECS ownership, with complete examples.

### Imported arena hero

The default world and `--arena` combat mode load the imported hero and animated
Bestiary monsters. `--procedural-hero` changes only the hero to procedural visuals.
Paired `--character-model` and `--character-animations` overrides replace the hero
files while retaining the selected definition's rig and clip settings.

| Character | Model | Animation sources |
| --- | --- | --- |
| Hero | Ch03 | Library 2 sword attack, earlier Quaternius sword idle, RPG run/roll/death |
| Grunt | Bestiary Imp | Library 2 idle/walk/attack, RPG death |
| Brute | Bestiary Puglin | Library 2 idle/walk/attack, RPG death |

The game binds five motions: `idle`, `run`, `attack`, `dodge`, and `death`. Monsters
have a dodge binding for the shared format, but their logic does not dodge.
Nonlethal damage does not interrupt animation: combat has no injury/stun state.
Attack and dodge playback follow authoritative action timing; animation never
controls damage, movement, or collision. Each entity owns its player and shares
immutable model/clip assets with other instances of its type.

The hero's sword uses an authored hand grip and socket; monsters use weapons
already skinned into their models. The [format contract](docs/plans/2026-09-17-character-definitions.md)
owns socket, rig, timing, and loading rules. See the [hero provenance](games/arena-arpg/assets/presentation/characters/hero/LICENSE.md)
and [monster content notice](games/arena-arpg/assets/presentation/characters/monsters/README.md)
for selected files, reproduction commands, licenses, and limitations. Imported
source packs live under [quaternius](games/arena-arpg/assets/presentation/quaternius/README.md).
The renderer uses base-color textures; full material fidelity and foot/weapon-contact
polish remain unfinished.

Through the bridge, `client_characters.resolved` inspects all three loaded types.
`client_state.animation` (arena) and `world_client_state.animation` (world) report
the local hero's playback. Their `actor_animations` arrays expose individual
players; world entries include stable object IDs. State includes motion, clip
index/time/completion, fade weight, model draw count, bounds, and visibility.
Attached-weapon fields are inactive for monsters with embedded weapons.

Content loads before host startup; invalid or missing imported assets fail startup.
The scene is limited to 256 draws. The [Bestiary review](docs/reviews/2026-09-17-bestiary.md)
separates native capture evidence from the later removal of injury playback.

### Native character preview

Run from the repository root with locally supplied assets:

```sh
cargo run -p nico-character-preview -- --model tmp/Ch03_nonPBR.glb --animation tmp/animations/RPG-Character@Unarmed-Attack-L1.glb
```

Omit `--animation` to play the model's own clips; add `--bind-pose` to start paused
at its reference pose. External animation currently uses the RPG-to-Mixamo presets.
Repeat `--animation PATH` to load multiple files, then use A/D to select clips.
Space pauses/resumes, R restarts, arrows orbit, and W/S zoom. Add `--once` to hold
the final frame instead of looping. Close the window to exit. The animation set
accepts at most 64 files and 256 MiB of aggregate source-file bytes (not a total
heap-memory bound), with at most 128 clips and printable display labels of at most
128 UTF-8 bytes. Omitted-extension diagnostics expose at most 32 names of 64
characters each and report the total count and truncation flag.

Add `--characters 16` for a shared-asset grid (1..64, with at most 256 total model
draws). Instances start at different clip phases and keep independent state. Add
`--update-hz 15` to cap pose evaluation; the default `0` evaluates every Update.
A cap deliberately holds the displayed pose between evaluations, so low rates look
stepped. Elapsed time accumulates without changing playback speed. Camera changes
force fresh evaluation; edits flush pending elapsed time before applying.

MCP `{"action":"select","value":3}` selects an instance for playback controls.
`{"action":"update_hz","value":15}` changes its cap (0 or 1..120); the effective
sampling rate cannot exceed host Updates. `position` with `x/y/z` moves the selected
instance; `target` with `x/y/z` moves the shared camera target. Keyboard playback
controls affect the selected instance. `preview_state.instances` exposes each
instance's sampled clip time, pending elapsed seconds, position, bounds, visibility,
and update cap. Top-level playback fields describe the selected instance; draw and
visible/evaluated counts describe the entire scene. This is a held-pose policy,
not prediction of future animation bounds or automatic distance-based LOD.

The HUD shows Update FPS plus average and maximum frame intervals in milliseconds.
Readings refresh after each non-overlapping window of at least one second. They
measure wall-clock time between preview Updates, including rendering/waits and
stalls; they are not GPU-completed FPS or CPU execution time. The first window shows
`WARMING UP`. `preview_state.frame_timing` exposes the same measurement with exact
FPS, mean/min/max milliseconds, sample count, and window duration (null initially).
Long gaps, including suspension, remain visible in the next reading.

The preview registers `preview_state` and `preview_control` through `nico-bridge`.
Discover its `character_preview` instance first. Control actions use
`{"action":"seek","value":0.25}` (also pauses), `playing`, `speed` (0..4), `clip`
(an index across the loaded set), `bind_pose`, `in_place`, `looping` (boolean),
and `fade_seconds` (0..5). `finished` reports a completed one-shot; `playing` means
unpaused, including when a completed clip holds its final frame. `fade_weight`
is null outside a transition. Camera control uses
`{"action":"camera","yaw":0,"pitch":0.1,"distance":3}`. Acceptance returns an ID;
check `preview_state.command_results` for application or rejection. The latest 32
results are retained. `render_bounds` exposes the current model-space bounds and
`visible` reports the frustum result. Standard `window_snapshot`, `status`, and
`stop` also apply.

The preview uses reusable `nico-presentation-control::model::ModelVisual` geometry
and updates model-space joint palettes;
the vertex shader deforms the geometry. Mesh/index GPU buffers remain resident while
referenced by the scene. Preview and imported arena hero rendering use conservative
pose bounds to skip offscreen draws. Pose evaluation defaults to every Update. The preview can cap its frequency
per instance as described below. The current renderer supports up to 256 joints per palette
and 256 draws per scene, with PNG base-color textures and unlit core material colors.
This inspection tool does not implement PBR, GLB sampler/alpha-mode fidelity, skeleton
overlays, or a timeline widget. Loading is bounded and happens before the host
starts; load errors exit with a diagnostic. Assets remain at their supplied paths.
The ECS spawn function shares immutable assets while keeping playback state per
instance; a serialized prefab format is not introduced.

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

## Physics

`nico-physics` integrates Rapier 3D behind Nico-owned types. It supports fixed,
dynamic, and kinematic bodies; balls, cuboids, and capsules; collision groups,
sensors, shape casts, and character movement. Simulation uses 64-bit coordinates
and unit quaternions. The arena now uses Rapier for wall/actor collision and sliding;
combat timing and floor-movement policy remain game-owned.

The optional runtime adapter maps `PhysicsBody` components to provider bodies,
steps at fixed boundaries, writes poses back, and publishes owned `ContactFrame`
events. Removing a component or entity removes its body at the next fixed step.
Run the bounded ECS example:

```text
cargo run -p nico-physics --example falling_box
```

This first integration has one collider per body and no exposed joints, mesh
colliders, or 2D dynamics. Cross-platform determinism and performance are unverified.
See [physics ownership](docs/architecture.md#physics) and the
[integration design](docs/plans/2026-09-15-physics.md).

## Workspace

| Location | Responsibility |
| --- | --- |
| `crates/nico-ecs` | World, resources, and hecs entity/component storage |
| `crates/nico-runtime` | Lifecycle, scheduling, fixed time, events, and services |
| `crates/nico-input` | Provider-neutral device state and frame-to-fixed-step accumulation |
| `crates/nico-presentation` | Immutable 2D/3D draw snapshots and optional runtime lifecycle |
| `crates/nico-presentation-control` | Camera control, coordinate helpers, and cached bitmap text |
| `crates/nico-spatial` | Conservative camera sphere/box queries |
| `crates/nico-physics` | Rapier 3D integration, character movement, and optional ECS/runtime adapter |
| `crates/nico-render` | Bootstrap, textured quad and mesh pipelines, uploads, and frame recording |
| `crates/nico-rhi` | Backend-neutral GPU contracts |
| `crates/nico-rhi-wgpu` | Concrete wgpu resources, device, and surface recovery |
| `crates/nico-winit` | Native event loop, input adaptation, client coordination, and optional in-process host control |
| `crates/nico-assets` | Asset identity, leases, procedural meshes, and optional PNG/GLB loading |
| `apps/nico-character-preview` | Native GPU-skinned model inspection with ECS instances and MCP controls |
| `crates/nico-animation` | CPU pose sampling, skin matrices, humanoid profiles and retargeting |
| `crates/nico-launch` | Native CLI, diagnostics, and optional client/server transport composition |
| `crates/nico-ops` | Host control, command bookkeeping, publication, optional tool catalogs and bridge transport |
| `apps/nico-bridge` | MCP entry point for independently launched game instances |
| `apps/nico-shaderc` | Standalone offline shader compiler tool |
| `assets/presentation/shaders` | Engine shader sources and committed WGSL artifacts |
| `games/minimal-game` | Shared gameplay, client/server executables, and game asset roots |
| `games/arena-arpg` | Persistent multiplayer world, arena combat test, and native client/server executables |

See [architecture](docs/architecture.md) for dependency direction and contracts, and
[AGENTS.md](AGENTS.md) for contribution rules. Audio, UI, and broader devtools
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
