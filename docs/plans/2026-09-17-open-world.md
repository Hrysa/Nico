# Open-world multiplayer milestone

Status: implemented and validated on Windows, 2026-09-17. This is the first playable milestone,
not a claim of MMO-scale capacity or production deployment readiness.

## Required result

Refactor the arena game into a server-authoritative action RPG world. Preserve the
existing arena as an explicit combat test mode. The default multiplayer path must
support two native clients in the same outdoor zone, a settlement, a monster camp,
movement, aimed sword attacks, dodge, death and respawn. A defeated monster drops
an item which a player can pick up and equip. Character position, equipment and
progress survive reconnect and a clean server restart.

Independent ECS entities replace fixed actor slots in the open world. Shared
character definitions remain immutable. Clients never decide hits, loot ownership,
health, progression or persistent inventory. Clients predict local movement,
reconcile against acknowledged inputs, and interpolate remote entities. Nearby
entity replication uses spatial interest management. The server validates bounded,
sequenced input and expires stale movement when a client stops sending.

## Boundaries

- `nico-net`: bounded native game transport, framed messages, connection lifetime
  and backpressure. No world access, game types, persistence or MCP transport.
- Game shared: protocol, authoritative ECS world, zones/spawns, actions, AI,
  inventory, character records and replication selection.
- Game server: composes native transport, persistence and runtime-owned systems.
- Game client: connection state, prediction/reconciliation, interpolation, input,
  per-entity animation players, world rendering and interaction HUD.
- `nico-ops` / `nico-launch`: retain bridge transport and host lifecycle. Game
  extensions queue bounded requests and publish owned snapshots only.

First transport is framed TCP for the local playable milestone. Connections default
to loopback; this is not an Internet login/security system. Stable local character
identity and exclusive active sessions must prevent duplicate control. Connection
loss must not stop the server, and failed persistence must be observable rather
than reported as a successful save. Protocol/schema versions reject mismatches.

The map starts as a bounded outdoor zone with authored obstacles, settlement spawn
points and a camp. Ordinary monsters respawn; their state is not durable. World
definitions and item definitions live under game logic assets. The existing
`.char.toml` / `.char-vis.toml` core/game composition remains in use.

## Implementation and verification sequence

1. Add independent ECS actors, combat, monster AI, respawns, drops, inventory and
   per-player snapshots. Verify action timing, damage authority, simultaneous
   claims, bounded movement, entity lifetime and spatial filtering.
2. Add bounded engine transport and the game session protocol. Verify two clients,
   malformed/oversized frames, disconnect, backpressure, sequencing and stale input.
3. Integrate a persistent authoritative server with atomic character saves and
   explicit save failures. Verify reconnect, duplicate sessions and restart restore.
4. Integrate native clients with movement prediction, acknowledgements and remote
   interpolation. Render the outdoor zone, both players, monsters and loot; retain
   the sword-specific animation and calibration. Add pickup/equip/respawn controls.
5. Expose structured world/session/entity/replication inspection and queued spawn
   and player actions through the bridge. Add an automated two-client native test.
6. Validate both exact client instances with active-action captures; verify shared
   combat, item pickup/equipment, death/respawn, reconnect and server restart. Record
   process identities, separately sampled state, rendered evidence and clean exits.
   Run relevant regression tests, workspace checks, formatting and Clippy.

## Deferred beyond this milestone

Large seamless worlds, cross-server transfers, guilds, trading, Internet account
security and large population capacity. Do not claim those from a two-client test.
Preserve the original full milestone until every requirement above is verified.

## Implementation checkpoint

The shared ECS world, framed `nico-net` transport, local character store, versioned
session protocol and server runtime adapter are implemented. The world is the
default client/server path; `--arena` selects the retained independent combat test.
The server exposes `--listen`, `--data-dir`, `--world-asset` and `--item-asset`.
`world_state` publishes owned world/session/interest data and command history;
`world_spawn` queues bounded requests at fixed simulation boundaries.

Tests exercise independent players, action authority, exclusive loot claims,
spatial interest filtering, respawn, save/reopen, two real loopback connections
and character restoration after server restart. Transport tests cover fragmented
frames, bounded queues, independent disconnect and final-frame delivery. The
respawn regression exposed sparse collider IDs in Parry's small-tree bulk builder;
the engine query adapter now uses insertion for one/two-leaf trees, with a focused
collision regression. Native clients now integrate prediction, remote interpolation,
per-entity animation, the outdoor zone, interaction controls and structured tools.
The [native checkpoint](../reviews/2026-09-17-open-world-native.md) records movement,
combat/death/respawn, loot/equipment and exact character restoration across reconnect
and server restart. It also records the clock-drift fixes and limits of the evidence.
The repeatable two-client native scenario passed, including cooperative combat,
dodge, loot, respawn and persistence. The linked review records the completion
audit, exact process identities, build checks and platform limitations. Remaining
product work beyond this milestone is tracked in [TODO](../../TODO.md).
