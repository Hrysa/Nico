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
│   ├── nico-runtime/         lifecycle, schedule, time, events, services
│   ├── nico-launch/          native CLI and diagnostics policy
│   ├── nico-presentation/    concrete no-device client smoke boundary
│   └── nico-assets/          stable AssetId and typed Handle<T> only
└── games/minimal-game/
    ├── shared/               authoritative gameplay used by client and server
    ├── client/               bounded client smoke host
    ├── server/               paced headless host
    └── assets/               logic and presentation ownership roots
```

There are no placeholder physics, devtools, window, input, renderer, audio, UI,
or asset-loading contracts. A new contract or crate requires a real consumer,
provider, and behavioral test.

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

### 3. Real client host [ ]

The first native window provider will determine the presentation contracts. Do
not add provider-neutral traits before completing this investigation.

- Select one window/event-loop library based on desktop lifecycle requirements.
- Let the provider own the permanent client loop and drive `App::start`, `tick`,
  and `shutdown`.
- Normalize only the device input required by the minimal game.
- Translate that input into a game-owned semantic command.
- Handle close, resize, focus, suspend/resume, and redraw behavior actually
  exposed by the selected provider.
- Preserve headless tests and the no-device presentation smoke path.
- Add an executable smoke test where platform automation permits it.

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
