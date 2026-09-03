# Nico roadmap

This roadmap records verified architecture and the next evidence-producing
steps. It deliberately avoids designing distant subsystem APIs before their
first provider and consumer exist.

## Legend

```text
[x] implemented and tested
[ ] next concrete work
[?] provisional direction; requirements and provider not yet established
```

## Current repository

```text
Nico/
├── crates/
│   ├── nico-ecs/             authoritative world, resources, hecs vocabulary
│   ├── nico-input/           provider-neutral physical device state
│   ├── nico-runtime/         lifecycle, schedule, time, events, services
│   ├── nico-launch/          native CLI and diagnostics policy
│   ├── nico-presentation/    immutable world presentation boundary
│   ├── nico-render/          backend-neutral frame and pipeline policy
│   ├── nico-rhi/             backend-neutral GPU contracts
│   ├── nico-rhi-wgpu/        first concrete RHI backend
│   ├── nico-winit/           concrete native client host and input adapter
│   └── nico-assets/          stable AssetId and typed Handle<T> only
└── games/minimal-game/
    ├── shared/               authoritative gameplay used by client and server
    ├── client/               game composition, bindings, command mapping
    ├── server/               paced headless host
    └── assets/               logic and presentation ownership roots
```

There are no placeholder physics, devtools, audio, UI, or asset-loading
contracts. Rendering currently covers the surface lifecycle and one concrete
bootstrap pipeline required by the native host. A new contract or crate requires
a real consumer, provider, and behavioral test.

## Completed foundations

### 0. Architecture baseline [x]

- Headless runtime with host-owned loops.
- Presentation separated from authoritative runtime.
- Canonical `nico_runtime::ecs` namespace over `hecs`.
- Shared client/server game code and native launch policy.
- Deterministic lifecycle stages, fixed time, diagnostics, and shutdown.

### 1. Runtime communication [x]

- Typed bounded broadcast events with independent readers.
- System writes commit only after success and are then visible to the next
  scheduled system.
- Overflow reports missed events; failed-system writes are discarded.
- The minimal game demonstrates multiple consumers and same-frame event chaining.

### 2. Portable service completion [x]

- Domain-typed owned request and completion values; no universal I/O enum.
- Bounded request and completion queues with explicit overload errors.
- Channel-local request identity, cancellation, and backend failure values.
- Host-selected backend endpoint with no access to `World`.
- Runtime-thread completion publication through typed events.
- Generational entity validation for targeted late completions.
- Deterministic manually controlled backend tests.
- Registered services close during application shutdown and reject late work.
- No Tokio or other executor dependency in runtime-facing contracts.

## Next milestone

### 3. Real client host [ ] IN PROGRESS

The native window and GPU providers establish presentation requirements. Keep
host lifecycle concrete; extend the RHI only with implemented backend behavior.

- [x] Select stable Winit 0.30 from documented desktop lifecycle requirements.
- [x] Let Winit own the permanent client loop and drive `App::start`, `tick`, and
  `shutdown`.
- [x] Create the window on resume, tick from monotonic time on redraw, pause on
  suspend, and observe resize and focus.
- [x] Preserve a bounded native-window smoke mode and non-GUI lifecycle tests.
- [x] Extract the proven Winit lifecycle and device adapter into the concrete
  `nico-winit` engine provider crate.
- [x] Add the headless `nico-input` engine crate for buttons, axes, vectors,
  motion, connection lifecycle, and focus-loss release.
- [x] Feed Winit keyboard, pointer, and touch events into engine input state.
- [x] Map aggregate input to game-owned `PlayerCommand` and `MovementVector`
  values without leaking Winit types into shared gameplay.
- [ ] Connect a native gamepad provider through `nico-input`.
- [x] Run the bounded executable native-window smoke check on the initial Windows
  development platform.
- [x] Keep the example client free of event-loop and platform-host machinery.
- [x] Add a Nico-owned RHI and a wgpu backend that clears, resizes, and presents
  the native surface.
- [x] Recover outdated, suboptimal, and lost surfaces while treating zero-size,
  timeout, and occlusion as non-fatal frame outcomes.
- [x] Implement core RHI resources, bindings, graphics and compute pipelines,
  queue uploads, transfer commands, and render/compute passes through associated
  provider types.
- [ ] Validate interactive GPU resize and minimize/restore on Windows and macOS.
- [ ] Define a provider-neutral host contract only if a second provider reveals
  reusable requirements.

## Following investigation

### 4. First runtime asset load [?]

The existing `AssetId` and `Handle<T>` establish identity only. The first loader
should prove the smallest end-to-end path before an import/cache architecture is
accepted.

- Choose one concrete asset needed for the first visible frame.
- Define a minimal resolved runtime descriptor for that asset.
- Load owned bytes through a domain-typed service channel.
- Publish success or failure on the runtime thread.
- Keep source files and authoring metadata outside the shipping runtime contract.
- Extract broader manifest, artifact, dependency, and import contracts only when
  this path reveals their requirements.

## Provisional directions

These are capability goals, not approved APIs, crate boundaries, or delivery
promises:

- First visible frame and spatial representation.
- Authoritative physics shared by server and client.
- Networking, replication, and dedicated-server hardening.
- Game-domain persistence and external services.
- Rendering, audio, UI, localization, and accessibility.
- In-process diagnostics and runtime inspection.
- Import tooling, content caching, packaging, and distribution.
- Replay, soak testing, performance, security, and platform hardening.

Each direction gets a detailed milestone only when it becomes next and its first
real provider or consumer is known.

## Immediate path

```text
completed runtime events
    -> completed portable service bridge
        -> real client host
            -> first runtime asset load
                -> first visible imported asset
```
