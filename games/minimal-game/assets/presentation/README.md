# Presentation assets

This root is reserved for client-only meshes, textures, materials, shaders,
animations, audio, fonts, and UI data. It currently contains no runtime content.

Presentation assets may map authoritative logic identities to visual or audio
representations. Future dedicated-server packaging must exclude this content;
a packaging pipeline is not implemented yet.

The existing bootstrap shader lives in the repository-level
[`assets/presentation/shaders/`](../../../../assets/presentation/shaders) root,
not this game content root. Reflection and asset-backed shader loading are
deferred; see [the roadmap](../../../../docs/roadmap.md).

See [game asset ownership](../README.md) for loading and observability requirements.
