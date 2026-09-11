# Minimal game assets

Game content is divided by runtime responsibility:

- [`logic/`](logic/README.md): authoritative data available to client and server.
- [`presentation/`](presentation/README.md): client-only visual, audio, and UI data.

These roots currently contain ownership documentation only. Gameplay is authored
in the shared Rust package; a general asset loader and packaging pipeline are
not implemented.

The running client's bootstrap shader belongs to the separate repository-level
[`assets/presentation/shaders/`](../../../assets/presentation/shaders) root.
It is compiled by `nico-shaderc` and read directly by the native host.
See [the shader workflow](../../../README.md#shader-workflow).

Future loading must preserve the logic/presentation split and follow the
[measurement and AI-operation requirements](../../../docs/architecture.md).
The first service-backed asset load is scoped by
[roadmap phase 3](../../../docs/roadmap.md#3-load-game-assets); concrete next actions
belong in [TODO](../../../TODO.md#next-asset-loading).
