# Nico architecture skeleton

## Intent

Nico is code-first. Rust code constructs game state and registers behavior. A
large visual authoring environment, scene-document format, and prefab system are
not architectural prerequisites.

This skeleton exists to review ownership and dependency direction before choosing
libraries or implementing engine subsystems in detail.

## Layers

```text
                         applications
                 ┌────────────┴────────────┐
                 │                         │
            game server               game client
                 │                         │
                 │                    presentation
                 │                         │
                 └────────────┬────────────┘
                              ▼
                           runtime
                              │
                              ▼
                              ecs
```

### Runtime

`nico-runtime` is the headless application kernel. It owns lifecycle, time,
schedules, plugins, and system execution. It depends on `nico-ecs`, which owns
the authoritative world.

`nico-ecs` combines typed resources with a `hecs` world. Nico exposes the real
provider query and command vocabulary through `nico_runtime::ecs` instead of
building a second lowest-common-denominator ECS API. The separate crate isolates
stable world infrastructure from runtime lifecycle and host policy. ECS remains
a simulation tool rather than storage for windowing, assets, rendering, UI,
mail, inventory databases, or other unrelated domains.

`nico_runtime::ecs` is the canonical engine-facing import namespace for `World`,
`Entity`, queries, and commands. ECS types are not also re-exported at the
`nico_runtime` crate root.

Each system receives a deferred ECS command buffer. Commands from a successful
system are flushed before the next system runs, so ordering is deterministic and
later systems observe structural changes. Commands produced by a failed system
are discarded.

`nico_runtime::events` provides typed, in-thread broadcast streams. Each
consumer owns an `EventReader<T>` cursor, so one consumer cannot remove an event
from another. A system's event writes are staged and become visible to the next
scheduled system only after the producer succeeds; failed-system writes are
discarded. Each event type has a bounded retained history, and lagging readers
are told how many events were evicted. Hosts may inject input or completion
events before a tick without receiving mutable access to the world.

The host drives the runtime through `start`, `tick`, and `shutdown`; the runtime
does not own a native event loop.

`nico_runtime::services` is the portable asynchronous completion boundary.
Each channel is typed by its domain request and completion values and uses
bounded queues in both directions. Runtime code submits owned requests; a
host-selected backend receives them without access to `World`. An ordinary
runtime system drains completions in controlled order and publishes
`ServiceCompletion<T>` events, so visibility follows the same deterministic
system boundary as other events. Request cancellation, overload, backend errors,
late shutdown results, and stale generational entity targets have explicit
behavior. The boundary does not select Tokio or any other executor.

Runtime systems can request orderly termination through `SystemContext`. Host
policies implement `AppRunner`: the dedicated server uses a paced fixed-rate loop,
while tests use bounded frames. A permanent client loop is deferred until the
native window/event-loop provider is selected.

Runtime and engine libraries emit structured diagnostics through `tracing` but
do not select output, filtering, or formatting policy. Native executables use
`nico-launch` to parse shared command-line options and install a
`tracing-subscriber`. Web and console hosts may provide different launch and
diagnostics integration without changing runtime code.

### Presentation

`nico-presentation` is optional and depends on the runtime. It coordinates
the current bounded client smoke path through a concrete no-device
`Presentation`. It does not define provider-neutral window, input, renderer,
audio, or UI traits. Those contracts will be extracted from the first real
provider and consumer after event-loop ownership is understood.

Runtime never depends on presentation. A dedicated server therefore has no
window, renderer, local input, audio, or UI dependency.

Presentation receives immutable access to the runtime world. It may query that
world directly and may maintain change-driven caches where profiling justifies
them. Mandatory full-world extraction is not part of the architecture.

### Shared and authoritative capabilities

`nico-assets` currently defines only stable `AssetId` and typed `Handle<T>`
identity shared across runtime and presentation. Paths, loading state, manifests,
and service contracts will be introduced with the first runtime asset loader.

Physics and devtools remain capability ideas, not workspace crates. Physics will
be authoritative if implemented because a server may need the same collision
rules as a client. Devtools should begin as a real in-process observer once
runtime inspection has a concrete use case.

## Code-first game construction

A game is a workspace package under `games/`:

```text
games/minimal-game/
├── shared/
│   ├── Cargo.toml
│   └── src/lib.rs
├── client/
│   ├── Cargo.toml
│   └── src/main.rs
├── server/
│   ├── Cargo.toml
│   └── src/main.rs
└── assets/
    ├── logic/
    └── presentation/
```

The shared crate owns authoritative game code used by the client and server. The
client adds presentation; the server uses only the headless runtime. Logic assets
are available to both sides, while presentation assets are client-only. Because
gameplay is Rust code, there is no runtime folder discovery or dynamic game
loading.

Games register ordinary Rust functions through plugins. Startup systems may
create entities through deferred commands, and simulation systems query
components directly:

```rust,ignore
impl Plugin for GamePlugin {
    fn build(&self, app: &mut AppBuilder) -> RuntimeResult<()> {
        app.add_system(Stage::Startup, "game::setup", setup_game);
        app.add_system(Stage::FixedUpdate, "game::movement", movement);
        Ok(())
    }
}

fn setup_game(context: &mut SystemContext<'_>) -> RuntimeResult<()> {
    context.commands.spawn((Position::default(), Velocity::default()));
    Ok(())
}

fn movement(context: &mut SystemContext<'_>) -> RuntimeResult<()> {
    for (position, velocity) in context
        .world
        .query::<(&mut Position, &Velocity)>()
        .iter()
    {
        position.advance(*velocity, context.time.delta());
    }
    Ok(())
}
```

Data files should be introduced only for content that benefits from runtime
tuning, localization, save data, or external asset workflows.

Run the example game with:

```text
cargo run -p minimal-game-client
cargo run -p minimal-game-server
```

The server command continues ticking at a fixed rate until exit is requested or
the process is stopped. `run_for_frames` remains available for deterministic
tests and bounded smoke applications.

## Dependency rules

1. Runtime cannot depend on presentation, provider adapters, or tooling.
2. Presentation may query runtime state immutably but does not own authoritative
   gameplay state.
3. Applications assemble capabilities and select concrete providers.
4. Backend-specific types do not leak into unrelated public APIs.
5. Gameplay consumes semantic commands rather than native device events.
6. ECS is a simulation tool rather than the universal storage model.
7. Devtools inspect the running application and do not define its content format.
8. New crates require a real ownership or dependency boundary, not merely a new
   namespace.
9. Engine libraries emit diagnostics but application hosts select and initialize
   the diagnostics subscriber.
10. Persistent business identifiers such as player, asset, and network IDs are
    distinct from temporary generational ECS entity IDs.
11. Runtime events are transient broadcast facts; persistent domain state still
    belongs in explicit resources, domain models, or external storage.
12. Service contracts are domain-typed and executor-independent; no universal
    I/O request enum or direct background access to `World` is allowed.

## Deliberately deferred

- Parallel scheduling, change detection, and higher-level ECS relationships.
- Math library and public math representation.
- Window/event-loop provider.
- Graphics API and rendering provider.
- Physics and audio providers.
- Asset import and caching pipeline.
- UI strategy.
- Scene, prefab, and serialization formats.

These are review points, not TODOs hidden behind placeholder implementations.
