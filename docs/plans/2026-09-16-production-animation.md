# Production animation implementation

Status: implemented 2026-09-16 for the documented supported subset. Current
validation is recorded in the [fresh review](../reviews/2026-09-16-uncommitted-mcp-review.md).
The [original acceptance record](../reviews/2026-09-16-production-animation.md)
is historical; its earlier MCP evidence is not current validation authority.

## Required outcomes

- Persistent GPU geometry, validated skin weights and joint palettes, static/skinned
  interoperability, bounded resource ownership and release, and GPU reference tests.
- Cached humanoid mappings and reusable pose evaluation storage; no per-frame mesh
  construction or index upload for animation.
- Runtime-independent playback with loop and one-shot modes, completion state,
  crossfades, interruption semantics, and deterministic time/transition tests.
- Client animation driven by owned arena snapshots, including attack/dodge/hit/death
  transitions and named-joint attachments. Simulation remains authoritative.
- Bounded multi-character preview and MCP inspection, update-rate/visibility policy,
  frame-cadence evidence and correctness checks across independent instances.
- Current documentation, package/workspace checks, shader verification, native GPU
  captures, lifecycle/failure regressions, and explicit supported limits.

## Implementation sequence

First replace CPU mesh deformation with GPU palette skinning while keeping static
rendering compatible. Next establish reusable animation evaluation and playback
contracts, then integrate transitions and attachments with arena snapshots. Finally
exercise multiple characters, resource lifetime and native failure paths. The
original acceptance record maps these outcomes to implementation. Fresh validation
does not yet cover the earlier 16/64-character measurements or contact calibration.

Profiling infrastructure remains deferred. Existing frame-cadence measurements,
controlled workload comparisons and rendering correctness tests provide evidence
without adding an application profiler.

## Implementation and validation scope

GPU skinning and shared persistent geometry are implemented through assets,
presentation, render and native host contracts. The real-GPU weighted reference
comparison passed on GTX 1660/Vulkan, and a native Ch03 retarget preview was captured.
Compiled `HumanoidRig` holds `Arc<Model>`; `PoseBuffer` and `HumanoidWorkspace` reuse
local/global/rotation storage. Buffer evaluation preserves the prior pose on failure.
The roadmap owns native measurement values. Playback modes/transitions and named
attachment transforms are implemented and tested. Shared model presentation is now
engine-owned and used by the preview. The arena now selects imported hero motion
from snapshots and renders a named hand attachment. Bounded crowd preview and
explicit pose caps are implemented. Right-hand contact pose, attachment axis and
weapon reach calibration are implemented; their earlier visual acceptance has not
been freshly verified. The fresh review records a two-character preview and a
user-confirmed imported-hero three-wave victory, with explicit limits.

## Playback and attachment contracts

`AnimationSet` shares immutable direct or humanoid-retargeted clips for one exact
`Arc<Model>`. `AnimationPlayer` owns independent clocks, reusable poses, and root
motion policy. Loop wraps elapsed time; Once clamps to its endpoint and emits
`just_finished` only when an update crosses that endpoint. Seeking cancels a fade
and does not emit completion. Pause freezes clip and fade clocks. Speed scales clip
time; fades use unscaled elapsed time while unpaused. Zero speed can therefore
still complete a fade. Zero-duration clips finish without advancing.

Normal crossfades advance both source and destination. Interrupting a fade freezes
the currently visible mixed pose as the new source, preserving continuity while
bounding blending to two poses. Invalid controls or evaluation failures preserve
the visible output and public playback clocks. Preview seek additionally pauses;
that is tool policy rather than the engine player's seek contract.

`Attachment` resolves an exact unique node name against its retained model. Its
matrix is instance placement * evaluated ancestor chain * local socket offset.
It retains full affine scale/shear, rejects foreign poses and invalid/overflowing
transforms, and performs no allocation or name lookup during evaluation. Game
integration must preserve that matrix rather than silently discarding shear through
uniform-scale decomposition. Gameplay hit timing remains simulation-owned.

`ModelVisual` shares immutable geometry/textures across instances and snapshots.
Extraction creates independent immutable palettes, checks node-matrix count and
finiteness and rejects palette overflow. Published snapshots retain resources until
the last owner drops them. Selected scenes and the 256-primitive bound apply per
visual; callers must also respect the renderer's 256-draw total scene bound.


## Arena composition

The client optionally loads a six-clip local RPG set and a Mixamo model through the
bounded importers. The hero normalizes reference-pose height to 1.8 metres, shares
persistent model geometry, and extracts a separate full-affine hand palette for the
blade. There is no asset copying, server dependency, or simulation mutation.

The game controller compares owned snapshots for movement, action identity, and
health loss. Dodge playback duration follows the authoritative action duration.
Attack playback maps its authored contact marker to windup completion, then maps
recovery to the remainder of the source clip. `AnimationPlayer::update_at` accepts
this external clock while preserving crossfades. Late observations consume the
already elapsed fade time rather than delaying the active pose. Hit
reaction interrupts current playback; death takes precedence. Repeated snapshots do
not advance ongoing simulation-clock animation twice. After a terminal run freezes
simulation ticks, presentation delta lets death complete. Run/wave changes and tick
rollback reset transient state. Enemies remain procedural in this integration.

The supplied `Attack-R1` motion reaches peak hand-forward extension near normalized
time 49/120 (0.3267 seconds on the inspected clip). The blade follows hand-local +Y,
and loading calibrates its length so its contact tip has the authoritative attack
radius in the ground plane. Invalid calibration fails before host startup. Native
MCP transitions, captures and crowd validation are recorded in the roadmap. Finger
poses and final weapon art remain content limitations of the body-only preset.


## Bounds and visibility

Current-pose bounds and draw culling are implemented using import-time influence
boxes, affine palette transforms, and the shared renderer camera projection. The
arena unions weapon geometry into its hero bounds. Invalid bounds fail open.
Synthetic regressions compare weighted vertex positions across 100 poses with
nonuniform/reflected scales, shear, instance transforms, inverse binds, and rigid
geometry. Frustum regressions include near/far planes, behind-camera geometry,
camera-enclosing bounds, and invalid input. These bounds do not provide a future
motion envelope: update-rate throttling must preserve correct re-entry and cannot
assume a stale pose bounds all future animation. The held-pose policy below has
bounded crowd and re-entry validation.


## Multi-character evaluation policy

The preview supports 1..64 ECS instances sharing one immutable asset set, with an
aggregate 256-draw admission limit before host startup. Playback starts at staggered
clip phases. Each instance owns its player, pending elapsed time, evaluation cap,
position and cached immutable palette snapshot. MCP selection controls one instance;
camera controls apply to the entire grid. The published instance array is bounded
at 64 and includes sampled time and pending elapsed seconds separately.

The default evaluates every Update. A 1..120 Hz cap holds the last displayed pose
until enough elapsed time accumulates; the next evaluation consumes all accumulated
time, including stalls. The cap is a maximum, not a promise that the host will reach
that rate. Camera changes force fresh evaluation, and edits flush pending time before
changing playback. Culling uses the pose actually being displayed, never assumes
that stale bounds enclose future motion. This deliberately trades temporal smoothness
for evaluation cost. Automatic distance-based LOD and future-motion envelopes are
not implemented. Tests compare capped/full playback after flushing, check pause and
one-shot completion, independent placement/re-entry, CLI limits, and resource release.
