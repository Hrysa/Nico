# Presentation assets

This root is reserved for client-only meshes, textures, materials, shaders,
animations, audio, fonts, and UI data. [`textures/sample.png`](textures/sample.png)
is an original 2x2 RGBA fixture with an opaque white/red/blue pixel and a transparent
pixel, provided under the repository license. The headless
[`load_texture` example](../../../../crates/nico-assets/examples/load_texture.rs)
consumes it; the native client also shares it between the movable world sprite and
fixed HUD icon. The 3D HUD keeps that transparency fixture, while the cube uses
[`textures/uv-checker.png`](textures/uv-checker.png), an original opaque 128x128
checker with A1¨CD4 labels and red/green/blue/yellow corner markers, under the repository
license. Columns increase U and rows increase V. The 3D sample loads
[`meshes/cube.glb`](meshes/cube.glb), an original 24-vertex, 36-index cube fixture
provided under the repository license. It contains positions and UVs with no material.

Presentation assets may map authoritative logic identities to visual or audio
representations. Future dedicated-server packaging must exclude this content;
a packaging pipeline is not implemented yet.

The existing bootstrap shader lives in the repository-level
[`assets/presentation/shaders/`](../../../../assets/presentation/shaders) root,
not this game content root. Reflection and asset-backed shader loading are
deferred; see [the roadmap](../../../../docs/roadmap.md).

See [game asset ownership](../README.md) for loading and observability requirements.
