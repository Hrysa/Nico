# Ch03 sword grip and attack selection

Historical review of the build and content described below. For current models,
bindings, and ownership, see the [character contract](../plans/2026-09-17-character-definitions.md)
and [Bestiary integration review](2026-09-17-bestiary.md).

## Result

The arena uses Quaternius `Sword_Idle` and `Sword_Attack` in place of the unarmed
idle/punch. A game-owned Ch03 reference curls 15 right-finger joints and keeps the
grip through all six actions. The left hand and body mapping are unchanged. The
handle crosses the palm; handle, guard and blade share one mesh/palette. The
1.10-metre socket-to-tip distance clears the floor in sampled clips while meeting
the target body at the existing two-metre attack range. Shared simulation rules
are unchanged.

The new marker is 0.444 seconds into the 1.5-second sword clip. The phase-to-clip
mapping places it at the authoritative active-phase boundary. This is an authored
visual contact, not animation-driven damage or guaranteed blade contact throughout
the entire damage sector.

The [asset notice](../../games/arena-arpg/assets/presentation/characters/hero/LICENSE.md#previous-quaternius-sword-selection-2026-09-17)
records the CC0 source, mirror revision, hashes, extraction command and license.
The official current download timed out; the selected mirror is the 2025 DEF-rig
export, predating the publisher's 2026 sword-elbow fix. Inspected poses are usable
for this local integration; this does not establish every-frame quality or parity
with that newer release. Original animation samples are unchanged in the extracted
skeleton-only GLBs.

## Implementation and automated validation

The game uses `HumanoidRig::from_reference`; no engine finger mapper, twist handling
or new pose-layer API was needed. The grip was initialized by comparing the supplied
RPG idle fist with Ch03. RPG/Ch03 provenance remains separate from the CC0 sword
clips. Missing, duplicate or incompatible finger chains fail before host startup.
Custom models need a separately validated grip; this is not an automatic solver
for arbitrary Mixamo characters.

`client_control` camera requests now accept optional `distance` (0.5..12 metres).
The runtime-owned extraction stage applies it and publishes the command ID. The
engine orbit controller retains collision shortening and smoothed restoration;
omission preserves the configured distance. This enabled close-up MCP inspection.

Validation on Windows, development build:

- `cargo test -p arena-arpg-client -p arena-arpg-shared -p nico-presentation-control`:
  24 client, 45 shared-game and 13 presentation-control tests passed.
- `cargo clippy -p arena-arpg-client -p nico-presentation-control --all-targets -- -D warnings` passed.
- Grip regressions cover all six clips at five times each, an intermediate fade,
  unchanged non-finger reference transforms, invalid names/hierarchy and full
  rendered weapon bounds.
- Contact tests check tip overlap with the forward target's 0.4-metre body radius
  at a centre distance of two metres and a body-height Y. Existing controller tests
  verify alignment with the authoritative active-phase boundary.
- Endpoint-inclusive 241-time sampling per clip checks every weapon vertex against
  the Y=0 floor. This is finite sampling of unblended clips, not a continuous proof
  or coverage of every transition.
- Re-extraction produced byte-identical shipping GLBs.

The trial 1.16-metre blade penetrated the floor by about 1.5 cm during the roll.
The final 1.10-metre version passed sampled clearance and contact checks.

## Native validation

Final client: PID **14772**, instance `18480-18d5f9a486be14ec-5`, Windows/GTX 1660/
Vulkan, 1920x1080 readback. Eight inspected PNGs plus before/after state, command
outcomes and diagnostics remain in ignored `target/grip-final-2026-09-17/`.

| Captures | Observation |
| --- | --- |
| `idle-grip-front`, `idle-grip-reverse` | Curled right hand and palm-centred handle from two views |
| `attack-strike`, `attack-windup` | Sword follows the equipped hand through swing and windup |
| `run-grip` | Moving hero retains grip and attached sword |
| `roll-grip`, `roll-recovery` | Inversion and recovery retain the sword; no obvious floor penetration in these images |
| `combat-0` | Sword action rendered among live opponents |

Commands completed through the bridge, including a 120-tick movement lease.
A brief encounter recorded enemy health dropping from 60 to 35; the hero died
before the planned five attacks completed. This was an action review, not a victory
run. Camera commands were observed applied. Host status reported no failure,
3,237 presentation API successes and 3,238 steps. MCP stop completed, the window
closed and process exit was 0. Diagnostics included the existing OBS Vulkan warning.

Images and state are separately sampled. The `combat-0` preceding snapshot reported
0.444 seconds, but the image is not asserted to be that exact contact frame. Some
after-snapshots show idle or hit. GPU readback does not establish desktop visibility;
no user-watched demonstration was confirmed.

Foot sliding, character-floor contact, self-intersection across all blends, a fresh
full-wave native run, replacement rigs and broad art acceptance remain outside
these results. Remaining work belongs in [TODO](../../TODO.md#client-humanoid-models-and-animation).
