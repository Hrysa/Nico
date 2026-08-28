# Nico skeleton roadmap

This roadmap describes the intended **architecture tree**, not a promise to
implement every subsystem immediately. Nico establishes complete ownership and
dependency boundaries first, then adds the smallest executable implementation
at each milestone. A contract is introduced only when its first real consumer
or provider can test it.

## Legend

```text
[x] implemented minimum   [~] contract/skeleton exists
[ ] planned               [?] decision gate; library/provider not selected
```

## Dependency tree

```text
game server
├── game shared
├── nico-launch                 native bootstrap only
└── nico-runtime
    └── nico-ecs

game client
├── game shared
├── game client presentation
├── nico-launch                 native bootstrap only
├── nico-presentation
│   ├── nico-runtime
│   │   └── nico-ecs
│   └── nico-assets
└── selected platform providers

authoring/build tools
├── nico-assets                 shared identity and artifact contracts
└── importer/tool providers     never runtime dependencies
```

The arrows always point inward toward more stable contracts. In particular,
`nico-runtime` never depends on presentation, native launch policy, an async
executor, or authoring tools.

## Target repository tree

```text
Nico/
├── crates/
│   ├── nico-ecs/                         [x] authoritative world boundary
│   │   ├── entities and components       [x] hecs vocabulary
│   │   ├── typed resources               [x]
│   │   └── deferred structural commands  [x]
│   │
│   ├── nico-runtime/                     [x] headless application kernel
│   │   ├── app and plugin composition    [x]
│   │   ├── lifecycle and host contract   [x]
│   │   ├── deterministic stages          [x]
│   │   ├── fixed-step time               [x]
│   │   ├── system diagnostics            [x]
│   │   ├── events                        [x] typed in-thread communication
│   │   └── services                      [ ] portable async completion boundary
│   │
│   ├── nico-assets/                      [~] logical asset boundary
│   │   ├── AssetId and typed Handle      [x]
│   │   ├── resolved runtime manifest     [ ]
│   │   ├── asynchronous runtime loader   [ ]
│   │   ├── artifact formats              [ ]
│   │   └── import/store contracts        [ ] authoring side only
│   │
│   ├── nico-physics/                     [~] authoritative capability
│   │   ├── provider-neutral service      [x]
│   │   ├── null/test provider            [x]
│   │   └── concrete provider             [?]
│   │
│   ├── nico-presentation/                [~] optional client layer
│   │   ├── client coordinator            [x] null-backed smoke path
│   │   ├── window and event loop          [~] provider contract pending review
│   │   ├── local input                    [~] device events only
│   │   ├── rendering                      [~] immutable World access
│   │   ├── audio                          [~]
│   │   └── UI                             [~]
│   │
│   ├── nico-launch/                      [x] native CLI and logging policy
│   └── nico-devtools/                    [~] optional in-process observer
│
├── games/minimal-game/
│   ├── shared/                           [x] authoritative Rust gameplay
│   │   ├── components and resources
│   │   ├── commands and events           [x] first shared-game event example
│   │   └── plugins and systems
│   ├── client/                           [x] bounded null-presentation host
│   │   └── permanent native loop         [ ] after window provider selection
│   ├── server/                           [x] paced headless host
│   └── assets/
│       ├── logic/                        [x] shared/server-visible root
│       └── presentation/                 [x] client-only root
│
├── tools/                                [ ] only concrete build tools live here
│   └── asset compiler/cache CLI          [ ] after import contracts stabilize
├── docs/                                 [x] architecture and decision records
└── tests/                                [ ] cross-crate tests only when needed
```

Provider crates are deliberately absent from the tree until a provider is
selected. For example, choosing a window library may justify a separate adapter
crate; merely having a `window` namespace does not.

## Delivery roadmap

```text
0. Architecture baseline                                              [x]
├── headless runtime and host-owned loops                             [x]
├── presentation separated from authoritative runtime                [x]
├── canonical nico_runtime::ecs namespace                             [x]
├── client / server / shared game layout                              [x]
└── native arguments and structured logging                           [x]

1. Runtime communication                                              [x]
├── typed event model
│   ├── broadcast semantics and independent readers                   [x]
│   ├── deterministic visibility point                                [x]
│   ├── bounded retention and overflow behavior                       [x]
│   └── discard writes from failed systems                            [x]
├── system-facing API under nico_runtime::events                       [x]
├── event ordering, retention, and failure tests                       [x]
└── minimal-game example: gameplay fact consumed by two systems        [x]

2. Portable service completion                                       [ ] NEXT
├── owned request, completion, cancellation, and error contracts
├── bounded cross-thread completion ingress
├── runtime-thread bridge: completion -> event / World update
├── entity-generation validation for late completions
├── deterministic manually driven test backend
└── native adapter decision                                           [?] Tokio candidate

3. Real client host                                                   [ ]
├── select window/event-loop provider                                 [?]
├── provider-driven App start / tick / shutdown
├── normalize device input
├── map device input to game-owned semantic commands
├── resize, focus, suspend, resume, and exit lifecycle
└── retain fully headless execution and null presentation

4. Runtime assets and import pipeline                                 [ ]
├── stable AssetId, subasset identity, and TargetProfile
├── ArtifactKey, ObjectId, artifact header, and dependency graph
├── resolved client/server manifests
├── local content-addressed object store and build index
├── deterministic importer contract and one reference importer
├── async runtime loading through the service boundary
├── remote cache contract and verified downloads
└── shipping bundles without source or .meta dependencies

5. Spatial foundation and first visible frame                         [ ]
├── choose math representation                                        [?]
├── transforms, hierarchy policy, camera, and bounds
├── select graphics provider                                          [?]
├── surface/device lifecycle and frame submission
├── one mesh/material/texture path through imported artifacts
├── direct immutable World queries with optional measured caches
└── presentation interpolation from fixed simulation state

6. Authoritative physics                                             [ ]
├── define ECS-to-physics ownership and synchronization
├── select provider                                                   [?]
├── collision layers, queries, events, and deterministic expectations
├── server-compatible collision data artifacts
└── headless integration and replayable tests

7. Networking and dedicated-server foundation                        [ ]
├── transport/service contract                                        [?]
├── connection, authentication hook, and session lifecycle
├── protocol/version and stable network identities
├── authoritative commands and snapshots
├── replication policy separate from raw ECS layout
├── interest management and bandwidth limits
└── optional prediction, interpolation, and reconciliation

8. Game-domain patterns                                              [ ]
├── game-owned typed commands and facts
├── pure domain workflows for quests, inventory, mail, and economy
├── ECS projections only for actively simulated state
├── persistence models with stable business IDs
├── save/database service boundary
└── reusable engine module only after a second proven game use case

9. Presentation capabilities                                         [ ]
├── render resources, materials, animation, lighting, and visibility
├── audio mixer, spatial playback, streaming, and device lifecycle
├── retained/immediate UI decision                                    [?]
├── text shaping, fonts, localization, and accessibility
└── presentation events remain non-authoritative

10. Devtools and code-first authoring                                 [ ]
├── diagnostics, frame timings, logs, and schedule inspection
├── read-only World inspector
├── controlled live value editing
├── render/physics/asset diagnostics
├── import status and cache inspection
└── standalone tool executable only when in-process tooling is insufficient

11. Production and platform hardening                                [ ]
├── deterministic tests, replay, soak tests, and benchmarks
├── graceful failure, crash context, and telemetry hooks
├── packaging, manifests, configuration, and content patching
├── web host and service adapters                                     [?]
├── console hosts and SDK-specific adapters                           [?]
└── platform certification, memory, performance, and security review
```

## Skeleton-complete checkpoint

The architectural skeleton is complete when one minimal game can run the same
authoritative logic as a headless server and a real client; communicate through
typed events; complete asynchronous requests without exposing the executor;
load target-resolved artifacts; render one frame; step authoritative physics;
and shut down cleanly. Every boundary must have a deterministic test or null
provider. Rich rendering, editor UX, content volume, and production networking
remain later implementations rather than skeleton requirements.

## Immediate path

The critical path is:

```text
typed events
    -> portable completion boundary
        -> real client host
            -> runtime asset loading
                -> first visible imported asset
```

Work on physics, networking, complex game services, UI, and standalone tools can
then proceed without changing the ownership rules above.
