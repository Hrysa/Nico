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

The import manifest also retains browsable GLB nodes, skeletons, and animation names. The
Editor reconstructs these under `Nodes`, `Skeletons`, and `Animations` when a GLB is expanded
in the File System panel; these rows describe objects inside the source file rather than new
physical files.

Baking uses SharpGLTF's evaluated node hierarchy, so STEP, LINEAR, and CUBICSPLINE source
curves, animated helper nodes, and mesh-node transforms share one compact runtime path.
Constant baked channels collapse to one key. Static GLB primitives continue to use
`nico/static-mesh`.

## Runtime

A `MeshInstance3D` that references a skinned-mesh artifact automatically receives a runtime
`AnimationController`. Animation ownership stays in game scripts rather than in an authored
scene component. A script can retrieve the controller with `Scene.Animation.GetRequired(Owner)`
when it only needs clips embedded in the model, or bind a separate imported animation set with
`Scene.Animation.Bind(Owner, animationSet)`. Scripts then select clips and configure looping,
speed, transitions, and playback through the controller.

`AnimationPlayer` advances and samples a preallocated `SkeletonPose` without per-frame managed
allocations. The resulting skin matrices are uploaded to a renderer-owned, double-buffered
storage buffer. A dedicated Vulkan pipeline performs four-weight linear-blend skinning in the
vertex shader, then uses the standard forward material and lighting path.
The source mesh-node world transform is applied after the skin palette and before the scene
instance transform. This preserves glTF armature unit and axis conversions without exposing
their inverse as an oversized or rotated bind pose.

Animation playback state is runtime-only and is not serialized into scene nodes. Both the Player
and the Editor's Game viewport advance controllers using simulation time, so pausing the game
also pauses skeletal playback. The Editor does not automatically play animations outside play
mode.

## Current boundaries

The runtime currently provides one clip at a time. Cross-fades, animation graphs, root motion,
events, inverse kinematics, and retargeting belong in later layers built above
`AnimationPlayer`; they are not encoded into the renderer or GLB importer.
