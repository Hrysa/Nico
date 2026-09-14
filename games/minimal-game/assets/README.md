# Minimal game assets

Game content is divided by runtime responsibility:

- [`logic/`](logic/README.md): authoritative data available to client and server.
- [`presentation/`](presentation/README.md): client-only visual, audio, and UI data.

The presentation root contains the transparent sample PNG, opaque UV checker, and
mesh-only GLB cube used by the paired rendering samples. The headless texture example
also consumes the transparent PNG. Gameplay is authored in the shared Rust package.
Runtime PNG/GLB loading is implemented; general importing and packaging are not.

Engine shaders belong to the separate repository-level
[`assets/presentation/shaders/`](../../../assets/presentation/shaders) root.
`nico-shaderc` generates the committed WGSL artifacts read by the native host.
See [the shader workflow](../../../README.md#shader-workflow).

Loading preserves the logic/presentation split and follows the
[measurement and AI-operation requirements](../../../docs/architecture.md).
Asset outcomes belong in [roadmap phase 3](../../../docs/roadmap.md#3-load-game-assets);
concrete next actions belong in [TODO](../../../TODO.md#next-define-the-reference-game).
