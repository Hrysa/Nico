# Nico

Nico is an experimental Rust 2024 game engine with a headless runtime, shared
client/server gameplay, and a Winit native client that renders a bootstrap triangle
through Nico's RHI and its wgpu backend.

The core native client host implementation is complete. Interactive Windows and macOS
resize/minimize/restore validation remains outstanding. Native gamepad integration and
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

Run from the repository root with a Rust toolchain supporting edition 2024:

```text
cargo run -p minimal-game-client
cargo run -p minimal-game-client -- --smoke-frames 3
cargo run -p minimal-game-server
```

The client runs until the window closes. The server runs at a configured 60 Hz by
default; Ctrl+C terminates the process. The server runner supports orderly shutdown when
runtime code requests exit or the registered MCP `stop` tool receives a request.

The client maps WASD and arrow keys to shared gameplay movement commands. The triangle
is fixed bootstrap geometry and does not yet visualize entity positions. The input model
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

The bootstrap shader source is
[`bootstrap.slang`](assets/presentation/shaders/bootstrap.slang); its generated
[`bootstrap.wgsl`](assets/presentation/shaders/generated/wgpu/bootstrap.wgsl) is checked
in and loaded at runtime. A normal client launch does not run Slang.

To regenerate or check the artifact, make `slangc` available through `PATH`,
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

## Workspace

| Location | Responsibility |
| --- | --- |
| `crates/nico-ecs` | World, resources, and hecs entity/component storage |
| `crates/nico-runtime` | Lifecycle, scheduling, fixed time, events, and services |
| `crates/nico-input` | Provider-neutral physical device state |
| `crates/nico-presentation` | Immutable world-facing presentation lifecycle; currently a null implementation |
| `crates/nico-render` | Bootstrap shader/pipeline selection and frame recording |
| `crates/nico-rhi` | Backend-neutral GPU contracts |
| `crates/nico-rhi-wgpu` | Concrete wgpu resources, device, and surface recovery |
| `crates/nico-winit` | Native event loop, input adaptation, client coordination, and optional in-process host control |
| `crates/nico-assets` | Stable `AssetId` and typed `Handle<T>` identity |
| `crates/nico-launch` | Native CLI, diagnostics, and optional client/server transport composition |
| `crates/nico-ops` | Host control, optional tool catalogs, and bridge transport |
| `apps/nico-bridge` | MCP entry point for independently launched game instances |
| `apps/nico-shaderc` | Standalone offline shader compiler tool |
| `assets/presentation/shaders` | Engine bootstrap shader source and generated artifact |
| `games/minimal-game` | Shared gameplay, client/server executables, and game asset roots |

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
