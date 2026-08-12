# Skeletal Animation

The engine owns its runtime animation model while SharpGLTF validates and evaluates imported glTF data. Asset-format concerns remain in `Engine.Assets`; animation playback and GPU contracts remain backend-independent in `Engine.Graphics`.

## GLB import

`GlbModelImporter` publishes skinned primitives as `nico/skinned-mesh` artifacts containing:

- indexed bind-pose geometry;
- four normalized joint influences per vertex;
- a parent-before-child skeleton and inverse bind matrices;
- embedded animation clips baked at 60 samples per second.

It also publishes standalone `nico/skeletal-animation` artifacts for source animations. The manifest retains browsable nodes, skeletons, materials, textures, and animation names, which the Editor exposes beneath the GLB source.

Baking uses SharpGLTF's evaluated hierarchy, covering STEP, LINEAR, and CUBICSPLINE curves, helper nodes, and mesh-node transforms. Constant channels collapse to one key. Static primitives use `nico/static-mesh`.

## Animation sets

A `.nanimset` is readable JSON that maps stable gameplay aliases to explicit imported animation artifacts. Each entry stores:

- alias;
- source `AssetReference`;
- optional exact clip name when the source contains multiple clips;
- default speed and loop state;
- optional in-place processing and root-motion joint.

The Animation Set panel can create, open, add, remove, reload, and save entries. Adding a multi-animation GLB creates entries for its animation artifacts; aliases remain the names used by scripts.

During binding, each source clip is matched to the target skeleton. In-place processing removes rendered-space horizontal translation from the selected root-motion joint. The editor detects common root joints and allows an explicit choice; unusual rigs should select the actual translating joint.

## Runtime playback

A `MeshInstance3D` referencing a skinned mesh receives one runtime `AnimationController`. Scripts retrieve embedded clips with:

```csharp
var controller = Scene.Animation.GetRequired(Owner);
```

or register a project animation set against the mesh skeleton:

```csharp
var controller = Scene.Animation.Bind(Owner, locomotionSet);
controller.DefaultFadeDuration = 0.15f;
controller.Play("Run");
```

`AnimationController` owns persistent named states and a base override layer. It supports looping, signed speed, normalized time, restart, stop/fade-out, completion callbacks, and cross-fades. `Play(name)` preserves the current state time; `PlayFromStart(name)` explicitly restarts.

Playback advances a preallocated pose without per-frame managed allocation. Active states sample retained local-transform buffers, blend into one pose, and upload skin matrices to a renderer-owned double-buffered palette. The source mesh-node world transform is applied after skinning and before the scene instance transform.

Playback state is runtime-only and is not serialized into scene nodes. Player and the Editor Game viewport advance controllers using simulation time, so pause also pauses animation. Edit-mode Scene preview displays the skinned mesh but does not automatically play clips.

## Current boundaries

The implemented controller has one override layer. Additive layers, masks, blend trees, animation events beyond state completion, extracted root-motion delivery, inverse kinematics, and generalized retargeting remain future layers. In-place baking removes locomotion translation but does not expose that motion as gameplay displacement.
