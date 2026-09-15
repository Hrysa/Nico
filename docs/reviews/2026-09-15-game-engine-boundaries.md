# Game/engine boundary follow-up review — 2026-09-15

## Scope and conclusion

**Status: resolved.** All four findings were fixed on 2026-09-15; see
[Resolution](#resolution--2026-09-15). The findings below preserve the original
review. Source line numbers refer to that earlier working tree and may have moved.

The review covered both games after camera, quaternion, text, mesh, spatial, input,
and command extractions. It found four follow-ups: two operational correctness
issues and two reuse opportunities. Production games compose engine hosts and do not
own bridge transport or service threads. Controller threads in examples demonstrate
the public control API and are not production host implementations.

## 1. Medium: rendering sample still implements operational command infrastructure

Evidence: `games/minimal-game/client/src/sample.rs:45`, `:86`, `:123`, `:317`, `:368`.
The sample owns a bounded channel, command IDs, terminal-history eviction, and closed
acceptance despite the new `nico-ops::commands` primitive. Its shutdown system marks
acceptance closed and releases assets, but does not drain accepted pending requests
into cancellation outcomes or publish a final closed sample snapshot. A request
accepted immediately before shutdown can lack a terminal result.

Move shared queue/ID/history/close mechanics into `nico-ops` and migrate this sample.
Preserve FIFO execution: arena CommandBook lanes have different ordering semantics,
so using whichever lane is empty is not a correct FIFO replacement. Keep sample
commands, asset selection, and Update-boundary application in the game.

Validation should cover queue order, overload, shutdown with accepted pending work,
and final readable outcomes. Existing rendering and bridge tests should still pass.

## 2. Medium: snapshot publication is duplicated and freshness is inconsistent

Evidence: `games/arena-arpg/client/src/view.rs:15`, `:29`, `:73`, `:84`;
`games/arena-arpg/shared/src/tools/mod.rs:66`, `:117`;
`games/minimal-game/shared/src/tools.rs:14`, `:30`.

Arena simulation snapshots track sequence/time; client and minimal-game snapshots
use separate ad hoc storage without consistent age or closed-state metadata. Arena
client_state describes pointer capture as actual host state but reads the last Update
snapshot. During suspension, it can retain a pre-suspension capture value indefinitely.
The engine window_state tool already provides the correct host observation path.

Extract an owned publication record with sequence, publication time, and closed state
into `nico-ops`. Keep serialization and gameplay fields in each game. Permit embedding
that record under an existing mutex so the arena can retain atomic snapshot/outcome
publication. Construct presentation outside the tooling lock and publish the result
under a short lock; this is an ownership improvement, not a measured speed claim.
Client state should describe its window fields as last-frame data or omit them in
favor of window_state. Test stale snapshots and final publication explicitly.

## 3. Medium: reusable coordinate transformations remain hand-written in the arena

Evidence: `games/arena-arpg/client/src/visuals.rs:186`, `:533`;
`games/arena-arpg/client/src/controls.rs:101`.

Actor-part local positions are rotated with manual sine/cosine expressions, movement
builds a camera-relative floor basis independently, and health bars derive cylindrical
billboard yaw. These are reusable mathematical operations. Quaternion snapshots are
already engine-owned, but the helpers constructing them are not consistently reused.

Use the existing engine quaternion operations for local-to-world transforms. Put
shared camera-plane basis and cylindrical billboard helpers in
`nico-presentation-control`; introduce no new crate or generic animation framework.
Keep axis conventions, choosing floor-relative movement, actor-part offsets, poses,
and animation timings in the game. This is not a claim of a current arena gimbal-lock
bug: its planar rules and restricted camera remain intentional.

Validation should preserve actor-part placement and screen-relative movement, and
specify behavior for a camera pointing parallel to the plane normal.

## 4. Low: initial-focus CLI policy is arena-only

Evidence: `games/arena-arpg/client/src/main.rs:19`, `:44`;
`crates/nico-launch/src/client.rs:17`.

The arena declares --background and maps it to native initial focus. This is native
host launch policy and is useful to other clients and automation. Move the flag and
its application into nico-launch::client while retaining NativeClientConfig as the
host configuration interface. Game titles and capture policy remain composition.
Validate parsing and launch configuration for both clients.

## Keep game-owned

Combat actions, damage, dodge buffering, monster scheduling, waves, spawns, healing,
restart arbitration, authored geometry, camera settings and target selection, input
bindings, palette, HUD wording/layout, and procedural actor poses remain game policy.
Small wrappers selecting mesh UVs/tessellation or font styles are legitimate game
composition. The existence of arithmetic or timers alone does not justify extraction.

## Suggested order

Fix sample command shutdown and reusable publication first, then coordinate helpers,
then the shared launch flag. Use existing engine crates; only add abstractions needed
by these concrete consumers.

## Resolution — 2026-09-15

All four findings are implemented in the working tree:

- `nico-ops::commands::FifoCommands` now owns the sample's queue ordering, IDs,
  bounded history, and queued-request finalization on close. The sample publishes
  cancellation outcomes and its final closed state before releasing resources.
- `nico-ops::publication::Publication` is used by both games' simulation and client
  state tools. Sequence, read-time age, and closed metadata are consistent. Arena
  rendering runs outside the tooling mutex; client window fields explicitly describe
  the last published frame, with `window_state` providing current host observations.
- `nico-presentation-control::coordinates` supplies quaternion point transforms,
  floor-relative direction rotation, and cylindrical billboards. The arena uses them
  while retaining its authored poses and bindings. Vertical billboard views return
  `None` and the arena selects an identity fallback.
- `nico-launch::client::ClientArgs` owns `--background` and applies initial-focus
  configuration for both clients. No new crate was introduced for these fixes.

Validation passed: workspace all-feature tests, strict all-target Clippy, both native
sample modes, and the full arena combat/window lifecycle scenario. Regressions cover
FIFO lane reuse, overload, queued shutdown cancellation, stale/closed publications,
coordinate conventions, billboard poles, and CLI parsing. Platform, scenario, and
artifact details are recorded in [roadmap phase 5](../roadmap.md#validation-evidence).
