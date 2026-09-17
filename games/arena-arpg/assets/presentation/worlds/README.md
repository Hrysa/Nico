# Meadow scenery

`meadow.world-vis.toml` is client-only content, selected with `--visual-world`.
It loads seven prepared Nature models. Two rocks replace the former obstacle
boxes, nine trees surround the settlement/camp, and 44 grass, flower, and bush
placements leave the main path and combat area open. Settlement houses retain
their existing placeholder geometry.

`models` maps local names to GLB paths relative to the visual definition.
`obstacles.<id>` selects a model for a named obstacle in the server-provided
zone. Without `height_m`, a rock is fitted to the full authoritative box.
With `height_m`, a tree is grounded at the trunk collider's X/Z centre and bottom
Y, scaled uniformly to the authored canopy height. The logic file owns all solid
positions and collider dimensions; the client never creates gameplay colliders.
Unnamed/unmapped obstacles retain box visuals. A different zone ID disables this
definition's imported scenery.

`decorations` contains non-colliding model placements with `position`,
`height_m`, and optional `yaw_radians`. Small plants are decorative; solid tree
trunks use conservative box colliders. Tree canopies and irregular rock surfaces
do not use mesh-accurate collision. Camera collision uses the same server obstacle
boxes as movement prediction and authoritative movement.

Static geometry and palettes are shared and prepared at load/bind time. Frustum
queries cull whole placements. Decorations use the draw budget remaining after
actors and solid scenery, preserving the 256-draw scene limit. The world MCP
state's `environment` field reports loaded model, solid placement, decoration,
and submitted decorative draw counts.

Reproduce runtime GLBs from the preserved source pack:

```powershell
python games/arena-arpg/tools/prepare_nature.py
```

The converter embeds adjacent buffers and PNGs and removes unsupported vertex
color bindings. Geometry, primary UVs, and texture bytes remain intact. Rendering
currently uses base-color textures and the existing 0.5 alpha cutoff, rather
than the source foliage material's 0.2 cutoff. Normal maps, lighting, shadows,
and the publisher's stylized shaders are not implemented by this import.

These assets are from Quaternius's Stylized Nature MegaKit Standard, under CC0.
The notice is retained in [nature/License.txt](nature/License.txt); the original
archive hash and source inventory remain in
[../quaternius/import-manifest.json](../quaternius/import-manifest.json).
