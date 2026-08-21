# Architecture review guide

The skeleton should be reviewed before detailed subsystem work begins.

## Decisions represented in code

- Runtime is headless and presentation-independent.
- Native hosts drive runtime frames.
- Presentation and devtools are optional.
- Physics is authoritative rather than presentation-only.
- Asset identity crosses the runtime/presentation boundary.
- Game setup and behavior are authored in Rust.
- `hecs` provides focused entity/component storage through `nico-ecs`; it does not
  own Nico lifecycle or application architecture.
- Presentation, physics, audio, and UI backend libraries remain unselected.

## Accepted decisions

1. `nico-ecs` owns the world/resource boundary. Engine-facing code uses the
   canonical `nico_runtime::ecs` namespace, which exposes `hecs` query and command
   types rather than recreating a provider-independent ECS API.
2. `Plugin::build(&self, &mut AppBuilder)` remains the composition boundary.
3. `Startup`, `FixedUpdate`, `Update`, and `Shutdown` are sufficient initial
   stages.
4. Presentation may query the runtime world directly through immutable access.
   Extraction and caching remain optional presentation-side optimizations for
   cases where they provide a measured benefit.
5. Asset identity remains a standalone shared crate.
6. Window, input, rendering, audio, and UI contracts are compacted into the
   presentation crate until concrete provider boundaries justify new crates.
7. Devtools run in-process initially.

## Out of scope for this review

- Performance optimization.
- Parallel scheduling.
- Visual authoring.
- Scene and prefab formats.
- Production renderer design.
- Backend-specific resource APIs.
