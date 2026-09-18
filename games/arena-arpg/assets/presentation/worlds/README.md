# Meadow scenery

`meadow.world-vis.toml` is client-only content, selected with `--visual-world`.
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
configured only for this world; the standalone arena keeps its existing lighting.

Walkable ground remains flat, matching authoritative movement and prediction.
The hills are background scenery, not walkable terrain. Clouds and grass are
static; dynamic shadows, wind, foliage translucency, and the publisher's exact
lighting/post-processing are not implemented. Native GPU captures have been
inspected; user-observed approval and visual parity with the reference are not
claimed. Tested build and scenario evidence belongs in the
[roadmap](../../../../../docs/roadmap.md#meadow-scenery-milestone).

`models` maps local names to GLB paths relative to the visual definition.
`obstacles.<id>` selects a model for a named obstacle in the server-provided
zone. Without `height_m`, a rock is fitted to the full authoritative box.
With `height_m`, a tree is grounded at the trunk collider's X/Z centre and bottom
Y, scaled uniformly to the authored canopy height. The logic file owns all solid
positions and collider dimensions; the client never creates gameplay colliders.
Unnamed/unmapped obstacles retain box visuals. A different zone ID disables this
definition's imported scenery and procedural landscape.

`decorations` contains non-colliding model placements with `position`,
`height_m`, and optional `yaw_radians`. Both obstacle bindings and decorations
accept `autumn = true`: only materials named with `leaves` receive a warm RGB
multiplier; bark, alpha cutouts, and normal maps are retained. Background decorative
trees have no collision; solid trunks use the server's conservative boxes.
Camera collision uses the same server obstacle boxes as authoritative movement.

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

## Editor authoring

`cargo run -p nico-editor -- --project games/arena-arpg` opens this world through
Arena's registered authoring adapter. It shares the client environment/landscape
code and previews the static world without a server. Obstacle and decoration
transforms use the rules shown in the Inspector; Save updates the original world
sources. Spawn and quest data remain inspectable without being simulated. The
editor's Play control builds and runs the declared client target as a separate
process, which for the multiplayer world still expects its server; Stop terminates
the launched client. See the
[editor workflow](../../../../../README.md#game-code-and-authored-scenes) for save,
reload, asset refresh, and formatting limits.
