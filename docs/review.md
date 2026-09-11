# Architecture review guide

Use this guide when reviewing changes to Nico. [Architecture](architecture.md) defines
ownership; [the roadmap](roadmap.md) records phase status and evidence;
[TODO](../TODO.md) tracks current work. Accepted provider decisions are recorded in [ADR
0001](decisions/0001-native-client-event-loop.md), [ADR
0002](decisions/0002-nico-rhi-wgpu-backend.md), and [ADR
0003](decisions/0003-render-pipeline-layer.md).

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

Profiling remains a cross-library requirement, with experimental Rust/LLVM XRay chosen
for future automatic function capture. Implementation, toolchain changes, and
compatibility investigation are deferred; client/server operation work must not acquire
a profiling dependency.

When profiling work resumes, review automatic capture coverage, call hierarchy,
inclusive/self timings, invocation counts, frame/thread context, overhead, and capture
completeness. Distinguish async polls from calls, elapsed time from actual on-CPU time,
and CPU timings from GPU timings. Preserve authoritative results for identical
input/tick sequences.

Existing tracing supports diagnostics. Do not describe XRay integration or validation as
complete. Performance fixes still require measurements identifying the bottleneck and a
repeatable before/after comparison.

## AI-accessible operations

Codex connects to the bridge; users launch games independently. Host and game APIs come
from connection registrations. Bridge disconnect must leave gameplay running. Status,
diagnostic retrieval, and graphics reporting are implemented; profiling remains
deferred.

- Cached schemas are distinct from live capabilities; disconnected instances return
  explicit unavailable errors and old IDs never route to replacement connections.
- Catalog changes emit notifications; fixed discovery/invocation tools remain usable
  without automatic client refresh.
- Client/server capabilities, arguments, results, and errors are machine-readable.
- Asynchronous operations have request correlation and explicit completion/timeout.
- Process and native-window operations remain host/tooling responsibilities.
- Simulation commands execute at controlled runtime boundaries.
- Inspection returns owned snapshots, and collection/queues remain bounded.
- Tests cover both hosts, unsupported/malformed requests, overload, disconnect,
  failed startup, and shutdown with pending work.
- Diagnostic pagination reports eviction and truncation; cursors remain
  process-local, and filtered events are not counted as retention loss.
- Presentation counts remain separate from session progress and readiness.
- Document only implemented tools and flags as available operations.

## Validation and documentation

Use the relevant checks from [the repository guidelines](../AGENTS.md). For graphics
changes, combine automated tests with bounded executable coverage and appropriate
interactive checks. Record which platform/backend was exercised.

The current smoke counter measures client-session frames rather than confirmed GPU
presentations. Windows/macOS interactive resize and minimize/restore remain open checks.
Do not infer minimized-window behavior from suspension tests alone.

Apply [documentation ownership](../AGENTS.md#documentation-ownership): task checklists
belong in TODO, phase criteria and evidence in the roadmap. Remove stale claims and
broken links when updating a capability. Preserve historical ADR rationale, label dated
implementation updates, and mark superseded plans clearly. Keep completed, unverified,
and deferred work distinct.