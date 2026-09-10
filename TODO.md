# Nico tasks

This file tracks actionable work. [The roadmap](docs/roadmap.md) records
completed milestones and deferred directions; [the architecture](docs/architecture.md)
defines ownership and requirements.

## AI-accessible client and server operations

This is the next implementation milestone. Existing tracing supplies diagnostic
context. Profiling is deferred and does not block this work.

See the [implementation plan](docs/plans/2026-09-10-ai-client-server-operations.md)
for proposed boundaries, tools, delivery sequence, and acceptance checks.

### Implemented minimal core

- [x] Add dependency-free `nico-ops` with owned status snapshots and a one-slot,
      idempotent stop signal between a controller and host.
- [x] Integrate optional control into the headless server at tick boundaries,
      with readiness after a successful tick and a stop-interruptible paced wait.
- [x] Preserve final success/failure and report unexpected endpoint disconnect;
      dropping the last controller requests orderly host stop.
- [x] Test lifecycle, failure, repeated stop, controller disconnect, isolation,
      unchanged simulation order, and stopping without waiting for another tick.
- [x] Run the controlled-server example through readiness and graceful shutdown.
- [x] Expose `status` and `stop` through the optional `nico-ops/mcp` adapter and
      `minimal-game-server --mcp-stdio`, with schemas and structured results.
- [x] Verify real stdio discovery, readiness, repeated stop, invalid tool calls,
      retained final status, and disconnect before/after initialization.
- [x] Document the server MCP invocation and connection lifecycle.
- [x] Move the fixed-rate runner and MCP startup/join into `nico-launch/src/server/`;
      games compose the engine host and may register tools through `ToolExtensions`.

### Remaining milestone work

- [ ] Define discoverable capabilities and typed requests/results for readiness,
      diagnostics, and orderly shutdown.
- [ ] Connect native client control and the child-process transport to the core;
      keep dispatch on host-owned boundaries and add structured diagnostics.
- [ ] Extend beyond the two-tool server MCP endpoint to local process launch and the shared
      operation boundary, with request correlation, timeouts, and explicit failures.
- [ ] Prove launch -> readiness -> diagnostics retrieval -> orderly stop for both
      client and headless server.
- [ ] Test malformed/unsupported requests, failed startup, timeout, overload,
      disconnect, and shutdown with work pending.
- [ ] Document additional operations as they become available.

## Profiling: deferred

Use experimental Rust/LLVM XRay for future automatic function profiling. Preserve
the goal of a Unity-style call hierarchy, inclusive/self timings, invocation
counts, and frame/thread context without hand-written per-method instrumentation.

No profiler code, prototype, custom collector/viewer, profiling toolchain changes,
or compatibility investigation is required now. XRay is not integrated or
validated for Nico. Revisit its setup and coverage when profiling work resumes.

The reported startup delay remains unmeasured. Profile capture/export and MCP
access to profiling data are later work; they do not block the operation baseline.

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
