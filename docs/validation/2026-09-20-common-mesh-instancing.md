# Common mesh instancing validation

Status: feature complete; further frame-time optimization deferred by the user. Scope is
the [complete plan](../plans/2026-09-20-common-mesh-instancing.md), without LOD.
This report consolidates the superseded incremental roadmap notes. Current contracts
belong in [architecture](../architecture.md); current usage belongs in the README.

## Final feature delivery (2026-09-21)

The user instructed the agent to stop deep optimization and finish the feature.
This supersedes the earlier requirement to achieve frame-time parity before
delivery. All four original implementation milestones remain included; LOD remains
excluded. Earlier performance-gate status entries below are historical evidence,
not a claim that parity has since been achieved.

The last hardware-clipping experiment was reverted, including its new distance
helper. The restored renderer source matches the saved pre-experiment SHA-256
`CBEE4EA4D63D801191B62D947C919D47FD8CDD8E33FA355D90C9AE0375E97CFC`.
It retains GPU per-instance culling at frustum boundaries and bypasses compute only
for fully visible bounds in Auto mode. The experiment's four failing indirect-path
assertions now pass without changing or weakening those tests. The latest retained
timestamp-free interleaved Vulkan control still shows about 0.032 ms mean editor
overhead. Experimental faster hardware-clipping timings do not describe delivery.

| Original milestone | Final evidence and disposition |
| --- | --- |
| Direct instancing and cached placement | Immutable affine records, shared resources, reflection/material separation, bounded fallback, split/pacing, replacement/retirement and independent-device reconstruction pass unit/GPU fixtures. Arena cache tests prove warm generator reuse and dependency invalidation; warm preparation previously improved 55.8% against the same-binary expanded reference. |
| GPU visibility and indirect rendering | Restored direct/indirect/fallback suites pass on Vulkan and DX12. Readbacks verify IDs/counts/arguments, reset/empty views, per-view ordering, selected groups, reusable ranges and allocation-failure recovery. Native instance 32 verifies matching CPU/GPU counts and zero unchanged source uploads. |
| Foliage and influence fields | Full/guarded-compact CPU deformation reference fixtures cover affine normals, reflection, wind/radial response, alpha mask and normal mapping. Unit/control tests cover seed response, bounded fields, expiry and clock control; prior identified native captures cover active influences. Generic repeated-object fixtures and the shrub consumer share the renderer. |
| Bounded streaming and lifecycle | Provider/cache tests cover generations, cancellation, limits, last-good replacement and joined shutdown. Native instance 32 validates full/partial eviction, stable retained bytes on repeated reentry, unchanged refresh and inspected captures; its user-authorized bridge stop and process exit are verified. |

The requirement table below supplies the remaining cross-cutting validation:
capability reporting and fallback, MCP controls/counter identities, preparation and
memory measurements, cold/warm/edit scenarios and resource reconstruction. Native
evidence remains specific to its recorded build and Windows/GTX 1660 environment.
Physical driver-loss recovery and user-watched demonstration success are not claimed.
No runtime profiling, XRay, LOD, occlusion or shadow system was added.

Final refreshed checks on the restored source:

- `cargo test --workspace --target-dir target/instance-streaming-validation`:
  **494 passed, 45 ignored, zero failed**, including Windows process tests outside
  the sandbox; `target/instance-final-feature-workspace.log`.
- Explicit release non-measurement graphics suite: **31 passed on Vulkan and 31
  on DX12**, no failures; `target/instance-final-feature-gpu-{vulkan,dx12}.log`.
- Workspace all-target Clippy with warnings denied passed;
  `target/instance-final-feature-clippy.log`.
- Formatting, diff checks and generated shader verification passed;
  `target/instance-final-feature-shaders.log` records `nico-shaderc --check`.

The only remaining work from this feature is optional further frame-time optimization,
tracked separately in TODO. The benchmark-only shader override and authorized GPU
timestamp helpers remain confined to ignored tests.

## Scope and evidence

Measurements below used Windows and NVIDIA GTX 1660, Vulkan driver 591.86 unless
stated otherwise. DX12 tests used the same adapter, driver 32.0.15.9186. This is
not a general hardware compatibility claim. `target/` evidence paths refer to
local, disposable validation artifacts; the test sources are the reproducible record.

| Requirement | Evidence | Result and limits |
| --- | --- | --- |
| Generic immutable batches, affine transforms, normals, winding, IDs and bounds | `nico-presentation` instance tests; expanded-reference GPU fixtures | Validated nonuniform scale, shear/reflection, invalid/singular inputs and unsupported blended/skinned batches |
| Shared resources and persistent uploads | Static direct/indirect/fallback GPU fixtures | Unchanged frames and cull/reentry reuse source uploads; replacement uploads only changed batches; released sources retire |
| Bounded fallback, splitting and pacing | Renderer segment tests and real-GPU split/upload tests | Ordinary fallback is bounded to 256 draws; preflight rejects overflow before acquisition; sources are paced at 8 MiB/view in native hosts |
| GPU visibility and indirect ABI | CPU-reference/readback test, 0/1/129/1025 records and up to 512 groups | IDs, counts, offsets and indirect arguments agree across resets, empty/all-culled views and camera cuts |
| View ownership | Two pages recorded in one pass, alternating submissions before CPU waits | Queue-ordered in-flight reuse verified; unsynchronized concurrent writes are not supported |
| Conservative culling | f64-reference plane-support regression and real Arena counts | Avoids projected z/w cancellation at the far plane; arithmetic margin matches GPU |
| Foliage behavior | CPU deformation reference, real-GPU pixel tests, active/expired native captures | Roots, maximum bend, affine normals, deterministic seed response, radial boundaries, expiry, overflow and pause/seek covered |
| Non-grass consumers | Generic repeated-mesh tests and Arena shrub provider | Shared renderer; shrubs have different geometry/material/response; ordinary rocks remain unaffected by foliage fields |
| Streaming lifecycle | 34 presentation-control tests and native unload/reentry | Generation/owner identity, last-good replacement, cancellation, worker/result budgets, eviction and joined shutdown covered |
| Cache behavior | Arena cache/provider and generic asset-cache tests | Stable placement, warm zero-generator reuse, recipe dependency invalidation, malformed/corrupt results and reentry covered |
| Diagnostics and controls | Ops/MCP tests and native instances 22/23 | Modes, capability rejection, visual clock/fields, upload/residency counters and bounded asynchronous counts observed |
| Resource reconstruction | Static fixtures replay identical snapshots on a second device | Exact pixels and zero second-frame uploads on direct/indirect/fallback paths; no physical driver-loss recovery claim |
| Native startup, refresh, edit and streaming | Isolated fixtures described below | Compact native build (instance 32) validated warm startup/refresh, CPU/GPU counts, captures and partial/full eviction/reentry; cold imports and edits retain earlier build-specific evidence |
| Warm grass preparation target | Same-test-binary expanded/cached measurements below | At least 50% reduction observed; excludes total startup and GPU work |
| No material steady-frame regression | Expanded versus instanced comparison below | **Not achieved**: moving-camera overhead remains |

Pre-completion audit update (2026-09-21): current source inspection reconfirmed
enabled-limit fallback selection, immutable affine/split contracts, foreign/stale
chunk-ticket rejection and last-good replacement retention. These are covered by
the named fixtures in the table; inspection does not replace their scoped test
evidence or establish the whole objective as complete. Refreshed workspace
all-target Clippy passed with `-D warnings` using the isolated validation target
(`target/instance-audit-workspace-clippy.log`, 6.13 seconds). The most recent full
production-layout workspace test run recorded 493 passed / 40 ignored before the
subsequently added manual tile benchmark and affine-conditioning probe; subsequent
scoped checks and their tested source states are recorded below.
Performance acceptance and the final requirement-by-requirement completion audit
remain open. The user authorized test-only GPU pass timestamps on 2026-09-21;
the ignored-benchmark measurements below implement that narrow exception.
Runtime profiling and XRay remain deferred.

## Image comparison

Static direct/indirect/fallback fixtures compare against expanded geometry with a
declared maximum 1/255 channel error. Foliage tests compare CPU-deformed positions
and normals with the GPU implementation. Identical uniform snapshots additionally
require exact repeated pixels and zero repeated uniform-upload bytes.

GPU compaction changes draw order within a group. Equal-depth overlapping opaque
surfaces can therefore choose different fragments. The full Arena fixture requires
exact visible-count agreement and deterministic CPU repeats. Every changed GPU/Auto
pixel must exactly match a color at that pixel from a reversed or one of four fixed
hashed CPU record orders; no additional numeric tolerance is allowed. Latest policy
validation had zero unexplained pixels in wide, near and editor views. Older native
capture differences are not retroactively declared explained by a different fixture.

Evidence: `target/instance-arena-threshold-1024-parity.log`. The fixture includes
326,175 grass/shrub records and fixed-time wind in wide/near views. It excludes
editor UI and is separate from the grass-only performance comparison.

## Performance

The preparation benchmark uses 323,871 blades / 64 chunks. Current debug-test results
were 2,001.390 ms expanded, 884.474 ms warm cached and 989.517 ms cold cached: 55.8%
less warm preparation. Compact placement is 9,068,644 bytes versus 63,478,716 bytes
of expanded geometry. Earlier three same-binary trials observed 55.6–57.0% reduction.
This excludes shrubs, worker scheduling, GPU uploads and total editor startup.
Evidence: `target/instance-preparation-current.log` and
`target/instance-grass-preparation-measurements.json`.

Steady-frame comparison uses the same grass placements, scenery, palette, normals
and camera in both paths; shrubs and wind are disabled. Expanded geometry is grouped
per visible chunk. Three trials alternate ordering, with ten warm-up frames at
840x764 in release. Earlier trials measured thirty frames; the current selected-group
comparison measures 300 (`NICO_MEASUREMENT_FRAMES=300`). Moving tests alternate two nearby camera poses,
assert no visibility-cache reuse and no warmed immutable source uploads. Completion
times include host/driver work and an explicit queue wait, not isolated GPU execution.

| View | Expanded completion (ms) | Auto completion (ms) | Status |
| --- | --- | --- | --- |
| Moving wide | 2.615–2.653 | 2.657–2.688 | Selected groups, two-batch waves, native upload budget, acceptance open |
| Moving near | 1.847–1.955 | 1.965–2.052 | Selected groups, two-batch waves, native upload budget, acceptance open |
| Moving editor | 2.111–2.126 | 2.224–2.231 | Selected groups, two-batch waves, native upload budget, acceptance open |
| Stationary wide, before uniform cache | 2.673–2.761 | 2.810–2.911 | Scoped fixed-view result |
| Stationary near, before uniform cache | 1.855–1.892 | 1.924–1.963 | Scoped fixed-view result |
| Stationary editor, before uniform cache | 2.015–2.102 | 2.111–2.158 | Scoped fixed-view result |

Evidence: `target/instance-moving-grouped-paced.log` and
`target/instance-static-threshold-1024.log`. Earlier new-path-only measurements
did not prove the expanded-baseline acceptance gate. Adding that baseline exposed
a large regression; caching unchanged visibility, sharing compute passes, fusing
single-group compaction and caching record bounds/uniforms reduced it substantially.
It remains open; stationary gains do not establish moving-camera acceptance.

Recent revisions add an empty-field foliage shader return (preserving normal
normalization) and groups compute dispatches by stage across independent pages.
The empty-field branch alone showed no measurable improvement in the scoped run;
stage grouping also produced only small differences within the observed timing
variation. These changes do not establish a new performance claim. Intermediate
measurements are retained in `target/instance-moving-empty-fields.log` and
`target/instance-moving-uniform-cache.log`.
Single-group compaction now shares its count with the indirect instance-count word,
reducing three atomic increments per visible record to one. Duplicate page inputs
are rejected before writes, preventing double dispatch from overflowing the count.
This also showed no material timing improvement; dispatch/draw granularity remains
the next performance investigation. The GPU visibility test checks the changed
layout across resets and repeated views, including duplicate-page rejection.

Native Auto uses a provisional 1,024-record minimum per segment, replacing the
earlier 4,096 threshold after those optimizations. Dense/sparse moving benchmarks
cover 64–262,144 records and one/64 chunks; small batches often favor CPU, larger
batches generally favor GPU. This is measured policy for the tested workload,
not a universal crossover. Forced modes and capability fallback remain available.
Evidence: `target/instance-moving-threshold-measurement.log` and
`target/instance-moving-threshold-1024.log`.

### Visibility page grouping experiment

`gpu_visibility_page_grouping_measurement` compares 64 independent single-group
pages with one 64-group page using the same immutable bounds and shared compute
kernel. Each path records visibility every frame, alternates the eye position,
warms ten frames and measures thirty, with three trials in alternating order.
Source creation and per-group count readback are outside timing. Every group must
match its known dense or 1/16-visible count. No rasterization is included.

| Backend / records per chunk | Separate pages, dense (ms) | Grouped, dense (ms) | Separate, sparse (ms) | Grouped, sparse (ms) |
| --- | --- | --- | --- | --- |
| Vulkan / 256 | 0.605–0.637 | 0.150–0.186 | 0.602–0.638 | 0.149–0.151 |
| Vulkan / 4096 | 0.682–0.695 | 0.267–0.288 | 0.691–0.727 | 0.220–0.249 |
| DX12 / 256 | 1.549–1.629 | 0.251–0.262 | 1.553–1.633 | 0.232–0.250 |
| DX12 / 4096 | 1.968–2.073 | 1.007–1.121 | 1.615–1.741 | 0.347–0.401 |

These completed-work wall times supported grouping production visibility pages.
They do not prove full-frame acceptance or establish GPU execution time. Evidence:
`target/instance-visibility-grouping-measurement.log` and
`target/instance-visibility-grouping-dx12.log`. Both measurements and backend
all-target Clippy passed.

Production now groups newly admitted GPU uploads by static/foliage pipeline and
enabled page limits. Each chunk keeps its own source and selection buffers; global
compacted IDs are converted back to the chunk's local record index. Existing pages
are not regrouped when the camera moves or another upload wave arrives. This retains
immutable-source pacing and avoids rebuilding existing visibility bounds. Shared page
storage is counted once, including unused groups after partial retirement, and drops
when the last associated chunk retires. Incomplete uploads are removed after failed
preparation. A shared page-level camera key prevents a returning chunk from reusing
results overwritten by another chunk's view. Count sampling copies only the drawn
groups and keeps per-chunk capacity validation.

The original 22 GPU regressions passed on Vulkan (20.40 s) and DX12 (21.11 s).
The new grouped-chunk test passed on Vulkan, checking alternating disjoint cameras,
exact return pixels, zero source reuploads, partial retirement accounting and final
release. It lives in `visibility_tests`, so the usual `--skip measurement` command
includes it. Full Arena CPU/GPU/Auto counts and controlled-order pixel comparisons
passed (`target/instance-arena-grouped-parity.log`). Native paced-grouping validation
and the final performance decision remain outstanding.
The grouped-chunk regression also passed on DX12 (1.12 s). Shader artifact checking,
targeted Clippy and workspace checking passed. The Arena measurement now uses the
native 8 MiB source-upload budget because grouping depends on upload-wave boundaries;
its ten-frame warm-up completes paced uploads before timings. The earlier unlimited
grouping run is retained at `target/instance-moving-grouped-production.log`.

## Native fixture

An isolated copy of 16 Arena files (53,469,255 bytes) started without `.nico`.
Debug editor build:
`bfa7a62350da07271980b9f86dd091a78fc83e11f570747bcf98a60bd6ea65ae`.

| Scenario | Observation |
| --- | --- |
| Cold PID 4348, instance `22020-18d6f3821187eef4-22` | Startup 15,991.245 ms: models 1,083.058 ms/13 imports; textures 13,177.168 ms/12 imports; scenery 1,692.254 ms/two generated imports |
| Cold unchanged refresh | Command 2: 1.543 ms, zero imports/rebuilds |
| Warm PID 26948, instance `22020-18d6f3821187eef4-23` | Startup 910.104 ms, zero imports; model/texture/generated hits 13/12/2; scenery 217.695 ms |
| Warm initial residency | 64 chunks, 128 batches, 326,175 records, 54,816,856 decoded bytes; Auto threshold 1024 |
| Relevant source edit | MCP moved obstacle 3 from [-15,1.5,5] to [0,1.5,5], saved and reloaded; 326,781 records / 54,918,664 decoded bytes, no streaming errors |
| Subsequent camera change | Zero immutable instance and visibility-source upload bytes |
| Far pan and return | All 64 chunks evicted; retained instance GPU bytes zero; return restored all edited records without errors |
| Final unchanged refresh | Command 8: 1.581 ms, zero imports/rebuilds |

First scene presentation API success was 16,089.115 ms cold / 1,062.003 ms warm.
These are elapsed wall times and API observations, not GPU completion or display
scanout. Captures from the exact instances were inspected. One edit capture caught
streaming in progress; later resident and return captures show the moved rock and
restored grass. State and image samples have separate frame identities. Cached
viewport counters and GPU samples can refer to older prepared views.

Both editors stopped through MCP and process exit was checked; the existing bridge
was left running. This was automated capture validation, not a user-watched demo.
Evidence: `target/instance-native-1024-evidence.json`,
`target/instance-native-1024-reentry-evidence.json`, cold/warm logs, and
`instance-native-{cold,edited-settled,return}-1024.png` under `target/`.
Earlier native active/expired influence evidence is retained in
`target/instance-foliage-native-evidence.json` (PID 29912, build recorded there).

## Reproduction and verification limits

### Larger bounded visibility reservations (2026-09-21)

The retained allocator now prefers larger spare pages for upload cohorts of at
least 4,096 records, with the compact reservation and then exact allocation as
device-limit/residency-budget fallbacks. Cohorts below 4,096 retain their existing
policy. Exact sizes are specified in [architecture](../architecture.md); reserved
capacity still counts against the existing 64 MiB renderer residency budget and
other pending chunks' minimum allocations remain protected. Source uploads,
record addresses, group leases and retirement behavior are unchanged.

The 300-frame Vulkan comparison now dispatches one page/two stages in each view,
both with all-admitted sources and two-batch publication waves. Before the change,
the all-admitted control used 4/4/3 pages and mean expanded/Auto times of
2.596/2.651 ms wide, 1.896/1.992 ms near and 2.076/2.219 ms editor. Current results:

| Scenario | Wide expanded/Auto (ms) | Near expanded/Auto (ms) | Editor expanded/Auto (ms) |
| --- | --- | --- | --- |
| All-admitted | 2.598 / 2.585 | 1.897 / 1.923 | 2.082 / 2.168 |
| Two-batch waves | 2.610 / 2.689 | 1.893 / 1.912 | 2.077 / 2.151 |
| Two-batch repeat | 2.592 / 2.580 | 1.883 / 1.892 | 2.076 / 2.140 |

The wide-view variation is retained, not excluded. The near/editor results support
retaining the reservation change for further validation, but do not establish
frame-time parity. Evidence: `target/instance-current-all-admitted.log` and
`target/instance-large-pages-{final-all,final-waves,repeat-waves}.log`.

This trades potentially higher retained capacity for fewer changed-view submissions.
In the full Arena count/image fixture, Auto retained 32,184,448 / 22,367,728 /
22,967,568 bytes for wide/near/editor, versus 32,194,144 / 17,128,080 / 17,727,920
before the change. Those are a different scenario from the grass-only timing
comparison; the added roughly 5 MiB in near/editor is not hidden by the timing gain.

The full Arena fixture passed exact visible-count and explained-image comparisons
(`target/instance-large-pages-arena.log`). The 26-test Vulkan suite plus the new
large-reservation regression passed; all 27 DX12 graphics tests passed. The new
regression exercises separately paced 8,192-record batches, direct/indirect views,
camera reuse, retained-page release, and compact fallback under an advertised
1 MiB storage-binding limit. Logs: `target/instance-large-pages-{vulkan,limits,dx12}.log`.
All 25 renderer unit tests passed, as did all-target render/RHI-wgpu Clippy,
formatting and diff checks. Refreshed workspace tests passed: 493 passed, 40 ignored
(`target/instance-large-pages-workspace.log`). Final workspace validation must be
refreshed after subsequent rendering changes stabilize.

Native instance 29 (PID 25748, build `e9ac2b1a4061`, process session
`a30d032d-80bb-459e-b7e1-34e336563eaa`) validated the larger reservations on
GTX 1660 Vulkan. The first capture failed because the window was zero-sized;
automation resumed after restoration. Captures at 2160x1350, frames 2730 and
4723, were inspected and show vegetation and the fixture rock before and after
eviction. These are rendered captures, not a user-watched demonstration.

Full eviction released all provider records and all retained instance bytes.
Reentry restored 64 chunks, 128 batches and 326,781 records (54,918,664 decoded
bytes). Two subsequent settled partial/reentry cycles retained exactly 15,884,400
bytes while away and 20,778,624 bytes on return, with no growth or streaming errors.
Earlier return samples were collected during streaming and are not treated as
settled measurements. Historical camera residency before full eviction explains
the larger initial allocation; it is not compared directly with the reset state.
At the restored viewport CPU submitted 72,468 instances; forced GPU independently
reported 72,468 visible of 91,357 candidates. Auto used 43,615 direct plus 28,853
GPU-visible instances. Samples and captures have separate frame/view identities.
Warm CPU/GPU preparations uploaded zero instance/visibility/foliage bytes and
dispatched no visibility work. The 91 retained diagnostics contained no errors,
dropped records or truncated fields. The owned editor was stopped through MCP;
evidence is in `target/instance-native-large-pages-evidence.json` and captures
`target/instance-native-large-pages-{auto,return}.png`. Frame-time parity remains open.

### Full Arena warm-round control (2026-09-21)

The expanded comparison now accepts `NICO_MEASUREMENT_WARM_ROUNDS=0..3` (default
zero for previous command compatibility). Each extra round exercises both paths
for the configured frame count before the three measured trials. Warm-up output
is explicitly labeled `expanded_comparison_warmup` and excluded from the measured
means; the per-path ten-frame warm-up and existing upload/visibility assertions
also remain. This is a predefined warm-up policy, not removal of slow measured
samples after observing them.

The unchanged production renderer, GTX 1660 Vulkan, 300 frames, two-batch source
waves, one full warm round and default block ordering measured mean expanded/Auto
completed wall times of 2.721/2.771 ms wide, 1.982/2.021 ms near and 2.133/2.239 ms
editor. All three measured trials were retained. A first run overlapped formatting
and Clippy and is excluded from performance evidence; the isolated rerun had no
other agent-owned validation commands running. Logs:
`target/instance-arena-full-warm-round-isolated.log` (evidence) and
`target/instance-arena-full-warm-round.log` (overlapped run).
Formatting and targeted all-target Clippy passed (`target/instance-warm-round-clippy.log`).

Warm-up does not close the frame gap. These elapsed scopes do not identify GPU
kernel/transition costs. The subsequently authorized pass timestamps below provide
GPU measurements; production behavior remains unchanged.

### Authorized test-only GPU pass timestamps (2026-09-21)

The user explicitly authorized a narrow exception to the profiling deferral.
`quad_tests/gpu_timestamps.rs` and all backend hooks compile only with `cfg(test)`.
The ignored Arena expanded comparison and tiny-prototype benchmark enable them through
`NICO_MEASUREMENT_GPU_TIMESTAMPS=1`. This explicitly requests `TIMESTAMP_QUERY`;
unsupported adapters fail device creation rather than silently returning CPU time.
Production feature requests, RHI contracts, pass layout and shaders are unchanged.

Beginning/end queries measure visibility compute and the opaque and masked pass.
The latter includes ordinary scenery in the full fixture;
`NICO_MEASUREMENT_GRASS_ONLY=1` isolates grass rendering. These are pass durations,
not individual draw, shader-stage, barrier, CPU execution or display timings.
Storage is bounded to 4096 queries and at most 500 measured frames/path/round.
The optional Arena-only `NICO_MEASUREMENT_DISPATCH_TIMESTAMPS=1` additionally requests
`TIMESTAMP_QUERY_INSIDE_PASSES` and brackets reset/count dispatches; its frame cap is
400 for the one-page comparison. Query allocation still rejects any larger total.
This option adds synchronization cost and is diagnostic, not a parity measurement.
Resolve and mapping happen after each round, outside frame timing. Timestamp period
converts ticks to milliseconds. Final code checks that every measured frame has an
opaque-pass pair. Warm-up results remain separate; all measured samples contribute.

Initial Windows GTX 1660 Vulkan results, 300 frames/path, one complete warm-up
round then three alternating block trials, moving camera, Auto and two-batch upload
waves (milliseconds, means across the three measured trials):

| View | Expanded opaque | Auto opaque | Auto visibility | Auto GPU pass sum minus expanded |
| --- | ---: | ---: | ---: | ---: |
| Wide | 1.161 | 1.128 | 0.055 | +0.022 |
| Near | 0.569 | 0.541 | 0.037 | +0.009 |
| Editor | 0.721 | 0.744 | 0.037 | +0.060 |

The final-code repeat, including the per-frame query-count assertion, passed with
editor means of 0.713 ms expanded opaque, 0.751 ms Auto opaque and 0.037 ms
visibility (+0.075 ms combined). Wide/near combined differences were +0.023/0.000 ms.
This agrees on the editor bottleneck while retaining the observed variation;
`target/instance-timestamp-final-full-vulkan.log` records the full repeat.

The grass-only editor control measured 0.217 ms expanded opaque, 0.236 ms Auto
opaque and 0.035 ms visibility, a combined +0.053 ms. Both visibility and rendering
contribute to the editor gap. These boundaries do not identify the underlying cause
of the rendering difference. A CPU-culling grass control passed, but its 7–12 ms
submission cost substantially changes the workload; it cannot establish that output
ordering causes the GPU rendering difference.

DX12 timestamp execution also passed. Its editor means were 0.697 ms expanded
opaque, 0.810 ms Auto opaque and 0.128 ms visibility. Keep backend results separate.
A timestamps-disabled Vulkan control passed with wide/near/editor complete-frame
means of 2.623/1.958/2.187 ms expanded and 2.626/1.982/2.208 ms Auto. Instrumented
means were 2.631/1.956/2.112 and 2.636/1.989/2.200 ms respectively. Between-run
variation prevents treating instrumentation overhead as a fixed correction or
claiming parity. No other agent-owned builds ran during timing.

Reproduce with `WGPU_BACKEND=vulkan` (or `dx12`),
`NICO_MEASUREMENT_GPU_TIMESTAMPS=1`, `NICO_MEASUREMENT_FRAMES=300`,
`NICO_MEASUREMENT_WARM_ROUNDS=1`, `NICO_MEASUREMENT_UPLOAD_WAVE_BATCHES=2`,
`NICO_MEASUREMENT_INSTANCE_MODE=auto`, then
`cargo test -p nico-rhi-wgpu --release gpu_arena_moving_grass_measurement -- --ignored --nocapture --test-threads=1`.
Logs: `target/instance-timestamp-{full-vulkan,grass-vulkan,full-dx12,disabled-full-vulkan,cpu-grass-vulkan}.log`.
Targeted all-target Clippy passed with `-D warnings`
(`target/instance-timestamp-clippy.log`). Formatting and `git diff --check` passed.
The final Vulkan CPU/GPU/Auto image-and-count control passed with timestamps disabled
(`target/instance-timestamp-image-count-control.log`), preserving its existing exact
reference comparisons. Performance acceptance remains open.

Subsequent controlled probes retained no production change:

| Probe | Evidence | Outcome |
| --- | --- | --- |
| Reserve adjacent records together, without subgroups/barriers | `target/instance-packet-{full-vulkan,two-full-vulkan,visibility-vulkan}.log` | Four/two-record versions cost 0.050/0.048 ms editor visibility versus approximately 0.037 ms scalar. The initial four-record form passed the visibility reference; measured variants did not close the full-frame gap. Both discarded. |
| Omit record-level tests proven by group bounds | `target/instance-coarse-mask-{visibility,full-vulkan}.log` | Conservative per-group plane/distance masks passed the visibility reference, but editor compute remained 0.037 ms. Added CPU metadata and larger uniforms were discarded. |
| Draw instance chunks from front to back | `target/instance-front-order-{full-vulkan,arena-vulkan}.log` | CPU/GPU/Auto counts and image references passed, but full editor means remained 2.165 ms expanded / 2.251 ms Auto. Discarded. |

Stationary grass-only timestamp controls avoid the moving CPU path's per-frame
compaction cost. Editor opaque-pass means were 0.217 ms expanded / 0.248 ms CPU
direct and 0.217 ms expanded / 0.236 ms Auto, with visibility reused and no compute
dispatches. The rendering difference therefore persists without a visibility pass
in that frame; it is not explained solely by compute-to-render synchronization.
Logs: `target/instance-timestamp-stationary-{cpu,auto}-grass.log`.

The retained test-only `NICO_MEASUREMENT_RECORD_ORDER=front|back` control permutes
the same immutable records inside each batch before uploads/timing. It preserves
IDs, transforms, chunk bounds, profiles and the original expanded baseline. This
does not measure runtime sorting cost or claim fully sorted GPU output: compaction
still chooses its own output order. With the same 300-frame/warm-round settings,
front/back source order gave editor Auto opaque means of 0.236/0.236 ms; front-order
stationary CPU direct measured 0.247 ms, versus 0.248 ms without the control.
These controls do not demonstrate a useful record-order optimization. They are
timing diagnostics, not image-parity acceptance tests. Logs:
`target/instance-record-{front-grass,back-grass,front-cpu-grass}-vulkan.log`.

All four production experiment files were restored byte-for-byte to their inputs.
Only ignored-benchmark controls, test-only timestamp reporting and evidence remain.
Targeted all-target Clippy passed (`target/instance-order-timestamp-clippy.log`).

### Compact affine-record experiment and conditioning regression (2026-09-21)

A vector-expression rewrite of the existing transform arithmetic left editor grass
rendering at 0.236 ms and was discarded (`target/instance-vector-transform-grass.log`).
An 80-byte source-record experiment instead removed stored normal rows, retained
three affine model rows, and uploaded foliage parameters, inverse determinant and
tint. Direct/storage shaders reconstructed normal rows from cofactors. This reduced
source bytes by 28.6% without changing placement caches or the logical instance type.

With the timestamp benchmark settings above, GPU grass-only opaque-pass means were:

| View | Original 112-byte control (ms) | Experimental 80-byte record (ms) |
| --- | ---: | ---: |
| Wide | 0.299 | 0.272 |
| Near | 0.162 | 0.149 |
| Editor | 0.236 | 0.212 |

The full-scene candidate measured expanded/Auto complete-frame means of
2.638/2.613 ms wide, 1.964/1.979 ms near and 2.125/2.180 ms editor. Restoring the
original layout gave 2.656/2.677, 1.983/2.020 and 2.102/2.182 ms respectively.
The isolated GPU reduction is repeatable against the restored control; variable
CPU/baseline wall time means the full-frame gap difference is not entirely attributable
to the encoding. Neither run establishes editor parity. Logs:
`target/instance-compact80-{full-vulkan,grass-vulkan,control-full-vulkan,control-grass-vulkan}.log`.
The candidate also completed a separately scoped DX12 measurement
(`target/instance-compact80-full-dx12.log`); no cross-backend performance claim is made.

The candidate passed the existing 27 GPU regressions on both Vulkan and DX12,
the Arena CPU/GPU/Auto image/count comparison, 26 renderer tests, generated-shader
verification, workspace Clippy and 494 workspace tests (41 ignored). These checks
were insufficient: a newly added real-GPU conditioning probe found that the accepted
affine scale `[1e-20, 1e-20, 100]` produces normal component **0 instead of 0.01** in
the compact decoder. An intermediate cofactor underflows on the tested Vulkan GPU.
The stored-normal 112-byte control passes the same probe. Thus this experiment is
**not adopted**, despite its performance gain and the preceding green checks.

`quad_tests/affine_normal_tests.rs` retains the regression. It invokes the actual
generated instance shader's compact decoder when present, otherwise reads its
stored normal rows, and compares GPU output with normals from accepted logical
records. This is a correctness readback, not additional profiling. It includes
identity, reflection/nonuniform scale and the anisotropic case. Evidence:
`target/instance-compact80-conditioning-{vulkan,control-vulkan}.log`.
All ten production experiment files were restored byte-for-byte, along with the
old-format upload assertions. The restored source passes all 28 GPU regressions
on Vulkan and DX12, including the conditioning probe
(`target/instance-compact80-restored-gpu-{vulkan,dx12}.log`). Targeted all-target
Clippy, formatting and diff checks pass (`target/instance-compact80-restored-clippy.log`).
A future compact path must retain a reliable full-normal-matrix fallback for
transforms it cannot reconstruct safely; weakening the affine contract is not an
acceptable performance fix. Frame-time parity and the final completion audit remain open.

### Guarded compact-record follow-up (2026-09-21)

The initial guarded renderer candidate was explicitly opt-in, with native hosts
still on 112-byte records. The later native validation below supersedes that host policy. Selection happens once per immutable batch at upload. Conservative
linear-component, inverse-determinant, condition and cofactor checks reject unsafe
reconstruction; one rejected record keeps the entire batch in the full representation.
The accepted anisotropic regression therefore uploads 112 bytes, while identity and
moderate reflected scale upload 80 bytes. Actual residency/upload counters follow the
selected encoding; source admission and device-capacity calculations remain at 112
bytes. Logical records and placement caches are unchanged.

A runtime uniform selector between layouts lost most of the earlier render-pass gain.
Separate `vertex_compact_main` entry points now specialize direct vertex fetch and
storage fetch. Each storage entry point accesses only its own typed binding alias;
no entry point accesses both aliases. Pipeline selection handles mixed layouts without
an extra view uniform. Custom shader users must explicitly provide the compact entry
points before enabling this option. Native enablement was deferred until the
subsequent lifecycle validation below.

Release GTX 1660/Vulkan, 840x764, two-batch upload waves, 300 measured frames per
trial, one complete warm-up round, three rotating trials, test-only GPU timestamps:

| View | Compact expanded/Auto wall (ms) | Full-record expanded/Auto wall (ms) | Compact/full Auto render pass (ms) |
| --- | ---: | ---: | ---: |
| Wide | 2.705 / 2.733 | 2.654 / 2.658 | 1.126 / 1.142 |
| Near | 2.038 / 2.037 | 1.946 / 1.984 | 0.532 / 0.541 |
| Editor | 2.219 / 2.294 | 2.123 / 2.221 | 0.722 / 0.747 |

The editor visibility pass remains 0.0366 ms compact versus 0.0370 ms full. GPU
rendering improves, but CPU/baseline variation prevents attributing the wall-time
change solely to encoding. The compact run still trails expanded geometry by about
0.076 ms at the editor camera. **Frame-time parity is not established.** Logs:
`target/instance-guarded-specialized-{full,control}-vulkan.log`. The preceding uniform
selector and storage-only experiments are recorded in
`target/instance-guarded-{full,typed-storage}-vulkan.log`.

The real-source normal probe now captures actual renderer uploads, checks sizes
80/80/112, reads normals through the selected generated shader representation, and
compares direct/indirect rendered images after switching modes. Vulkan passes this
updated probe and the other 27 GPU regressions. The initial probe-only candidate
also passed all 28 DX12 regressions. The updated mixed direct/indirect probe and
Arena CPU/GPU/Auto image/count comparison pass on both Vulkan and DX12. The 26
renderer unit tests, targeted all-target Clippy, shader artifact verification,
formatting and diff checks pass. Logs use `target/instance-guarded-specialized-`
with `gpu-vulkan`, `gpu-dx12`, `affine-vulkan`, `affine-dx12`, `arena`, `arena-dx12`,
`render-tests`, `clippy` and `shader-check` suffixes. Final workspace validation,
native compact-path lifecycle testing and the original-plan completion audit remain
open; prior native evidence applies to the full layout.

### Guarded compact native integration (2026-09-21)

Native installation now opts into guarded compact records after loading the matching
built-in direct/storage/foliage artifacts. Unsupported compact pipeline creation keeps
the full-record path; affine records rejected by the compact safety guard retain full
normal matrices. Custom renderer users still opt in explicitly.

The isolated fixture ran in editor PID **23396**, instance
`22020-18d6f3821187eef4-32`, process session
`10320b67-9bd6-498a-8f6e-95eb569b8f5e`, build
`df63cea7c10b2a39e9ccb11724a88ed13e98e791f02071ff35109557f5d9b6cb`.
The diagnostics log confirms compact enablement on GTX 1660/Vulkan. Warm startup
was 925.459 ms, scenery preparation 218.619 ms, first scene presentation API success
1045.862 ms, with 13 model/12 texture/2 scenery cache hits and zero imports.
Unchanged refresh command 112 completed in 1.488 ms with zero imports/rebuilds.
These are elapsed loader times, not CPU/GPU execution or desktop visibility timings.

At the fixed native camera and 2160x1350 capture extent, CPU submitted **89,889**
visible instances. GPU prepared view 25 reported exactly **89,889** from 111,354
candidates across 46 indirect draws. That unchanged view reused all 46 batches with
zero source/visibility/foliage uploads and zero dispatches. Captures from frames
423 (Auto), 783 (CPU), 1209 (GPU) and 2445 (settled return) were retained; Auto, GPU
and settled return were inspected and show consistent grass, shrubs, ordinary rocks
and authored scenery. Counts and captures retain separate view/frame identities.
CPU/GPU PNGs are not pixel-identical (66 of 2,916,000 pixels differ); no exact native
image equality or user-watched demonstration claim is made.

Pan to `[2000,0,0]` evicted all 64 chunks and released all renderer instance bytes
(view 26). Return restored 128 batches/326,781 records in 64 streaming chunks.
At the selected camera, 46 renderer batches retained 20,093,440 bytes after the
streaming upload waves. Two partial cycles to `[560,0,0]` retained 40 streaming
chunks and 20 renderer batches/14,431,344 bytes; both returns restored exactly
20,093,440 renderer bytes. A settled unchanged preparation after full return used
zero source uploads. No renderer readback failures or streaming errors were observed.

Automated control stopped after these checks. Later camera changes outside the
agent's commands indicated use of the window; it was left open rather than stopped.
The user subsequently authorized closing instance 32. Its bridge stop completed at
host frame 9,939; fresh discovery reported `stopped` and disconnected, and a process
lookup confirmed PID 23396 absent before the resumed performance runs below.
The measurements above precede that external camera activity (refresh is separately
identified by command 112). This is scoped native lifecycle evidence, not a frame-time
parity result. Build and targeted `nico-winit` all-target Clippy passed. Evidence:
`target/instance-compact-native-evidence.json`, `target/instance-compact-native-*.png`,
and the corresponding build/Clippy/stdout/stderr logs. Refreshed workspace tests
passed **494 tests, 42 ignored**; workspace all-target Clippy with `-D warnings`,
formatting and diff checks also passed (`target/instance-compact-workspace-{tests,clippy}.log`).
Performance acceptance and the final completion audit remain open.

### Resumed compact performance controls (2026-09-21)

After verifying the test editor exited, the unchanged compact renderer ran serial
release comparisons on GTX 1660, 840x764, full scenery, moving cameras, two-batch
upload waves, Auto mode, 300 measured frames per trial, one warm-up round and three
rotating block trials. Pass timestamps were enabled; dispatch timestamps were not.
Both benchmark invocations passed their upload assertions.

| Backend/view | Expanded/Auto completed wall (ms) | Expanded/Auto render pass (ms) | Auto visibility (ms) |
| --- | ---: | ---: | ---: |
| Vulkan wide | 2.725 / 2.704 | 1.169 / 1.105 | 0.0550 |
| Vulkan near | 2.203 / 2.105 | 0.579 / 0.538 | 0.0369 |
| Vulkan editor | 2.276 / 2.221 | 0.724 / 0.718 | 0.0364 |
| DX12 wide | 8.276 / 8.097 | 1.107 / 1.156 | 0.2630 |
| DX12 near | 7.309 / 7.227 | 0.564 / 0.581 | 0.1385 |
| DX12 editor | 7.660 / 7.383 | 0.702 / 0.774 | 0.1173 |

Wall time favored Auto in this run, unlike earlier editor controls. This alone does
not establish consistent parity. The editor's measured render-plus-visibility cost
still increased by about 0.030 ms on Vulkan and 0.189 ms on DX12; elapsed submission
savings outweighed that cost in these block trials. These are elapsed measurements,
not CPU execution samples. No production optimization was added for this rerun.
Evidence: `target/instance-compact-resumed-{vulkan,dx12}.log`.

A subsequent Vulkan control disabled all timestamps, alternated expanded/Auto every
frame, and used 1,000 measured frames per path per trial after three complete warm-up
rounds. It passed in 87.80 seconds. Mean expanded/Auto completed wall times were
2.724 / 2.643 ms wide, 2.022 / 2.030 ms near, and 2.173 / 2.205 ms editor.
The editor regressed in each of the three trials (0.038, 0.028 and 0.029 ms), while
its elapsed submission means were 1.313 / 1.308 ms. Thus the block-run advantage
does not establish parity; the residual editor gap persists without timestamp
instrumentation. Future optimization must improve the completed-frame result,
not merely submission timing. Evidence:
`target/instance-compact-resumed-paired-vulkan.log`. Frame-time parity remains open.

### Squared-distance visibility probe (2026-09-21)

The ignored Arena fixture accepts `NICO_MEASUREMENT_VISIBILITY_SHADER` as a path to
an alternate WGSL visibility artifact, mutually exclusive with the existing subgroup
override. This does not request subgroup features or replace production artifacts.
Paths resolve from the test process's working directory; absolute paths avoid
Cargo's package-relative working directory. An initial relative-path invocation
failed before measurements, then the corrected absolute-path invocation passed.

A probe replaced the distance-test square root with squared-distance comparison.
On GTX 1660/Vulkan, compact records, full scenery, two-batch upload waves, moving
cameras, alternating paths, 300 measured frames and one warm-up round, its
wide/near/editor visibility means were 0.05439 / 0.03681 / 0.03666 ms. The original
shader in the same executable measured 0.05382 / 0.03664 / 0.03660 ms. Editor
expanded/Auto wall times were 2.249 / 2.288 ms for the probe and 2.193 / 2.227 ms
for the control. Both runs passed their upload assertions; the probe showed no
useful visibility gain and was not adopted. Boundary rounding and overflow
equivalence were not established, so these timings are not correctness acceptance.
Production shaders remain unchanged and frame-time parity remains open. Logs:
`target/instance-squared-distance-{vulkan-retry,control}.log`; the failed initial
path lookup is preserved in `target/instance-squared-distance-vulkan.log`.

### Compact-path coverage audit (2026-09-21)

The foliage CPU-expanded reference fixture previously installed the indirect pipeline
but left Auto mode enabled. Its one-record, fully visible batch could therefore
remain on the direct path. Earlier success of that fixture alone did not prove
indirect deformation/reference equality. The corrected fixture explicitly forces
CPU/GPU mode and asserts indirect/submitted draw counters before comparing images.
Both full and compact encodings now run the reflection, shear, nonuniform scale,
wind/radial response and expiry cases through the intended paths. First-use source
uploads are asserted as 112 or 80 bytes; subsequent field/time changes upload zero
source records.

A new compact material fixture adds nondegenerate UVs, a normal map and alpha masking.
It verifies independently that removing the normal map changes shading and removing
the alpha mask increases coverage, then compares direct/indirect rendering against
CPU-deformed expanded geometry. The predeclared tolerance remains at most one 8-bit
value per channel. Its directional light illuminates the tested normals; the initial
fixture setup was rejected because its light direction made normal-map presence
visually indistinguishable.

The existing immutable-snapshot reconstruction fixture now also exercises compact
direct/indirect paths and capability-limited ordinary fallback. It verifies initial
80-byte source uploads, unchanged/cull-reentry reuse, replacement-only uploads,
retirement, and identical pixels after reconstruction on an independent device.
The fallback remains bounded and rejects overflow before acquiring a frame. This
establishes resource reconstruction, not physical driver-loss recovery.

All **31** non-measurement graphics regressions pass on Vulkan and DX12 on GTX 1660
(`target/instance-compact-audit-gpu-{vulkan,dx12}.log`); targeted all-target Clippy,
formatting and diff checks pass. These additions change validation only. The preceding
workspace result (494 passed/42 ignored) predates the three new ignored graphics tests.
Performance acceptance remains open.

### Interleaved compact control and reusable-range cost (2026-09-21)

Alternating expanded/Auto frames (`NICO_MEASUREMENT_PAIRED=1`) with the same
specialized compact candidate and 300-frame timestamp settings gives expanded/Auto
complete-frame means of 2.835/2.739 ms wide, 2.050/2.048 ms near and 2.238/2.254 ms
editor. Editor submission means are 1.340/1.320 ms, while GPU render means are
0.732/0.722 ms and Auto visibility is 0.0368 ms. This run points to a remaining GPU
cost rather than slower CPU submission; it complements the block-order runs and
does not establish parity. Log: `target/instance-guarded-specialized-paired-vulkan.log`.

A temporary benchmark-only visibility artifact omitted reusable input-range checks
for the non-evicting fixture, to bound the benefit of replacing atomic range loads
with immutable metadata. Visibility means changed from 0.0541/0.0368/0.0368 ms
wide/near/editor to 0.0514/0.0348/0.0357 ms. Editor complete-frame overhead remained
0.0167 ms versus 0.0153 ms in the control. The small upper-bound gain does not
justify a metadata redesign. The original generated visibility artifact was restored
byte-for-byte; range safety is unchanged. This probe is not a correctness-valid
replacement for reusable allocation retirement.
Log: `target/instance-range-upper-bound-vulkan.log`.

The retained mixed-layout regression additionally verifies zero unchanged source
uploads, release of all renderer instance residency after source owners disappear,
and two exact-image re-entry cycles in both forced CPU and GPU modes. Re-entry
uploads 272 bytes for the three 80/80/112-byte records. It passes on Vulkan and DX12
(`target/instance-guarded-lifecycle-{vulkan,dx12}.log`). These are offscreen renderer
lifecycle checks; native-host lifecycle verification remains outstanding.

### Visibility specialization and dispatch breakdown (2026-09-21)

A temporary visibility artifact specialized the reusable, multi-group layout, making
partitioning and count-layout branches compile-time constants. Under the same compact,
paired 300-frame Vulkan fixture, wide/near/editor visibility means were
0.0540/0.0373/0.0362 ms, versus 0.0541/0.0368/0.0368 ms in the generic control.
Editor complete-frame means remained 2.212 ms expanded versus 2.239 ms Auto.
This does not justify extra production compute variants. The original generated
artifact was restored byte-for-byte (`target/instance-specialized-visibility-vulkan.log`).

The authorized test-only timestamp helper now optionally brackets the existing reset
and count dispatches. No runtime feature request, RHI method or production shader
changed. On GTX 1660, three measured rounds of 300 frames give:

| Backend/view | Whole visibility pass (ms) | Reset bracket (ms) | Count bracket (ms) |
| --- | ---: | ---: | ---: |
| Vulkan wide | 0.0639 | 0.0053 | 0.0536 |
| Vulkan near | 0.0482 | 0.0063 | 0.0361 |
| Vulkan editor | 0.0482 | 0.0062 | 0.0362 |
| DX12 wide | 0.2428 | 0.0051 | 0.2374 |
| DX12 near | 0.1271 | 0.0050 | 0.1219 |
| DX12 editor | 0.1313 | 0.0049 | 0.1262 |

The count/compaction stage dominates both runs. These brackets can include backend
synchronization and are not pure shader execution measurements. Extra query writes
raise the Vulkan editor pass from roughly 0.037 to 0.048 ms, so these instrumented
full-frame timings cannot establish parity. Logs:
`target/instance-dispatch-timestamps-{vulkan,dx12}.log`.

A further ignored-benchmark control accepts `NICO_MEASUREMENT_RECORD_ORDER=morton`.
It sorts each batch by ten-bit-per-axis spatial codes before upload/timing, preserving
record values, chunk bounds and the original expanded baseline. With ordinary pass
timestamps (no dispatch timestamps), Vulkan visibility measured
0.0517/0.0352/0.0363 ms wide/near/editor, and expanded/Auto wall means were
2.793/2.702, 2.047/2.051 and 2.256/2.276 ms. The editor result provides no useful gain
over the unchanged-order paired control; no provider/cache ordering change was adopted.
Log: `target/instance-morton-compact-vulkan.log`. Targeted all-target Clippy passes;
frame-time parity and final acceptance remain open.

### Tiny-prototype instance packing isolation (2026-09-21)

Follow-up with authorized GPU timestamps: the same warmed test now accepts
`NICO_MEASUREMENT_GPU_TIMESTAMPS=1`. Query resolve/readback remains outside frame
timing, and every measured draw has a timestamp pair. At tile sizes 1/4/8/16/32,
Vulkan render-pass means were 0.2930/0.2924/0.2925/0.2929/0.2926 ms; DX12 means were
0.2939/0.2932/0.2930/0.2929/0.2929 ms. Both runs passed exact image comparisons,
including the incomplete final tile. This provides no useful packing gain for
this procedural shader; it does not measure Arena storage fetch or PBR shading.
Logs: `target/instance-tile-timestamps-{vulkan,dx12}.log`.

The ignored `gpu_instance_tile_measurement` compares one, four, eight, sixteen
and thirty-two logical five-vertex/nine-index records per hardware instance.
It repeats only the shared prototype's index pattern, computes identical logical
positions/colors in the vertex shader and renders 262,145 logical records.
The final valid record is enlarged at the center so accidental surplus tail
records would visibly overwrite it. Every variant must match the one-record
reference pixel-for-pixel; all comparisons passed on GTX 1660 Vulkan and DX12.
This fixture uses procedural positions and flat color, without Arena storage
fetches, PBR, culling or indirect draws. It isolates a scheduling hypothesis,
not production performance or full renderer correctness.

Initial runs showed a slower first one-record sample that did not persist. The
retained benchmark therefore executes a complete warm-up round across all five
variants before three rotating measured rounds (300 measured frames after ten
additional warm frames per variant). Completed wall means after that warm-up:

| Logical records per hardware instance | Vulkan (ms) | DX12 (ms) |
| --- | --- | --- |
| 1 | 0.426 | 0.469 |
| 4 | 0.423 | 0.467 |
| 8 | 0.431 | 0.471 |
| 16 | 0.425 | 0.481 |
| 32 | 0.420 | 0.479 |

These results do not establish a consistent gain that would justify adding tiled
prototype/argument/tail handling to production. The first-sample improvement is
not treated as an optimization result. Logs:
`target/instance-tile-warmed-{vulkan,dx12}.log`; initial evidence remains in
`target/instance-tile-vulkan.log` and `target/instance-tile-tail-{vulkan,dx12}.log`.
Formatting and all-target RHI-wgpu Clippy passed (`target/instance-tile-clippy.log`).
Production rendering is unchanged; frame-time parity remains open.

### Compiler and subgroup-convergence checks (2026-09-21)

Compiling the retained visibility Slang source with explicit `-O3` produced
identical WGSL after the shader tool's whitespace normalization (9,592 characters
in each normalized text). The installed compiler help identifies optimization
level 1 as default and 3 as maximal. No compiler option change or new performance
claim follows from identical generated code. Artifacts:
`target/instance-visibility-o3.wgsl`, `target/instance-slang-help.txt`.

The saved test-only subgroup shader was also checked on DX12. Replacing its
lane-zero election with `WaveIsFirstLane()` emitted `subgroupElect`, which the
pinned WGSL parser rejected. With the original lane-zero election restored but
the whole-subgroup early return removed, the first wide GPU count was still
wrong: 118,980 versus CPU 130,984. Thus removing that early return does not fix
the known failure; the cause is not attributed to a driver or compiler. The
unchanged production scalar fixture subsequently passed the full DX12 count/image
comparison in 13.32 seconds. The saved subgroup source was restored byte-for-byte.
Logs: `target/instance-subgroup-convergence-dx12-valid-path.log`,
`target/instance-subgroup-no-early-dx12.log`, and
`target/instance-subgroup-convergence-scalar-control.log`. An initial invocation
used a relative shader path unavailable from the test package directory and did
not exercise GPU correctness (`instance-subgroup-convergence-dx12.log`).

### Selected-group repeat and paired timing (2026-09-21)

Increasing the selected-group candidate from 32 to 64 X workgroups did not
improve its grass-only editor gap: mean expanded/Auto was 0.483/0.541 ms. The
32-workgroup repeat produced full-scene means of 2.686/2.684 ms wide,
2.020/2.057 near and 2.226/2.205 editor, but its grass-only editor repeat was
0.477/0.555 ms. Those conflicting gaps motivated a complementary paired timing
control rather than treating an isolated negative gap as parity evidence.

The manual benchmark now accepts `NICO_MEASUREMENT_PAIRED=1`. Each camera step
renders both paths with alternating first-path order; each path keeps ten warm
frames and the configured measured frame count. Default block scheduling remains
available. Logs retain counters separately for each path, and zero-warm-upload
and changed-camera visibility assertions still apply. Pairing reduces the time
between comparable samples, but switching paths can change cache behavior; this
diagnostic does not replace steady-path acceptance or isolate GPU execution.

Three paired 300-frame trials on GTX 1660 Vulkan, two-batch publication waves:

| Scope / variant | Wide expanded/Auto (ms) | Near (ms) | Editor (ms) |
| --- | --- | --- | --- |
| Full-scene selected-group candidate | 2.880 / 2.800 | 2.057 / 2.060 | 2.302 / 2.339 |
| Full-scene original control | 2.799 / 2.744 | 2.016 / 2.017 | 2.196 / 2.235 |
| Grass-only selected-group candidate | 0.786 / 0.711 | 0.474 / 0.473 | 0.528 / 0.547 |
| Grass-only original control | 0.766 / 0.714 | 0.479 / 0.484 | 0.529 / 0.562 |

Full-scene editor gaps are nearly unchanged (0.037 versus 0.039 ms), despite
a smaller grass-only gap. The candidate is not promoted; the original production
renderer and shaders are restored. Formatting and all-target RHI-wgpu Clippy
passed (`target/instance-paired-clippy.log`). Evidence:
`target/instance-selected-dispatch-{64-grass,repeat-full,repeat-grass}.log` and
`target/instance-selected-dispatch-paired-{full,control-full,grass,control-grass}.log`.
Frame-time parity remains open.

### Dispatch only selected reusable groups (2026-09-21)

A temporary variant uploaded a dense list of selected group IDs in the view
uniform (160 to 2,208 bytes), then dispatched 32 X workgroups by selected-group
count in Y. Threads traversed each exclusive record interval in 2,048-record
strides, retaining counts, indexed arguments and existing source addresses.
Reset still covered all active groups; unselected groups retained zero counts.
The full Arena Vulkan count/image fixture passed with zero unexplained pixels
(`target/instance-selected-dispatch-arena.log`). Unlike the earlier group-range
probe, the count dispatch did not launch groups excluded by the selection mask.

Three alternating 300-frame trials, GTX 1660 Vulkan, two-batch waves:

| Scope / variant | Wide expanded/Auto (ms) | Near (ms) | Editor (ms) |
| --- | --- | --- | --- |
| Grass-only candidate | 0.695 / 0.655 | 0.434 / 0.460 | 0.493 / 0.537 |
| Grass-only restored control | 0.696 / 0.687 | 0.432 / 0.472 | 0.477 / 0.547 |
| Full-scene candidate | 2.821 / 2.900 | 2.028 / 2.148 | 2.256 / 2.292 |
| Full-scene restored control | 2.846 / 2.732 | 1.992 / 2.038 | 2.172 / 2.306 |

Grass-only results support investigating this direction further, but full-scene
results are mixed and show substantial baseline variation. They do not establish
a consistent improvement or frame-time parity. Original production sources and
shader were restored with matching hashes. The temporary implementation also
raised the dispatch-dimension requirement to 512; any retained version needs
explicit limit/fallback and layout/lifecycle validation, not just the Arena test.
Candidate copies remain at `target/instance-selected-dispatch-candidate.{rs,slang,wgsl}`.
Logs: `target/instance-selected-dispatch-{grass,control-grass,full,control-full}.log`.

### Indirect-count reuse with larger pages (2026-09-21)

The earlier single-atomic idea was rechecked after adopting larger shared pages,
using the grass-only diagnostic to reduce unrelated scenery cost. A temporary
partitioned-page shader used the indexed argument instance count as its atomic
slot allocator; asynchronous renderer diagnostics copied that argument word
instead of the separate count region. The full Arena Vulkan CPU/GPU/Auto
count/image fixture passed with zero unexplained pixels
(`target/instance-argument-count-arena.log`). This did not validate the existing
public contiguous-count-region contract, so adopting it would require explicit
API/layout and low-level test changes rather than silently dropping that contract.

Three alternating 300-frame grass-only trials on GTX 1660 Vulkan with two-batch
publication waves measured mean expanded/Auto completed wall times of
0.688/0.678 ms wide, 0.420/0.452 near and 0.488/0.550 editor. The restored original
control measured 0.677/0.670, 0.422/0.456 and 0.471/0.532 ms respectively.
Editor overhead was essentially unchanged (0.062 versus 0.061 ms). No consistent
gain supports the layout change. Original readback code and both shader files
were restored with matching hashes. Evidence:
`target/instance-argument-count-{grass,control}.log`. Production behavior remains
unchanged and full-scene parity remains open.

### Single-dispatch reusable-group probe (2026-09-21)

A temporary reusable-page kernel assigned one workgroup to each live group.
It reset a workgroup-local atomic counter, culled the group's exclusive record
interval in lane-strided loops, wrote visible IDs and then published final group
counts and indexed arguments after a workgroup barrier. Thus reusable pages used
one dispatch without a separate reset. Packed/non-reusable pages retained their
existing stages. The 64-thread candidate passed the full Arena Vulkan
CPU/GPU/Auto count/image fixture with zero unexplained pixels
(`target/instance-single-dispatch-arena.log`).

Grass-only moving-camera diagnostics used three alternating 300-frame trials,
two-batch publication waves and GTX 1660 Vulkan. Mean expanded/Auto completed wall
times were:

| Candidate | Wide (ms) | Near (ms) | Editor (ms) |
| --- | --- | --- | --- |
| One dispatch, 64 threads/group | 0.710 / 0.716 | 0.442 / 0.529 | 0.476 / 0.602 |
| One dispatch, 256 threads/group | 0.680 / 0.658 | 0.428 / 0.470 | 0.478 / 0.554 |
| Restored two-dispatch control | 0.695 / 0.687 | 0.437 / 0.463 | 0.493 / 0.548 |

Reducing dispatch count did not establish a net improvement; increasing parallel
lanes reduced the candidate's cost but left larger near/editor gaps than the
control. The 256-thread version received timing checks, not the full image suite.
Neither candidate is retained. Original renderer, Slang and generated shader
hashes matched after restoration, including the original device-limit checks.
Logs: `target/instance-single-dispatch-{grass,256-grass,control}.log`. These scoped
diagnostics do not replace full-scene acceptance; parity remains open.

### Grass-only diagnostic isolation (2026-09-21)

The manual expanded comparison accepts `NICO_MEASUREMENT_GRASS_ONLY=1` to remove
the same 161 ordinary scenery draws from both paths before constructing either
reference. Placement, grass prototype, palette, camera, upload waves and rendering
remain unchanged. This diagnostic is separate from the default full-scene
acceptance workload and must not replace it.

On GTX 1660 Vulkan, three alternating 300-frame trials with two-batch publication
waves produced these mean expanded/Auto completed wall times:

| Diagnostic | Wide (ms) | Near (ms) | Editor (ms) |
| --- | --- | --- | --- |
| Moving camera | 0.682 / 0.674 | 0.426 / 0.451 | 0.478 / 0.540 |
| Stationary camera | 0.687 / 0.577 | 0.431 / 0.400 | 0.483 / 0.492 |

Moving editor submission means were 0.229/0.236 ms; stationary submission means
were 0.233/0.218 ms. The remaining moving-view difference persists after removing
ordinary scenery and shrinks substantially with visibility reuse. This supports
investigating changed-view visibility work rather than ordinary scenery bindings;
elapsed submission/completion scopes do not isolate GPU execution time or individual
kernel costs. Logs: `target/instance-grass-isolated-{waves,stationary}.log`.
Formatting and all-target RHI-wgpu Clippy passed
(`target/instance-grass-isolated-clippy.log`). Production code is unchanged and
full-scene frame-time parity remains open.

### Frustum-first culling probe (2026-09-21)

A temporary scalar shader tested all six frustum planes before the nearest-bound
distance calculation, retaining the same finite-input predicates and margins.
The full Arena Vulkan CPU/GPU/Auto count/image fixture passed with zero
unexplained pixels (`target/instance-frustum-first-arena.log`).

Three alternating 300-frame trials per view on GTX 1660 Vulkan, with two-batch
publication waves, measured expanded/Auto completed wall means of 2.673/2.692 ms
wide, 1.905/1.976 ms near and 2.150/2.220 ms editor. The restored distance-first
control measured 2.650/2.622, 1.945/1.938 and 2.144/2.191 ms respectively. The
candidate does not establish an improvement; the original Slang and generated
visibility shader were restored and matched their pre-experiment hashes.
Logs: `target/instance-frustum-first-{waves,control}.log`. No production change
is retained, and frame-time parity remains open.

### Expanded-baseline timing boundary (2026-09-21)

The moving comparison prepares the expanded chunk draw list before timing, while
instance chunk selection runs inside the renderer. The manual benchmark now
measures that excluded selection separately over 10,000 iterations: camera matrix,
batch-bound frustum tests, selected draw clones, vector allocation/drop and a
count assertion against the actual preselected lists. It does not measure original
placement generation, mesh expansion, full game snapshot construction or CPU
execution time; it is elapsed time for this bounded selection loop.

On GTX 1660 Vulkan, mean selection elapsed times were 3.876 microseconds wide,
2.791 near and 2.835 editor (`target/instance-selection-boundary-waves.log`). This
small timing-boundary asymmetry does not explain the remaining rendering gap.
No subtraction or acceptance-threshold adjustment is made. The same run's editor
completed-frame means were 2.218 ms expanded and 2.362 ms Auto, with visible
run-to-run drift in both paths. Formatting and all-target RHI-wgpu Clippy passed
(`target/instance-selection-boundary-clippy.log`). Production rendering is unchanged.

### Precomputed frustum-plane probe (2026-09-21)

A temporary variant derived the six clip planes once on the CPU and uploaded
them instead of the clip matrix, increasing the view uniform from 160 to 192
bytes. The record test retained the same support-point and arithmetic-margin
formula. The full Arena CPU/GPU/Auto count/image fixture passed on GTX 1660
Vulkan with zero unexplained pixels (`target/instance-precomputed-planes-arena.log`).

Three alternating 300-frame trials with two-batch source waves measured mean
expanded/Auto completed wall times of 2.730/2.660 ms wide, 1.957/1.976 ms near,
and 2.144/2.285 ms editor. The restored matrix-upload control measured
2.737/2.672, 1.969/1.987, and 2.171/2.250 ms respectively. The editor gap was
larger with precomputed planes (0.141 versus 0.079 ms); this does not support
retaining the extra uniform storage or changing the ABI. The original Rust,
Slang and generated visibility shader were restored. Logs:
`target/instance-precomputed-planes-{waves,control}.log`. Frame-time parity
remains open; no GPU timestamp or per-method profiling claim is made.

### Culling workgroup-size probe (2026-09-21)

With the larger page allocator retained, temporary 128- and 256-thread count
kernels were compared with the original 64-thread kernel. Reset/scatter stayed
at 64; count dispatch dimensions and temporary capability checks matched each
candidate. The 128-thread full Arena count/image fixture passed with zero
unexplained pixels (`target/instance-workgroup-128-arena.log`).

Three alternating 300-frame trials per view, Vulkan GTX 1660, two-batch upload
waves, produced these mean expanded/Auto completed wall times:

| Count threads | Wide (ms) | Near (ms) | Editor (ms) |
| --- | --- | --- | --- |
| 128 | 2.646 / 2.667 | 1.919 / 1.938 | 2.113 / 2.206 |
| 256 | 2.674 / 2.706 | 1.934 / 1.937 | 2.170 / 2.348 |
| Restored 64 | 2.760 / 2.708 | 2.080 / 2.169 | 2.238 / 2.360 |

The restored control also slowed relative to earlier runs, including its expanded
baseline and submission scopes. These sequential runs do not establish a reliable
workgroup ranking or a consistent improvement. Neither candidate is retained;
the renderer, Slang source and generated visibility shader were restored and
their hashes matched the pre-experiment copies. Device compatibility remains
unchanged. Evidence: `target/instance-workgroup-{128,256,64-restored}-waves.log`.
Frame-time parity remains unproven; these results do not replace the preceding
stable benchmark evidence.

### Subgroup compaction probe (2026-09-21)

Production still uses scalar compaction. The manual Arena fixture can request
`Features::SUBGROUP` and load a candidate shader through
`NICO_MEASUREMENT_SUBGROUP_SHADER`; the default test-device feature set is unchanged.
The candidate source lives in
`crates/nico-rhi-wgpu/src/quad_tests/visibility_subgroup.slang`. Compile its four
reset/count/scan/scatter entries to WGSL with `slangc`, then point the environment
variable to that output. The test loader removes Slang's `enable subgroups;`
declaration because the pinned Naga rejects it; wgpu's native feature flag remains
required. Lane zero leads only while all surviving subgroup lanes remain active.
This is test tooling, not an implemented RHI capability or automatic fallback policy.
See wgpu's [native subgroup feature contract](https://wgpu.rs/doc/wgpu/struct.Features.html#associatedconstant.SUBGROUP).

The final candidate aggregates same-group survivors with subgroup prefix/sum
operations, falls back to per-record atomics for mixed groups, and exits whole
subgroups with no survivors. It passed the full Arena CPU/GPU/Auto count/image
fixture on GTX 1660 Vulkan with zero unexplained pixels. It **failed** that fixture
on DX12: first wide-view GPU count 118,714 versus CPU 130,984. The default scalar
path then passed the complete DX12 fixture in 12.80 seconds. The candidate must
not enter production; the discrepancy is not attributed to a driver or compiler
without further isolation. Evidence:
`target/instance-subgroup-empty-{arena,dx12-arena}.log` and
`target/instance-subgroup-scalar-dx12-arena.log`.

Vulkan two-batch-wave comparisons used 300 measured frames per trial. Mean
expanded/Auto times for the scalar control were 2.589/2.612 ms wide,
1.892/1.946 ms near and 2.079/2.180 ms editor. The final subgroup candidate measured
2.591/2.612, 1.892/1.939 and 2.068/2.180 ms respectively; it did not establish a
consistent parity improvement. The earlier candidate without whole-subgroup exit
also passed Vulkan count/image validation and retained a 0.098 ms editor gap.
Logs: `target/instance-subgroup-{control-waves,empty-waves,waves,arena}.log`.
All-target Clippy for `nico-rhi-wgpu` passed after adding the manual control
(`target/instance-subgroup-clippy.log`). Production compatibility and native build
evidence remain those of the unchanged scalar renderer. Frame-time parity is open.

### Group-range dispatch and explicit plane-check experiments (2026-09-21)

A temporary reusable-page kernel dispatched by group range, rejecting unselected
groups before loading record bounds. It retained the linear kernel for other page
layouts and devices whose dispatch dimension could not hold all active groups.
Balanced X dispatches and strided traversal covered unequal ranges without moving
records or changing the page ABI. All 26 Vulkan graphics tests passed. However,
the 300-frame two-batch-wave comparison measured mean expanded/Auto completed wall
times of 2.659/2.666 ms wide, 1.928/2.012 ms near and 2.137/2.258 ms editor. This
did not establish a consistent improvement, so the additional kernel, dispatcher
and compiler entry point were removed. Evidence:
`target/instance-group-dispatch-{vulkan,waves}.log`.

A separate experiment replaced the shader's indexed six-plane local array/loop
with six explicit short-circuit calls, preserving support-point arithmetic,
margin and the original negated less-than rejection. It also passed all 26 Vulkan
graphics tests after rebuilding the restored renderer dependency. Mean
expanded/Auto times were 2.648/2.686 ms wide, 1.921/1.981 ms near and 2.133/2.245 ms
editor. It was removed because the frame comparison did not show a consistent
benefit (`target/instance-plane-checks-{vulkan,waves}.log`). An initial test attempt
had a stale compiled renderer expecting the discarded group-kernel entry point;
that attempt did not validate the plane-check experiment.

Both production sources and generated shader were restored. A freshly rebuilt
Vulkan suite passed all 26 graphics tests in 15.32 seconds
(`target/instance-group-experiments-restored-vulkan.log`); formatting and diff
checks also passed. These experiments add no retained rendering capability or
performance claim. The frame-time parity gate remains open.

### Stationary visibility and reset-copy experiment (2026-09-21)

The current same-binary stationary/moving comparisons used Windows/GTX 1660 Vulkan,
two-batch upload waves, ten warm frames and 300 measured frames in each of three
alternating trials. Both retained normal count diagnostics and native upload pacing.
Mean expanded/Auto completed wall times were:

| View | Stationary (ms) | Moving (ms) |
| --- | --- | --- |
| Wide | 2.652 / 2.579 | 2.669 / 2.684 |
| Near | 1.936 / 1.898 | 1.930 / 1.997 |
| Editor | 2.127 / 2.142 | 2.132 / 2.241 |

Stationary Auto used zero visibility dispatches after warm-up; moving Auto used
8/8/6 dispatches across 4/4/3 pages. This comparison points to changed-view work
as a significant contributor; it does not isolate compute execution from host
view preparation or establish performance acceptance for moving cameras. Evidence:
`target/instance-current-{stationary,moving}-waves.log`.

A temporary renderer experiment replaced reusable-page reset compute dispatches
with copies of zero counts and persistent indirect-argument templates, maintaining
templates on append/replacement. It reduced dispatches to 4/4/3 and passed the
full Arena CPU/GPU/Auto count and image-order fixture. However, mean expanded/Auto
times were 2.661/2.699 ms wide, 1.939/2.011 ms near and 2.124/2.258 ms editor.
The implementation was removed after that result. It did not undergo final
residency/upload-budget integration or backend acceptance and is not a production
capability. Evidence: `target/instance-copy-reset-{waves,arena}.log`. The retained
renderer continues using compute resets; frame-time parity remains unmet.

### Asynchronous count-readback control (2026-09-21)

The Arena performance fixture accepts `NICO_MEASUREMENT_NO_READBACK=1` to disable
the test device's advertised buffer-readback capability. This is a `cfg(test)`
field initialized to false; production capability reporting and sampling remain
unchanged. The image/count fixture rejects this setting because it requires count
readback. The control preserves compute culling, compaction, indirect draws,
source upload pacing and the original expanded baseline.

Two sequential same-binary Windows/GTX 1660/Vulkan runs used two-batch publication
waves, ten warm frames, 300 measured frames per trial and three alternating trials.
Mean completed wall times were:

| Readback | View | Expanded (ms) | Auto (ms) | Difference (ms) |
| --- | --- | --- | --- | --- |
| Disabled | Wide | 2.668 | 2.680 | +0.011 |
| Enabled | Wide | 2.655 | 2.691 | +0.036 |
| Disabled | Near | 1.933 | 1.996 | +0.063 |
| Enabled | Near | 1.945 | 1.994 | +0.049 |
| Disabled | Editor | 2.126 | 2.239 | +0.113 |
| Enabled | Editor | 2.134 | 2.247 | +0.113 |

Differences use unrounded means. The editor-view penalty remains essentially
unchanged; these results do not support count diagnostics as its dominant cause.
No production readback optimization or sampling reduction was made. Evidence:
`target/instance-no-readback-waves.log` and
`target/instance-readback-control-waves.log`. The default full Arena count/image
fixture passed (`target/instance-readback-control-arena.log`), as did all-target
Clippy for `nico-rhi-wgpu` (`target/instance-readback-control-clippy.log`). These
elapsed comparisons are not isolated CPU or GPU execution measurements. Frame-time
parity remains unmet.

### Native draw-binding validation (2026-09-21)

The current isolated editor build
`52d5c6799e109ac7a4f04292dbe5396bb2925633866cbdb590f482da484977cc`
ran as PID 26276, instance `22020-18d6f3821187eef4-28`, session
`ef75aac2-405e-41bd-9b23-847785827e63`. It used the existing
`target/instance-native-fixture-1024` project on GTX 1660 Vulkan at 1440x900.
MCP controlled only this owned editor; no project edits were made.

Warm startup took 921.506 ms, including 217.479 ms scenery preparation. First
scene presentation API success was 1064.375 ms; this is not GPU completion or
desktop scanout. Model/texture/generated cache hits were 13/12/2, with zero
imports/rebuilds. Refresh command 2 completed in 1.592 ms, including 1.140 ms
source checking and zero imports/rebuilds.

At camera yaw 0.5, pitch 0.4, distance 22 with visual time paused, Auto view 26
reported 54,320 direct instances and the same-camera GPU sample from view 25
reported 35,690, totaling 90,010. CPU view 29 reported 90,010 directly. GPU view
40 retained the view-33 sample of 90,010 visible out of 111,354 candidates across
46 indirect draws. Warm views had zero immutable source, visibility-source,
foliage and CPU-visible-record uploads, and zero visibility dispatches on reuse.
The sampled GPU view and current host/prepared view identities remain distinct.

Pan command 3 to [2000,0,0] evicted all 64 chunks and released all GPU instance
residency. Command 4 returned to [0,0,0], restoring 128 batches, 326,781 records
and 54,918,664 decoded bytes in 64 chunks, with no errors or pending workers.
Reentry view 67 reused 46 GPU batches with zero warm uploads; its view-65 sample
again reported 90,010 visible instances. This full-release cycle does not replace
the earlier partial-range reclamation tests or repeated native partial cycles.

Inspected captures came from Auto/CPU/GPU/reentry frames 193/561/947/1443 of this
same process. They show the expected vegetation, road, rocks and editor UI; no
native pixel-exact comparison or user-watched demonstration is claimed. Files:
`target/instance-native-binding-{auto,cpu,gpu,reentry}.png`. All 59 retrieved
diagnostic events had zero errors, dropped records or truncation. Stop was accepted
at host frame 1723; bridge state subsequently became stopped and PID absence was
confirmed. The owned window is closed. Structured evidence is
`target/instance-native-binding-evidence.json`, with `.out.log`/`.err.log` beside it.
This closes the current draw-binding build's scoped native validation gap, but
does not establish frame-time parity or general backend compatibility.

### Workspace refresh and completion audit (2026-09-21)

The current retained renderer, restored visibility shader and optional benchmark
batch-size control passed `cargo test --workspace --target-dir
target/instance-streaming-validation`: 493 passed, 39 ignored, zero failed and
zero filtered out across 53 unit/integration/doc-test result summaries. The first
sandboxed run stopped at the compiler-descendant cancellation test after Windows
denied child termination. The full rerun outside the sandbox passed that test and
the rest of the workspace; it did not filter out the failure. Evidence:
`target/instance-current-workspace-tests{,-elevated}.log`. Ignored graphics and
measurement fixtures remain separate evidence, not part of those 493 passes.
Workspace all-target Clippy passed on the same isolated target directory
(`target/instance-current-workspace-clippy.log`).
Generated shader artifact checking passed after restoration of the visibility
shader (`target/instance-current-shader-check.log`).
The isolated `nico-editor` build also passed (`target/instance-current-native-build.log`);
its executable SHA-256 is
`52d5c6799e109ac7a4f04292dbe5396bb2925633866cbdb590f482da484977cc`.
This build result is not native execution evidence.

The plan audit retains all original milestones and its no-LOD scope. Immutable
records, direct/fallback/GPU rendering, foliage, placement caching and bounded
streaming have the scoped unit/GPU/native evidence mapped above. Native cold and
warm opens, edits, refreshes and influence captures predate the latest draw-binding
cache; they do not prove native validation of that change. The current full Arena
graphics fixture covers counts and explained image differences but excludes editor
UI and native host lifecycle. Native validation was therefore outstanding at this
audit; the subsequent native draw-binding validation above supplies that evidence.
Frame-time parity is contradicted by the moving-camera comparisons and remains
unmet. These checks do not complete the goal or replace the final requirement-by-
requirement audit after the outstanding work is finished.

### Workgroup reservation and batch-size experiments (2026-09-21)

A local shader experiment reserved visible output slots once per 64-thread
workgroup when its surviving records shared one group; mixed workgroups retained
per-record atomics. It passed all 26 Vulkan graphics tests, including replacement
ranges, selected groups and image fixtures. The focused 262,144-record measurement
showed selected-group completed wall times of 0.181–0.185 ms versus the preceding
0.187–0.198 ms control, but all-group times remained 0.198–0.201 ms versus
0.190–0.202 ms. The full moving-camera comparison did not establish a consistent
benefit. Mean expanded/Auto times were 2.645/2.674 ms wide, 1.922/1.992 ms near
and 2.117/2.252 ms editor. The experiment was removed, and all shader artifacts
were regenerated from the restored source. A restored-source repeat passed with
means 2.659/2.686, 1.918/2.020 and 2.132/2.236 ms, respectively. Evidence:
`target/instance-workgroup-{baseline,kernel,vulkan,waves,restored-waves}.log`.
This rules out retaining this particular implementation as a demonstrated parity
fix; it does not establish isolated GPU execution costs.

The existing Arena comparison now accepts the test-only
`NICO_MEASUREMENT_COALESCE_GRASS` record limit. It checks shared mesh/material,
foliage profile and distance, then repacks the same records outside timed frames.
The expanded baseline keeps the original chunks. This changes batch bounds and
GPU allocation cohorts as well as draw count; it does not isolate draw-call cost
or model production streaming, source uploads or eviction. Production batches
and source ownership are unchanged. With 300 frames per trial and two-batch upload
waves, these Windows/Vulkan results were observed:

| Record limit | View | Expanded mean (ms) | Auto mean (ms) | Instance draws |
| --- | --- | --- | --- | --- |
| 16,384 | Wide | 2.652 | 2.624 | 19 |
| 16,384 | Near | 1.918 | 1.959 | 13 |
| 16,384 | Editor | 2.127 | 2.223 | 16 |
| 32,768 | Wide | 2.654 | 2.627 | 10 |
| 32,768 | Near | 1.925 | 1.975 | 7 |
| 32,768 | Editor | 2.102 | 2.225 | 9 |

Evidence: `target/instance-coalesced-{16384,32768}.log`. Both fixtures passed
their warm-upload assertions; these performance controls are not image-parity
tests. Neither size closed the editor-view gap, so no production batch-size change
was made. All-target Clippy for `nico-rhi-wgpu` passed after the fixture addition
(`target/instance-layout-control-clippy.log`). Frame-time parity remains open.

### Instance draw-binding cache (2026-09-21)

The retained renderer avoids rebinding identical pipelines, shared resources and
prototype geometry within the opaque instance loop, while preserving draw order.
A mixed static/foliage direct/indirect fixture checks three batch orders against
CPU-reference pixels and requires zero repeated source/visibility uploads. All
26 non-measurement GPU tests passed on Vulkan and DX12 on the adapter above
(`target/instance-binding-final-{vulkan,dx12}.log`). Workspace all-target Clippy
also passed (`target/instance-binding-final-clippy.log`). Shader artifact checking
passed (`target/instance-binding-final-shader-check.log`), as did workspace
formatting and `git diff --check`. These changes postdate
the native instance 27 validation below; refreshed native validation is pending.

The retained build's full Arena fixture passed in 7.45 seconds with exact
CPU/GPU/Auto visible-count agreement: 130,984 wide, 59,376 near and 89,659 editor.
All image differences were explained by the fixture's permitted equal-depth
record-order alternatives, with zero unexplained pixels. Evidence:
`target/instance-binding-final-arena.log`.

The two-batch publication-wave comparison used 300 measured frames after ten warm
frames, three alternating trials and the native upload budget. Mean completed
wall times were expanded/Auto 2.666/2.687 ms wide, 1.916/1.984 ms near and
2.139/2.254 ms editor (`target/instance-binding-final-waves.log`). This does not
establish a consistent improvement over the preceding revision and does not
meet frame-time parity. A trial that replaced the storage vertex shader's offset
lookup with a supplied source offset also showed no consistent benefit and was
removed; the retained shader still reads the page's group offset table.

A forced-CPU control of the same moving-camera comparison passed, but completed
in 14.641–14.742 ms wide, 7.999–8.042 ms near and 9.179–9.190 ms editor. Its
submission scopes alone took 13.028–13.058, 7.119–7.180 and 8.094–8.121 ms,
respectively (`target/instance-binding-cpu-waves.log`). These are elapsed scopes,
not CPU execution samples. CPU compaction is therefore not a parity remedy for
this workload; the result does not isolate the much smaller GPU-path overhead.
The CPU fallback remains supported. Final workspace tests, native validation
and the full original-plan completion audit remain pending; acceptance is open.

### Selected groups and native reclamation (2026-09-21)

Each page view now includes a 512-bit selection of groups actually using indirect
draws. Excluded groups receive zero counts and skip per-record visibility tests.
The selection is part of the cache key, so removing and restoring a live chunk at
the same camera must recull, while an identical repeat reuses output. The view ABI
and charged uniform allocation increase from 96 to 160 bytes per page. Source upload
allowances remain unchanged because these are dynamic view writes.

The new graphics test crosses group bit boundaries 0/31/32/127/128/511, alternates
all/subset/empty/all selections, and checks counts, IDs and arguments for packed,
partitioned and reusable pages submitted before CPU waits. The dense renderer test
changes selection at one camera without new source uploads. A test-only per-device
fault injection rejects the second new selection binding: old residents retain
their pixels/allocation, pending buffers are discarded, and retry uploads the full
cohort into existing capacity. This hook is absent from non-test builds.
Vulkan passed 25 graphics tests; the allocation-failure extension subsequently
passed its focused fixture. DX12 then passed all 25 tests including that extension.
The full Arena count/image-order fixture passed with zero unexplained pixels.
Renderer unit tests, workspace all-target Clippy and shader artifact checks passed.
Evidence: `target/instance-group-mask-{vulkan-final,allocation-failure,dx12-final,arena,unit,clippy-final,shader-check}.log`.

A 262,144-record/64-group reusable-page measurement completed in 0.177–0.186 ms
with 16 groups selected, versus 0.191–0.198 ms for all 64. This is elapsed completed
compute submission time, not a GPU timestamp or full-frame result. The Arena
comparison additionally supports `NICO_MEASUREMENT_ALL_GROUPS=1`, which modifies
only the test's shader selection helper while retaining the same renderer ABI and
ownership. Selected/control/selected runs of 300 measured frames per trial did not
establish a consistent full-frame benefit: latest Auto-minus-expanded means remain
0.036 ms wide, 0.084 ms near and 0.112 ms editor. Parity remains **not achieved**.
Evidence: `target/instance-group-mask-{kernel,waves,control,selected-repeat}.log`.
The subsequent all-available run (`target/instance-group-mask-all.log`) retained
4/4/3 dispatched pages and measured mean expanded/Auto times of 2.639/2.677 ms wide,
1.900/2.007 ms near and 2.100/2.234 ms editor. This motivated the subsequent
instance draw-binding cache described below; those timings predate that change.

Native validation used build `a7f73ce284819a6a69ffd056159bd879d2e10c16d2b96aca3cc2f263a670a28b`,
PID 25748, instance `22020-18d6f3821187eef4-27`, session
`f9845411-7824-488a-a769-440749583368`, at 1440x900 on the recorded Vulkan adapter.
Only the owned `target/instance-native-fixture-1024` editor was controlled:

- Warm startup took 920.131 ms, scenery preparation 218.184 ms, and first scene
  presentation API success 1065.383 ms. Imports/rebuilds were zero. Unchanged refresh
  command 11 took 1.606 ms, including 1.127 ms checking scenery sources, with zero
  imports/rebuilds. These are scope elapsed times, not CPU/GPU execution times.
- The editor camera dispatched three shared pages/six dispatches on its first
  changed view. Warm Auto reported 54,320 direct instances plus a separate recent
  GPU sample of 35,690, totaling 90,010. CPU reported 90,010; forced GPU sampled
  90,010 of 111,354 candidates at prepared view 81. Warm source, retirement and
  visible-record uploads were zero; GPU readback failures were zero.
- Absolute pan `[560,0,0]` evicted 24 chunks while 40 remained. Three partial
  eviction/reentry cycles restored 64 chunks, 128 batches and 326,781 records,
  with no pending workers or streaming errors at each settled observation.
  Retained instance bytes were 28,112,432 while partially evicted and stabilized
  at 36,043,552 after each reentry. Each last upload wave reported 12 retirement
  bytes. Initial residency before these cycles was 33,409,584 bytes: this is a
  bounded reuse observation, not optimal packing or zero capacity growth.
- Pan `[2000,0,0]` released all provider and GPU instance residency. Returning
  to `[0,0,0]` restored all chunks and records without streaming errors.
- Inspected Auto/reentry/CPU/GPU captures came from frames 397/1192/2112/2980.
  They show the expected scene; native captures were not tested for exact pixel
  equality and are separate from the status/count sample frames. This was not a
  user-watched demonstration. All 75 diagnostic events read before shutdown had
  no errors, eviction or truncation. Stop was accepted at host frame 4599; bridge
  terminal state and absence of PID 25748 independently confirmed exit.

Native evidence: `target/instance-native-group-mask-evidence.json`,
`target/instance-native-group-mask-{auto,reentry,cpu,gpu}.png`, and the corresponding
`.out.log`/`.err.log`. The fault-injection test addition postdates this executable;
it does not change the non-test renderer. Final workspace-wide tests and the full
original-plan audit remain pending alongside frame-time parity.

### Renderer range reclamation before selected groups (2026-09-21)

Renderer batches now hold strong interval/group reservations; the page pool retains
weak reservations. Planning coalesces the complement of live intervals and reuses
group slots before allocating another bounded page. Selection bindings are created
before writes to an existing page. Retired groups are disabled before their former
record space is reused, preventing stale records from writing into a new live range.
Dynamic retirement writes are reported as `visibility_retirement_upload_bytes` and
are separate from immutable pacing (144 bytes/record plus 40 bytes/group).

The dense renderer fixture replaces one 1,024-record chunk with 512, 1,536, 256,
1,280 and 1,024 records while its neighbor stays live. Captures and exact retained
allocation charges agree; warm repeats upload no source or retirement bytes.
Low-level coverage moves a live range across a disabled neighbor. Pure planner
tests cover coalescing, fragmentation and an unsuccessful transaction followed by
a successful smaller request. At this step, actual binding-allocation failure had
not yet been fault-injected; the selected-group section above records that extension.
The allocator requires an entire incoming cohort to fit one existing page and still
uses conservative admission charges; this is not an optimal packing claim.

`target/instance-range-ownership-vulkan.log` passed all 24 graphics tests;
`target/instance-range-ownership-unit.log` passed 25 renderer tests (one ignored),
and the MCP-enabled ops suite passed 29 tests. Workspace check and all-target
Clippy passed. After removing the atomic experiment below, the retained code also
passed all 24 DX12 graphics tests (30.24 s), the full Arena count/image fixture
(7.45 s), 25 renderer tests (one ignored), formatting and workspace all-target
Clippy. Evidence: `target/instance-range-reuse-{dx12,arena,unit,clippy}.log`.
Native range-reuse validation was still pending at this step; see the current
instance-27 evidence above.

The initial current two-batch-wave timing run
(`target/instance-reuse-parity-waves.log`) measured mean completed frames of
2.753/2.805 ms expanded/Auto wide, 1.865/1.971 ms near and 2.035/2.140 ms editor.
The all-available run (`target/instance-range-reuse-all.log`) measured
2.723/2.758 ms wide, 1.884/2.014 ms near and 2.057/2.133 ms editor, with the same
4/4/3 dispatched-page counts. These short sequential runs are elapsed completion
measurements, not isolated GPU timings. Frame-time parity remains **not achieved**.

A subsequent experiment used indirect argument counts to eliminate one atomic
increment per survivor. Both backends passed 24 graphics tests, and the full Arena
CPU/GPU/Auto fixture passed its count and image-order checks. However, repeated
moving-frame measurements did not establish a consistent benefit; the experiment
was removed. Its evidence is `target/instance-single-atomic-{vulkan,dx12,arena,waves,waves-repeat}.log`;
those runs do not describe the retained shader. Further investigation should check
work spent culling page groups that have no indirect draw in the current view.

### Replaceable group intervals before renderer integration (2026-09-21)

This historical step introduced `with_reusable_capacity` and `replace_group`;
renderer integration is recorded above.
Reusable pages require contiguous groups ordered by group ID. Each group's current
record start and exclusive end live in existing offset/cursor scratch words; the
cull shader skips stale records outside that interval. Shrinking or disabling a
group therefore needs no stale-tail clear. Allocation size and shader binding
layout remain unchanged; initializing/appending a reusable group adds four upload
bytes for its range end. Replacement uploads 32 bytes per supplied bound plus 24
bytes of group metadata and interval endpoints.

Replacement validates populated bounds, group index, local group references and
record capacity before writes. The owner must prove interval ownership and invalidate
visibility after replacement even when active prefix counts stay unchanged. The
renderer at that step still used append-only pages; its free-range allocator and
corresponding upload accounting had not switched to reusable pages.

The extended GPU fixture rejects unordered append without changing active counts,
shrinks a group with a still-visible stale tail, disables its neighbor, and reuses
their adjacent space. Earlier submitted readback retains the shrunk/disabled result;
the later result contains the new IDs and preserves the other groups. Invalid
capacity, group and local-reference replacements are rejected. All 24 GPU
regressions passed on Vulkan (28.17 s) and DX12 (30.23 s); 23 renderer tests,
targeted all-target Clippy and shader artifact checking passed.
Full Arena count/image validation also passed (7.82 s). Evidence:
`target/instance-reusable-ranges-{test,unit,vulkan,dx12,clippy,shader-check,arena}.log`.
This is correctness groundwork for reclamation, not frame-time acceptance evidence.

### Sharing capacity across upload waves

The renderer now selects a live page with sufficient unused record/group capacity
before allocating another page. Its page index holds weak references; group owners
retain the page and shared visibility key. Dense cohorts reserve up to four times
their rounded record count, capped at 65,536 records of spare-policy capacity, and
at least 64 groups. Larger required cohorts use their required size. Cohorts below
1,024 records retain exact allocation. Device limits and the available residency
budget can reduce a reservation to exact size.

Admission charges the complete allocation once plus per-group selection storage,
after protecting other pending chunks' minimum allocations. All new selection
bindings must allocate successfully before an existing page is appended. Successful
append invalidates its shared camera key; GPU reuse counters are computed afterward,
so previously visited groups are not incorrectly reported as reused. Source buffers
and prior group addresses remain unchanged. Partial retirement retains charged
capacity; last-owner retirement releases the page. Individual range reclamation,
native validation of this integration and final performance acceptance remain open.

The renderer regression forces two 1,024-record batches through separate upload
allowances into one 4,096-record / 64-group allocation, verifies its exact retained
byte charge, append invalidation, camera changes, source-upload reuse, partial
retirement and final release. All 24 GPU regressions passed on Vulkan (27.35 s)
and DX12 (29.06 s), alongside 23 renderer tests and targeted all-target Clippy.
Full Arena CPU/GPU/Auto count and controlled draw-order image checks passed.
Evidence: `target/instance-shared-capacity-{unit,clippy,vulkan,dx12,arena}.log`.

Both publication patterns now dispatch 4/4/3 pages (8/8/6 dispatches) in wide,
near and editor views, compared with 19/12/9 pages for the previous two-batch-wave
path. Latest three-trial Vulkan GTX 1660 completed-frame wall measurements at
840x764, with the native 8 MiB upload allowance:

| Publication | View | Auto ms | Expanded ms |
| --- | --- | --- | --- |
| All available | Wide | 2.693–2.923 | 2.604–2.753 |
| All available | Near | 1.927–1.986 | 1.837–1.907 |
| All available | Editor | 2.119–2.132 | 2.026–2.057 |
| Two-batch waves | Wide | 2.646–2.782 | 2.607–2.775 |
| Two-batch waves | Near | 1.912–1.953 | 1.848–1.883 |
| Two-batch waves | Editor | 2.117–2.209 | 2.016–2.067 |

Warm upload assertions passed. Page fragmentation from publication waves is reduced,
but frame-time parity is not established. These are elapsed wall measurements, not
GPU execution times. Logs: `target/instance-shared-capacity-{all,waves}.log`.

### Appendable visibility-page foundation

The low-level partitioned page API now separates fixed allocation capacity from
active record/group prefixes. `with_partitioned_capacity` reserves stable output
addresses; `append` uploads only new bounds, metadata and fixed-range offsets.
Validation rejects record/group overflow and invalid local group references before
writes. The owner must serialize append with recording, submit earlier consumers
first, and invalidate cached visibility after active counts change. The shader keeps
the 96-byte view layout, using active counts to avoid reading unused allocation space.

At this foundation revision, streaming still created one page per admitted upload
cohort; the subsequent integration is recorded above. The foundation alone is not
evidence of frame-time parity.

The new native-GPU fixture submits the initial page, appends before waiting for that
submission, and reads the original and appended outputs independently. It verifies
stable existing addresses, visible IDs and indirect arguments against the CPU
reference, an empty appended group, invalid group references, record/group capacity
rejection, an all-culled camera, and visibility restored after the camera returns.
All 24 GPU regressions passed on Vulkan (25.64 s) and DX12 (27.57 s); 23 renderer
tests passed (one measurement ignored), targeted all-target Clippy passed, and
`nico-shaderc --check` verified regenerated artifacts. Full Arena CPU/GPU/Auto
count/image validation also passed with zero unexplained pixels against controlled
CPU draw-order references. Evidence:
`target/instance-append-foundation-{test,unit,vulkan,dx12,clippy,shader-check,arena}.log`.

### Indirect-validation isolation

A bounded release fixture run disabled `WGPU_VALIDATION_INDIRECT_CALL` only in its
child shell to isolate wgpu's indirect validation cost. Native defaults and production
code remain unchanged. The dependency's local `wgpu-types 30.0.1/src/instance.rs`
documents both argument-range protection and DX12 built-in adjustments supplied by
this validation, so disabling it is not an accepted production optimization.

With two-batch upload waves, Auto still measured 2.764–2.856 ms versus expanded
2.600–2.722 wide, 1.965–2.015 versus 1.853–1.886 near, and 2.133–2.179 versus
1.999–2.065 at the editor camera. Page/dispatch counts remained 19/38, 12/24 and
9/18. This isolation does not establish parity and rules out indirect validation
as the sole explanation for the residual regression. Warm upload assertions passed;
the run is diagnostic evidence, not normal-configuration acceptance evidence.
Log: `target/instance-indirect-validation-isolation.log`.

### Device-capacity calculation

Mode validation formerly repeated a binary search for every batch's GPU record
capacity. After checking single-record capability support, it now computes the
same capacity from source-buffer size, storage-binding size and dispatch width.
For a single group and at least one record, the 112-byte source stride dominates
both 32-byte bounds and the `8*N+32` visibility output allocation. The existing
upload allowance and aggregate segment budgets still apply.

A 1,000-iteration public mode-validation benchmark over the actual 128-batch Arena
scene measured 12.009–12.414 microseconds per call before, and 3.153–3.186 after.
Rendering validates both authored and segmented scenes, so this removes repeated
CPU work without caching device limits or weakening descriptor validation.
The focused capacity test checks 294 combinations of buffer, storage and dispatch
limits, proving the chosen record count fits and its successor does not.

Two-batch-wave frame measurements remain above expanded geometry: Auto
2.833–2.923 ms versus 2.634–2.695 wide, 2.001–2.044 versus 1.846–1.861 near,
and 2.159–2.171 versus 2.009–2.047 at the editor camera. Page and dispatch counts
are unchanged from the whole-batch bypass. Frame-time parity remains open.
Evidence: `target/instance-mode-validation-before.log` and
`target/instance-mode-validation-after-waves.log`. The added timing is confined to
the ignored measurement fixture; production has no new profiling instrumentation.

All 23 renderer tests passed (one ignored measurement), targeted all-target Clippy
and workspace type checking passed, and all 23 GPU regressions passed on Vulkan
(24.87 s) and DX12 (26.52 s). Evidence:
`target/instance-capacity-{unit,clippy,workspace-check,vulkan,dx12}.log`.

### Whole-batch visibility bypass

Auto now draws an already resident source directly when its complete conservative
bound is inside the clip volume and draw-distance sphere and the direct pipeline
is available. Boundary batches and forced GPU mode retain indirect visibility.
Direct draws neither commit GPU visibility cache keys nor enter GPU count readback;
their known counts use `submitted_instances`. Source upload pacing and ownership
are unchanged. This is rendering-path selection, not mesh LOD.

The grass fixture identified 13 fully visible batches / 57,912 records wide,
6 / 27,864 near, and 11 / 53,688 at the editor camera. With two-batch publication
waves, dispatched pages fell from 23 to 19 wide, 14 to 12 near, and 14 to 9 at the
editor camera (38, 24 and 18 dispatches respectively). The all-resident fixture
uses 4, 4 and 3 dispatched pages. Three rotated Vulkan GTX 1660 trials at 840x764
with the native 8 MiB source-upload allowance measured:

| Publication | View | Auto completed wall ms | Expanded completed wall ms |
| --- | --- | --- | --- |
| All available | Wide | 2.698–2.857 | 2.730–2.839 |
| All available | Near | 1.987–2.012 | 1.836–1.886 |
| All available | Editor | 2.108–2.180 | 2.001–2.078 |
| Two-batch waves | Wide | 2.819–3.018 | 2.617–2.807 |
| Two-batch waves | Near | 2.042–2.073 | 1.857–1.867 |
| Two-batch waves | Editor | 2.169–2.189 | 2.013–2.057 |

All warm immutable-source and visibility-source upload assertions passed. These
are elapsed completed-frame wall measurements, not GPU execution times. Reduced
dispatches have not closed frame-time parity; the explicit user requirement remains
open. Logs: `target/instance-moving-full-bounds{,-waves}.log`.

Validation passed: 16 presentation tests, 22 renderer tests (one measurement
ignored), targeted all-target Clippy, and all 23 GPU regressions on Vulkan
(24.85 s) and DX12 (26.69 s). The grouped lifecycle fixture now also covers
independent upload-wave pages and Auto direct/indirect/direct/indirect camera
transitions, including zero reupload, correct cache reuse, captures and retirement.
Forced-indirect fixtures explicitly select GPU mode so their coverage still
exercises the indirect path. Full Arena CPU/GPU/Auto counts remain 130,984 wide,
59,376 near and 89,659 at the editor camera; image differences have zero unexplained
pixels against the controlled CPU draw-order references. Evidence:
`target/instance-full-bounds-{unit,clippy,vulkan,dx12,arena-parity}.log`.
Full workspace test results recorded below predate this bypass. Native validation
of the bypass and device-capacity calculation is recorded next.

A preceding camera-upload experiment measured one 96-byte write at 84.907–85.890
microseconds completed wall time versus 114.930–142.337 for 16 writes. Sharing view
buffers between equal page layouts passed its lifecycle test but did not establish
a clear Arena frame-time improvement; that production experiment was removed.
The ignored upload-only benchmark remains reproducible with
`gpu_visibility_camera_upload_measurement`; logs are
`target/instance-camera-upload-measurement.log` and
`target/instance-moving-shared-view-waves.log`.

### Native whole-bound and capacity validation

The isolated editor build
`3dd250ef1e1528011efe68ac104c15d0e35782f33b007e148ebc47650c39ed3d`
ran as PID 29416, instance `22020-18d6f3821187eef4-26`, process session
`f9f5b6f9-6c87-40dc-9d43-5c0ac28a3fa4`, on GTX 1660 Vulkan at 1440x900.
The existing fixture `target/instance-native-fixture-1024` retained rock-west at
`[1, 1.5, 5]`; this run did not edit or save project content.

Warm startup completed in 925.599 ms, scenery preparation in 221.178 ms, with
13/12/2 model/texture/generated cache hits and zero imports. First scene presentation
API success was 1066.005 ms from loader start. Refresh command 5 completed in
3.120 ms with zero imports or rebuilds. These are elapsed wall times.

At yaw 0.5, pitch 0.4, distance 22, paused with no active influence fields:

| Mode | Prepared view / host frame | Direct instances | GPU visible / candidates | Indirect draws |
| --- | --- | --- | --- | --- |
| Auto | 25 / 480 | 54,320 | 35,690 / 56,880 | 12 |
| CPU | 48 / 1234 | 90,010 | Not used | 0 |
| GPU | 57 / 1578 | 0 | 90,010 / 111,354 | 46 |

Auto and GPU sample identities matched their respective prepared-view counters.
All three warm views had zero source, visibility-source, compacted-record and
foliage uniform uploads. Auto initially dispatched 8 pages / 16 dispatches on the
changed camera (view 19), then zero on unchanged views. Auto's 45 submitted draws
comprised 33 direct draws and 12 indirect draws.

Pan command 2 set `[2000,0,0]`, evicting all 64 chunks and releasing GPU instance
residency to zero. Command 3 set `[-2000,0,0]` (pan is absolute, not additive),
remaining outside the scene. Command 4 restored `[0,0,0]`: all 64 chunks / 128
batches / 326,781 records returned with zero streaming errors. CPU mode was briefly
selected while outside the scene, then Auto restored before reentry.

Inspected captures from this same process show consistent vegetation, road and rock
composition: Auto frame 256, CPU frame 1134, GPU frame 1580, saved as
`target/instance-native-capacity-{auto,cpu,gpu}.png`. Captures and status are different
frames; this was automated rendered-output validation, not a user-watched demo or
pixel-exact native comparison. All 47 retained diagnostic events were read with zero
errors, eviction or truncation. Stop was accepted at frame 2122, bridge state became
stopped, and PID absence was independently verified. The existing bridge remained
running.

Evidence: `target/instance-native-capacity{,-final}-evidence.json`,
`target/instance-native-capacity.{out,err}.log`, and
`target/instance-capacity-native-build.log`. During this run, the MCP Auto policy
description was updated to mention whole-bound bypass; that text-only correction
postdates the tested executable. Its two rendering contract tests and client-feature
all-target Clippy passed (`target/instance-native-capacity-policy-{tests,clippy}.log`).
Frame-time parity and the final full-workspace completion audit remain open.

### Instance residency lookup update

The instance batch and prototype-mesh searches now compare `Weak::as_ptr()` with
`Arc::as_ptr()` instead of constructing a temporary weak reference for every
candidate. This preserves allocation identity and ownership without dereferencing
pointers. A focused release benchmark of 64 searches in 128 residents measured
57.252–57.327 microseconds with temporary weak references and 3.371–3.397 with
pointer comparisons, in three alternating trials. This is a lookup-only result,
not a frame-time claim. Reproduce with `cargo test -p nico-render --release
resident_identity_lookup_measurement -- --ignored --nocapture`; local evidence is
`target/instance-resident-lookup-measurement.log`.

All 22 renderer tests passed (one manual measurement ignored), targeted all-target
Clippy passed, and all 23 GPU regressions passed on Vulkan (22.20 s) and DX12
(23.39 s), including replacement, reentry, pacing and retirement. Logs use
`target/instance-identity-{render-tests,clippy,vulkan,dx12}.log`. No shader ABI or
native control behavior changed.

The moving all-resident fixture measured Auto 2.662–2.807 ms versus expanded
2.676–2.759 ms (wide), 1.974–1.975 versus 1.860–1.870 ms (near), and 2.132–2.147
versus 2.011–2.029 ms (editor). Two-batch waves measured Auto 2.840–2.963,
2.047–2.078 and 2.228–2.253 ms versus expanded 2.600–2.718, 1.874–1.912 and
2.015–2.074 ms respectively. All warm source-upload assertions passed. The isolated
lookup win does not establish frame-time parity. Evidence:
`target/instance-moving-identity{,-waves}.log`.

A threshold-zero two-batch-wave trial retained the same page/dispatch counts and
did not establish a clear frame-time win (`target/instance-moving-identity-gpu0-waves.log`).
The native 1024-record policy remains unchanged. The user explicitly chose to keep
pursuing frame-time parity after reviewing the residual 0.16–0.25 ms mean overhead;
performance acceptance remains open rather than accepting that overhead.

### Four-stage visibility update

Superseded for renderer-owned immutable pages by the fixed-range path below;
the general tightly packed visibility constructor retains this implementation.

The current multi-group kernel combines indirect argument finalization with its
prefix scan. Counts and offsets are already available there; scatter does not
change them. Rendering still follows scatter in submission order. This removes
one dispatch per multi-group page without changing the output ABI, bounded counts,
or the two-dispatch single-group path. All 23 GPU tests passed on Vulkan (22.41 s)
and DX12 (21.89 s), including empty/reset, 512-group indirect arguments, grouped
lifetime, camera changes and image-reference fixtures. Slang generation and
artifact checking passed. These results supersede the five-stage implementation;
earlier native measurements below retain their historical dispatch counts.
Targeted all-target Clippy for render/wgpu/shaderc passed; the publication-wave
fixture also passed its subsequent wgpu Clippy run, formatting and diff checks.

The moving benchmark (`target/instance-moving-four-stage.log`) now reports 16
dispatches across 4 pages for wide/near, and 18 across 5 pages for editor. Auto
completed wall means were 2.756–2.954 ms versus expanded 2.597–2.768 ms (wide),
2.007–2.120 versus 1.799–1.872 ms (near), and 2.186–2.247 versus 2.017–2.082 ms
(editor). The residual gap remains; fewer commands alone are not acceptance.

`NICO_MEASUREMENT_UPLOAD_WAVE_BATCHES=2` publishes two immutable batches per
preparation before timing, clearing prior-view residency for each camera scenario.
It keeps source generation/upload work outside the timed 30 warm frames and still
asserts zero warm source uploads. This deterministic publication sequence models
small cohorts, not measured native worker timing. The run
`target/instance-moving-four-stage-waves.log` used 23 pages/78 dispatches (wide),
14/44 (near), and 14/46 (editor). Auto means were 3.031–3.427, 2.084–2.128 and
2.266–2.388 ms respectively; expanded references were 2.642–2.710, 1.809–1.930 and
2.012–2.056 ms. Small publication waves remain a relevant optimization case.

### Fixed-range immutable visibility

Renderer-owned pages now reserve disjoint ID ranges from immutable group capacities
at upload time. Each frame resets counts/arguments, then culls and compacts directly
into those ranges in a second dispatch. Group tails are unused and never drawn;
total output allocation is unchanged. Source pacing and split limits charge the
additional four-byte offset upload per group (144 bytes per source record plus
36 bytes per batch, including source, visibility metadata and selection).
The general tightly packed constructor still runs scan/scatter. Both constructors
are tested with interleaved groups, empty groups/pages, 512 groups, camera cuts,
reset, IDs, counts and indirect arguments. Grouped renderer retirement/reuse and
the paced-source regression cover the production constructor.

All 23 GPU tests passed on Vulkan (23.35 s) and DX12 (23.26 s); all 22 renderer
unit tests passed. Targeted all-target Clippy and Slang generation/check passed.
Evidence: `target/instance-partitioned-{vulkan,dx12,clippy,shader-check}.log`.
Full Arena comparison initially failed because reordered CPU references were
captured after only one upload-paced frame. The fixture now waits for deferred
uploads to finish and requires the full expected visible count before each
reference capture. With that stronger check, all three views pass exact counts
(130984/59376/89659), deterministic CPU repeats and exact CPU-order pixel matches
with zero unexplained colors. Evidence:
`target/instance-partitioned-arena-parity-settled.log`; the failed preliminary run
is retained as `target/instance-partitioned-arena-parity.log`.

The all-resident moving fixture uses 8 dispatches for wide/near and 10 for editor.
Auto completed wall means were 2.722–2.850 versus expanded 2.614–2.744 ms (wide),
1.944–1.959 versus 1.845–1.882 ms (near), and 2.114–2.149 versus 2.036–2.060 ms
(editor). Two-batch waves use 46/28/28 dispatches: Auto 2.889–2.989, 2.045–2.084,
and 2.229–2.265 ms versus expanded 2.615–2.768, 1.863–1.922, and 2.006–2.052 ms.
Warm source uploads remain zero in both fixtures. Logs:
`target/instance-moving-partitioned.log` and `target/instance-moving-partitioned-waves.log`.
The residual performance gate remains open; native validation of this path is
recorded below.

Latest isolated native build `98e17401813f0010339939c3c22cd1d0bb11bbef23b0c62a0c1d86e27074e099`
ran as PID 25980, bridge instance `22020-18d6f3821187eef4-25`, process session
`0c9d7875-f629-465b-b8f5-543018177797`. Warm startup took 913.681 ms with zero
imports (13 model, 12 texture and 2 scenery hits); first scene presentation API
success was 1062.952 ms. Refresh command 7 took 1.671 ms with no imports/rebuilds.
The isolated fixture still has 326,781 records in 64 chunks. Its rock-west edit
from x=0 to x=1 was saved and reloaded successfully; the actual game was not edited.

Camera prepared view 19 used 11 pages/22 dispatches and zero source uploads.
Forced unchanged-view preparation 20 had zero dispatches and zero uploads.
Far pan evicted all 64 chunks and dropped retained instance bytes to zero (view 21).
Return restored all chunks without errors. After edit/save/reload, fresh GPU sample
81 reported 90,010 visible records across 46 indirect draws; CPU prepared view 83
submitted the same 90,010. These are identified, separately sampled views, not a
claim that status and capture refer to one frame. Subsequent viewport statistics
at shutdown are not used for that comparison.

Captures inspected from this process: initial frame 209, settled edited GPU frame
1135 and CPU frame 1677. All show vegetation and rocks. Frame 702 captured the edit
transition with grass temporarily absent; it is retained but not used as settled
visual acceptance. PNGs are `target/instance-native-partitioned-{start,transition,gpu,cpu}.png`.
The editor stopped through MCP at host frame 2193 and PID absence was verified.
Raw evidence: `target/instance-native-partitioned-evidence.json` and
`target/instance-native-partitioned-final-evidence.json`. This was automated capture
validation, not a user-watched demonstration or a native frame-time measurement.

Workspace tests were refreshed on the fixed-range revision. The initial run stopped
at `nico-launch`'s compiler-descendant cancellation test when Windows denied child
termination. That exact test passed separately with elevated execution (3.48 s).
The workspace rerun excluding only that test completed with 488 passed, zero failed,
33 ignored and one filtered test across 53 test/doc-test suites. The opt-in GPU
fixtures remain separately accounted above. Evidence:
`target/instance-partitioned-workspace-tests{,-rest}.log` and
`target/instance-partitioned-process-test.txt`.
`cargo check --workspace`, workspace all-target Clippy with warnings denied,
formatting and diff checks also passed on this revision. Check/Clippy logs are
`target/instance-partitioned-workspace-{check,clippy}.log`. Further implementation
changes will require appropriate validation again; these results do not close
the remaining native/performance gates.

The earlier grouped native build `ec85523d3307e892f537a80d70c6d28eaefdb346ea24c9ebd4e0e94f790b131e`
ran in isolated editor PID 25416, bridge instance `22020-18d6f3821187eef4-24`,
process session `780931af-de1c-4f0f-aad3-d5ec6f2b5181`. Warm loading took
912.548 ms with 13 model, 12 texture and 2 scenery cache hits and zero imports;
first scene presentation API success was 1065.004 ms. Unchanged refresh command 6
took 1.551 ms with zero imports/rebuilds. Its already-edited fixture retained
326,781 records in 64 chunks.

After native streaming, prepared view 23 used 22 indirect draws, 11 dispatched pages
and 52 dispatches. Far pan evicted all 64 chunks and released all retained GPU
instance bytes (view 24). Return restored the full resident set; camera view 44
uploaded zero immutable/visibility sources and used 13 pages/47 dispatches.
Empty-field override forced preparation without changing the camera: view 45 reused
44 batches with zero dispatches and zero uploads. This demonstrates that native
streaming creates more, smaller page cohorts than an all-resident upload benchmark;
grouping does not collapse every draw into one page.

Forced CPU view 47 submitted 81,631 visible records. Forced GPU view 49's bounded
readback reported the same 81,631 across 44 draws. Mode changes rebuild residency,
so their source uploads are expected. Counts and captures retain separate frame
identities. Starting frame 301, return frame 758 and CPU frame 1145 captures were
inspected and show vegetation and the fixture's moved rock. Raw state and loading
evidence are in `target/instance-native-grouped-evidence.json`; captures use
`target/instance-native-grouped-{start,return,cpu}.png`. GPU frame 1697 was also
inspected (`instance-native-grouped-gpu.png`); the scene is consistent, while GUI
tab/selection state differs, so whole-window pixel equality is not asserted.
The editor stopped through MCP at host frame 2123 and PID absence was verified;
`target/instance-native-grouped-final-evidence.json` records capture and stop identity.
This native lifecycle check
does not establish moving-camera performance acceptance.

The counter-enabled expanded comparison (`target/instance-moving-grouped-counters.log`,
release/Vulkan, same GTX 1660) passed its zero-warm-upload assertions. Wide/near
views used 4 pages and 20 dispatches; the editor view used 5 pages and 22 dispatches.
Completed wall means across three rotating trials were Auto 2.900–3.028 ms versus
expanded 2.599–2.780 ms (wide), 2.084–2.202 versus 1.871–1.896 ms (near), and
2.270–2.399 versus 2.040–2.079 ms (editor). Native and benchmark camera/viewport
scenarios differ; these page counts establish different upload cohorts, not a
direct native timing comparison. Native workers publish in smaller waves than
the fixture's all-resident source admission. The residual performance gate remains
open; future dispatch optimizations must also account for those smaller waves.

```text
cargo test -p nico-presentation -p nico-presentation-control -p arena-arpg-presentation --lib
cargo test -p nico-render --lib
cargo test -p nico-rhi-wgpu --release -- --ignored --test-threads=1 --skip measurement
cargo test -p arena-arpg-presentation measurement -- --ignored --test-threads=1 --nocapture
cargo test -p nico-rhi-wgpu --release gpu_arena_moving_grass_measurement -- --ignored --nocapture --test-threads=1
cargo test -p nico-rhi-wgpu --release gpu_arena_instance_path_measurement -- --ignored --nocapture --test-threads=1
cargo test -p nico-rhi-wgpu --release gpu_visibility_page_grouping_measurement -- --ignored --nocapture --test-threads=1
```

Set `WGPU_BACKEND=dx12` for the alternate GPU suite. Measurement-only environment
variables select `NICO_MEASUREMENT_MOVING=1` for the synthetic benchmark,
`NICO_MEASUREMENT_INSTANCE_MODE=cpu|gpu|auto`, and
`NICO_MEASUREMENT_GPU_MIN_RECORDS` for the expanded Arena comparison.

Latest empty-field/stage-order changes passed all 22 non-measurement GPU tests on
Vulkan (20.32 s) and DX12 (21.10 s), targeted all-target Clippy, shader artifact
checking and formatting. The preceding uniform-cache change passed 22 renderer unit tests.
The subsequent single-counter layout passed all 22 Vulkan GPU tests (20.00 s),
the visibility/duplicate-page regression on DX12 (1.38 s), layout unit tests,
targeted Clippy, shader artifact checks and formatting.
The 60 presentation/control/Arena unit tests passed after cached bounds. Workspace
checking passed after the 1024 policy. Earlier workspace tests/Clippy passed using
an isolated target directory; the play process-cancellation test needed separate
elevated execution because Windows denied child termination in the sandbox.
Shader generation/check passed after the last shader change. These results span
recorded revisions; final whole-workspace validation remains to be refreshed.

After grouped-page diagnostics were added, all 23 non-measurement GPU tests passed
in the debug test profile on the GTX 1660 with `WGPU_BACKEND=vulkan` (14.54 s) and
`WGPU_BACKEND=dx12` (24.89 s). The grouped lifetime regression now checks that two
chunks dispatch one shared page in five stages, camera changes recompute that page,
and a retained unchanged view dispatches zero pages/stages. MCP serialization
preserves `visibility_dispatched_pages` and `visibility_dispatches` with the source
prepared-view identity. The focused MCP test, all-feature checks and all-target
Clippy for render/winit/ops, formatting and diff checks passed. These command counts
exclude reuse and describe encoded work, not GPU completion. Native observation of
the new fields is recorded above; these checks do not close the moving-camera
performance gate.

Physical device-loss recovery, runtime timestamp-query profiling, occlusion, dynamic shadows,
transparent instancing, automatic ordinary-draw batching and skinned crowds are not
claimed. The original plan explicitly defers these extensions; LOD is excluded.
No XRay integration or per-method profiling instrumentation was added.
