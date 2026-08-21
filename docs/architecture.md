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
             ┌───────────────┼───────────────┐
             │               │               │
           server          player          sandbox
             │               │               │
             │         presentation    presentation + devtools
             │               │               │
             └───────────────┴───────┬───────┘
                                     ▼
                                  runtime
```

### Runtime

`nico-runtime` is the headless application kernel. It owns lifecycle, time,
schedules, plugins, and the authoritative world.

The current world only stores typed resources. We have not selected an ECS or
committed Nico to an ECS-first public API. If an ECS is selected, it will serve
runtime simulation rather than windowing, assets, rendering, UI, or devtools.

The host drives the runtime through `start`, `tick`, and `shutdown`; the runtime
does not own a native event loop.

### Presentation

`nico-presentation` is optional and depends on the runtime. It coordinates
client-facing capabilities represented by these boundary crates:

- `nico-window`
- `nico-input`
- `nico-render`
- `nico-audio`
- `nico-ui`

These crates contain high-level contracts only. Event-loop, graphics, audio, and
UI library choices remain open. Concrete adapters will be added after review.

Runtime never depends on presentation. A dedicated server therefore has no
window, renderer, local input, audio, or UI dependency.

Presentation receives immutable access to the runtime world. It may query that
world directly and may maintain change-driven caches where profiling justifies
them. Mandatory full-world extraction is not part of the architecture.

### Shared and authoritative capabilities

`nico-assets` defines asset identity and loading-state vocabulary shared across
runtime and presentation. It does not yet define an import pipeline or storage
format.

`nico-physics` is on the authoritative side of the architecture because a server
may need to execute the same collision rules as a client. No physics provider is
selected.

### Devtools

`nico-devtools` is an optional runtime observer. It may eventually provide a
world inspector, diagnostics, profiling, render debugging, and live value
tweaking. It is not an authoring database and does not own game content.

The `nico-sandbox` executable demonstrates code-authored setup, runtime systems,
optional devtools, and null presentation in one small program.

## Code-first game construction

For now, games register ordinary Rust functions through plugins:

```rust,ignore
impl Plugin for GamePlugin {
    fn build(&self, app: &mut AppBuilder) -> RuntimeResult<()> {
        app.add_system(Stage::Startup, "game::setup", setup_game);
        app.add_system(Stage::Update, "game::update", update_game);
        Ok(())
    }
}
```

Once an ECS is selected, `setup_game` may spawn entities using concise typed Rust
APIs. Data files should be introduced only for content that benefits from runtime
tuning, localization, save data, or external asset workflows.

## Dependency rules

1. Runtime cannot depend on presentation or devtools.
2. Presentation may query runtime state immutably but does not own authoritative
   gameplay state.
3. Applications assemble capabilities and select concrete providers.
4. Backend-specific types do not leak into unrelated public APIs.
5. Gameplay consumes semantic commands rather than native device events.
6. ECS, if adopted, is a simulation tool rather than the universal storage model.
7. Devtools inspect the running application and do not define its content format.
8. New crates require a real ownership or dependency boundary, not merely a new
   namespace.

## Deliberately deferred

- ECS library, component storage, queries, and commands.
- Math library and public math representation.
- Window/event-loop provider.
- Graphics API and rendering provider.
- Physics and audio providers.
- Asset import and caching pipeline.
- UI strategy.
- Scene, prefab, and serialization formats.

These are review points, not TODOs hidden behind placeholder implementations.
