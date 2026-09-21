# GPU instance scene and foliage rendering

Status: feature implementation complete; further frame-time optimization deferred by
the user on 2026-09-21. User-supplied reference reviewed on 2026-09-20.

Scope correction: the user explicitly excluded LOD. Each type has one prototype;
geometry-detail selection belongs to a future virtualized-geometry system. The
four implementation milestones below remain in scope. The user's 2026-09-21
instruction to finish the feature supersedes the earlier requirement to keep
pursuing frame-time parity before delivery. Parity is not claimed.

Implementation evidence: [roadmap](../roadmap.md#common-mesh-instancing-2026-09-20-feature-complete).
Direct instancing, compact grass caches, a bounded ordinary fallback and host
submission counters are implemented. GPU culling/compaction now drives scene
indexed indirect draws, with real-GPU parity and native editor evidence. GPU-derived
diagnostic counts and path controls are implemented. Foliage influences, grass/shrub
consumers and bounded provider streaming have unit/GPU and scoped native evidence.
The [validation report](../validation/2026-09-20-common-mesh-instancing.md)
records the final feature audit, refreshed checks, tested lifecycle/native/image
scenarios and their limits. Its final delivery note supersedes earlier open-gate
status entries. The residual performance gap remains a follow-up optimization.

## Reference and purpose

Reference reviewed: the user-supplied design in the 2026-09-20 conversation,
covering Placement, Spatial, Instance Data, Culling, Rendering and Interaction.
Its key boundaries are provider-independent rendering, a common GPU instance scene,
and a foliage extension for deformation and high-density masked rendering. This
supersedes the initial draft's pending-reference note and direct-instancing-only scope.

The target pipeline is:

```text
Placement providers -> immutable chunk data -> persistent GPU instance scene
                                                 |
                     coarse chunk visibility -> GPU frustum/distance culling
                                                 |
                                             visible compaction -> indirect draws
                                                 |
                              shared PBR + optional foliage deformation
```

The common layer serves rocks, props, debris and vegetation. A foliage extension
adds type configuration, wind and bounded world influence fields. Grass and flowers
are configurations/consumers, not separate engine rendering systems. Crowd rendering
can reuse ownership/culling concepts later but needs a separate skin-palette contract.

The first delivery removes expanded grass geometry and repeated placement work.
The second delivers the reference's GPU culling and indirect path. The third
adds foliage deformation/interaction, and the fourth bounded chunk streaming.
Direct instancing remains the compatibility path, not the final architecture.
Occlusion and shadows require separate depth/shadow infrastructure and have explicit
integration points below; they are not represented as existing capabilities.

Arena grass is the first real consumer; a repeated-rock fixture proves the same
engine API works without vegetation-specific renderer code. Placement rules,
palettes, density, exclusions and seeds remain game-owned configuration. The
existing [PBR plan](2026-09-17-pbr-render-pipeline.md) supplies the current pass,
material and resource-lifetime constraints.

## Observed baseline

- [Landscape construction](../../games/arena-arpg/presentation/src/landscape.rs)
  visits 8 x 8 chunks with 1,800 candidate tufts each. Accepted tufts generate
  three blades; each blade duplicates five vertices and nine indices.
- Each chunk becomes an ordinary `Mesh`; construction generates normals and
  indexed centroids. The ground texture is cached, but grass geometry, hills,
  and sky are reconstructed when the project opens.
- Chunk visibility is checked before extraction. Up to 64 grass chunks already
  mean up to 64 grass draws, not one draw per blade.
- [Mesh rendering](../../crates/nico-render/src/meshes.rs) retains uploaded
  geometry but allocates/writes a 208-byte transform/tint uniform per scene draw
  and issues `draw_indexed(..., 0..1)`.
- [RHI](../../crates/nico-rhi/src/lib.rs) already exposes
  `VertexStepMode::Instance` and an instance range on indexed draws. No compute
  pipeline or indirect draw is needed for the first direct-instancing milestone.
  Compute pipelines/passes, storage buffers and `BufferUsages::INDIRECT` already
  exist, but the render-pass trait lacks indexed indirect drawing. GPU-driven
  rendering needs that operation and additional explicit limits/capabilities.
- The retained [warm report](../../target/bc3-cache-migration/warm-loading.json)
  measured 1,983.7 ms for the combined scenery phase, with zero imports, on the
  Windows debug build tested on 2026-09-20. This is elapsed wall time for the whole
  phase, not an isolated grass CPU measurement. First scene presentation API
  success took 4,699.3 ms; the later delay must be measured separately.

Expected gains are less geometry construction, smaller geometry buffers, and
reusable instance data. Instancing does not automatically reduce the existing
64 chunk draws or the number of shaded blades/fragments.

## Ownership and contracts

| Owner | Proposed responsibility |
| --- | --- |
| `nico-assets` | Existing immutable mesh/material prototypes; existing importer/cache infrastructure |
| `nico-presentation` | Validated immutable instance records, batches and scene snapshot references |
| `nico-presentation-control` | Runtime-free provider/chunk coordination, transform/bounds helpers and model-to-batch preparation |
| `nico-render` | Instance scene residency, culling/compaction, draw preparation; separate foliage shader extension |
| `nico-rhi` / `nico-rhi-wgpu` | Direct/indirect draw and compute contracts, resource ordering and enabled device limits |
| Arena presentation | Deterministic grass placement importer, prototype and chunk ownership; client/editor composition |
| Native host operations | Bounded rendering counters exposed through `nico-bridge` |

No GPU resources enter runtime state or presentation snapshots. No new crate is
needed. Tool threads read published reports or queue requests at owned boundaries.

Proposed API names are illustrative:

```text
StaticInstance { affine_transform, tint }
StaticInstanceData { validated immutable records, conservative bounds }
MeshInstanceBatch { Arc<Mesh>, Arc<PbrMaterial>, Arc<StaticInstanceData> }
Scene3d { existing camera/lighting/meshes, instance_batches }
```

Keep `MeshInstance` and its static/skinned consumers working. Batch constructors
require explicit valid mesh/material resources. An instance transform is a finite,
invertible affine transform; support nonuniform scale and shear without baking
transformed geometry. Partition reflected and non-reflected instances because
front-face winding is pipeline state. Validate tint, bounds, instance counts,
byte counts and overflow before allocation/upload or surface acquisition.

Publish data through `Arc` and reuse the same allocation across unchanged frames.
Replacement creates a new immutable allocation; snapshots retaining the old data
remain valid. Do not identify GPU contents by a frame-local vector index or an
unchecked caller-supplied revision. Moving-camera snapshots must not rebuild the
instance records or trigger their revalidation every frame.

## Direct instancing foundation (milestone 1)

1. Validate the scene and classify ordinary draws and explicit instance batches.
   Batching compatibility includes mesh identity, material identity, alpha mode,
   culling/double-sided state, winding and vertex shader variant. Shared textures
   alone do not make materials compatible.
2. Reuse mesh/material residency. Upload a batch's instance buffer once for its
   immutable data identity; retain it across temporary invisibility while the
   source is owned. Retire it after its source owners disappear. Handle device
   recreation by rebuilding residency from CPU snapshots.
3. Bind prototype vertices in slot 0 and instance-rate data in slot 1, then issue
   one indexed instanced draw per visible compatible chunk/range. Bind a buffer
   slice and use a zero-based instance range initially; do not require optional
   indirect or nonzero-first-instance capabilities.
4. Supply view-projection once per view in frame uniforms. Persistent instance
   records contain model transforms, not camera-dependent clip transforms.
5. Reuse the PBR fragment path and its texture format fallback. Add an instanced
   static vertex entry point and pass instance tint through to the fragment stage;
   the current draw-uniform tint must not remain accidentally shared by all instances.
6. Preserve Scene3D opaque/masked ordering, ordinary sorted transparency, then
   camera-dependent Scene2D and UI. Existing skinning remains independent.

Start with an explicit packed GPU layout: three `float4` model rows, three padded
normal-transform rows, and one `float4` tint (112 bytes). With the current three
mesh attributes this uses ten vertex attributes across two buffers. Store or
derive the inverse-transpose once when constructing validated instance data;
normal mapping and mirrored double-sided shading must match ordinary draws.
Serialize float bytes explicitly and test row/column conventions; do not rely on
Rust struct layout or unsafe casts. A smaller TRS/affine encoding can be measured
later without constraining the public affine transform contract.

Check enabled device limits, including vertex attribute count, vertex buffer
count, maximum vertex stride and maximum buffer size. Add missing limit fields to
RHI with values from the enabled device. Split oversized batches into legal buffer
segments/draws. Define separate limits for submitted ordinary draws, batch draws,
instance records and upload bytes; do not reinterpret the current 256-draw limit
as 256 grass blades. Initial proposed budgets: 512 batches, 500,000 records and
64 MiB instance-buffer bytes; finalize against the baseline and device limits.
Reject excess explicitly before drawing, rather than silently dropping grass.

If the instanced layout is unsupported, use the ordinary static path for bounded
small batches and report that fallback. If expansion would exceed the ordinary
draw budget, return a structured unsupported/limit result; never issue hundreds
of thousands of fallback draws or silently truncate. Test both branches.

Alpha-blended batches are rejected by the initial API: instancing must not bypass
global back-to-front sorting. Callers can retain ordinary transparent draws.
Opaque and alpha-mask materials are supported. Skinned crowd instancing is a
separate extension requiring palette indexing and its own contract.

## Provider and type boundary

Providers publish owned chunk additions, replacements and removals. The renderer
never invokes a provider, reads terrain, runs gameplay rules or resolves authored
files. Provider work completes outside rendering and is published at the existing
presentation boundary. Begin with procedural/cache and runtime-spawn adapters;
density maps, terrain rules and designer painting can produce the same records
when those authoring systems exist. Avoid unused provider implementations.

Separate rendering type data from placement policy:

```text
InstanceType: prototype, material, max draw distance,
              conservative prototype bounds, optional extension schema
PlacementSettings: density, seed, scale range, terrain/path/obstacle rules
FoliageProfile: deformation variant, wind/interaction response, maximum bend
InstanceRecord: stable ID, transform, tint, seed, optional typed extension data
ChunkUpdate: provider/chunk identity, generation, type groups, records, bounds
```

Type and chunk handles use generations; stale asynchronous results cannot update a
newly reused ID. Seeds and stable IDs survive GPU compaction.
Changing draw distance or wind strength must not regenerate placements. Changing
placement inputs invalidates the placement cache. Prototype/shape changes update
render data and conservative bounds; cache keys include shape inputs only where
those inputs actually affect placement. Extension data is versioned, typed and
bounded, not an unvalidated universal float array. Bind incompatible extension
layouts to distinct shader/batch variants. Static rocks allocate no wind data.

## GPU culling and indirect drawing (milestone 2)

Keep persistent source records separate from frame/view-local visibility output.
Use CPU chunk culling as a conservative broad phase, then GPU per-instance frustum
and distance tests with transformed bounds. Every type has one prototype. There
are no mesh-LOD chains, thresholds, hysteresis state or transitions in this task.

Use compute-written compacted visible instance IDs, retaining transforms and typed
extension data in persistent source storage buffers. Partition output by compatible
(type, material, winding, extension) groups within bounded chunk pages. Count
visible records, perform an exclusive prefix sum, then scatter IDs into disjoint
ranges. A final dispatch writes indexed indirect argument records and group offsets.
Capacity is bounded by the page's input count because each input selects at most
one output group. Group counts, scan arithmetic and dispatch dimensions must be checked for
overflow. This avoids copying full transform/normal records during every compaction.

Add a storage-fetch instanced vertex entry point: a small CPU-known group index
selects GPU-written group metadata; `SV_InstanceID` indexes that group's visible
IDs, then fetches the persistent source record. Group metadata offsets remain on
GPU. The CPU must not try to bind vertex slices at offsets produced by a GPU prefix
sum. Share transform/PBR/deformation functions with the direct vertex-stream path,
and validate storage layout/alignment separately. Group-index bindings are bounded
and reusable, rather than a new per-blade uniform allocation.

Reset counters and indirect arguments each frame, including empty/fully culled
pages. Dispatch boundaries separate count, scan, scatter and finalization; do not
assume a workgroup barrier synchronizes different workgroups. Finish compute before
using its output as vertex-stage storage/indirect input, using provider-owned synchronization.
Isolate transient output per in-flight view/frame or prove queue ordering prevents
reuse hazards. Never overwrite data still needed by a submitted frame.

Add `draw_indexed_indirect(buffer, offset)` to `RhiRenderPass` and its wgpu provider.
Define the argument ABI and alignment explicitly. Use one indirect draw per bounded
group and `first_instance = 0`; bind the corresponding group index. No multi-draw,
GPU-generated draw-count extension or nonzero-first-instance feature is required.
GPU counts control visibility without synchronous CPU readback. The CPU knows group
capacity/prototype identity, so it can encode a fixed set of draws, including zero
instance counts. Page/group-count overhead must be included in measurements.

Report enabled compute/storage/indirect capabilities and actual device limits,
including workgroup sizes/counts, storage bindings and buffer sizes. Select the path
before resource creation:

| Device path | Behavior |
| --- | --- |
| Compute + storage + indexed indirect supported | GPU per-instance culling, compaction and indirect draws |
| Direct instancing supported | CPU culling with the same type policy and instanced draws; retain unchanged visible sets |
| Instance layout unsupported | Bounded ordinary-draw fallback as described in milestone 1, or explicit unsupported result |

CPU fallback may upload a changed visible set when the camera changes. Distinguish
this transient upload from re-uploading immutable source records. GPU compaction
writes transient IDs/counts each frame; zero CPU upload is not zero GPU bandwidth.
The chosen path and reason must be observable through MCP. Test fallback selection
by disabling capabilities on a supported device as well as on actual target
backends when available.

Occlusion is a later extension: a depth pyramid, frame history, camera-cut rules and
conservative disocclusion behavior must exist first. Do not claim frustum culling
implements occlusion. Shadows need separate light-view visibility, shadow distance
and the same deformed geometry; never reuse only the main-camera visible list.

## Foliage and world influence fields (milestone 3)

Keep static instance shaders and buffers independent of foliage. A foliage variant
adds root-to-tip deformation weights and typed per-instance seed/response data.
Configured grass, flowers and shrubs share that extension; rocks use the static
path. Reuse PBR lighting, alpha-mask cutoff and double-sided normal conventions.
The initial geometric grass need not become alpha cards merely to use instancing.

Publish finite, bounded world-space influence snapshots containing stable ID,
position/radius, direction, strength, kind and lifetime. Begin with directional
wind and radial bend; impulse/trail fields can follow through the same validated
contract. Use visual time and deterministic phase seeds so editor pause/seek and
runtime behavior are reproducible. Fields are render-only observations; gameplay
burn/cut/damage state belongs to authoritative game logic and supplies explicit
placement/state updates. Shader deformation is not collision or cloth simulation.

Use spatial field lists per chunk with explicit capacities and deterministic overflow
selection; avoid an unbounded all-fields loop for every blade vertex. Define the
combination rule, falloff, maximum displacement and recovery behavior. Expand
culling/streaming bounds for maximum deformation; keep roots anchored and update
normals consistently. Time/camera/field changes update bounded frame/field buffers,
not all immutable placements. Flags or cloth require their own deformation model;
they may consume the field data without sharing a vegetation shader.

## Chunk streaming and lifecycle (milestone 4)

Separate resident, visible and requested chunks. Use world-space load/unload distances
with hysteresis and bounded request, decoded-byte and upload budgets. Provider tasks
carry chunk generations and cancellation; stale completions are discarded. Publish
complete updates atomically and keep last-good data during a failed replacement.
An unloaded chunk releases its owning references; GPU retirement follows outstanding
snapshots/submissions. Becoming invisible alone does not unload it.

Runtime-spawn/removal uses stable IDs and chunk updates through the owning boundary.
First implement immutable whole-chunk replacement; dirty ranges are an optimization
requiring evidence. Diagnostics distinguish pending, resident, visible, failed and
evicted chunks. Missing data behavior and last-good retention must be explicit;
streaming cannot silently change authoritative obstacles or gameplay placement.

## Grass conversion and disk cache

Create one canonical bent blade mesh with five vertices and nine indices. Express
current blade width, height, yaw, bend and translation through instance transforms;
use per-instance tint with a shared neutral material to preserve the current
palette. Confirm geometric and shading parity before removing the generated-mesh
path. Keep a deterministic expanded reference in tests, not a second production
renderer. If one prototype cannot reproduce existing shape variation, use a small
explicit prototype set rather than adding game-specific fields to engine shaders.

Keep 8 x 8 spatial chunks initially. A chunk owns immutable instance data and a
conservative bound covering every transformed blade. Camera visibility selects
chunk references only; culling does not destroy GPU residency. Do not merge all
grass into one always-visible world batch. A second generic fixture exercises
repeated rocks, material separation, scale and reflection.

Add a game-owned `GrassPlacementImporter` using the existing import/cache contract.
Persist compact seed-derived placement records and chunk membership in
`<game>/.nico`, not expanded vertices, normals, or backend-specific buffers.
Recipes include generator version, seed, density, extent, path parameters,
obstacle footprints and relevant appearance/shape settings. Include each external
dependency through the existing recipe mechanism; do not hide dependencies in a
source file whose metadata never changes. Camera and lighting changes must not
invalidate placement. First version may rebuild the whole placement set for an
affected zone; per-chunk incremental invalidation is a later optimization.

Warm open deserializes validated records and builds GPU upload data without
rerunning placement loops or per-blade `Mesh::triangles`. An unchanged refresh
keeps existing `Arc` identities. Check cancellation between chunks; validate cache
sizes/counts and corrupted data before allocation. Failed regeneration retains
the session's last-good scenery under existing editor replacement rules. CPU
placement loading remains asynchronous and bounded; publication swaps a complete
result at the owning boundary.

This plan targets grass. Measure remaining hills/sky construction separately; cache
them only if their cost warrants it. Instancing must not be described as removing
all startup preparation or solving vegetation overdraw.

## Implementation sequence

1. **Baseline and diagnostics:** measure placement, geometry construction, other
   landscape work and first upload separately using existing diagnostics or a
   focused test harness. Record blade counts, vertices, indices, bytes and wall
   times for cold and warm scenarios. Add bounded native rendering counters.
2. **Milestone 1 — shared direct instancing and cached placement:** implement
   immutable records/types, normal/bounds validation, Slang/PBR changes and resident
   instance buffers. Prove repeated rocks use the generic path, then convert Arena
   grass and cache its placements. Validate startup gains before adding GPU culling.
3. **Milestone 2 — GPU-driven visibility:** add the missing indirect RHI operation,
   enabled capability/limit reporting, count/scan/scatter and CPU fallback.
   Validate per-view data ownership and actual indirect output through readback.
4. **Milestone 3 — foliage extension:** add typed deformation data, wind and bounded
   radial interaction fields; exercise grass and a flower/shrub configuration.
   Validate deformed bounds, normals, roots, editor time control and fallback parity.
5. **Milestone 4 — streaming:** add provider chunk lifecycle, bounded asynchronous
   requests/uploads, cancellation, stale-generation rejection and native controls.
6. Record evidence for each milestone separately in the roadmap and update current
   architecture/README only after implementation. A completed direct-instancing
   milestone does not mark the GPU-driven or foliage milestones complete.

## Validation and acceptance

- Unit tests: affine/nonuniform/reflected transforms, normals, bounds, immutable
  identity, invalid/singular data, split boundaries, empty batches and overflow.
- Cache tests: deterministic placement, cold/warm equality, zero generator calls
  on warm reuse, unchanged refresh identity, each recipe dependency invalidating,
  corruption repair, cancellation, failure retention and bounded shutdown.
- Renderer tests: distinct mesh/material/winding separation, explicit rejection of
  blended/skinned batches, ordinary/instanced coexistence and UI ordering. For the persistent direct chunk path, verify
  upload bytes remain zero after first use and after camera cull/reentry; replacing
  one chunk uploads only that chunk. Release sources and verify residency retires.
- Real-GPU readback: ordinary expanded reference versus instanced geometry with
  tint, depth, nonuniform scale, reflection, alpha mask and normal maps. Test limits
  and fallback separately. Declare numeric image tolerances before comparisons.
- Use discovered bridge instances for native operations. Publish bounded counters
  for visible chunks, submitted instances/draws, mesh/instance upload bytes,
  retained bytes, cull counts, streaming states, influence overflow
  and fallback use, with frame/snapshot identity. GPU-derived counts use bounded
  asynchronous readback with explicit age; never block rendering for diagnostics.
  Add discoverable bounded controls to force CPU/GPU paths and freeze visual time
  in owned test sessions, rejecting unsupported modes before mutation. Counters are not
  a profiler; XRay, custom collectors and per-method tracing remain deferred.
- Native scenarios: fresh-cache open, warm reopen, unchanged refresh, relevant
  source edit, orbit away/back, unload/reload and device recovery where supported.
  Inspect captures from the exact test instance; command success alone is not
  visual proof. User-watched acceptance requires the user's confirmation.
- Acceptance: warm grass loading does not regenerate expanded geometry; unchanged
  frames/reentry do not re-upload persistent source data; game rules and appearance remain
  consistent; a non-grass consumer uses the same API. Provisional performance
  target is at least 50% less warm grass preparation wall time than the isolated
  baseline on the same build/hardware, with no material steady-frame regression.
  This is a target, not measured evidence. Report total startup separately.
- Run focused crate tests, workspace checks/Clippy, formatting, shader generation
  and `nico-shaderc --check`, plus explicit GPU/native scenarios. Use isolated
  output directories when running user processes lock build artifacts.

Additional milestone gates:

- GPU visibility must match a CPU reference for boundary cases, distance limits and
  visible IDs. Validate count reset, all-culled/empty frames, output capacity,
  reflected groups, camera cuts, concurrent views and in-flight resource reuse.
  Compaction order may vary for opaque/masked draws; compare stable visible IDs,
  not incidental output order. No synchronous GPU count readback in the draw path.
- Wind/interaction tests cover radius boundaries, field expiry/overflow, root
  anchoring, maximum bend bounds, paused time and recovery. Inspect captures during
  active influence, with static rocks demonstrably unaffected.
- Streaming tests cover cancellation, stale generations, replacement failure,
  unload/reentry, bounded overload and shutdown with outstanding provider work.
- Measure cold/warm preparation, first upload, CPU submission, GPU workload where
  supported and memory separately. Compare dense and sparse views: compute and
  compaction overhead can exceed direct instancing for small scenes. Select a
  measured threshold without changing visible results or material semantics.

Occlusion, dynamic shadows, automatic batching of all ordinary draws, transparent
instancing and skinned crowd palettes remain explicitly deferred dependencies or
extensions. GPU culling, indirect drawing, foliage fields and bounded streaming
are included in this staged proposal, not implied to be implemented today.

## Remaining performance investigation: capacity shared across upload waves

Initial investigation status (superseded by the implementation update below):
append-only pages, streaming selection and reserved-capacity accounting were
implemented; individual range reclamation, native validation of cross-wave sharing
and performance acceptance were pending.
The user initially required continued frame-time parity work, then deferred that
investigation on 2026-09-21 to finish feature delivery. The [validation report](../validation/2026-09-20-common-mesh-instancing.md)
records the remaining small-publication-wave penalty and the indirect-validation
isolation; disabling native indirect validation is not the proposed remedy.

Implementation update (2026-09-21): bounded spare capacity now accepts later upload
waves, and weak per-group reservations permit retired interval/group reuse while
preserving live source buffers and selections. GPU replacement and binding-failure
recovery tests pass. Native instance 27 validated partial and full eviction/reentry,
stable retained bytes across repeated partial cycles, counts and captures. Per-view
group selection skips culling for chunks without an indirect draw. Performance
acceptance remains open. The following requirements
continue to govern this work:

- Separate immutable allocation/layout capacity from active record/group counts.
  Output-region offsets must remain stable as records are appended; dispatches
  must never read uninitialized capacity. Keep public immutable-page constructors
  and independent-view behavior intact.
- Upload only new bounds, group metadata and fixed-range offsets. Existing source
  records and group addresses must not change when another chunk is admitted.
  Charge reserved page capacity against residency before allocation, and keep all
  initialization writes inside the upload allowance. Avoid a maximum-size allocation
  for every sparse or small scene.
- Appending invalidates the page visibility key before the next submission. Render
  ownership must enforce submission-before-update for shared input/output storage.
  Partial allocation failures must leave existing residents valid and pending chunks
  retryable without publishing incomplete groups.
- Track page occupancy independently from chunk source ownership. Releasing the last
  group must release the page; partial retirement must not leave unbounded dead
  capacity. Design range reuse and stale-ID/readback handling before enabling reuse.
- First measure identical all-resident and two-batch-wave scenes. Then validate
  append after cached visibility, independent cameras, retirement/reuse, exhausted
  capacity, upload pacing, failure rollback, CPU/GPU counts and captures on both
  backends. Native eviction/reentry and the original completion gates still apply.

This changes visibility storage and scheduling only; it adds no LOD or placement
policy and does not relax the existing performance or correctness requirements.
