# AI-accessible client and server operations

- Status: minimal host control and server MCP `status`/`stop` implemented;
  client integration, process supervision, and diagnostics forwarding remain planned.
- Date: 2026-09-10
- Scope: local development processes launched by Nico tooling.
- Profiling: deferred to future experimental XRay use. This plan adds no
  profiler code, capture interface, instrumentation, or toolchain changes.

## Outcome

The minimal implementation uses `nico-ops::control_channel` for owned status and
idempotent stop. Its optional `mcp` feature exposes exactly two tools directly
from `minimal-game-server --mcp-stdio`, using the official Rust SDK on a dedicated
thread. `nico-launch/src/server/` owns arguments, the runner, control composition,
MCP startup, and the final join. Games build the App and may supply extra tools
through `ToolExtensions`; engine lifecycle tool names are reserved.
The default core has no external dependencies and runtime has no MCP or
executor dependency. See [agent setup and tool semantics](../../README.md#agent-access-through-mcp).

The implemented flow is: MCP client launches the server executable, discovers
`status`/`stop`, polls readiness, requests stop, reads final host status, then
closes stdin to exit. Stop acknowledges acceptance; it does not await shutdown.
EOF also requests host stop. Stdout contains MCP only; diagnostics use stderr.
Real-process tests cover this flow, invalid calls, repeated stop, and disconnect
before/after initialization. The in-process controlled example remains available.

Everything below describes the larger **future supervisor milestone**, including
its proposed seven tools and private child protocol. Those are not requirements
for the implemented two-tool endpoint and remain subject to concrete needs.

An AI client can discover runnable targets, launch a Nico client or server, wait
for explicit readiness, inspect status and structured diagnostics, and stop the
process cleanly. Multiple managed instances have separate identities and results.
Normal standalone launches continue to work without the tooling connection.

The acceptance flow is:

```text
discover target -> launch -> wait for readiness -> read status/diagnostics
    -> request stop -> observe shutdown completion and process exit
```

Runtime remains headless. Window/device operations execute on the native host
thread; application shutdown executes at a host-owned boundary. Background I/O
never receives mutable access to World.

## Future supervisor architecture

```text
AI client
    | MCP over stdio
    v
apps/nico-mcp: tool adapter and managed process supervisor
    | private child stdin/stdout pipes; versioned operation messages
    +-> minimal-game-client -> nico-winit -> App
    +-> minimal-game-server -> FixedRateServerRunner -> App
```

The two pipe connections are independent: child output never forwards directly
to the MCP server's stdout. The MCP endpoint uses JSON-RPC over stdio, which
reserves stdout for protocol messages and permits diagnostics on stderr.
Use the official Rust SDK rather than implementing MCP lifecycle and framing.
Pin a compatible released SDK version during implementation; its executor stays
inside the standalone tool. Sources:
[MCP stdio transport](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports),
[official Rust SDK](https://github.com/modelcontextprotocol/rust-sdk).

The child connection is a small Nico protocol, not another MCP server. Use
versioned newline-delimited JSON with bounded message sizes, session/request
identity, typed payloads, and explicit error results. Child stdout is reserved
for these messages only when managed mode is enabled. Human diagnostics use
stderr; a single writer serializes protocol output.

Proposed ownership:

| Location | Responsibility |
| --- | --- |
| `crates/nico-ops` | Implemented host endpoints and optional direct MCP adapter; future child protocol subject to need; no runtime or Winit dependency |
| `crates/nico-launch` | Implemented server host/MCP lifecycle; diagnostic forwarding remains planned |
| `crates/nico-winit` | Client status snapshots, wakeups, and main-thread operation dispatch |
| `crates/nico-launch/src/server/` | Server runner, status/control boundaries, MCP arguments and thread ownership |
| Game entry points | Build the App, delegate to engine hosts, optionally register game tools |
| `apps/nico-mcp` (new) | MCP SDK integration, target registry, process ownership, deadlines, and retained diagnostics |

`nico-ops` currently has the server as its first consumer; the client and
supervisor will follow. Keep its
protocol and native I/O in separate modules; do not invent a universal engine
command interface. The existing runtime service bridge remains for domain-owned
requests/completions. These host lifecycle operations must work before runtime
startup and while client ticking is suspended, so they do not depend on an
Update-stage service publication system. Use existing `App::request_exit` and
`App::shutdown`; no runtime protocol dependency is needed.

## Initial tools

Names and fields below are proposed API contracts, not available commands.

| Tool | Inputs | Result |
| --- | --- | --- |
| `nico_targets` | None | Configured target IDs, client/server role, allowed launch options |
| `nico_launch` | Target ID, validated launch options | Instance ID and starting state, or spawn failure |
| `nico_instances` | None | Instances owned by this supervisor, including bounded retained exit records |
| `nico_status` | Instance ID | Latest host snapshot, snapshot age, lifecycle, capabilities, and process state |
| `nico_wait_ready` | Instance ID, bounded timeout | Ready snapshot or structured timeout/startup/exit error |
| `nico_diagnostics` | Instance ID, cursor, bounded limit, optional level | Structured events, next cursor, and missed-record count |
| `nico_stop` | Instance ID, bounded timeout | Graceful shutdown result and exit code, or timeout with current state |

Tool inputs and outputs have schemas. Return structured results and a concise
text representation for clients that display text only. Tool execution failures
use MCP tool error results; malformed protocol requests remain protocol errors.
See [MCP tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools).

The initial target registry is explicit startup configuration: target ID, role,
prebuilt executable path, working directory, and allowed options. Provide a
minimal-game example with client and server entries. Resolve paths before launch
and spawn executables directly without a shell. Build binaries separately with
Cargo; this milestone does not add build management or attach to arbitrary PIDs.
Keep the native client window visible; helper processes should not open consoles.

## State and completion semantics

- Instance IDs are unique within the supervisor session and are distinct from
  PIDs, preventing stale requests from controlling reused process IDs.
- `launch` returns after spawning and registering ownership, not after readiness.
  The child handshake negotiates the Nico protocol version and capabilities.
- Server readiness requires successful App startup and one successful tick.
- Client readiness requires successful App startup and the first successful GPU
  presentation. Count `RenderStatus::Presented`, not the current session smoke
  counter. Surface skips leave readiness pending and visible in status.
- Track lifecycle (`starting`, `running`, `stopping`, `exited`, `failed`) separately
  from client activity (`active`, `suspended`) and graphics readiness.
- Status uses owned host snapshots with frame/tick counters, role, and last
  failure. The supervisor adds PID, observed exit, and snapshot age. A cached
  snapshot must not be presented as a fresh response from a blocked host.
- Repeated stop requests converge on one shutdown. A successful stop response
  requires both the final shutdown result and observed process exit. Exit alone
  without the final result is reported as unconfirmed or failed shutdown.
- A timeout ends the wait; it does not imply cancellation or undo a launched
  process or accepted stop. The instance ID remains available for follow-up.
- Supervisor shutdown closes managed inputs and requests orderly child stops,
  then reaps children. After its cleanup deadline it may terminate only its own
  child processes, reporting forced cleanup separately from graceful shutdown.
- A normal `nico_stop` timeout does not silently force termination. A child
  detects pipe EOF and requests orderly host shutdown; abnormal supervisor loss
  can only guarantee graceful cleanup while the child host remains responsive.

The current GPU initialization blocks the Winit callback. This milestone reports
starting/stale state and deadlines accurately; it does not redesign GPU startup.
Likewise, a host cannot interrupt a blocking game system safely in mid-execution.

## Responsiveness and bounded storage

Use a bounded request queue and host wakeup. Winit uses a user event and
`EventLoopProxy` so control can run even without redraws or while suspended.
Drain a bounded amount of control work per callback and retain a shutdown signal
even if the ordinary request queue is full.

The server waits for either its next tick deadline or a control request instead
of sleeping uninterruptibly for the entire tick interval. An early control wakeup
must not advance the simulation or reset its deadline. Process requests between
ticks, and retain the existing fixed-step behavior when tooling is disabled.

Read and write pipes off the host thread. Bound message size, in-flight requests,
diagnostic events, exited-instance records, and response queues. Diagnostic
overflow drops oldest records and reports the loss; lifecycle replies must not
be displaced by log traffic. Backpressure cannot block the simulation thread.
If control delivery becomes impossible, expose transport failure and begin the
managed disconnect path rather than claiming a response was delivered.

Capture typed tracing events through an optional diagnostics layer, preserving
severity, target, message/fields, and sequence. This is event forwarding, not
profiling: do not collect span timing or add per-method instrumentation. Existing
human logs go to stderr in managed mode. Raw stderr is retained separately as
bounded fallback text for crashes; it is never parsed as authoritative readiness.

## Implementation sequence

### 1. Shared operation contract and pipe lifecycle

- Add `nico-ops`, its protocol values, bounded endpoints, handshake, and I/O adapter.
- Define schemas/limits, request correlation, lifecycle snapshots, EOF behavior,
  and explicit overload/version/unsupported-operation errors.
- Test round trips, partial reads, malformed/oversized messages, duplicate IDs,
  queue saturation, disconnect, and late responses with in-memory I/O.

Deliverable: a test host can exchange typed operations with no runtime or MCP.

### 2. Headless server integration

- Add opt-in managed arguments/composition and structured diagnostic forwarding.
- Extend `FixedRateServerRunner` with optional control and deadline-aware waiting.
- Publish readiness/status and dispatch stop through the existing App lifecycle.
- Test identical input/tick outcomes, prompt stop at low tick rates, startup/tick
  failure, duplicate stop, and EOF cleanup.

Deliverable: a test driver launches the real server, observes progress, retrieves
diagnostics, and confirms orderly shutdown and exit.

### 3. Native client integration

- Add optional operations to host composition without placing Winit machinery in
  the example client. Preserve the ordinary `run_native_client` use case.
- Introduce user-event wakeups and snapshot publication on the main thread.
- Report first successful GPU presentation separately from session frames.
- Dispatch stop through the existing idempotent `ClientSession` shutdown path.
- Test suspended/no-redraw control, skipped presentation, repeated stop, startup
  failure, and window close racing with a stop request without a GUI where possible.

Deliverable: the same operation contract controls the native client and server.

### 4. MCP supervisor

- Add `apps/nico-mcp` with the official SDK, explicit target registry, and stdio.
- Implement the seven tools, owned process registry, separate pipe pumps, bounded
  diagnostic history, readiness waits, and deadline/cleanup behavior.
- Ensure a slow request for one instance does not block operations on another.
- Test tool discovery/schema validation, structured success/errors, protocol-only
  stdout, cross-instance isolation, failed launch, and supervisor disconnect.

Deliverable: an MCP client completes the acceptance flow against the real server.

### 5. End-to-end validation and usage documentation

- Exercise a client and server concurrently through MCP on Windows: readiness,
  advancing frames/ticks, independent diagnostics, and graceful stop of each.
- Test missing shader, early exit, readiness timeout, diagnostic overflow, and
  cleanup of owned children. Check terminal/session retention stays bounded.
- Preserve the standalone smoke path and existing workspace behavior.
- Repeat native lifecycle checks on macOS when available; record untested platform
  cases explicitly rather than making them invisible completion assumptions.
- Document build steps, target configuration, MCP invocation, tool schemas,
  errors, managed stdio ownership, and disconnect semantics. Update TODO/roadmap
  only for work actually verified.

Run `cargo check --workspace`, `cargo test --workspace`,
`cargo fmt --all -- --check`, and
`cargo clippy --workspace --all-targets -- -D warnings`, plus the bounded native
smoke and MCP integration flows. Keep public API integration tests at package
level; other tests stay beside their owning modules.

## Completion boundary

This milestone is complete when both local hosts can be launched, observed, and
stopped through documented MCP tools with structured results and tested failure
paths, while normal launches and deterministic runtime tests still pass.

Profiling/XRay setup, arbitrary world editing, input injection, screenshots,
window manipulation, remote network access, and attaching to unrelated processes
are follow-ups. They require concrete consumers before adding new operations.
