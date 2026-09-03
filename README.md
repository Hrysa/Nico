# Nico

Nico is an experimental game engine written in Rust. This repository currently
contains a review-oriented architecture skeleton: a deterministic headless
runtime and a Winit-owned native client host with minimal GPU presentation.

Entity/component storage is provided by `hecs` behind Nico's focused world crate.
Winit owns the first desktop window and event loop. Nico owns a small rendering
hardware interface whose first backend uses `wgpu`; physics, audio, and UI
providers remain under architectural review.

## Architecture

The runtime never depends on its native provider:

```text
game client ──> nico-winit ──┬──> input
                             ├──> presentation ──> runtime
                             ├──> render ──> RHI ──> wgpu backend
                             └──> runtime
game server ─────────────────────────────────────> runtime
```

- `nico-runtime` owns lifecycle, schedules, fixed-step time, plugins, and world
  execution. Its typed event streams provide bounded broadcast communication
  between systems and host integrations. Typed service channels bridge owned
  requests and completions without selecting an async executor.
- `nico-ecs` owns the authoritative world, typed resources, entity storage,
  queries, and deferred structural commands while exposing the real `hecs` query
  vocabulary.
- `nico-input` owns provider-neutral device identity, normalized events, and
  aggregate button, axis, vector, and motion state.
- `nico-winit` is the concrete native provider for window lifecycle, frame
  scheduling, input adaptation, and client-session coordination.
- `nico-rhi` defines backend-neutral capabilities, resources, bindings,
  pipelines, commands, queue operations, and surface lifecycle through
  associated provider types. `nico-rhi-wgpu` implements that contract.
- `nico-render` owns backend-neutral frame policy. Its first bootstrap pipeline
  creates the shader and graphics pipeline, records the triangle pass, submits,
  and presents through `nico-rhi`.
- `nico-launch` provides command-line and diagnostics bootstrap for native
  executables; non-CLI platforms supply their own launch integration.
- `nico-presentation` coordinates immutable world presentation. Render-world
  extraction and synchronization remain deferred.
- `nico-assets` owns stable `AssetId` and typed `Handle<T>` identity. Loading and
  import APIs remain deferred until their first implementation.

Game structure and behavior are authored in Rust. Nico does not currently define
a scene document, prefab format, or visual editor. Those are deliberate review
decisions rather than missing implementations.

See [docs/architecture.md](docs/architecture.md) for dependency and ownership
rules, [ADR 0001](docs/decisions/0001-native-client-event-loop.md) for native
event-loop ownership, [ADR 0002](docs/decisions/0002-nico-rhi-wgpu-backend.md)
for the first GPU boundary, [ADR 0003](docs/decisions/0003-render-pipeline-layer.md)
for renderer ownership, and [docs/roadmap.md](docs/roadmap.md) for completed
foundations and the current milestone.

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

## Input

`nico-input::InputManager` is the engine-owned, provider-neutral input boundary.
It tracks connected keyboards, pointers, gamepads, and touch devices, including
held and transitional buttons, scalar axes, persistent vectors, and frame-local
motion. It has no dependency on Winit, presentation, runtime, or a game.

`nico-winit` feeds keyboard, pointer, wheel, raw motion, and touch events into
that state. The minimal client supplies only a game mapper that emits
`minimal_game_shared::PlayerCommand` with a normalized `MovementVector`, so
native provider types and device state never enter runtime or shared gameplay.
A future gamepad provider can feed the same engine input boundary.

## Commands

```text
cargo test --workspace
cargo clippy --workspace --all-targets -- -D warnings
cargo run -p nico-shaderc
cargo run -p nico-shaderc -- --check
cargo run -p minimal-game-client
cargo run -p minimal-game-client -- --smoke-frames 3
cargo run -p minimal-game-server
```

The bootstrap graphics shader is authored in
`assets/presentation/shaders/bootstrap.slang`.
`nico-shaderc` is a standalone development executable that invokes `slangc`
from `PATH`, `NICO_SLANGC`, or its `--slangc` argument and writes the runtime
WGSL artifact. Rust builds do not watch or compile shader files. Refresh or
verify generated shaders with:

```text
cargo run -p nico-shaderc
cargo run -p nico-shaderc -- --check
```

The tool searches upward from its working directory for the shader root. A
standalone installed executable can instead receive the project explicitly with
`nico-shaderc --root <PROJECT>`.

The native client loads
`assets/presentation/shaders/generated/wgpu/bootstrap.wgsl` at runtime, so
regenerating the shader does not recompile or relink Rust crates.

The minimal-game server runs continuously at 60 ticks per second until the
process is stopped. The client opens a native window and runs until it is closed;
`--smoke-frames` provides bounded executable validation.

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
