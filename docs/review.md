# Architecture review guide

Use this guide when reviewing changes to Nico. [Architecture](architecture.md)
defines ownership; [the roadmap](roadmap.md) records milestone status;
[TODO](../TODO.md) tracks current work. Accepted provider decisions are recorded
in [ADR 0001](decisions/0001-native-client-event-loop.md),
[ADR 0002](decisions/0002-nico-rhi-wgpu-backend.md), and
[ADR 0003](decisions/0003-render-pipeline-layer.md).

## Ownership and contracts

- Runtime remains headless, and `nico-ecs` does not depend on runtime.
- Game clients own composition/bindings; shared gameplay consumes semantic commands.
- Provider types stay inside their integration boundaries.
- Presentation reads gameplay immutably; GPU resources stay outside authoritative state.
- `nico-render` owns draw policy; the RHI provider owns native resources and recovery.
- New contracts have concrete consumers, providers, lifecycle rules, and tests.
- New crates establish real ownership or dependency boundaries.

## Runtime behavior

- Stage ordering and fixed-time behavior remain deterministic for identical inputs.
- Successful structural command and event writes are visible to later systems.
- Failed-system structural commands/events are discarded; direct world mutations
  must not be described as transactional.
- Events have independent readers and bounded retention with explicit missed counts.
- Services use owned typed values, bounded queues, and runtime-thread publication.
- Cancellation, stale entity targets, overload, failure, and shutdown are covered.
- Background tasks and tooling never mutate the authoritative world directly.

## Measurement and profiling

These are requirements for every library, not claims of complete current coverage.

- Meaningful work exposes consistent spans, timing units, counters, and correlation.
- Hosts control collection/export with bounded retention and visible overflow.
- Wall-clock profiling is separate from simulation time; CPU and GPU durations
  are labeled accurately.
- Capture overhead is measured and controllable.
- Performance fixes include evidence identifying the bottleneck and a repeatable
  before/after comparison.
- Identical input/tick sequences produce the same authoritative results with
  instrumentation enabled or disabled.

## AI-accessible operations

The shared operation boundary and MCP adapter are planned work.

- Client/server capabilities, arguments, results, and errors are machine-readable.
- Asynchronous operations have request correlation and explicit completion/timeout.
- Process and native-window operations remain host/tooling responsibilities.
- Simulation commands execute at controlled runtime boundaries.
- Inspection returns owned snapshots, and collection/queues remain bounded.
- Tests cover both hosts, unsupported/malformed requests, overload, disconnect,
  failed startup, and shutdown with pending work.
- Document only implemented tools and flags as available operations.

## Validation and documentation

Use the relevant checks from [the repository guidelines](../AGENTS.md).
For graphics changes, combine automated tests with bounded executable coverage
and appropriate interactive checks. Record which platform/backend was exercised.

The current smoke counter measures client-session frames rather than confirmed
GPU presentations. Windows/macOS interactive resize and minimize/restore remain
open checks. Do not infer minimized-window behavior from suspension tests alone.

Keep README focused on current usage, architecture on contracts, roadmap on
milestones, and TODO on actionable work. Preserve historical ADR rationale and
label later implementation updates. Keep completed, unverified, and deferred work
distinct.
