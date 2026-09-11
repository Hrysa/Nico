# MCP bridge for independently launched games

Implemented bridge contract, updated 2026-09-11. This replaces the [historical
supervisor proposal](2026-09-10-ai-client-server-operations.md). Usage belongs in
[README](../../README.md#agent-access-through-mcp); phase status and validation evidence
belong in [the roadmap](../roadmap.md#2-control-clients-and-servers-with-ai).

## Connection and ownership

```text
Codex -- MCP stdio --> nico-bridge <-- loopback TCP -- game client
                                  <-- loopback TCP -- game server
```

- `apps/nico-bridge` is the CLI entry point. Engine implementation lives under
  `nico-ops/src/bridge/`, behind the optional `bridge` feature.
- `nico-launch` composes host arguments, control, diagnostics, and bridge adapters.
  `nico-ops` implements transport threads, reconnects, and joins; `nico-winit` owns
  native dispatch and graphics/activity/readiness reports.
- Games register tool schemas and handlers through `ToolExtensions`. Shared game
  tooling can publish owned snapshots or queue runtime-owned commands. Transport
  threads do not mutate the App or World.
- The bridge never launches, kills, or supervises OS processes. MCP disconnect
  closes the bridge connection; game adapters reconnect and keep gameplay running.
- Only an explicit routed `stop` call requests host shutdown through tooling.
  Direct client/server MCP transports are removed; all host and game tools are
  discovered from connection registrations.

## Registration and cached discovery

Games attempt a background bridge connection by default. `--bridge <loopback-address>`
overrides the endpoint; `--no-bridge` disables attempts. Games keep running if the
bridge is absent. Default bridge listener: `127.0.0.1:47631`. No connection is required
for startup; unavailable connections retry once per second after connection attempts.
Each registration contains:

- Protocol version (currently 1), stable game name, client/server role, API version.
- PID for observation only, initial host status, and the complete tool catalog.
- Each tool's name, description, input/output schemas, and optional MCP metadata.

Names and API versions accept 1-48 ASCII letters, digits, underscores, or hyphens. The
control catalog supplies `status`/`stop`; games cannot override those names. Native
hosts add `diagnostics`, rejecting conflicting game registrations. The minimal game adds
`game_state`, an owned snapshot published after Update.

The bridge allocates a new instance ID for every accepted connection, including
reconnects of the same process. It never redirects an old instance ID to a new one. Tool
names exposed to MCP are `<game>.<role>.<tool>`, with an `instance_id` and an
`arguments` object wrapping the original game arguments. Catalogs are shared by
game/role. Simultaneously connected instances must have the same API version and tool
definitions; incompatible registrations are rejected with a reason.

After the last instance disconnects, its schemas remain cached. A later registration
replaces that game's role catalog, removing obsolete tools and updating changed schemas.
The cache is in memory for the bridge lifetime; a fresh bridge learns APIs from new
registrations. Offline manifests and disk persistence are not implemented.

The bridge advertises `tools.listChanged` and sends `notifications/tools/list_changed`
when registrations change. Protocol integration tests verify this notification and
refreshed discovery on the same connection. Session-specific compatibility evidence is
recorded in the roadmap.

Five stable tools work independently of dynamic refresh: `bridge_status`,
`list_instances`, `instance_status`, `list_game_tools`, and `call_game_tool`. The last
two provide original schemas and invocation through a fixed MCP catalog.

## Status and call semantics

- Registration connectivity and last-seen age are separate from the latest host
  snapshot. A stale/disconnected snapshot does not establish process exit.
- Host `active` reports current host activity; client suspension clears activity
  without clearing latched readiness. Server readiness follows a successful tick;
  client readiness follows actual GPU presentation.
- Session/tick progress remains distinct from `graphics.presented_frames`, which
  counts successful presentation API calls. Optional graphics snapshots retain the
  last outcome, including skips and failures, across suspension and shutdown.
  Old snapshots without graphics remain readable; server graphics stays null.
- Native hosts register `diagnostics` for bounded
  tracing event history: 256 retained events, at most eight per page, process-local
  exclusive cursors, eviction reporting, and bounded fields with truncation flags.
  Capture shares the stderr filter and excludes span timing. History survives
  reconnects but is unavailable after the game disconnects.
- Games send snapshots every 250 ms. The bridge sends one ping per second and drops
  connections with no snapshot for over four seconds at the next heartbeat check.
  Adapters similarly detect missing bridge messages and reconnect.
- Calls use connection-local request IDs and per-call response channels. A game
  handler validates its arguments against its registered schema. The bridge validates
  routing/envelope arguments; it does not implement a general JSON Schema validator.
- Five-second call timeouts do not undo an operation. A disconnected or timed-out
  call may already have executed. There is no automatic replay of mutations.
- Cached offline calls return `instance_unavailable`; routing mismatches, unsupported
  tools, overload, and lost connections have explicit structured errors.
- Shutdown publishes final host status before the adapter closes when delivery is
  possible. If delivery fails, the bridge retains the last snapshot with disconnected
  status. A successful stop result acknowledges delivery, not process exit.

## Bounds and deployment scope

The game wire format is tagged, newline-delimited JSON. Reads retain partial frames
across actor wakeups and reject malformed, truncated, or oversized input. Limits:

| Resource | Limit |
| --- | --- |
| Concurrent game connections, including pending handshakes | 32 |
| Cached game/role catalogs | 32 |
| Retained instances | 128; oldest disconnected records evicted first |
| Tools per registration, including engine tools | 64 |
| Game protocol frame | 256 KiB |
| Queued calls and pending replies per instance | 32 each |
| Forwarded arguments | 128 KiB serialized |
| Connection handshake and individual writes | 2 seconds |
| MCP forwarded call wait | 5 seconds |

The listener only accepts loopback addresses. This is an unauthenticated interface for
trusted local development processes, not a remote service. Multiple bridges need
different ports. Host handlers must return promptly; blocked handlers can delay
heartbeats and adapter shutdown. A blocked game callback also delays host stop. The
default `nico-ops` core still has no external dependencies.

## Validation

Protocol tests cover malformed frames, version/name rejection, bounds, catalog
replacement, offline discovery, isolation, overload, timeouts, and reconnects. Native
coverage exercises both roles, diagnostics, graphics snapshots, explicit stops, and
independent restarts. See [README validation commands](../../README.md#run-and-validate)
and [recorded evidence](../roadmap.md#2-control-clients-and-servers-with-ai).

Profiling remains deferred; this work adds no profiler or function instrumentation.