# Skeletal Animation

The engine owns its runtime animation model while SharpGLTF validates and evaluates imported
glTF animation data. This keeps asset-format concerns in `Engine.Assets` and leaves
`Engine.Graphics` independent of SharpGLTF and Silk.NET.

## Asset pipeline

`GlbModelImporter` publishes mesh primitives referenced by a glTF skin as
`nico/skinned-mesh` artifacts. Each artifact contains:

- indexed bind-pose geometry;
- four joint indices and normalized weights per vertex;
- a parent-before-child skeleton and inverse bind matrices;
- animation clips baked at 60 samples per second.

Baking uses SharpGLTF's evaluated node hierarchy, so STEP, LINEAR, and CUBICSPLINE source
curves, animated helper nodes, and mesh-node transforms share one compact runtime path.
Constant baked channels collapse to one key. Static GLB primitives continue to use
`nico/static-mesh`.

## Runtime

Attach `AnimatorComponent` to a `MeshInstance3D` that references a skinned-mesh artifact.
Its `Clip` selects an exact imported name; null selects the first clip. `PlayAutomatically`,
`Loop`, and `Speed` configure initial playback.

`AnimationPlayer` advances and samples a preallocated `SkeletonPose` without per-frame managed
allocations. The resulting skin matrices are uploaded to a renderer-owned, double-buffered
storage buffer. A dedicated Vulkan pipeline performs four-weight linear-blend skinning in the
vertex shader, then uses the standard forward material and lighting path.

Animator settings are persisted in scene format 5 and cloned into Editor play mode. Both the
Player and the Editor's Game viewport advance animation using simulation time, so pausing the
game also pauses skeletal playback.

## Current boundaries

The runtime currently provides one clip at a time. Cross-fades, animation graphs, root motion,
events, inverse kinematics, and retargeting belong in later layers built above
`AnimationPlayer`; they are not encoded into the renderer or GLB importer.
