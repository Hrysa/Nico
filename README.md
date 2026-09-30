# Nico

Nico is an experimental Rust 2024 game engine with shared headless gameplay and native 2D/3D rendering.
The reference game includes a persistent multiplayer world and a separate Arena combat test.
Native clients use Winit and wgpu.
Splash screens and HUDs use bundled Noto Sans with TrueType shaping and cached glyph textures.
See the [font license and coverage](games/arena-arpg/assets/presentation/fonts/README.md).
See [roadmap](docs/roadmap.md) for current progress and known test limits.

## Quick start

Use a Rust toolchain supporting this workspace and install Git LFS.
Run commands from the repository root.
Fetch game assets before launching:

~~~sh
git lfs install --local
git lfs pull
~~~

Start the server and each client in separate terminals:

~~~sh
cargo run -p arena-arpg-server
cargo run -p arena-arpg-client -- --character alice
cargo run -p arena-arpg-client -- --character bob
~~~

Each connected character needs a unique name.
Names allow 1–32 lowercase ASCII letters, digits, or underscores.
Networking is loopback-only; these names are development identities, not authenticated accounts.

| Control | Action |
| --- | --- |
| WASD | Move |
| Mouse motion | Aim camera |
| Left click | Attack |
| Space | Dodge |
| E | Talk or pick up loot |
| F | Equip the iron sword |
| R | Respawn after death |
| Q | Reconnect |
| F3 | Toggle world combat debug shapes |
| Escape | Release pointer capture |

Click the viewport to capture the pointer.
Speak to the warden, defeat three camp monsters, and return to complete Meadow Watch.

Combat debug shows green character capsules and obstacle boxes, plus magenta target centres.
Yellow outlines show attack sectors; red marks active hit ticks.
Attacks overlap these sectors with capsule footprints, including the target radius at range and angle boundaries.
The sword mesh does not determine damage. Capsule heights affect movement collision; combat remains on the ground plane.
Hero, grunt, and brute capsules are 1.8, 1.65, and 2.05 metres tall, each with a 0.4-metre radius.
Rebuild and restart both world hosts after changing character collision definitions.
The overlay uses client presentation state, including local prediction and remote interpolation.
It does not prove a server hit. Geometry follows normal depth testing and the scene draw budget.

The game endpoint defaults to 127.0.0.1:47640.
Override it with server --listen and client --server.
Server saves default to target/world-data.
Use --data-dir outside target to retain saves through build cleanup.
Only one server may own each save directory.
Character progress is saved periodically, on disconnect, and during orderly shutdown; monsters are not saved.

### Standalone arena combat test

Run either host with --arena for its own three-wave solo encounter:

~~~sh
cargo run -p arena-arpg-client -- --arena
cargo run -p arena-arpg-server -- --arena
~~~

These hosts do not share the same encounter.
Use default world mode for multiplayer.
Arena uses the same movement and combat controls; R restarts the encounter.
The hero's dodge lasts 23 ticks, about 0.38 seconds, playing the trimmed roll about 1.5 times faster.
At 8 metres per second, an unobstructed dodge travels about 3.1 metres.
Its cooldown lasts 52 ticks. Invulnerability covers the first 12 ticks.
Add --procedural-hero to skip the hero's imported model and animation.

Clients support --background to avoid requesting initial focus and --smoke-frames N for bounded sessions.
The smoke limit counts session frames, not successful GPU presentations.
Use --help after the Cargo argument separator to see each executable's options.
Close client windows or use the registered stop tool for orderly shutdown.

## Build and test

~~~sh
cargo check --workspace
cargo test --workspace
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
~~~

Use --all-features when checking optional library paths.
GPU and manual measurement tests may require separate runs.
A passing default test run does not cover every native or GPU path.

Development and test builds keep only file and line information for workspace crates.
External dependencies omit debug information. Variable inspection and dependency source locations are unavailable with these settings.
Runtime optimization levels and release settings remain unchanged.
This follows [Cargo's build performance guidance](https://doc.rust-lang.org/cargo/guide/build-performance.html#reduce-amount-of-generated-debug-information).
For full debug information, rebuild with:

~~~sh
cargo build --config 'profile.dev.debug="full"' --config 'profile.dev.package."*".debug="full"'
~~~

Build Arena smoke binaries separately to avoid Windows locks from running games:

~~~sh
cargo build -p nico-mcp-bridge -p arena-arpg-client -p arena-arpg-server --target-dir target/bridge-validation
python apps/nico-mcp-bridge/tests/arena_native_smoke.py --bin-dir target/bridge-validation/debug
python apps/nico-mcp-bridge/tests/world_native_smoke.py --bin-dir target/bridge-validation/debug
~~~

These scripts open test windows, use isolated ports, and clean up only their own processes.
World tests use temporary saves and write results under target/world-native-evidence.
They do not prove desktop visibility or user-observed smoothness.
See [native check tasks](TODO.md#native-host-validation) for remaining manual work.

## Agent access through MCP

### Connect the bridge

Build the bridge:

~~~sh
cargo build -p nico-mcp-bridge
~~~

Register only nico-mcp-bridge with your MCP client.
For Codex, the server configuration can use:

~~~toml
[mcp_servers.nico-mcp-bridge]
command = "E:/repos/Nico/target/debug/nico-mcp-bridge.exe"
args = ["--listen", "127.0.0.1:47631"]
~~~

Replace the executable path with your build output.
Reload the MCP connection after changing its configuration.
The MCP client starts a stdio frontend. It connects to a shared daemon, starting one when needed.
Several Codex conversations can share this daemon and its connected game instances.
You start games independently.
The bridge never launches games, and bridge disconnect never stops them.

Closing one frontend does not stop the daemon or other frontends.
The daemon exits after 30 seconds without connected frontends or games.
Use `--idle-seconds N` to set this timeout when starting a new daemon.
Frontend reconnects keep game instance IDs. Daemon restarts require fresh instance discovery.
Lost calls return an uncertain outcome and are never replayed automatically.

Daemon state and startup logs live under the temporary directory in `nico-mcp-bridge/<game-address>/`.
Use `--state-dir PATH` to override this location; frontends sharing an address must use the same directory.
The daemon publishes a private loopback endpoint there. Port `47631` remains the game-registration endpoint.
On Windows, startup uses a detached process, with a CIM process-broker fallback when job rules forbid breakaway.
If local policy blocks both paths, run `nico-mcp-bridge --daemon` separately before connecting MCP.
After upgrading from the old bridge, close its MCP connections before using the new executable.
An old bridge holding port `47631` prevents daemon startup; the frontend never stops it automatically.

Windows process-lifetime validation:

~~~sh
python apps/nico-mcp-bridge/tests/daemon_job_smoke.py --bin-dir target/bridge-validation/debug
~~~

Development hosts connect to 127.0.0.1:47631 by default.
They retry quietly when the bridge is unavailable.
Use --no-bridge to disable attempts or --bridge ADDRESS to select another bridge.
The registration listener accepts loopback game traffic, not MCP HTTP requests.
Host executables do not support --mcp-stdio.

### Discover and call tools

1. Call list_instances and select a connected instance.
2. Call list_game_tools with its instance_id.
3. Call call_game_tool using the returned schema.

Example arguments for call_game_tool:

~~~json
{
  "instance_id": "<connected-instance-id>",
  "tool_name": "status",
  "arguments": {}
}
~~~

Game reconnects create new instance IDs. Reconnecting only an MCP frontend preserves them.
Cached schemas do not prove a host is connected.
Use these fixed tools when dynamic tool names do not refresh.

| Tool group | Main operations |
| --- | --- |
| All native hosts | status, stop, diagnostics |
| Arena client and server gameplay | scene_info: selected scene and definition validation, without a calculated content revision |
| Native clients | window_state, window_control, window_snapshot |
| World server | world_state, world_spawn |
| World client | world_client_state, world_action, client_characters |
| Solo Arena | Discover game_state and the registered game action tools |
| Character preview | preview_state, preview_control |

Use `world_action` with `{"action":"debug_combat","enabled":true}` to enable the world overlay through MCP.
Read `world_client_state.debug_combat` and command outcomes to confirm application. Set `enabled` to `false` to hide it.

Mutation results may only acknowledge queued work.
Check request IDs and resulting state for completion.
Timed-out mutations may have run; never retry them blindly.
Stop acceptance proves neither completed shutdown nor process exit.
Presentation counts prove neither GPU completion nor display scanout.

### Diagnostics and window tools

Call diagnostics with {"after":0,"limit":8}; use next_cursor for later pages.
Cursors survive bridge reconnects to the same process, but reset after process restart.
Dropped and truncated fields report missing history.
Diagnostics share the host logging filter and do not include profiling.

Call window_control with an action such as {"action":"restore"}.
Poll window_state with the returned request_id and check observed window state.
Applied means the platform call returned; the window manager may ignore it.

Call window_snapshot with {}, then poll using its returned request_id.
A ready result contains a local PNG path and dimensions.
Only the latest capture is retained; copy it before requesting another.
Captures show rendered content, not desktop visibility.
Discover current limits and supported actions through each tool's schema.

### Release debugging

Release hosts require --enable-debug; --bridge alone does not enable debugging.
Local release MCP permits inspection only.
Development MCP permits inspection, capture, mutation, and stop.
The editor RPC endpoint, credential files, and editor capture downloader have been removed.
MCP window capture remains available through window_snapshot and window_snapshot_read.
See [debug access code](crates/nico-ops/src/bridge/access.rs) for local permission rules.

## Content and tools

### Saved scenes and assets

Load Arena's declared project:

~~~sh
cargo run -p arena-arpg-client -- --project games/arena-arpg
~~~

The project’s `default_scene` is `assets/scenes/splash.scene.toml`.
The client shows NICO for one second, then loads Meadow with a progress bar and completed asset counts.
It switches scenes when CPU preparation finishes. Server connection and first GPU presentation remain separate steps.
The server follows the splash’s target directly, without the visual delay.
`--scene assets/scenes/meadow.scene.toml` loads Meadow directly and skips the splash.
Use `--scene assets/scenes/arena.scene.toml` to select another scene within that project.
`--arena` selects that same solo scene and cannot be combined with `--scene`.
Scenes compose entities from typed components, including world references, placements, camera settings, and lights.
Servers create shared gameplay components and skip client components.
Restart affected hosts after editing source files.
The `scene_loading` MCP tool reports splash, loading, preparing, loaded, or failed state.
It also reports application frames and active scene generation. The application remains alive across the switch.
Scene transitions refresh bridge registration; discover the new instance ID before calling game tools.
Loading errors remain on the splash and include details in diagnostics and `scene_loading`.
Run the isolated native check with
`python3 apps/nico-mcp-bridge/tests/splash_native_smoke.py --bin-dir target/bridge-validation/debug`.
It opens a test window, uses private ports, and stops only its own processes.

CLI games and preview tools reuse imported data in debug and release builds.
The `.nico` cache sits beside the project manifest, or above the nearest `assets` folder.
Standalone sources keep their cache in the same folder.
Source or import setting changes trigger a new import. Deleting `.nico` forces a rebuild.
Explicit ImportCache and watch APIs remain available to library callers.
Automatic discovery skips folders starting with `~` and everything inside them.
This applies to declared asset roots too. Explicit file loads still work.
GPU resource reuse is separate from persistent import caching.
Arena and character preview load models and textures with up to four CPU workers per batch.
The splash stays responsive while required assets load. Library callers can also poll batches without blocking.
Arena makes one recursive load request from `nico.project.toml`.
`default_scene` selects a `SceneDefinition`. Its components reference rules, character catalogs, and the scenery library.
Scenes live under `assets/scenes`; referenced assets live under `assets/logic` and `assets/presentation`.
Shared source files load once. Each model completes after its referenced textures are ready.
Progress counts unique GLB and PNG source files once, then logs only completed-count changes.
Embedded textures belong to their model's progress unit.
The `[authoring]` section selects the retained adapter, which edits the same scene loaded by both hosts.

| Need | Starting point |
| --- | --- |
| World scenery and content preparation | [World guide](games/arena-arpg/assets/presentation/worlds/README.md) |
| Hero source and license limits | [Hero notices](games/arena-arpg/assets/presentation/characters/hero/LICENSE.md) |
| Imported monster content | [Monster notices](games/arena-arpg/assets/presentation/characters/monsters/README.md) |
| Custom importers | [Public import example](crates/nico-assets/src/import.rs) |
| Physics integration | [Physics crate](crates/nico-physics/src/lib.rs) |

### Native character preview

Supply a model path; add an external clip path when needed:

~~~sh
cargo run -p nico-character-preview -- --model path/to/model.glb --animation path/to/clip.glb
~~~

Omit --animation to use the model's clips.
External animation currently uses RPG-to-Mixamo presets.
A/D selects clips, Space pauses, R restarts, arrows orbit, and W/S zooms.
Use --bind-pose for the reference pose or --once to hold the final frame.
The preview also exposes playback and camera controls through MCP.
See --help and live tool schemas for additional options.

## Shader workflow

Shader sources and generated WGSL live under [assets/presentation/shaders](assets/presentation/shaders).
Keep generated artifacts committed.
Normal Cargo builds use those files and do not require Slang.

For regeneration, provide slangc through PATH, NICO_SLANGC, or --slangc PATH:

~~~sh
cargo run -p nico-shaderc
cargo run -p nico-shaderc -- --check
~~~

Use --root PATH when the tool cannot find the repository shader root.
Shader compilation is separate from Cargo; runtime still creates backend pipelines.

## Logging and known limits

Hosts accept --log-level off, error, warn, info, debug, or trace.
An explicit level overrides RUST_LOG; the default is info.
Logs go to stderr.

Native play keeps wgpu API validation enabled but makes driver validation opt-in.
To enable it in PowerShell:

~~~powershell
$env:WGPU_VALIDATION = '1'
cargo run -p arena-arpg-client -- --character alice
Remove-Item Env:WGPU_VALIDATION
~~~

Nico is not a finished release.
Internet deployment, broad hardware support, and cross-platform determinism remain unverified.
Native gamepad support, full UI, audio, and broader player settings remain unfinished.
XRay profiling is planned but neither integrated nor validated.
See [roadmap](docs/roadmap.md) for scope and [TODO](TODO.md) for open tasks.

## Project map

Engine libraries live in crates; executables live in apps; games own their code and assets under games.
[Architecture](docs/architecture.md) lists crate ownership and dependency rules.
[AGENTS.md](AGENTS.md) defines contribution and writing rules.
