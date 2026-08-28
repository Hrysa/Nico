# Nico

Nico is an experimental game engine written in Rust. This repository currently
contains a review-oriented architecture skeleton: a deterministic headless
runtime and a bounded no-device client presentation path.

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
  execution. Its typed event streams provide bounded broadcast communication
  between systems and host integrations. Typed service channels bridge owned
  requests and completions without selecting an async executor.
- `nico-ecs` owns the authoritative world, typed resources, entity storage,
  queries, and deferred structural commands while exposing the real `hecs` query
  vocabulary.
- `nico-launch` provides command-line and diagnostics bootstrap for native
  executables; non-CLI platforms supply their own launch integration.
- `nico-presentation` currently provides only the concrete no-device boundary
  used by the client smoke path. Real provider contracts remain deferred.
- `nico-assets` owns stable `AssetId` and typed `Handle<T>` identity. Loading and
  import APIs remain deferred until their first implementation.

Game structure and behavior are authored in Rust. Nico does not currently define
a scene document, prefab format, or visual editor. Those are deliberate review
decisions rather than missing implementations.

See [docs/architecture.md](docs/architecture.md) for dependency and ownership
rules, and [docs/roadmap.md](docs/roadmap.md) for completed foundations, the
current tree, and the next evidence-producing milestone.

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

## Runtime events

Game-owned event types are sent and read through `SystemContext`:

```rust,ignore
let mut reader = nico_runtime::events::EventReader::<EnemyDefeated>::new();
app.add_system(Stage::Update, "quests", move |context| {
    for event in context.events.read(&mut reader) {
        // Update quest-domain state from this authoritative fact.
    }
    Ok(())
});
```

Each reader receives events independently. System writes become visible after
that system succeeds and are discarded if it fails. Streams retain a bounded
number of events per type; lagging readers can inspect `EventRead::missed()`.

## Portable services

Domain code creates a typed bounded channel with
`nico_runtime::services::service_channel`. Runtime code submits owned requests;
the host-selected backend receives them without access to `World` and returns
owned results. `AppBuilder::add_service` publishes those results as
`ServiceCompletion<T>` events at the next `Update` stage boundary and closes the
channel during shutdown.

The standard-library channel is the boundary, not an executor policy. A test can
drive `ServiceBackend::try_next` manually, while a native adapter may move the
same backend endpoint to a worker thread or async executor. Both request and
completion queues are bounded, cancellation is explicit, and completions aimed
at dead generational entities are discarded.

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
