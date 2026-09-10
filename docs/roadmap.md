# Nico roadmap

This roadmap separates implemented foundations, outstanding validation, and
planned work. Detailed current tasks live in [TODO](../TODO.md). Requirements and
dependency rules live in [the architecture](architecture.md).

## Completed foundations

### 0. Runtime and ECS

- Headless, host-driven application lifecycle with deterministic stage ordering
  and fixed-step simulation.
- A hecs-backed world/resource boundary in `nico-ecs`, exposed to engine-facing
  code through `nico_runtime::ecs`.
- Rust plugins and shared client/server gameplay.
- Native CLI and tracing diagnostics through `nico-launch`.
- Behavioral tests for lifecycle, scheduling, timing, failure, and shutdown.

### 1. Runtime communication

- Typed bounded broadcast events with independent readers.
- Successful system writes become visible to the next scheduled system;
  failed-system event and structural command writes are discarded.
- Explicit missed-event reporting for lagging readers.
- Multiple consumers and same-frame event chaining demonstrated by the game.

### 2. Portable services

- Domain-typed owned requests and completions over bounded channels.
- Request identity, cancellation, overload, and backend failure results.
- Backends operate without access to the authoritative world.
- Runtime-thread completion events and stale generational entity rejection.
- Shutdown closes registered services and rejects late work.
- Deterministic manually driven backend tests without selecting an async executor.

### 3. Real client host: core implementation complete

- Winit 0.30 native window lifecycle and permanent event loop in `nico-winit`.
- Monotonic redraw-driven ticks, suspension handling, and orderly session shutdown.
- Provider-neutral `nico-input` with keyboard, pointer, wheel, motion, and touch
  adaptation; focus-loss release and game-owned movement commands.
- A backend-neutral RHI with a wgpu provider for resources, bindings, graphics
  and compute pipelines, uploads, transfers, render/compute passes, and surfaces.
- Non-fatal zero-size, timeout, and occlusion outcomes, plus surface recovery.
- A bootstrap triangle pipeline in `nico-render`.
- Offline Slang-to-WGSL compilation through `nico-shaderc` and direct runtime
  loading of the generated file without rebuilding Rust.
- Automated non-GUI lifecycle tests and a recorded bounded Windows GPU smoke run.

Interactive Windows/macOS resize and minimize/restore validation remains open;
see [the checklist](../TODO.md#outstanding-host-validation). Gamepad integration
and reflection/asset-backed shaders are deferred. The bootstrap triangle does
not yet visualize shared gameplay state. The smoke limit counts session frames,
not confirmed GPU presentations.

## Next milestone

### 4. AI-accessible client and server operations

The [implementation plan](plans/2026-09-10-ai-client-server-operations.md) records
the proposed local process/MCP boundary and staged delivery.

Expose capabilities, readiness, diagnostics, and orderly shutdown with structured
requests and explicit results/errors. Prove local client and server launch/control
through an MCP adapter while preserving host and runtime ownership. Validate
failure, timeout, overload, shutdown, and unchanged simulation results for
identical input/tick sequences.

The minimal in-process core is implemented in `nico-ops`: owned status snapshots,
idempotent stop, controller-disconnect stop, and explicit final success/failure.
The headless server accepts an optional endpoint and wakes its paced wait on
stop. Focused tests and a runnable controlled-server example verify that path.
The optional `nico-ops/mcp` adapter now exposes `status` and `stop` directly from
`minimal-game-server --mcp-stdio`. Real-process tests cover discovery, readiness,
invalid calls, repeated stop, final-status reads, and disconnect cleanup.
The engine's `nico-launch/src/server/` owns the runner and MCP lifecycle. Games
compose `ServerHost` and can register extra tools without overriding engine tools.
Client integration, process supervision, and diagnostics forwarding remain pending.

Profiling remains a cross-library requirement but is deferred. Experimental
Rust/LLVM XRay is the chosen future direction for automatic function capture,
call hierarchy, inclusive/self timings, and invocation counts without per-method
annotations. It is not integrated or validated for Nico. No profiler code,
prototype, custom collector/viewer, toolchain configuration, or compatibility
investigation is required now.

The startup delay remains unmeasured. Future profiling integration and access to
its results through MCP do not block the client/server operation baseline.

## Following investigation

### 5. First runtime asset load

`AssetId` and `Handle<T>` currently establish identity only. The direct bootstrap
shader read does not implement the planned service-backed asset path.

Choose one concrete asset needed to render game content, define its smallest
resolved runtime descriptor, load owned bytes through a typed service, and
publish success/failure on the runtime thread. Introduce manifest, dependency,
artifact, and import vocabulary only as that path requires it. Source files and
authoring metadata remain outside the shipping runtime contract.

## Deferred directions

- Experimental XRay profiling integration, platform validation, and profiling data
  access through AI tooling.
- Native gamepad integration.
- Slang reflection and asset-backed shader packaging.
- Visible game entities, spatial representation, and imported content.
- Authoritative physics, networking, replication, and persistence.
- Production rendering, audio, UI, localization, and accessibility.
- Richer runtime inspection and development tools.
- Import caching, packaging, distribution, replay, and platform hardening.
- A provider-neutral host contract only when a second provider proves its need.

These directions do not imply approved subsystem APIs, crate boundaries, or
delivery dates. Define each milestone around real providers and consumers when
it becomes next.

## Delivery order

```text
completed runtime, events, and services
    -> completed core client host
        -> AI-accessible client/server operations
            -> first service-backed asset load
                -> first visible imported asset
```

Interactive host validation remains tracked alongside this work.
