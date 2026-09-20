# Editor debug RPC contract

Status (2026-09-19): attachment, bounded inspection/capture, and authenticated SSH
forwarding implemented and validated on macOS. This covers milestones 1–3 of the
[boundary design](2026-09-18-editor-client-boundary.md). Separate-machine deployment
is unverified; play profiles and encounter authoring remain later work.

## Transport and identity

The existing game-registration endpoint uses wire protocol 2. Hosts and bridge must
be rebuilt together; protocol 1 registrations are rejected explicitly. Game/role,
API version, PID, connection instance ID, status and exact tool schemas identify a
registered target. Different builds may advertise different schemas concurrently.
Registration and attach include a process-lifetime UUID and a SHA-256 fingerprint of
the executable file opened at first debug startup. That fingerprint is cached for the
process lifetime, so an on-disk rebuild does not relabel an already running process.
It is an artifact identity, not remote code attestation. API compatibility is checked
independently of build equality. A game can supply `content_revision` through its
host builder; null explicitly means unknown. No source linking or content-equivalence
claim is allowed for unknown revisions. Automatic revision production by content
loaders remains part of the later play-profile/content-verification work.

The bridge optionally accepts editor RPC on a second loopback TCP listener while
continuing to serve MCP over stdio. Codex continues to use MCP exclusively. The editor
uses bounded newline-delimited JSON, independent of MCP protocol negotiation.
It sends `{ "protocol": 1, "token": "<endpoint token>" }` first. Unsupported
versions and credentials fail before discovery. Eight editor connections are allowed;
a handshake times out after two seconds and an idle request after 120 seconds.
Frames are limited to 256 KiB. No component launches a game in this attach path.

The bridge assigns a unique editor session ID. The peer cannot choose a call origin
or claim to be local MCP. One connection attaches to at most one exact instance ID.
Reconnect requires explicit discovery/attach; neither stale IDs nor timed-out requests
are redirected or replayed.

## Requests

| Method | Arguments | Effect |
| --- | --- | --- |
| `list` | None | Discover connected and retained instances |
| `attach` | `instance_id`, `api_version`, `credential` | Require exact API version and authorize host status; replace prior attachment |
| `catalog` | None | Reauthorize inspection and return the attached instance's exact schemas |
| `call` | `tool_name`, `arguments` | Route to the attached instance with this authenticated session's host credential |
| `detach` | None | Clear attachment without stopping the host |

Replies use the same structured tool-result representation as MCP. Attach includes
host-reported permissions and required access classes in `debug_access`. These are
an observation, not a promise that a subsequent call will still be authorized.

## Host grants and revocation

Endpoint admission and host authorization are separate. `--editor-token-file` selects
a private file containing a randomly generated 64-character hexadecimal endpoint
token. The bridge rereads it on every request. Removing or changing it revokes the
next request and closes that editor connection.

Each host can independently select `--debug-access-file` containing:

```json
{
  "grants": [
    { "credential": "<independent 64-character hexadecimal host token>" },
    { "credential": "<another independent host token>", "permissions": ["inspect", "capture"] }
  ]
}
```

Omitted permissions mean inspection only. Explicit permissions are `inspect`,
`capture`, `mutate`, and `stop`; no class implies another. The file is limited to
16 KiB and 32 unique grants. Malformed, missing, oversized, or duplicate-grant files
deny editor operations. Credentials never enter registrations, tool schemas, host
snapshots, debug formatting, or diagnostics.

The trusted engine host checks its local policy before calling a handler. It rereads
the policy at every admission, so the host operator can revoke access without editor
cooperation. Tool owners explicitly classify inspection/capture operations; all
unclassified extensions require mutation permission. MCP annotations grant nothing.
Stop retains its built-in name and requires separate stop permission.

Development hosts preserve full local MCP access. Release hosts do not connect by
default, even with a custom `--bridge`; `--enable-debug` explicitly opts in. Local MCP
on an opted-in release host is inspection-only. Privileged editor operations still
require host grants. `--no-bridge` disables operational connections. Rejected and
privileged handler outcomes produce bounded native diagnostic events without
credentials or arguments; connection instance and call ID support correlation. A handler reply can acknowledge queued work rather than
prove application; existing game outcome tools retain their own authority.

Revocation stops subsequent admission. It does not undo already accepted work.
Disconnect, detach, and editor exit never request host stop. Native process owners
remain responsible for independently started hosts.

## Editor worker and automation

The editor's Attach / Inspect panel and its `editor_debug_command` tool use one
engine-owned joined worker. Eight queued commands plus one in flight are allowed;
arguments are bounded to half a frame. Connection and file I/O never run on the UI
or game runtime thread. `editor_debug_state` reports last-observed connectivity,
attached ID, pending work, snapshot age, and bounded outcome summaries.

Queue acceptance returns `command_id`. A terminal `reply` means a tool reply arrived;
a `transport_error` leaves any sent mutation's outcome unknown. Large summaries are
explicitly truncated. `editor_debug_result` retrieves full retained replies as UTF-8
JSON fragments, using `command_id`, byte `offset`, and `limit` (at most 16 KiB).
Follow returned `next_offset` until `complete`. Retention is at most 32 outcomes and
1 MiB; eviction is reported and evicted results stay unknown. Shutdown joins the
worker, closes its connection, and records a terminal `cancelled` outcome for each
accepted command that had not started.

Transport failures close the connection. A failed attach clears the prior selection;
credential-file failure also closes the connection so later calls cannot accidentally
use the previous target. No operation retries automatically.

## Owned entity inspection

`nico-ops::inspection::Inspection` is runtime-free. Games extract explicitly selected
properties at their owned update boundary and publish a validated snapshot. The
minimal game's client/server publish generational ECS IDs and position properties;
this is not automatic reflection of arbitrary components or raw ECS memory.

`debug_entities` begins a pinned snapshot or continues its returned `next_cursor`.
`debug_entity` reads properties using that `snapshot_id` and an entity reference.
Both require inspection permission. References contain process UUID, world UUID,
world generation, connection epoch, and the game's generational entity ID. World
replacement invalidates the generation; bridge reconnect invalidates the connection
epoch without claiming the world was replaced. Old references and query cursors fail
explicitly. A pinned query remains on its original revision/tick while updates proceed.

A publication holds at most 4,096 records and 1 MiB of serialized entity data. Each
record has at most 64 properties and 16 KiB; duplicate IDs and invalid/oversized
candidates preserve the last-good snapshot. Games report total entity count separately
so truncated extraction remains visible. At most eight pinned snapshots are retained
for 30 seconds. Pages contain at most 64 records and 64 KiB of entry JSON; property
lookups are bounded by the record limit. Evicted/expired query IDs stay stale. Shutdown
closes inspection without changing world state. The editor panel offers entity pages
and property inspection alongside registered tools; replies retain their target
instance ID so cached output from a prior attachment is not mistaken for the new one.

## Bounded capture transfer

`window_snapshot` retains an immutable encoded PNG of at most 32 MiB. Its ready
result identifies the process session, capture request, zero-based host frame,
dimensions, byte count, and SHA-256. Frame identity is not a simulation tick or proof
of GPU completion. `window_snapshot_read` requires capture permission and reads up to
16 KiB per request by capture ID and byte offset; a replacement capture expires the
previous ID. Reads use retained bytes, never a caller-supplied host path.

The editor's **Capture to file** action polls and downloads through its worker with
a 30-second operation deadline plus bounded call time. It validates identity and
ranges on every chunk and checks the completed hash. The destination is editor-local,
created exclusively without overwriting an existing file. Failure removes its partial
file; cancellation is supported. The host-local compatibility path is not used by
this transfer. MCP automation uses `editor_debug_command` with action `capture` and
`destination`, followed by the normal command-result lookup.

## Deployment limits

Both listeners trust the local machine's process boundary. Remote editors must use
an authenticated encrypted tunnel to the editor listener only. Do not expose either
plain TCP port publicly. Tunnel admission does not replace the endpoint token or host
grants. Authenticated OpenSSH forwarding was tested on one Mac; separate-machine,
WAN, and Windows deployment remain unverified. Local credential files should be
private to their owner, outside source control, and populated using a cryptographically
secure random generator.

## Validation evidence (2026-09-19, macOS)

Workspace all-feature tests and strict all-target/all-feature Clippy passed during
implementation. The endpoint regression uses real loopback sockets and an engine
host worker to exercise version/credential rejection, exact API selection, read
permission, denied and granted mutation, revocation, detach, and host survival.
Worker tests cover bounded retention, explicit eviction, credential-error redaction,
UTF-8 result pagination, closed queues, and joined shutdown.

`python3 apps/nico-bridge/tests/editor_rpc_smoke.py --bridge target/debug/nico-bridge
--release-server target/release/minimal-game-server` passed after building those
binaries. The isolated test observed a live release server without any registration
for two seconds when only `--bridge` was supplied; the explicit opt-in instance became
ready and supported inspection. Local MCP and inspection-only editor stop calls were
denied. Host policy removal revoked calls; policy restoration allowed inspection.
Detach and a bridge restart left the server running. Reconnection assigned a new ID;
the old ID was rejected. Diagnostics were readable and separately granted stop led to
exit 0. Neither credential appeared in process logs. Only test-owned processes were
cleaned up. This is a headless process scenario, not native editor or remote-tunnel
acceptance.

Identity/inspection follow-up: all 39 `nico-ops` all-feature tests passed, as did the
minimal-game shared tests and strict workspace Clippy. The updated release-server
smoke test compared the registered fingerprint against the actual executable bytes,
checked process identity across bridge restart, read entity properties from the same
pinned tick, and rejected the old entity reference after reconnect. No native or
remote conclusion follows from that server-only scenario.

Native acceptance (macOS, Apple M4/Metal): `editor_native_smoke.py --rebuild` kept
editor PID 40276, instance `40275-18d67a7caa7c5458-1`, running while it attached to
release client PID 40278 (`...-2`), inspected an entity at pinned tick 18, and detached.
The client's presentation counter advanced from 171 to 190 after detach. While that
editor stayed open, Cargo rebuilt the compatible client with additional debug info;
the executable fingerprint changed from `a053a2fe...` to `8d146a34...`. The same editor
attached to replacement client PID 40307 (`...-3`) and inspected its separate process
and world identities. Authorized stop was followed by exit 0 for both clients and
the editor; all test-owned windows closed and automated control ended.

GPU captures retained in `target/editor-attach-native/` were inspected: the editor
capture shows the attached-instance panel and entity inspection; both release-client
captures show the textured 2D sample and HUD. Captures and state were sampled
separately. This establishes rendered output, not desktop visibility, physical UI
interaction, or user-observed acceptance. `evidence.json` retains full identities,
request IDs and separately sampled status. No existing user processes were stopped.

Authenticated-tunnel acceptance (same Mac, Apple M4/Metal):
`editor_native_smoke.py --ssh-tunnel` used independently launched editor PID 41114,
release client PID 41116, and release server PID 41119. The fixture rejected a wrong
SSH key, pinned the server key, exercised host-policy revocation and diagnostics,
and confirmed both hosts survived tunnel loss before reconnecting. Client
presentations advanced 168 to 189 after detach. The editor downloaded the client PNG
through RPC chunks: frame 165, 17,967 bytes, SHA-256
`e616ce9b83dca31d8838af0f6802a06faba6e25373b2b35a15293ab0593c6619`.
The downloaded image and editor capture were inspected; retained identities and
separately sampled state are in `target/editor-attach-ssh/evidence.json`.
Authorized shutdown completed and all test-owned processes/windows closed.
This validates encrypted forwarding on loopback, not a physically remote deployment
or user-watched demonstration. Capture tests also cover missing host-path access,
identity changes, corruption, partial-file cleanup, cancellation, and no overwrite.
The final operations regression suite passed all 42 tests, including per-command
shutdown outcomes.
