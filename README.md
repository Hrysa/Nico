# Nico

Nico is an experimental Rust 2024 game engine with a headless runtime, shared
client/server gameplay, and a Winit native client that renders a bootstrap
triangle through Nico's RHI and its wgpu backend.

The core **Real client host** implementation is complete. Interactive Windows
and macOS resize/minimize/restore validation remains outstanding. Native gamepad
integration and Slang reflection/asset-backed shaders are deferred.

The next milestone is **AI-accessible client/server operations**: structured
launch, readiness, diagnostics, and orderly shutdown through an MCP adapter.
The minimal core and server MCP endpoint are implemented: `nico-ops` provides
status and orderly stop, exposed by `minimal-game-server --mcp-stdio`.
Native client control, process supervision, and structured diagnostics forwarding
remain planned.

Profiling remains a cross-library requirement, with experimental Rust/LLVM XRay
chosen as the future direction for automatic function capture and a Unity-style
call hierarchy, timings, and invocation counts. Profiling implementation,
compatibility investigation, and toolchain changes are deferred. XRay is not
integrated or validated for Nico, and no custom profiler is planned now.

## Run and validate

Run from the repository root with a Rust toolchain supporting edition 2024:

```text
cargo run -p minimal-game-client
cargo run -p minimal-game-client -- --smoke-frames 3
cargo run -p minimal-game-server
```

The client runs until the window closes. The server runs at a configured 60 Hz
by default; Ctrl+C terminates the process. The server runner supports orderly
shutdown when runtime code requests exit or the optional MCP endpoint receives stop.

The client maps WASD and arrow keys to shared gameplay movement commands. The
triangle is fixed bootstrap geometry and does not yet visualize entity positions.
The input model supports gamepads, but there is no native gamepad provider.

The smoke limit bounds client-session frames. It does not count only successful
GPU presentations, so it is not proof that every requested frame reached the
display. See [the validation checklist](TODO.md#outstanding-host-validation).

```text
cargo check --workspace
cargo test --workspace
cargo fmt --all -- --check
cargo clippy --workspace --all-targets -- -D warnings
```

Both executables accept `--log-level <off|error|warn|info|debug|trace>`.
Diagnostics go to stderr. An explicit level overrides `RUST_LOG`; the default is `info`. The server also
accepts `--tick-rate <TICKS_PER_SECOND>`:

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
`nico_ops::HostEndpoint`. Another thread
can retain its `HostControl` to read status or request stop:

```rust,ignore
let (control, operations) = nico_ops::control_channel();
let runner = FixedRateServerRunner::new(step).with_operations(operations);
// Move `runner` to the host and retain `control` in an adapter.
let snapshot = control.status();
control.request_stop()?;
```

Run the complete [controlled server example](games/minimal-game/server/examples/controlled.rs):

```text
cargo run -p minimal-game-server --example controlled
```

It starts the real server, observes readiness after one successful host tick,
requests stop from another thread, and prints the final status. Repeated stop
requests coalesce. Stop and last-controller disconnect wake the server's paced
wait; shutdown still runs on the host thread. Final success/failure remains
readable after the host endpoint closes.

Snapshots are cached host reports; stop acceptance is distinct from completion,
and host completion does not imply operating-system process exit.

## Agent access through MCP

Build the server, then configure your MCP client to launch the executable with
`--mcp-stdio`:

```text
cargo build -p minimal-game-server
```

For clients using an `mcpServers` configuration, an example on Windows is:

```json
{
  "mcpServers": {
    "nico-server": {
      "command": "E:/repos/Nico/target/debug/minimal-game-server.exe",
      "args": ["--mcp-stdio"]
    }
  }
}
```

Adjust the absolute executable path to your checkout/build output. Each connection
launches one server. The agent discovers two tools, both taking `{}`:

| Tool | Result |
| --- | --- |
| `status` | Cached `state`, `completed_steps`, `ready`, `finished`, and nullable `failure` |
| `stop` | `accepted` plus the current `status`; a failed host produces a tool error |

Both return structured JSON and matching text. Poll `status` until `ready: true`,
call `stop`, then poll until `finished: true`. Check `state` and `failure` to
distinguish successful shutdown from failure. `stop` is idempotent; acceptance
does not mean shutdown has completed. MCP remains available after host shutdown
so the agent can inspect the result. Close the connection's stdin to exit the
process. Disconnect while running also requests orderly host shutdown.

The optional `nico-ops/mcp` feature implements the service with the official Rust
MCP SDK. `nico-launch/server` owns the server arguments, fixed-rate runner,
control channel, MCP thread, and shutdown join. The game builds its App and calls
`ServerHost::new(args.server).run(&mut app)`.
Stdout carries only MCP; logs use stderr. This endpoint controls its own
headless host; client control, diagnostic retrieval, and multi-process supervision
are follow-ups. Build/argument failures before host startup appear as process
errors rather than tool status. A blocked game system still delays orderly stop.

Games can extend the engine service using `nico_ops::mcp::ToolExtensions`:
register a `Tool` schema and a handler, then pass the registry to
`ServerHost::with_mcp_tools`. The engine advertises and dispatches these tools
alongside `status` and `stop`. Reserved or duplicate names are rejected.
Handlers validate their arguments and return promptly on the MCP thread, reading
owned data or sending host requests. They do not receive mutable App access.

## Shader workflow

The bootstrap shader source is
[`bootstrap.slang`](assets/presentation/shaders/bootstrap.slang); its generated
[`bootstrap.wgsl`](assets/presentation/shaders/generated/wgpu/bootstrap.wgsl)
is checked in and loaded at runtime. A normal client launch does not run Slang.

To regenerate or check the artifact, make `slangc` available through `PATH`,
`NICO_SLANGC`, or the tool's `--slangc <PATH>` argument:

```text
cargo run -p nico-shaderc
cargo run -p nico-shaderc -- --check
```

The tool searches upward from its working directory for the shader root.
Use `--root <PROJECT>` to specify it explicitly. Shader compilation is separate
from Cargo builds; changing the generated artifact does not relink Rust crates.

The client reads the WGSL file synchronously and waits for GPU initialization
and pipeline creation before its first redraw. This is a direct bootstrap file
load, not a general asset loader. Offline WGSL generation still leaves backend
shader and pipeline preparation at runtime. The reported roughly one-second
startup delay has not been profiled; its cause remains unconfirmed.

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
| `crates/nico-winit` | Native event loop, input adaptation, and client coordination |
| `crates/nico-assets` | Stable `AssetId` and typed `Handle<T>` identity |
| `crates/nico-launch` | Native CLI, diagnostics, and optional server host/MCP lifecycle |
| `crates/nico-ops` | In-process host status and orderly-stop control |
| `apps/nico-shaderc` | Standalone offline shader compiler tool |
| `assets/presentation/shaders` | Engine bootstrap shader source and generated artifact |
| `games/minimal-game` | Shared gameplay, client/server executables, and game asset roots |

Rust dependencies point toward contracts and the headless core:

```text
game client -> nico-winit -> nico-input
                        -> nico-presentation -> nico-runtime -> nico-ecs
                        -> nico-runtime
                        -> nico-render -> nico-rhi
                        -> nico-rhi-wgpu -> nico-rhi
                                        -> wgpu
game server -> shared gameplay -> nico-runtime
```

The diagram shows the core dependency paths; manifests include additional
composition dependencies. In particular, `nico-rhi` does not depend on wgpu or
its provider. Physics, audio, UI, and broader devtools have no placeholder crates.

## Architecture requirements

- Keep runtime headless and shared gameplay independent of client providers.
- Author game construction and behavior in Rust; introduce data formats with
  concrete consumers.
- Make meaningful library work measurable and profileable, with collection and
  export controlled by the host.
- Make client and server operations discoverable and accessible to AI tooling
  through structured arguments, status, diagnostics, and results.
- Introduce new contracts and crates only with real ownership boundaries,
  implementations, consumers, and behavioral tests.

## Documentation

- [Architecture](docs/architecture.md): ownership, runtime contracts, and required tooling boundaries.
- [Roadmap](docs/roadmap.md): completed milestones, next milestone, and deferred work.
- [TODO](TODO.md): actionable work and outstanding validation.
- [Review guide](docs/review.md): checks for proposed changes.
- [ADR 0001](docs/decisions/0001-native-client-event-loop.md): native event-loop ownership.
- [ADR 0002](docs/decisions/0002-nico-rhi-wgpu-backend.md): RHI and wgpu provider.
- [ADR 0003](docs/decisions/0003-render-pipeline-layer.md): rendering policy.
- [Repository guidelines](AGENTS.md): contributor and agent instructions.
- [Game asset roots](games/minimal-game/assets/README.md): logic and presentation ownership.
