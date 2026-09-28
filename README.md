# Nico

Nico is an experimental Rust 2024 game engine with shared headless gameplay and native 2D/3D rendering.
The reference game includes a persistent multiplayer world and a separate Arena combat test.
Native clients use Winit and wgpu.
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
| Escape | Release pointer capture |

Click the viewport to capture the pointer.
Speak to the warden, defeat three camp monsters, and return to complete Meadow Watch.

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

Build Arena smoke binaries separately to avoid Windows locks from running games:

~~~sh
cargo build -p nico-bridge -p arena-arpg-client -p arena-arpg-server --target-dir target/bridge-validation
python apps/nico-bridge/tests/arena_native_smoke.py --bin-dir target/bridge-validation/debug
python apps/nico-bridge/tests/world_native_smoke.py --bin-dir target/bridge-validation/debug
~~~

These scripts open test windows, use isolated ports, and clean up only their own processes.
World tests use temporary saves and write results under target/world-native-evidence.
They do not prove desktop visibility or user-observed smoothness.
See [native check tasks](TODO.md#native-host-validation) for remaining manual work.

## Agent access through MCP

### Connect the bridge

Build the bridge:

~~~sh
cargo build -p nico-bridge
~~~

Register only nico-bridge with your MCP client.
For Codex, the server configuration can use:

~~~toml
[mcp_servers.nico-bridge]
command = "E:/repos/Nico/target/debug/nico-bridge.exe"
args = ["--listen", "127.0.0.1:47631"]
~~~

Replace the executable path with your build output.
Reload the MCP connection after changing its configuration.
The MCP client may start the bridge; you start games independently.
The bridge never launches games, and bridge disconnect never stops them.

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

Reconnects create new instance IDs.
Cached schemas do not prove a host is connected.
Use these fixed tools when dynamic tool names do not refresh.

| Tool group | Main operations |
| --- | --- |
| All native hosts | status, stop, diagnostics |
| Native clients | window_state, window_control, window_snapshot |
| World server | world_state, world_spawn |
| World client | world_client_state, world_action, client_characters |
| Solo Arena | Discover game_state and the registered game action tools |
| Character preview | preview_state, preview_control |

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
The separate authenticated debug RPC endpoint requires explicit host grants for mutation, capture, and stop.
Its APIs remain available, but the old editor UI and Play launcher are removed.
See [debug access code](crates/nico-ops/src/bridge/access.rs) for policy types and validation.

## Content and tools

### Saved scenes and assets

Load Arena's declared project:

~~~sh
cargo run -p arena-arpg-client -- --project games/arena-arpg
~~~

Both Arena hosts accept --project for declared logic and visual content.
It cannot be combined with --arena or conflicting content overrides.
Restart affected hosts after editing source files.

CLI games and preview tools reuse imported data in debug and release builds.
The `.nico` cache sits beside the project manifest, or above the nearest `assets` folder.
Standalone sources keep their cache in the same folder.
Source or import setting changes trigger a new import. Deleting `.nico` forces a rebuild.
Explicit ImportCache and watch APIs remain available to library callers.
Automatic discovery skips folders starting with `~` and everything inside them.
This applies to declared asset roots too. Explicit file loads still work.
GPU resource reuse is separate from persistent import caching.
Arena and character preview load models and textures with up to four CPU workers per batch.
Startup waits for required assets; library callers can poll batches without blocking.
Arena makes one recursive load request from `nico.project.toml`.
`default_scene` selects world content. Arena's character catalog supplies its character definitions.
Game assets live under `assets/logic` and `assets/presentation`; the project needs no root or startup asset lists.
Shared source files load once. Each model completes after its referenced textures are ready.
Progress counts unique GLB and PNG source files once, then logs only completed-count changes.
Embedded textures belong to their model's progress unit.
The `[authoring]` section holds the retained adapter and source paths; `[editor]` has been removed.

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
