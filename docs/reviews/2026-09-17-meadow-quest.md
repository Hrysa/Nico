# Meadow Watch validation

Windows validation, 2026-09-17. The [contract](../plans/2026-09-17-meadow-quest.md)
owns gameplay rules and persistence semantics.

- Client all-feature tests: 39 passed. Shared all-feature tests: 66 passed.
  New simulation tests cover three real combat kills, acceptance, camp origin,
  proximity, dead-player interaction, reconnect, one-time reward, and already-owned
  swords. Store reopen coverage preserves partial progress; missing quest fields
  retain old-save compatibility and invalid stage/count pairs are rejected.
- Strict workspace all-target Clippy and formatting passed.
- Native binaries built in `target/bridge-validation`. The two-client scenario
  passed MCP quest acceptance, existing combat/loot/death/respawn checks, reconnect,
  and server restart, comparing quest state in persisted records.
  Evidence: `target/quest-world-evidence-retry/report.json`.
- PID-checked clients: `28820-18d613310782a034-2` (30624) and
  `28820-18d613310782a034-3` (24828). Inspected the first client's
  `quest-accepted.png`: gold procedural warden, marker, progress, distance, and
  nearby talk prompt rendered. GPU captures and snapshots are separate samples;
  no user-watched desktop confirmation was collected.
- The earlier run accepted the quest but stopped when the human-input/focus-loss
  guard cancelled movement. Its evidence remains at `target/quest-world-evidence`.
  The guard was unchanged for the successful rerun. Both runs cleaned up only
  their own windows/processes; automated control has stopped.

The full three-kill/return/reward loop is covered in simulation, not yet in the
native scenario. The NPC uses placeholder procedural art, has no collision or
combat role, and provides a direct interaction rather than a dialogue interface.
