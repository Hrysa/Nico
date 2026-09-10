# Nico tasks

This file tracks actionable work. [The roadmap](docs/roadmap.md) records
completed milestones and deferred directions; [the architecture](docs/architecture.md)
defines ownership and requirements.

## Measurement and AI operations

This is the next implementation milestone. Existing tracing spans and logs are
the foundation; shared profile capture/export and an MCP adapter are still planned.

### Measurement and profiling

- [ ] Define shared span names, timing units, counters, and correlation fields
      for meaningful work across every library, using existing tracing where suitable.
- [ ] Add host-controlled capture and machine-readable export with bounded
      retention, explicit overflow reporting, and controllable collection overhead.
- [ ] Measure startup phases: window creation, shader read, graphics instance,
      adapter/device creation, surface configuration, shader module creation,
      pipeline creation, and first successful GPU presentation.
- [ ] Record a repeatable baseline for the reported startup delay, including
      build profile, backend, adapter, and repeated launches before optimizing.
- [ ] Extend coverage to runtime stages/systems, service queues, ECS/input/asset
      operations, rendering, client frames, and server ticks as applicable.
- [ ] Distinguish wall-clock profiling from simulation time and CPU submission
      timings from GPU execution timings; report unavailable GPU timing explicitly.
- [ ] Verify bounded collection, export on orderly shutdown, overhead, and
      unchanged authoritative results for identical input and tick sequences.

### AI-accessible client and server operations

- [ ] Define discoverable capabilities and typed requests/results for readiness,
      diagnostics, profile capture, and orderly shutdown.
- [ ] Keep host/process operations outside runtime and apply simulation commands
      at runtime-owned boundaries; expose owned snapshots for inspection.
- [ ] Add an MCP adapter for local development process launch and the shared
      operation boundary, with request correlation, timeouts, and explicit failures.
- [ ] Prove launch -> readiness -> profile capture -> result retrieval -> orderly
      stop for both client and headless server.
- [ ] Test malformed/unsupported requests, failed startup, timeout, overload,
      disconnect, and shutdown with work pending.
- [ ] Document implemented operations and invocation examples when available.

## Outstanding host validation

The core Real client host implementation is complete. Automated lifecycle tests
and a bounded Windows GPU smoke run are recorded in the roadmap. Interactive
checks below remain unverified.

- [ ] Windows: resize repeatedly, maximize/restore, minimize/restore, and close
      after transitions; record OS, backend, adapter, and results.
- [ ] macOS: repeat the same checks and record the environment and results.
- [ ] Check rendering recovery, GPU validation errors, frame timing after
      restore, focus-loss input release, and clean shutdown; fix observed failures.

The current smoke limit counts client-session frames, including frames for which
GPU presentation may be skipped. Successful presentation must be observed
separately. Winit suspension and desktop minimization are separate lifecycle
cases; minimize/restore behavior requires the interactive checks above.

## Deferred follow-ups

- Native gamepad provider connected through `nico-input`.
- Slang reflection and asset-backed shaders for the first real primitive.
- First service-backed runtime asset load and visible imported content.
- A provider-neutral host contract only if a second provider establishes shared
  requirements.

These additions do not block the completed core client-host implementation.
