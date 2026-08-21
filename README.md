# Nico

Nico is an experimental game engine written in Rust. This repository currently
contains a review-oriented architecture skeleton: a deterministic headless
runtime, an optional presentation layer, and optional runtime devtools.

Entity/component storage is provided by `hecs` behind Nico's focused world crate.
No native window, renderer, physics, audio, or UI library has been selected yet;
those choices remain under architectural review.

## Architecture

The runtime never depends on presentation:

```text
game client ──> presentation ──> runtime
game server ──────────────────> runtime
```

- `nico-runtime` owns lifecycle, schedules, fixed-step time, plugins, and world
  execution.
- `nico-ecs` owns the authoritative world, typed resources, entity storage,
  queries, and deferred structural commands while exposing the real `hecs` query
  vocabulary.
- `nico-launch` provides command-line and diagnostics bootstrap for native
  executables; non-CLI platforms supply their own launch integration.
- `nico-presentation` contains window, input, rendering, audio, and UI contracts
  and coordinates optional providers.
- `nico-physics` defines an authoritative runtime capability usable by servers.
- `nico-assets` owns shared asset identities and loading-state contracts.
- `nico-devtools` demonstrates optional runtime observation through public APIs.

Game structure and behavior are authored in Rust. Nico does not currently define
a scene document, prefab format, or visual editor. Those are deliberate review
decisions rather than missing implementations.

See [docs/architecture.md](docs/architecture.md) for dependency and ownership
rules.

## ECS usage

Engine-facing code imports ECS vocabulary through the canonical runtime
namespace:

```rust,ignore
use nico_runtime::{
    ecs::{Entity, World},
    RuntimeResult, SystemContext,
};
```

`nico_runtime::ecs` exposes the selected `hecs` query and command types without
duplicating them at the `nico_runtime` crate root. Systems query the authoritative
world directly and record structural changes through `context.commands`.
Successful commands are flushed before the next system runs; commands from a
failed system are discarded.

## Commands

```text
cargo test --workspace
cargo clippy --workspace --all-targets -- -D warnings
cargo run -p minimal-game-client
cargo run -p minimal-game-server
```

The minimal-game server runs continuously at 60 ticks per second until the
process is stopped. The client remains a bounded smoke application until a native
window/event-loop provider is selected.

Native client and server executables accept `--log-level
<off|error|warn|info|debug|trace>` and otherwise use `RUST_LOG`, defaulting to
`info`. The server also accepts `--tick-rate <TICKS_PER_SECOND>`:

```text
cargo run -p minimal-game-server -- --tick-rate 30 --log-level debug
RUST_LOG=nico_runtime=trace cargo run -p minimal-game-client
```

## Creating a game

Games are directories under `games/` containing separate shared, client, and
server Rust packages. The shared crate contains authoritative gameplay; only the
client links presentation. Each game also owns `assets/logic` and
`assets/presentation` roots.

See [`games/minimal-game`](games/minimal-game) for the first executable template.
