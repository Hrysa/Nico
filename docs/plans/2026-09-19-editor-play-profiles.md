# Editor client/server play profiles

Status: implemented for paired minimal-game and Arena world profiles (2026-09-19). Implements milestone 4 of the
[editor boundary design](2026-09-18-editor-client-boundary.md).

The first profile runs the minimal game's separate client and server. These hosts
currently simulate independently; this workflow does not add multiplayer transport.
The editor owns the session; the bridge remains a router and never launches games.
Externally attached processes are never adopted or cleaned up by a play session.

## Content and configuration

A profile selects declared Cargo client/server targets, project root, build mode,
bridge registration endpoint, editor RPC endpoint, and endpoint credential file.
Implementation update (2026-09-21): Play now loads the existing project, as requested,
instead of copying content into a temporary project on every launch. This supersedes
the original snapshot-isolation behavior recorded in the historical validation below.
The editor and hosts share the game-root `.nico` import cache. After building, a
deterministic bounded SHA-256 revision covers relative paths
and exact file bytes. Symlinks and special files are rejected. Both hosts load this
project and independently calculate/report its revision. Unknown or mismatched
revisions fail readiness. Import caches and persistence are not content inputs.
Content is limited to 32,768 files, 65,536 visited entries, directory depth 64, and
512 MiB. The digest is versioned and includes UTF-8 relative names and lengths.
Hash loops check cancellation. Edits during host startup can fail revision checks;
saved edits require restart rather than promising immutable session content.
Play reuses the game's `.nico/play/server-data` across launches. A file lock in
`.nico/play` excludes concurrent Play sessions for the same project. Unsaved edits require
saving before launch/restart. `content_path` identifies the original project;
`session_path` identifies the reusable project-local Play directory.

## Ownership and lifecycle

Engine code in `nico-launch` owns a joined worker, bounded commands, owned status,
build subprocess, and direct executable children. File I/O, building, discovery,
readiness waits, and process waits happen off the editor UI/runtime thread.
Build completes before hosts launch, avoiding ownership of a `cargo run` wrapper.
Cargo uses the normal workspace `target/debug` or `target/release` binaries directly.
Play creates no temporary projects, separate build trees or executable copies.
On Windows, another running instance may prevent rebuilding its executable; stop
that instance before rebuilding. Play does not stop independently launched hosts.
Builds have a ten-minute deadline; each host readiness wait has a 120-second deadline
plus bounded RPC calls. Stop allows three seconds after an accepted host stop before
forcing direct-child termination. The UI remains responsive during these waits.
The worker starts the server, discovers its exact PID and verifies ready/content
identity, then starts and validates the client. Preparing, building, starting,
running, stopping, exited, and failed remain distinct. Command acceptance does not
mean readiness or process exit. Startup has a deadline and cancellation. Cargo builds use a dedicated Unix process
group (Windows process-tree termination), so cancelling a build also stops compiler
children. Game cleanup still uses only the retained direct child handles.

The session creates private host credentials and uses existing editor RPC for
inspection and orderly stop. Release hosts explicitly opt into debugging. The
worker retains child handles; partial startup failure, stop, restart, and editor
exit clean up only these children. Failure of RPC triggers bounded direct-child
termination/reaping. No PID scans, process-name kills, or ownership by attachment.
Bridge loss is reported independently and does not itself stop a running session.

## UI and automation

The editor exposes profile configuration, start/stop/restart, phase, role-specific
PID/instance/readiness, content revision, persistence path, and terminal error.
The existing `editor_command` exposes `configure_play`, `play`, `stop`, `restart`,
and `save_and_restart`; `editor_state` publishes `play_profile` and `play_session`.
The command queue records intent application, distinct from the worker's eventual
session phase. The worker admits one active session and retains summaries of eight
previous sessions. Role observations carry their own age, not the editor publication
age. Loss of observation clears connected/readiness without claiming process exit. Save/restart shares
the editor's existing save validation and conflict checks. Existing external
Attach / Inspect remains independent of play ownership.

## Acceptance

Focused tests cover deterministic snapshots, changed bytes/paths, bounds and unsafe
paths; clean/dirty launch behavior; build and startup failures; revision mismatch;
cancellation and terminal command outcomes; isolated persistence; restart; graceful
and fallback cleanup; and survival of unrelated processes. Native MCP validation
starts the paired profile, checks both content identities and readiness, inspects a
capture from the owned client, restarts after a saved content change, and confirms
only owned processes exit. Report platform and rendered-output limits explicitly.

## Persistence and limits

Minimal-game server `--data-dir PATH` optionally loads bounded versioned
`progress.json` before runtime startup and saves durable stamina, quest progress,
and coins after orderly shutdown. Tick counts and transient movement are not saved.
Without that flag, existing in-memory behavior remains. Play retains its project-local
server data after child exit, and restart loads that saved progress.
A forced stop cannot promise a progress save; `server_progress_saved` reports whether
the file existed before cleanup. `resources_removed` reports session credential cleanup;
it does not mean the project, saved data, or import cache was removed.

Profiles use a game-owned project CLI contract and both manifest targets.
The optional manifest `[play]` declaration provides `content_roots`, `server_args`,
`client_args`, `server_tool`, `client_tool`, and a `server_address` JSON pointer.
Defaults preserve minimal-game behavior. Additional content roots participate in
hashing without widening the editor's asset browser. Tool names
are bounded identifiers, argument lists have at most 32 strings of 1,024 bytes, and
roots retain containment checks. Arguments go directly to the executable without a
shell. The only substitution is a whole `{server_address}` client argument, resolved
from the owned server's ready result and required to be a bound loopback endpoint.
The game readiness tool returns `ready`; host readiness is checked independently.

Arena declares its own world tools, `--listen 127.0.0.1:0`, and the matching client
`--server` argument. Both hosts accept `--project`, load world source paths and
character/item content from the saved project, verify it after startup loading, and
publish the revision. The server uses the session data directory rather than
`target/world-data`. `server_progress_saved` specifically describes minimal-game's
`progress.json`; it does not certify Arena's character-file persistence.
Networking between the minimal hosts, remote launch, pause/step, and persistent
profile presets remain outside this slice.
Local content is trusted input; symlink rejection is not a hostile-filesystem sandbox.

## Validation

`cargo test --workspace --all-features` passed on macOS. Focused tests cover content
identity, snapshot isolation, cancellation, oversized/symlink inputs, server scene
loading, isolated progress storage, exact PID/role/revision selection, worker closure,
private policies, and direct-child ownership. The native harness is
`python3 -B apps/nico-bridge/tests/editor_play_smoke.py --bin-dir target/debug
--evidence target/editor-play-evidence` after building editor, bridge, and minimal server.

Native macOS/Apple M4 Metal evidence: editor PID 44265, instance
`44263-18d69db7365f5440-2`, started server PID 44290 and client PID 44291 with matching
content revision `sha256:f0eb03b48f94c360e9d71b929d03df5272804fe06030b9b37c142cf696875ed3`.
A saved transform edit and restart produced matching revision
`sha256:a4e02b8de6439e1ba14cfc44f1128171c09806a88e321b82d4d1454029b6dd26`
in replacement hosts, with the edited position in both owned scene snapshots.
Orderly stop exited both with code 0 and saved server progress before deleting the
session directory. Build failure, partial client-startup failure, and cancellation
while a server waited to register all cleaned up.
Both owned hosts survived bridge restart, kept process identities, and acquired new
connection IDs. Revoking endpoint admission exercised forced child cleanup; an
independently launched server survived every session operation. Editor exit also
removed its owned session. Captures show the profile panel and authored cube in the
owned client. Captures and state were sampled separately, with no desktop visibility
or user-observed acceptance claim. All test-owned windows/processes closed.
Full identities and outcomes are retained in `target/editor-play-evidence/evidence.json`.
The final focused checks also cover compiler-descendant cleanup and restored game
progress without duplicate rewards. Strict workspace all-target/all-feature Clippy,
formatting, and whitespace checks passed. Windows and physically remote play launch
are unverified.

Arena regression (2026-09-19, macOS/Metal): `editor_play_smoke.py --arena` passed
with test editor PID 45418, server PID 45593, and client PID 45595. The client
connected to the server's dynamically assigned port, both revisions matched, and
rendered meshes/input readiness were published. Inspected captures show the Arena
profile panel and Meadow with its hero and connected HUD. State and capture samples
are separate; the initial capture displayed FPS 2 and establishes no performance or
user-observed acceptance. Both owned hosts exited 0, session files were removed,
and the unrelated test server remained alive. Full identities and PNGs are retained
in `target/editor-arena-play-evidence/`. The existing user editor was left open.
Regression tests cover adapter Play admission, additional snapshot roots, and
rejecting missing, unbound, or non-loopback server endpoints.
