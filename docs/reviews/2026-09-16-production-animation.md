# Production animation acceptance review

Date: 2026-09-16. Historical acceptance record. The original conclusion that all
six gates were validated is superseded: the user rejected the earlier MCP test
authority. The [fresh review](2026-09-16-uncommitted-mcp-review.md) owns current
validation and remaining findings. In particular, the 16/64-character measurements
and contact calibration below have not been freshly verified.

## Original implementation and evidence record

The following preserves the original claims for traceability, not current acceptance.

| Required outcome | Originally recorded implementation and verification |
| --- | --- |
| Persistent GPU geometry, skin validation, static/skinned interoperability and bounded ownership | `nico-assets::Mesh::skinned_triangles` validates weights, palette count and indices. `nico-render::meshes` shares GPU geometry by immutable asset identity, uploads palettes, bounds scene draws/joints at 256, and releases cache entries no longer referenced by a scene. Renderer regressions reject invalid palettes. Four explicit GTX 1660/Vulkan GPU tests pass, including weighted CPU-reference comparison, shared geometry with independent palettes, static draws, textures/HUD and readback. |
| Cached humanoid mappings and reusable evaluation storage | `HumanoidRig` retains its model and compiled mapping/reference corrections. `PoseBuffer` and `HumanoidWorkspace` reuse local/global/rotation storage. Sampling and retarget regressions verify capacity retention, parity with convenience APIs, wrong-model rejection and failure preservation. Preview/arena reuse mesh geometry; skinning does not rebuild vertices or indices per animation frame. |
| Runtime-independent playback | `nico-animation::playback` has no runtime/renderer dependency. Tests cover 30/60/144-rate timing, loop/once completion, pause/seek/speed, moving crossfades, interrupted-fade continuity, invalid controls, failed evaluation, independent players, retarget/root policy, and externally clocked destinations that preserve fades. |
| Snapshot-driven arena animation and named attachments | Arena owns idle/run/attack/dodge/hit/death selection from immutable snapshots. Tests cover skipped/repeated snapshots, identity changes, death after terminal simulation freeze, run/wave reset, and contact at the active boundary. Native MCP verified those motions and restart. Right-hand `Attack-R1`, local +Y blade orientation and the source contact marker are calibrated; contact blade length is solved against unchanged hero reach. `Attachment` tests cover hierarchy/instance transforms, scale/shear, ambiguity, foreign poses and invalid inputs. |
| Bounded crowd preview, inspection, update/visibility policy and measurement | CLI admits 1..64 instances and at most 256 aggregate draws. MCP exposes selected/per-instance state, position, caps, sampled/pending time and visibility. Cap regressions preserve elapsed time and completion, with independent placement and forced refresh. Native 16-character checks verified independent pause/seek, 16→15→0→16 draw visibility/re-entry. Native release displayed/evaluated 64 independent instances at 58.39–58.61 Updates/s across five windows. These are cadence measurements, not CPU/GPU execution timings. |
| Documentation, workspace/shader/native checks, lifecycle/failure tests and supported limits | Full workspace/all-feature tests and strict workspace/all-target/all-feature Clippy pass in the isolated target. Formatting and generated shader verification pass. Native captures are inspected and sessions stop orderly with exit 0. Missing local content exits 1 before host startup. Import, playback, command, bounds, palette and CLI failure paths have regressions; despawn/snapshot-drop tests verify resource ownership. Preview clip labels and extension diagnostics are bounded before bridge publication. |

## Evidence and limits

The [roadmap](../roadmap.md#client-character-milestone) owns platform/scenario
measurement details. Local ignored evidence is under
`target/character-preview-evidence/`: `production-workspace-tests.log`,
`production-clippy.log`, `production-gpu-tests.log`, `gpu-skinning.png`,
`animated-bounds.png`, `crowd-16.png`, `crowd-64-release.png`, and
`arena-calibrated-contact.png`.

The native contact observation had clip time 0.326666647 seconds and weapon length
1.159424782 metres. The blade tip computed from that observed world matrix lies at
the two-metre hero attack radius. The active-phase capture confirms attachment and
orientation; it is not a frame-exact capture of the separately observed contact
snapshot. Simulation damage, movement and collision remain authoritative.

Supported limits remain explicit: 256 joints/palette, 256 draws/scene, 64 preview
instances, 128 preview clips with printable labels up to 128 UTF-8 bytes, PNG base
color and unlit materials, linear/step model tracks, and 22 mapped humanoid body
roles. Root-motion policy is visual; it does not drive simulation. Held-pose caps
trade smoothness for evaluation frequency and are not interpolated crowd LOD or
future-motion envelopes. Model geometry remains resident while referenced by the
scene; leaving and re-entering can require re-upload after cache eviction.

Further content/product work remains in [TODO](../../TODO.md): exact animation
source/license attribution before distribution, broader clip/art acceptance,
finger/twist animation, skeleton/timeline UI, imported enemies, lighting and effects.
XRay profiling, blend graphs, layered masks, IK, ragdolls, additional format adapters
and automatic distance-based LOD are not claimed as implemented. Local assets were
not copied into shipping asset roots. Current validation scope is limited to the
scenarios in the fresh review; this historical record does not establish acceptance.
