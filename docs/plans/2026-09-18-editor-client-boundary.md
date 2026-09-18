# Editor and game client boundary redesign

Status: separate-process direction selected by the user on 2026-09-18. Details below
are proposed and unimplemented. This replaces this document's earlier embedded-game
proposal. Existing editor and bridge plans still describe current capabilities.

## Decision and ownership

The editor authors content and attaches to separate game clients and servers through
a versioned debug RPC contract. The same contract supports development and released
native desktop games with debugging explicitly enabled. Game rebuilds and restarts
leave the editor open. This is application-state debugging, not source breakpoints,
arbitrary memory access, code injection, or profiling. XRay remains deferred.

| Owner | Responsibilities |
| --- | --- |
| Editor | Documents, selection, undo, property UI, saving, attach UI, local test-session orchestration |
| Client | Runtime world, input, camera, animation, prediction, networking, rendering |
| Server | Multiplayer authority, persistence, authoritative debug commands |
| Game authoring adapter | Content schema, validation, serialization, preview interpretation |
| Engine hosts/operations | Transport, access control, registration, queues, diagnostics, reconnects, lifecycle |
| Bridge | Discovery and routing; never launching or supervising game processes |

Runtime stays headless. Tooling threads read owned snapshots or enqueue commands;
mutations run at runtime-owned boundaries. Games register domain tools without
owning debug service threads or transport lifecycle.

The editor does not embed gameplay. Shared content/presentation libraries may support
its authoring preview, which is not evidence of actual gameplay behavior. Initially
retain static authoring adapters. Extract reusable editor composition and move Arena
registration to a small game editor executable when removing the generic editor's
Arena dependency. That executable only calls engine-owned runners. A fully generic
editor additionally needs remote authoring schemas/validation or an authoring worker;
live debug RPC alone does not solve this. Released games need no source-writing tools.

## Protocol

Reuse nico-ops registrations, bounded commands, owned publications, and bridge routing.
Editor RPC and MCP use one domain operation contract and shared handlers. Editor access
to bridge routing is new work: the MCP stdio endpoint does not establish concurrent
editor/AI consumer support. Codex still connects only to nico-bridge; games expose no
direct MCP endpoints.

Handshake identifies protocol version, build, game/API, role, session/process, content
revision, and permitted capabilities. Compatibility need not require identical builds.
Replace the current game/role-wide catalog restriction with per-instance negotiated
capabilities so differing release builds can coexist. Preserve status, stop, and
diagnostics names; authorization may deny invocation without renaming them.

Initial operations: discovery/attach, status, diagnostics, paged entity/property
inspection, bounded capture, and registered game commands. Validated property edits
and content reload follow. Entity references include session/world generation;
snapshots report tick/revision and age, captures report their own frame identity.
Reject stale handles after reconnect or world replacement.

Commands distinguish rejected, accepted, applied, and failed outcomes. Never replay
mutations automatically after timeout. Bounded result lookup can resolve uncertain
outcomes; evicted results remain unknown. Bound queues, pages, captures, and rates,
with overload/truncation reporting. Start with polling; add subscriptions on demand.
Do not expose arbitrary execution, shell commands, or raw ECS memory transfer.

## Released games and remote access

The existing bridge is loopback-only and is not a production remote-access service.
Keep development defaults intact. A release artifact may include debug capability,
but operational connection is disabled by default and explicitly enabled at startup,
even for loopback. Compiling the capability must not automatically expose stop or
mutation tools.

First remote deployment: run a bridge on the game machine and reach its editor access
endpoint through an authenticated encrypted tunnel. Game connections remain loopback.
Implement that endpoint and authorization before claiming remote support. Do not
publicly expose the existing plain TCP bridge port. A future native remote transport
needs encryption, endpoint authentication, scoped credentials, and revocation.
Tunnel admission alone must not give every consumer unrestricted game control.

Remote access defaults to inspection-only. Separate grants authorize mutation,
capture, and host stop. Enforce policy at the trusted host boundary, bound to the
authenticated route/session, not only in editor UI. Exclude credentials from diagnostics
and source-controlled project files. Record privileged command identity/outcome as
operational diagnostics. Disconnect or revocation removes control and leaves games
running; a process can disable debugging without editor cooperation. Non-desktop
release platforms require their own deployment design and validation.

## Content and runtime state

Authored documents and undo belong to the editor. Runtime edits are temporary unless
the game explicitly defines persistence. Snapshots never silently overwrite source.
Applying runtime changes back later needs authored IDs, validation, conflict detection,
and an explicit editor operation.

Begin with save, restart an isolated playtest, and verify the actual loaded content
revision. Matching paths alone are insufficient. Reload comes later: validate a whole
candidate, adopt it at an owned boundary, and retain last-good state on failure.
Incompatible changes require restart. Authoritative world edits go through the server,
not a client-side spawn/collider patch.

Remote attach initially inspects deployed content. It assumes neither a shared
filesystem nor automatic asset upload. Remote deployment, staging, verification,
rollback, and permissions are separate capabilities if needed. Mapping runtime
entities to local authored sources requires matching content identities; otherwise
show runtime inspection only.

## Lifecycle and viewport

Attach grants no process ownership. Detach/editor exit leaves external games alive;
authorized host stop is a separate explicit operation. Launch creates an editor-owned
local test session with executable/Cargo target, arguments, mode, project/content
revision, server endpoint, and disposable persistence directory. Engine-owned lifecycle
code starts and cleans up only owned processes. The bridge never launches games.

Start an isolated server, observe readiness, then start its client. Partial failure
cleans up only owned resources. Report preparing, starting, running, stopping, exited,
and failed separately from connectivity, readiness, and presentation. Stop acceptance
is not process exit. Pause/step initially applies only to local simulation advertising
support; pausing a network client does not pause its authoritative server.

Start with the actual game in its own window beside the authoring viewport. RPC carries
commands and bounded snapshots/captures, not rendering operations every frame.
Embedding the remote Game view is deferred: GPU image sharing/video transport, input
forwarding, latency, resize, and focus are separate graphics work. Captures prove
rendered output, not desktop visibility.

## Migration milestones

1. Specify identity, compatibility, grants, and command/result contracts; implement
   editor access to bridge routing and per-instance catalogs while preserving MCP.
2. Implement development/release attach and inspection. Test disabled release defaults,
   allowed/denied calls, reconnect, stale IDs, version mismatch, disconnect survival,
   and bounded operational overhead.
3. Validate remote attach through an authenticated tunnel: authorization, revocation,
   diagnostics, capture, and independently running client/server hosts. Document the
   supported deployment only after validation.
4. Add local play profiles, save/restart/revision verification, isolated server data,
   readiness, startup failure cleanup, and process ownership tests.
5. Extend Arena encounter/spawn/collider authoring and supported reload; preserve save
   conflicts, validation, and undo. Separate generic editor from game registration.

Each milestone includes UI and structured automation. Visual validation identifies
the exact client and separates command completion, rendering, and user observation.
No implementation validation has been performed for this design.

Architectural acceptance: keep the editor open, attach to an opted-in release client,
inspect, detach without stopping it, then attach to a rebuilt compatible client without
rebuilding the editor. Authoring acceptance: edit and save an Arena encounter, then
verify it in the real client/server with matching content revisions.
