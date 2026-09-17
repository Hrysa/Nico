# Character composition review — 2026-09-17

Historical review of the build and content described below. For current models,
bindings, and ownership, see the [character contract](../plans/2026-09-17-character-definitions.md)
and [Bestiary integration review](2026-09-17-bestiary.md).

## Scope

Follow-up to the [initial migration](2026-09-17-character-definitions.md).
The current [format contract](../plans/2026-09-17-character-definitions.md)
uses explicit `core`/`arena` sections in all six hero, grunt and brute files.
`nico-assets::character` provides runtime-free descriptors behind an optional
feature; arena wrappers compose them with typed game rules. Core animation keys
are independent of game actions. Arena bindings resolve the six required actions
once, preserving the existing sword selection, grip, contact marker and playback.

No actor storage rewrite, asset inheritance, flattening, automatic ECS spawning,
new collider shapes or imported enemy models were added. The unreleased format
remains version 1; its previous flat development layout is no longer accepted.

## Automated checks

Passed on this Windows workspace:

- `cargo test -p nico-assets --features character -p arena-arpg-shared -p arena-arpg-client -p arena-arpg-server`: 31 client, 50 shared, 13 asset unit tests, four asset integration tests and one asset doctest.
- `cargo test -p nico-assets --no-default-features --features character`: five unit tests, three integration tests and one doctest, without runtime loading or native import features.
- `cargo test --workspace --exclude nico-bridge`.
- `cargo test -p nico-bridge --target-dir target/bridge-validation`: three registration tests. The separate output directory avoids touching the user's running bridge executable.
- `cargo clippy --workspace --all-targets -- -D warnings`.
- `cargo fmt --all -- --check` and `git diff --check`.

New regressions cover loading core data without arena rules, rejecting misplaced
game fields, core clip libraries with one arbitrary action name, binding a renamed
sword clip, accepting unused library entries, and rejecting missing bindings.
Existing collision, combat timing, custom settings, MCP inspection, grip, blend
and sword-contact tests pass. Cargo emitted incremental-cache finalization access
notes; compilation and tests completed successfully.

## Native execution

Fresh hosts loaded the six migrated files from disk. Only test-owned processes
were controlled. The user's bridge PID 18480 remained running.

| Host | PID | Bridge instance | Result |
| --- | --- | --- | --- |
| Client | 30244 | `18480-18d5f9a486be14ec-9` | Run 3 won at tick 3,221, wave 3, health 80 |
| Server | 9724 | `18480-18d5f9a486be14ec-10` | Run 2 won at tick 2,375, wave 3, health 100 |

Both hosts accepted restart and published wave one with 100 health. Both accepted
orderly stop, reached disconnected/stopped bridge state and exited with code zero.
Client stopped after 3,305 host steps; its pre-stop observation recorded 3,304
successful presentation API calls and no host failure. Server stopped after 3,402
host steps with no failure. These counters do not mean GPU completion or scanout.

The client used GTX 1660/Vulkan at 1920×1080. Six GPU captures under
`target/character-composition-2026-09-17/` show an active sword attack, both cleared
wave screens, wave-two/three rosters and victory. The imported hero, attached sword,
procedural grunt/brute and HUD were visible in those captures. The attack capture
was bracketed by attack and idle snapshots; those samples are not the same frame.
`evidence.json` preserves the separately sampled states, loaded definitions,
terminal results, restart results and host status. Automation stopped and the
client window closed after validation.

This was not a user-watched demonstration, so no user-observed success is claimed.
The existing OBS Vulkan layer-version warning was present. Foot sliding and broad
animation-quality acceptance remain outside this composition refactor.
