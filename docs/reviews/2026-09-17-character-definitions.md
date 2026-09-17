# Character definition migration review — 2026-09-17

Historical review of the build and content described below. For current models,
bindings, and ownership, see the [character contract](../plans/2026-09-17-character-definitions.md)
and [Bestiary integration review](2026-09-17-bestiary.md).

## Implemented scope

Hero, grunt and brute now have `.char.toml` authoritative definitions and
`.char-vis.toml` client definitions. Native hosts read files at startup; they do
not require rebuilding to apply edits. The hero retains the imported Ch03 model
and selected sword/RPG clips. Grunt and brute retain their procedural models,
whose parts, proportions, colors and walk parameters now come from content.

Logic definitions drive health, movement, collision radius, attack range/angle,
phase timing, damage, dodge speed/duration/invulnerability/cooldown/buffering.
Spawn checks account for later brute waves; wall clamping, HUD and MCP phases
use configured values. Visual definitions own rig mappings, reference-pose
overrides, sockets, weapon parts, clip selectors, marker alignment, blend/speed,
height/floor alignment and optional authored locomotion speed.

Definitions are immutable shared content. The existing arena resource still owns
mutable simulation state; no ECS storage rewrite was needed. Client playback
uses compiled motion parameters and resolved attachments, with no per-frame
parsing or bone-name lookup. The [format contract](../plans/2026-09-17-character-definitions.md)
documents the supported subset and explicit limitations.

## Automated verification

The arena suite passes 29 client and 50 shared tests. Coverage includes unchanged
combat timing and deterministic three-wave completion, custom logic values,
collision radius changes, conservative spawn validation, shared catalog identity,
malformed content, ID matching, missing socket/clip resolution, model alignment,
contact markers, blend/stride speed and MCP definition/action-phase publication.
Existing grip and sampled weapon-clearance tests now load the migrated content.

Commands used for final verification:

```text
cargo test --workspace --exclude nico-bridge
cargo test -p nico-bridge --target-dir target/bridge-validation
cargo clippy --workspace --all-targets -- -D warnings
cargo fmt --all -- --check
git diff --check
```

The initial combined `cargo test --workspace` build could not replace the running
bridge executable in `target/debug`. The bridge was left running; its tests were
built separately in the isolated target directory above. This is an output-file
lock, not an arena test failure. No shader source changed.

## Native verification

Windows debug builds, client NVIDIA GTX 1660/Vulkan at 1920×1080. Test processes
were launched independently of the existing bridge, discovered through MCP, and
controlled only through their registered bridge tools.

| Host | PID | Instance | Final complete encounter |
| --- | --- | --- | --- |
| Client | 27360 | `18480-18d5f9a486be14ec-7` | Won wave 3 at tick 3,065, health 80 |
| Server | 9836 | `18480-18d5f9a486be14ec-8` | Won wave 3 at tick 2,700, health 100 |

`game_characters` returned identical logic catalogs on the two hosts.
`client_characters` reported all visual definitions and resolved hero clip
indices/durations, model scale, floor translation and weapon socket bone index.
The resolved sword attack duration was 1.5 seconds, with the authored contact at
0.444 seconds. The client restarted after victory into wave 1 with health 100.

Eight inspected GPU PNGs are retained under ignored
`target/character-definitions-2026-09-17/`: migrated idle and attack, wave-one and
wave-two clear screens, mixed wave-two and wave-three compositions, defeat and
victory. They show the imported hero and attached sword alongside the red grunts
and larger purple brutes. `evidence.json` records capture requests, separate
before/after presentation observations, subsequent game state, definitions,
diagnostics and run outcomes. `definition-hashes.json` fingerprints all six TOML
files. Repeated capture names retain the final sample and its matching record.

Earlier automation was not a clean encounter pass: the initial capture-heavy run
lost during wave 2 at tick 2,433. A subsequent one-tick movement policy reached
the 180-second observation limit during wave 2. The final controller used six-tick
movement commands and attacked during sufficiently early enemy windups, avoiding
excessive stop/start overhead. No authoritative parameters were changed to obtain
the client or server victories.

The client had 17,262 successful presentation API calls at the pre-stop status
sample and no host failure. Final retained host state recorded 17,266 client
steps and 16,436 server steps, both stopped without failure. Both Cargo sessions
returned exit code 0 after MCP stop. Automated control ended; the client window
closed. The Vulkan diagnostics contained the existing OBS layer API-version
warning, with no host rendering failure.

Captures and game/presentation observations are separate samples, not the same
frame. GPU readback establishes rendered output, not desktop visibility or display
scanout. No user-watched success was claimed or collected.

## Remaining work outside this migration

Foot contact, stride calibration, foot locking if needed, broader art acceptance,
imported enemy art, and original Ch03/RPG provenance remain open. The migrated run
keeps constant playback speed until stride measurements justify a value. There is
no hot reload, generalized equipment inventory, arbitrary behavior graph, or
serialized ECS prefab system. Collision remains the arena's grounded ball policy.
