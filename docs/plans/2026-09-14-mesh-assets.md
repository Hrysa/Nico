# Mesh assets and the 3D sample

**Implementation update (2026-09-16):** Static GLB decoding now implements the
[public importer contract](2026-09-16-extensible-asset-import.md), with independently
selectable decoder/runtime features. The restricted geometry subset and existing
sample behavior below are preserved; this does not add character import.

Status: implemented 2026-09-14. Validation evidence belongs in the
[roadmap](../roadmap.md#4-display-the-game-world).

## Asset contract

`Mesh` owns immutable positions, UVs, and triangle indices, without backend types.
`MeshStore` and `TextureStore` are specializations of `AssetStore<T>`, sharing the
[texture lifecycle](2026-09-14-texture-assets.md): copyable identity handles, owning
leases, bounded background work, runtime publication, explicit retry, and joined
shutdown. Each store has one worker and a separate typed service completion, so
matching request IDs cannot consume another asset type's results. Notification events
do not retain decoded content. Snapshots may explicitly pin ready content with `Arc`.

The shipping mesh format is a deliberately restricted GLB: one embedded buffer, one
mesh, one indexed triangle primitive, float32 POSITION and TEXCOORD_0, unsigned scalar
indices, and at most one identity mesh node. External buffers, required extensions,
materials/images, animations, skins, morph targets, sparse/normalized attributes,
node hierarchies, and transformed nodes are rejected. The game assigns a separately
loaded PNG; this loader does not import a glTF scene or resolve dependencies.

Validation checks accessor offsets, counts, strides, and buffer-view ranges before
constructing readers, then rejects nonfinite attributes and out-of-range indices.
Defaults bound each store to 64 entries, each input to 16 MiB, decoded mesh data to
64 MiB, vertices to 250,000, and indices to 750,000. These are separate bounds rather
than a total process-memory budget. Cancellation retires publication but does not
interrupt active local I/O; shutdown joins the worker. Paths are trusted host config.

## Rendering and sample

`Scene3d` contains a perspective camera and mesh instances with translation, unit
quaternion orientation, positive uniform scale, tint, and optional ready mesh/texture
references. Camera poses also use unit quaternions; the renderer inverts the pose
directly, supporting roll and vertical views without fixed-up reconstruction.
The renderer uses right-handed view/projection matrices with zero-to-one depth,
indexed draws, a depth target recreated when extent changes, and a distinct uniform
buffer for each instance. Draws are capped at 256; geometry has the same vertex/index
bounds as the default loader. Invalid cameras or transforms return errors.

Unlit texture sampling uses nearest filtering and alpha cutoff 0.5, writing opaque
color with depth. Missing geometry draws a tetrahedron; missing textures use the
existing checkerboard. Lighting, PBR, skinning, animation, and sorted transparent
meshes are deferred.

The mesh renderer shares the quad renderer's texture cache and binding layout.
The mesh pass clears the target; the quad pass loads it and overlays the HUD without
depth testing, followed by one presentation. Weak CPU cache keys permit retirement;
providers retain resources referenced by recorded/submitted work.

The client selects `--sample 2d|3d` and uses the same shared movement state. The cube
maps XY positions to Z=0; both modes retain the same fixed HUD. Structured sample
tools expose CPU readiness separately from presentation counts and queue bounded
edits for runtime application, including mesh release/reload, camera position, and yaw.
Per-command outcomes distinguish accepted requests from successful application.

## Validation texture update (2026-09-14)

The cube now uses a separate opaque UV checker with A1–D4 labels and colored corners.
The HUD keeps the transparent 2x2 fixture. Both textures use the existing store and
GPU cache; shared functionality does not require identical content. Texture controls
release/reload or retry both in 3D mode, with separate checker state in MCP snapshots.
The mesh shader still supports alpha cutoff; the checker makes face orientation and
solid geometry easier to inspect without seeing opposite faces through cutouts.
