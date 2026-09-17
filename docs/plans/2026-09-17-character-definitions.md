# Arena character definitions, version 1

Implemented format shared by the world and arena hero, grunt and brute. This document specifies
the composed authoring contract: reusable engine descriptors plus typed arena rules.
It is not a universal ECS prefab format.

## Files and identity

Each character has two TOML files:

```text
games/arena-arpg/assets/logic/characters/hero.char.toml
games/arena-arpg/assets/presentation/characters/hero.char-vis.toml
```

Logic uses `core.id = "arena.hero"`; presentation uses `core.character = "arena.hero"`.
Both require `schema_version = 1`. The arena catalog loads the fixed filenames
`hero`, `grunt` and `brute`, resolving those game roles to compact indices. IDs must
be unique; each visual ID must match the corresponding logic ID. This is a logical
content identity, not an ECS entity ID or a new engine asset-handle encoding.

Native hosts load logic files at startup from `--logic-characters`; the client also
loads visual files from `--visual-characters`. Defaults are the directories above.
Paths inside a visual file are relative to that file's directory. References must
contain only normal relative path components; absolute paths, traversal and URLs
are rejected. Files are local trusted content, not a filesystem sandbox. Existing
paired `--character-model`/`--character-animations` overrides replace the hero's
model path and animation directory while retaining the selected visual definition's
rig, clip selectors and other settings. `--procedural-hero` skips the imported model
and clips and uses the hero's procedural definition.

The library's default constructor embeds these same logic files for headless tests
and examples; native client/server constructors explicitly load disk content.
Editing a native host's file requires restarting that host. No inheritance,
implicit merging, live reload, scripts, or serialized Rust type names exist in v1.

## Composition and ownership

`nico-assets` exposes `CharacterCore` and `VisualCore` through its optional
`character` feature. These Serde descriptors have no runtime, physics-provider,
animation-player, or game dependency. Their `validate()` methods check reusable
content constraints; the game adds its own rules and resolves imported resources.
The engine does not parse or require an arena envelope.

The arena uses ordinary Rust composition, with matching TOML nesting:

```rust
pub struct CharacterDefinition {
    pub schema_version: u32,
    pub core: CharacterCore,
    pub arena: ArenaRules,
}
```

```toml
schema_version = 1

[core]
id = "arena.hero"

[core.collision]
shape = "ball"
radius_m = 0.4

[arena.stats]
max_health = 100
# The complete file also requires movement, attack and dodge settings.
```

The visual wrapper similarly contains `core: VisualCore` and
`arena: ArenaVisualRules`. Engine core owns identity, collision descriptors,
model references, humanoid profile mappings, pose overrides, sockets and a named
clip library. Arena owns health, movement policy, attacks, dodge, procedural
humanoid geometry, equipment composition and action-to-animation bindings.
There is no inheritance, field flattening, extension dictionary or `Deref`
forwarding. Another game can embed the same core beside its own typed rules.

This remains version 1 of the unreleased format; the earlier flat development
layout is replaced and is rejected by strict deserialization. All six shipped
files and both inspection tools use `core`/`arena` nesting.

## Authoritative definition

See the complete [hero](../../games/arena-arpg/assets/logic/characters/hero.char.toml),
[grunt](../../games/arena-arpg/assets/logic/characters/grunt.char.toml), and
[brute](../../games/arena-arpg/assets/logic/characters/brute.char.toml) definitions.

| Table | Fields and meaning |
| --- | --- |
| `arena.stats` | `max_health`: positive u16 health |
| `arena.movement` | `speed_mps`: normal movement speed, 0–60 m/s |
| `core.collision` | `shape = "ball"`, `radius_m`: 0.01–3 m |
| `arena.attacks.primary` | `damage`, `range_m`, `half_angle_degrees`, `windup_ticks`, `active_ticks`, `recovery_ticks` |
| `arena.dodge` | `speed_mps`, `duration_ticks`, `invulnerable_ticks`, `cooldown_ticks`, `buffer_ticks` |

Arena action timing uses integer ticks at the existing fixed 60 Hz step. Attack
phases must each be nonzero and their sum must fit u16. Attack damage is bounded
to prevent overflow when three monsters hit simultaneously. Invulnerability is
the half-open interval `[0, invulnerable_ticks)`; it must fit inside the dodge.
Cooldown must cover the dodge duration; the input buffer must fit the cooldown.

The collider is a kinematic ball whose centre Y equals its radius; authoritative
positions and motion are on X/Z. For unequal radii, sphere contact includes the
different centre heights. There is no authored collider offset, alternate shape,
or per-character filtering in v1: those would require extending the arena's
collision policy. All living actors block one another. Spawn checks conservatively
use the largest grunt/brute radius that may occupy each enemy slot in later waves;
wall clamping and physics shapes use the actual spawned character's radius.

Attacks use the existing centre-distance sector test, with the authored range and
half-angle. Weapon meshes and sockets never drive authoritative hits. Movement,
damage, dodge, health UI, telegraphs, and MCP action phases all consume the same
resolved logic definitions. Wave composition, AI scheduling, and the between-wave
heal amount remain encounter rules rather than character properties.

## Visual definition

The complete [hero visual](../../games/arena-arpg/assets/presentation/characters/hero.char-vis.toml)
is the reference example. [Grunt](../../games/arena-arpg/assets/presentation/characters/grunt.char-vis.toml)
and [brute](../../games/arena-arpg/assets/presentation/characters/brute.char-vis.toml)
select the imported Bestiary Imp and Puglin. All three definitions retain procedural
parts for definitions without models; `--procedural-hero` selects those parts only
for the hero.

Coordinates are right-handed, Y up, character forward +Z. Physical dimensions are
metres. Quaternion arrays are XYZW; nearly unit quaternions are normalized after
validation. Source bone offsets explicitly use source model units.

| Table | Contract |
| --- | --- |
| `arena.procedural` | Body/torso/weapon scales, body/head colors, horns flag, walk angular rate per tick and amplitude |
| `arena.procedural.parts` | Required `body`, `head`, `leg`, `arm`, `weapon`, `horn` boxes; each has `size_m` and `position_m`. Leg, arm and horn X offsets are mirrored. Walk modifies leg Z, and attack modifies weapon yaw. |
| `core.model` | Optional imported `asset`, `profile`, `target_height_m`, `floor_offset_m` |
| `core.profiles.<name>` | Explicit source/target `bones` mapping and `motion_root`; built-in `mixamo`/`rpg` names are reserved |
| `core.pose` | Array of bone-local rotation overrides: exact `bone`, expected `parent`, `rotation_xyzw` |
| `core.sockets.<name>` | Exact `bone`, `translation_model`, `rotation_xyzw` |
| `arena.weapon` | Optional attached weapon: socket name, `tip_m`, and bounded array of box parts with `size_m`, `center_m`, `color_rgba` |
| `core.animations.<name>` | Relative `asset`, exact `clip` name, source `profile`; names have no engine gameplay meaning |
| `arena.animations.<motion>` | `animation` references a core library key; `playback`, `speed`, `blend_seconds`, optional markers/tuning |

The game requires five bindings for imported models: `idle`, `run`, `attack`,
`dodge`, and `death`. Core libraries can contain any named clips, including unused
ones; the engine imposes no action list. Nonlethal health loss does not select a
reaction because the simulation has no injury/stun state. For example, an `attack`
binding can reference a core `sword_slash` entry. Missing binding targets fail
before playback. Idle/run loop; other motions play once. GLBs may contain multiple
clips, but the selected name must match exactly once. Bone and socket names must
also resolve uniquely. A pose override must match the authored direct parent;
the hero uses this to preserve its equipment finger pose through retargeting.

Custom profile `bones` entries follow `nico-animation::humanoid::Bone::ALL` order:
hips; spine, chest, upper chest; neck, head; left shoulder, upper arm, lower arm,
hand; right shoulder, upper arm, lower arm, hand; left upper leg, lower leg, foot,
toes; right upper leg, lower leg, foot, toes. The `quaternius_2025` profile maps
the older DEF rig used by the hero idle; `ual2` maps Library 2 and Bestiary rigs.
These are explicit content mappings, not automatic rig detection.

Model normalization measures reference mesh bounds once. Placement is
`Y = -reference_min_y * model_scale + floor_offset_m`, with
`model_scale = target_height_m / reference_height`. Socket translation remains in
bone-local source units. The weapon follows placement × animated bone hierarchy ×
socket transform; reference bone scale is compensated so weapon dimensions remain
in metres. An optional attached weapon uses 1–16 boxes drawn as one skinned mesh.
Its declared tip must match the forward extent of its geometry. Models with
embedded skinned weapons, including Imp and Puglin, omit `arena.weapon`.

`arena.animations.attack.contact_seconds` must lie strictly inside the source clip.
Its marker maps to the start of `arena.attacks.primary`'s active phase; the remaining
source time maps across active and recovery. Dodge maps to its configured duration.
Attack/dodge `speed` must be 1 because simulation phases own their timing. Other
clips use authored speed and blend duration. Optional run `authored_speed_mps`
scales playback by observed displacement speed, capped at 10×; omission preserves
the configured constant speed. The migrated run leaves this unset pending stride
measurement. This format does not implement foot locking or fix foot sliding by itself.

## Loading, ECS ownership and inspection

Parsing is bounded to 1 MiB per definition and rejects unknown fields, unsupported
versions, invalid numbers, missing dependencies, duplicate IDs and incompatible
references. Model/animation import retains existing byte and texture budgets.
Native startup finishes validation before entering the host loop. No automatic
fallback conceals a broken imported character.

The immutable validated `CharacterCatalog` is shared through `Arc`. Each actor
retains its catalog and role index; snapshots clone references without copying
definitions. Mutable health, movement, action progress and physics handles stay
with the instance/simulation. Client `CharacterAssets` shares imported models,
clips, attachments and compiled playback parameters; a `Character` owns its player,
pose and render state. Semantic animation names become fixed indexed parameters
before playback. Arena simulation remains a single runtime resource; the world
uses independent ECS actors. Imported presentation works in both modes without
per-frame parsing. Arena playback resets when a slot changes character type;
world playback is keyed by object ID and cleared on removal or epoch change.

Arena wrappers and gameplay validation live in `arena-arpg-shared` and the client.
`nico-assets::character` owns the reusable descriptors and content validation;
`nico-animation` owns evaluation. Engine host crates own transport and lifecycle.
The `character` feature adds Serde without enabling runtime loading or GLB/PNG
decoders. Runtime never depends on presentation.

Through `nico-bridge`, discover the connected mode's tools before calling them.
Arena hosts expose `game_characters` for authoritative definitions; both client
modes expose `client_characters` for startup visual definitions and a `resolved`
array ordered hero, grunt, brute. Entries include scale, optional weapon metadata,
and clip indices/names/durations. `hero_imported` and `hero_resolved` remain
available. These tools do not mutate or reload assets. Client state also publishes
per-entity `actor_animations`; world entries include stable object IDs.

## Verification

Regression coverage includes default behavior parity, custom health/speed/timing,
collision radius and spawn checks, malformed definitions, ID matching, missing
bones/clips, shared catalog lifetime, visual alignment/contact settings, and the
existing grip/weapon and deterministic three-wave tests. Native evidence is recorded
in the [migration review](../reviews/2026-09-17-character-definitions.md) and
[composition review](../reviews/2026-09-17-character-composition.md). Composition
regressions also cover independent engine-core decoding, strict ownership of
fields, arbitrary clip-library names, unused clips and missing action bindings.


### 2026-09-17 PBR implementation update

Model and animation source/import limits are unchanged. Material texture loading
now includes every referenced core PBR slot. The decoded RGBA texture budget is
256 MiB per character/preview model and 256 MiB across world decorations; the
selected hero alone contains four 4096-by-4096 images totaling 256 MiB. These limits
cover resident decoded pixels, not decoder temporary memory or GPU allocations.
