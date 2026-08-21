# Nico

Nico is an experimental game engine written in Rust. This repository currently
contains a review-oriented architecture skeleton: a deterministic headless
runtime, an optional presentation layer, and optional runtime devtools.

No native window, renderer, ECS, physics, audio, or UI library has been selected
yet. Those choices will be made after their abstraction boundaries are reviewed.

## Architecture

The runtime never depends on presentation:

```text
game client ──> presentation ──> runtime
game server ──────────────────> runtime
```

- `nico-runtime` owns lifecycle, schedules, fixed-step time, plugins, and world
  resources.
- `nico-launch` provides command-line and diagnostics bootstrap for native
  executables; non-CLI platforms supply their own launch integration.
- `nico-presentation` contains window, input, rendering, audio, and UI contracts
  and coordinates optional providers.
- `nico-physics` defines an authoritative runtime capability usable by servers.
- `nico-assets` owns shared asset identities and loading-state contracts.
- `nico-devtools` demonstrates optional runtime observation through public APIs.

Game structure and behavior are authored in Rust. Nico does not currently define
a scene document, prefab format, visual editor, or ECS API. Those are deliberate
review decisions rather than missing implementations.

See [docs/architecture.md](docs/architecture.md) for dependency and ownership
rules.

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
