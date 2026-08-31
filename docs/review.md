# Architecture review guide

The skeleton should be reviewed before detailed subsystem work begins.

## Decisions represented in code

- Runtime is headless and presentation-independent.
- Provider-neutral input is a separate headless engine capability.
- Native hosts drive runtime frames.
- Presentation is optional and currently no-device only.
- `nico-winit` owns the first native desktop client loop as a concrete engine
  provider.
- Stable asset identity is isolated from loader and importer policy.
- Game setup and behavior are authored in Rust.
- `hecs` provides focused entity/component storage through `nico-ecs`; it does not
  own Nico lifecycle or application architecture.
- Rendering, physics, audio, and UI libraries remain unselected.

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
6. Window, rendering, audio, and UI engine contracts remain absent until
   concrete providers and consumers establish reusable requirements.
7. Devtools and physics remain deferred capabilities rather than placeholder
   crates.
8. Runtime events use typed bounded broadcast streams with independent readers.
   Successful system writes are visible to the next scheduled system; failed
   writes are discarded.
9. Portable services use domain-typed bounded channels. Backends receive owned
   requests without `World` access; runtime systems publish controlled
   completions as events and reject stale entity targets.
10. `nico-winit` owns Winit 0.30 window lifecycle, scheduling, and native input
    adaptation. No provider-neutral window trait exists before a second provider.
11. `nico-input` owns provider-neutral multi-device state. The minimal-game
    client owns only bindings and game-command mapping; provider-specific and
    physical-input types stay out of shared gameplay and runtime.

## Out of scope for this review

- Performance optimization.
- Parallel scheduling.
- Visual authoring.
- Scene and prefab formats.
- Production renderer design.
- Backend-specific resource APIs.
