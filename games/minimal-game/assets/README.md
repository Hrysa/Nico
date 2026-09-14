# Minimal game assets

Game content is divided by runtime responsibility:

- [`logic/`](logic/README.md): authoritative data available to client and server.
- [`presentation/`](presentation/README.md): client-only visual, audio, and UI data.

The presentation root includes a tiny sample PNG consumed by the headless texture
loading example. Gameplay is authored in the shared Rust package. Runtime PNG loading
is implemented; a general import and packaging pipeline is not.

The running client's quad shader belongs to the separate repository-level
[`assets/presentation/shaders/`](../../../assets/presentation/shaders) root.
It is compiled by `nico-shaderc` and read directly by the native host.
See [the shader workflow](../../../README.md#shader-workflow).

Future loading must preserve the logic/presentation split and follow the
[measurement and AI-operation requirements](../../../docs/architecture.md).
The first service-backed asset load is scoped by
[roadmap phase 3](../../../docs/roadmap.md#3-load-game-assets); concrete next actions
belong in [TODO](../../../TODO.md#next-paired-rendering-samples).
