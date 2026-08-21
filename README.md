# Nico

Nico is an experimental game engine written in Rust. This repository currently
contains a review-oriented architecture skeleton: a deterministic headless
runtime, an optional presentation layer, and optional runtime devtools.

No native window, renderer, ECS, physics, audio, or UI library has been selected
yet. Those choices will be made after their abstraction boundaries are reviewed.

## Architecture

The runtime never depends on presentation:

```text
player/sandbox ──> presentation ──> runtime
server ──────────────────────────> runtime
```

- `nico-runtime` owns lifecycle, schedules, fixed-step time, plugins, and world
  resources.
- `nico-presentation` coordinates optional rendering and audio providers.
- `nico-window`, `nico-input`, `nico-render`, `nico-audio`, and `nico-ui` define
  presentation-facing contracts.
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
cargo run -p nico-server
cargo run -p nico-player
cargo run -p nico-sandbox
```
