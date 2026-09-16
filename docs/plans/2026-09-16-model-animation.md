# Model import and CPU humanoid animation

Status: generic GLB model import, CPU pose sampling/skinning matrices, and initial
humanoid conversion implemented 2026-09-16. A standalone GPU-skinned preview now
consumes these APIs. Compiled rigs and reusable pose buffers are implemented;
playback transitions and named attachments are implemented; arena integration
and full production acceptance are tracked
in the [production plan](2026-09-16-production-animation.md). This implements the CPU consumers of the
[import interface](2026-09-16-extensible-asset-import.md) and the
[character direction](2026-09-16-character-assets.md#humanoid-conversion-direction).

## Ownership and public extension

`nico-assets::model` owns immutable `Model` bundles. Userland builds public
`ModelData` and calls `Model::new` to validate references, hierarchy, transforms,
skins, geometry, materials, and tracks. Borrowing `Model::data` cannot mutate the
validated bundle. The default assets crate remains dependency-free.

`ModelGlbImporter` is an ordinary public importer under `gltf-import`, with typed
`ModelGlbSettings`. `AssetStore<Model>::install_with_importers` uses the existing
runtime ownership, retry, cancellation, and atomic publication lifecycle. A model
is one asset bundle: its nodes, meshes, skins, clips, materials, and encoded images
share ownership. No cross-store partial publication or dependency scheduler is added.

`nico-animation` depends only on CPU assets and glam. It owns sampling, pose/world
transforms, skin matrices, profiles, and humanoid conversion. It has no runtime,
presentation, renderer, or provider dependency. A `Pose` borrows the exact immutable
model whose indices it uses. Mapping rejects a pose from another model, even if
node counts match. The arena selects states/clips from simulation snapshots;
the preview publishes model-space joint palettes for GPU rendering. Arena integration
is implemented in the [production pipeline](2026-09-16-production-animation.md).

## Supported GLB subset

The interpretation follows the
[glTF 2.0 specification](https://registry.khronos.org/glTF/specs/2.0/glTF-2.0.html).
Source units, right-handed Y-up coordinates, ancestor TRS, joint indices, and
column-major inverse bind matrices are preserved. Rotations are XYZW quaternions.
There is no automatic rescaling of centimeter-authored local joints: their source
ancestor scale remains in the hierarchy.

The importer accepts one embedded buffer; multiple nodes, scenes, meshes and
indexed triangle primitives; float positions/normals; UV0; four joint indices and
weights per vertex; skins; and STEP/LINEAR translation/rotation/scale tracks.
Normalized U8/U16 UVs and weights are supported, as are U8/U16 joints and
U8/U16/U32 indices. Missing inverse bind matrices use identity. Missing normals
are recorded by `has_normals = false`; missing UV0 uses zero coordinates.

Materials preserve core metallic/roughness factors, texture references, normal
scale, occlusion strength, emissive factors, alpha mode/cutoff, and double-sided
state. Texture records preserve image selection, wrapping, and filtering.
Embedded PNG/JPEG bytes are retained as encoded data, not decoded or GPU-ready
textures. Image decoding and color-space handling remain later consumer work.

Required extensions are rejected. Optional `KHR_materials_specular` and
`KHR_materials_ior` need explicit `allow_material_fallback = true`; the bundle
lists them in `omitted_extensions` while retaining core material values. This is
the selected inspection setting for Ch03/RPG, not full support for those extensions.
Other extensions, external URIs, matrix-form nodes, sparse accessors, morphs,
CUBICSPLINE tracks, cameras, extra UV/joint sets, tangents/colors, and nonindexed
or nontriangle geometry currently return explicit unsupported errors.

Validation rejects cycles/multiple parents, invalid scene roots and skin bindings,
nonfinite or invalid TRS, singular/nonaffine inverse bind matrices, out-of-range
indices, invalid weights, duplicate channel targets, invalid quaternions, and
non-increasing/nonfinite key times. Animated scales must be positive; static TRS
can retain nonzero signed scales, although humanoid conversion is stricter.

Default settings bound nodes to 4,096, aggregate top-level objects and primitives
to 8,192 each, skin joints to 256 per skin, vertices to one million, indices to
three million, tracks to 4,096, and copied channel keyframes to two million.
The GLB JSON chunk is capped at 8 MiB before parsing. Source/output byte budgets
remain per catalog entry. Output arrays, strings, and retained image bytes are
accounted before allocation; parser data, validation scratch collections, container
capacity overhead, and simultaneous source/output buffers are not one combined
memory bound. Cancellation is checked at import phases and primitive/track budget
claims, with runtime checks before and after importer execution.

## Pose evaluation

`sample(model, clip, elapsed, Playback)` uses elapsed time relative to the earliest
key in the clip. Clamp keeps the last key; Loop wraps at duration. Each track clamps
outside its own time range, and zero-duration clips remain static. STEP holds the
preceding key; LINEAR interpolates vectors and uses normalized quaternion slerp.
Unanimated channels keep node reference values. NaN, infinity, negative elapsed
time, and out-of-range clip indices fail explicitly.

World transforms use a validated parent-before-child traversal, without recursive
descent. Skin matrices are mesh-local:
`inverse(mesh_world) * joint_world * inverse_bind`.
Finite checks reject unusable pose/palette results. These are CPU contracts;
evaluating a palette does not submit or draw anything.

## Canonical humanoid motion

The initial format has 22 named body roles, 15 required. Chest/upper chest, neck,
shoulders, and toes are optional. A `HumanoidProfile` binds roles by unique name
or explicit node index, supplies a model-to-canonical orientation, and identifies
the motion root. Mixamo and RPG presets are conveniences; userland can replace
any mapping or build a profile. Reports list mapped roles, missing optional roles,
and unmapped nodes. Ambiguous names, missing required roles, duplicate assignments,
wrong ancestry, and invalid motion roots fail rather than guess.

The canonical frame is Y-up/Z-forward. Profiles must explicitly correct a source
whose facing differs. The reference defaults to the imported node pose; a caller
can supply a calibrated reference with `from_reference(model: Arc<Model>, local, profile)`.
A compiled rig retains its model, mapping, reference matrices and node-to-role table.
`PoseBuffer` double-buffers local poses, and `HumanoidWorkspace` reuses rotation/world
scratch vectors. `sample`, `capture` and `apply` remain allocating convenience APIs;
playback uses reusable sampling, `capture_into` and `apply_into`. Failed pose-buffer
evaluation does not publish partial/invalid transforms. Supported reference
scales are positive and approximately uniform. Animated scale changes are rejected
instead of silently discarded during humanoid conversion.

Capture converts each mapped bone's world rotation delta from its reference into
the canonical frame. Apply transfers that delta to the target reference orientation
and solves local rotations against the current target parent. This accounts for
different reference bone axes/poses without replacing skinning joints. Target local
limb translations are retained. Hip and root displacement are expressed in source
leg lengths and scaled by target leg length; world hip translation is solved back
into target-parent space.

`RootMotion::InPlace` subtracts horizontal displacement of the configured motion
root while retaining vertical motion and relative hip motion. `Preserve` retains
displacement for inspection or an explicit consumer. Neither mode mutates gameplay
state. Finger/twist/helper nodes keep their reference local pose and follow their
parents. The current body-only mapping does not transfer finger animation, apply
IK/foot locking, distribute twist, guarantee contact, or perfectly preserve all
motions between arbitrary anatomies. Crossfades and snapshot-driven game action
synchronization are implemented in the [production pipeline](2026-09-16-production-animation.md).

## Automation and validation

The headless `inspect_model` example accepts local GLBs and emits JSON counts,
clip ranges, and omitted material extensions. `inspect_retarget` takes a Mixamo
target followed by RPG files, evaluates nine poses per clip, and reports mappings,
root displacement, CPU-skinned bounds, and vertex changes. Both examples bound
source reads at 64 MiB and decoded output at 128 MiB. They do not control a running
game. The standalone native preview uses MCP for playback controls and capture;
see [usage and limits](../../README.md#native-character-preview).

Synthetic regressions cover malformed binary ranges and hierarchy, binding/time
errors, limits, encoded image retention, explicit material fallback, reference
corrections, proportion and basis differences, root policies, mapping overrides,
loop/clamp/STEP/slerp behavior, and skin-matrix composition. Supplied assets are
local validation inputs, not checked-in regression fixtures. Recorded environment
and results belong in the [roadmap](../roadmap.md#client-character-milestone).

## GPU skinning consumer

Skin-capable `Mesh` geometry stores four validated influences per vertex and a joint
count (1..256). `MeshInstance.skin_palette` pins immutable column-major model-space
matrices for the draw. Each matrix is `joint_world * inverse_bind`; the instance
transform is applied after skinning. Unskinned model primitives can use a single
node-world matrix. The renderer validates palette presence/size/finiteness before
acquiring a frame. Static geometry rejects extraneous palettes.

The skinned shader has its own pipeline and 52-byte vertex format. Static draws keep
the existing 20-byte format. Joint palettes use a 16 KiB uniform binding with only
the active prefix written per update. Geometry uploads are keyed by immutable Arc
identity; palette buffers persist per draw slot. Removing scene draws retires their
geometry/palette resources through existing backend ownership. Shading remains unlit
base-color/alpha-cutoff; production materials are a separate concern.
