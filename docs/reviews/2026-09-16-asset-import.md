# Asset import extensibility review

Scope: `nico-assets` identity, loading, PNG/GLB decoding, and the proposed humanoid
pipeline. Reviewed 2026-09-16. Findings concern extension requirements; the narrow
PNG/static-mesh implementation was deliberate and remains the current capability.
The [refined design](../plans/2026-09-16-extensible-asset-import.md) addresses these
findings. The dated implementation update below records resolved extension
findings and remaining model work. Actions belong in [TODO](../../TODO.md).

## Findings

These findings describe the pre-refactor implementation reviewed on 2026-09-16.

### High: userland cannot implement an importer

`loading.rs` seals `DecodeAsset` to `Texture` and `Mesh`, and constrains
`AssetStore<T>` to that trait. An external crate cannot add its own asset type.
Simply removing sealing is insufficient: putting decoding on `Texture` still
prevents an external crate from implementing another decoder for that foreign
type, and only allows one implementation per type. Separate importer identity
and settings from output asset type; accept multiple registered importers for T.

### High: the existing mesh contract cannot represent characters

`loading/mesh.rs::decode_bytes` rejects skins, animations, materials/images, and
multi-node or transformed scenes. `MeshVertex` holds only position and UV.
Adding bone-name mapping alone cannot import the supplied characters. Introduce
generic model, skeleton, skin, and clip data before humanoid interpretation, with
explicit validation and bounds. Preserve the existing static-mesh consumer.

### Medium: import policy and infrastructure are coupled

`DecodeAsset::decode` receives a filesystem path and global `AssetLimits`.
PNG and GLB duplicate bounded file reading. Limits mix texture dimensions and
mesh counts; errors encode built-in formats. There is no importer instance,
per-entry typed configuration, importer version, cancellation context, or generic
structured import diagnostic. External importers would otherwise have to duplicate
policy and mislabel errors. Move source reading to a shared context and keep
format-specific limits/settings with each importer.

### Medium: import interfaces currently require runtime and both decoder packages

The `loading` feature enables `nico-runtime`, `png`, and `gltf`; the decoder
contract lives inside that feature. An offline converter or external importer
cannot use a dedicated runtime-free import API. Split the public CPU import
contract, optional built-in importers, and runtime store adapter, retaining the
existing `loading` feature as a compatibility umbrella.

### Medium: source identity is too narrow for configurable model import

The catalog is `AssetId -> PathBuf`. It cannot select among importers targeting the
same output type or distinguish configurations of one source. Models also contain
multiple related objects and clips; importing each independently can duplicate
decoding or expose inconsistent partial content. Record importer/configuration in
catalog entries and initially publish one immutable model bundle atomically.
Do not add a general dependency scheduler without a concrete consumer.

## Contracts to retain

Typed handles and owning leases, shared requests, immutable ready `Arc`s,
generation checks, explicit retry, bounded serial dispatch, runtime-boundary
publication, and joined shutdown already establish the needed lifecycle.
Workers receive owned requests and never the world. Existing tests cover shared
ownership, release/reacquisition, stale results, failures, cancellation, decoder
bounds, backend loss, and shutdown. Extend this foundation rather than replace it.

The current worker checks cancellation before and after decoding. Shutdown may
wait for decoding to finish. Giving user code a cooperative cancellation context
does not create preemption or sandbox arbitrary importer code.

## Review outcome

Refine import extensibility before character import. Prove an external importer
for an existing engine type and another for a user-defined type through public-API
integration tests. Then introduce generic model import and humanoid conversion as
separate consumers. No production Rust changes are included in this review.

**Baseline validation (Windows, 2026-09-16):**
`cargo test -p nico-assets --all-features --target-dir target/asset-import-review`
passed all 19 tests. This verifies the existing implementation, not the proposed
importer interface. Documentation whitespace and local links were also checked.

## Implementation update (2026-09-16)

The sealed decoder, infrastructure coupling, feature coupling, and per-entry
configuration findings are resolved by `AssetImporter`, `ImportRegistry<T>`,
`ImportBudget`, and `AssetStore::install_with_importers`. PNG and static GLB use
the public interface. The output type has no importer-trait requirement; external
tests provide both custom output types and additional texture importers. Input
reading, cancellation, provenance, and bounded errors are shared. Separate decoder
and runtime features preserve the `loading` compatibility feature.

The subsequent [model/animation implementation](../plans/2026-09-16-model-animation.md)
resolves the model representation finding with a separate validated model bundle
and importer; the static mesh consumer retains its subset. CPU humanoid mapping
now consumes that interface. A standalone GPU-skinned native preview now consumes the model/animation APIs;
production playback and arena integration are implemented in the
[production pipeline](../plans/2026-09-16-production-animation.md). Success warning
collections, external dependency reads, persistent caching, and hot reload remain deferred. Validation
evidence is owned by the [roadmap](../roadmap.md#3-load-game-assets).
