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

Profiling remains a cross-library requirement, with experimental Rust/LLVM XRay
chosen for future automatic function capture. Implementation, toolchain changes,
and compatibility investigation are deferred; the current operation milestone
must not acquire a profiling dependency.

When profiling work resumes, review automatic capture coverage, call hierarchy,
inclusive/self timings, invocation counts, frame/thread context, overhead, and
capture completeness. Distinguish async polls from calls, elapsed time from
actual on-CPU time, and CPU timings from GPU timings. Preserve authoritative
results for identical input/tick sequences.

Existing tracing supports diagnostics. Do not describe XRay integration or
validation as complete. Performance fixes still require measurements identifying
the bottleneck and a repeatable before/after comparison.

## AI-accessible operations

The minimal in-process status/stop core and headless server integration exist.
The headless server also exposes MCP `status` and `stop` through `--mcp-stdio`.
The engine owns the service and host lifecycle; games can register additional tools.
Native client integration, process supervision, and diagnostics forwarding remain planned.
Readiness, diagnostics, and orderly shutdown form the initial baseline; profiling
operations are deferred until that capability is available.

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
