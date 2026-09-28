# Meadow scenery

`meadow.world-vis.toml` is a model library referenced by `meadow.scene.toml`.
The scene owns scenery placements, collider boxes, camera settings, and lights.
The current layout takes the Nature pack's `Preview_1.jpg` as its art reference:
bright green planting in the west, warm autumn foliage in the east, rocky
landmarks, flowers, ferns, and mushrooms along a winding dirt route. It loads 13
prepared Nature models, maps all 13 authoritative obstacles to rock/tree visuals,
and adds 86 non-colliding placements. The former house boxes now look like rocky
outcrops while keeping the same server-owned collision volumes.

The client builds a mottled flat meadow floor with static obstacle contact shading,
using smooth hashed noise at resolved spatial scales to avoid repeating road bands,
distant rolling hills outside the
playable square, a camera-centered sky with cloud wisps, and 64 grass patches at
zone bind time. Each patch combines many blades into one mesh, is frustum culled,
and uses only the remaining draw capacity. Curved grass blades vary in height and
green/gold color while leaving the path and solid obstacle footprints open.
The world camera uses a 200-metre far
plane for the background. Directional sunlight and diffuse ambient lighting are
authored in each scene, including the standalone arena.

Walkable ground remains flat, matching authoritative movement and prediction.
The hills are background scenery, not walkable terrain. Clouds and grass are
static; dynamic shadows, wind, foliage translucency, and the publisher's exact
lighting/post-processing are not implemented. Native GPU captures have been
inspected; user-observed approval and visual parity with the reference are not
claimed. Tested build and scenario evidence belongs in the
[roadmap](../../../../../docs/roadmap.md#meadow-scenery-milestone).

`models` maps local names to GLB paths relative to the library.
Scene entities select these names through `arena.scenery` and set positions through `nico.transform`.
Entities with `arena.box_collider` also define shared collision dimensions.
Without `height_m`, scenery fits the full collider box.
With `height_m`, trees stand at the collider bottom and scale uniformly to the authored canopy height.
Colliders without scenery keep box visuals.

Decoration entities use client scope and provide `height_m` without a collider component.
Transforms support Y rotation. `autumn = true` warms materials whose names contain `leaves`.
Bark, alpha cutouts, and normal maps remain intact.
Camera collision uses the same authoritative obstacle boxes as movement.

Static geometry and palettes are shared and prepared at load/bind time. Identical
encoded images across models share one decoded texture and GPU image identity,
within the existing 256 MiB decoded-image budget. Decorations use the draw budget
remaining after actors and solid scenery, preserving the 256-draw scene limit.
The world MCP state's `environment` field reports loaded models, solid placements,
imported decorations, and submitted decorative draws (including grass patches).

Reproduce runtime GLBs from the preserved source pack:

```sh
python3 games/arena-arpg/tools/prepare_nature.py
```

The converter embeds adjacent buffers and PNGs and removes unsupported vertex
color bindings. Geometry, primary UVs, and texture bytes remain intact. Rendering
uses core metallic/roughness PBR textures and material alpha cutoffs. These assets
are from Quaternius's Stylized Nature MegaKit Standard, under CC0. The notice is
retained in [nature/License.txt](nature/License.txt); the original archive hash and
source inventory remain in [../quaternius/import-manifest.json](../quaternius/import-manifest.json).

## Editing world content

Edit `assets/scenes/meadow.scene.toml` and its referenced TOML sources, then restart affected hosts. The integrated editor has been removed. Arena's shared
presentation and authoring adapter remain library APIs.
