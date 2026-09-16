# Extensible asset import

Status: importer interface, registry, built-in migration, and runtime adapter
implemented 2026-09-16. Model import and CPU humanoid consumers are implemented
in the [model contract](2026-09-16-model-animation.md); GPU skinning is implemented; production playback and arena rendering are pending.
This follows the user's
requirement to develop importers in userland and the
[asset import review](../reviews/2026-09-16-asset-import.md). The existing
[texture lifecycle](2026-09-14-texture-assets.md) and
[static mesh loader](2026-09-14-mesh-assets.md) remain current behavior.
This plan replaces their sealed decoder arrangement in the implementation, while
retaining their identity, ownership, publication, and shutdown contracts.

## Extension boundary

Userland means a game crate or external Rust crate linked by the host. It can
implement and configure an importer without modifying engine crates. The first
version uses ordinary Rust trait implementations registered during app setup;
dynamic-library plugins, runtime registration replacement, and hot reload are not
required. Registration order must not silently determine format selection.

Separate four responsibilities:

| Layer | Responsibility |
| --- | --- |
| Source access | Bounded primary bytes from a configured content root and source metadata |
| Importer | Decode a supported format into validated, owned CPU data with typed settings |
| Asset store | Identity, leases, scheduling, generations, failure/retry, publication, shutdown |
| Animation processing | Generic pose evaluation and humanoid mapping/retargeting of decoded skeletons and clips |

Rendering and gameplay are not importer responsibilities. An importer receives no
world, GPU device, or runtime service handle. Hosts own composition; reusable
importers may live in engine or third-party crates, and game-specific importers
may live in the game.

## Typed interface

The public contract lives in `nico_assets::import`:

```rust
pub trait AssetImporter: Send + Sync + 'static {
    type Output: Send + Sync + 'static;
    type Settings: Clone + Send + Sync + 'static;

    fn descriptor(&self) -> ImporterDescriptor;
    fn validate_settings(&self, settings: &Self::Settings) -> Result<(), ImportError> {
        Ok(())
    }
    fn import(
        &self,
        context: &mut ImportContext<'_>,
        settings: &Self::Settings,
    ) -> Result<Self::Output, ImportError>;
}
```

`ImporterDescriptor` supplies an explicit namespaced ID, implementation version,
supported source formats, and diagnostic metadata. These describe capability;
extensions alone do not prove the input is valid. Successful import returns T;
failures contain bounded structured diagnostics. Successful warning collections
and dependency-read records are not implemented. The store wraps successful output
in an immutable `Arc`. Public validated constructors let external
importers produce engine assets without access to private fields.

`ImportRegistry<T>` holds different importer implementations whose output is T.
Registration returns a typed token retaining the importer's settings type. Catalog
entry construction accepts that token, an asset ID, source path, typed settings,
and a per-entry `ImportBudget`;
it internally erases the importer/settings pair into an owned worker request.
Reject duplicate importer IDs, foreign-registry tokens, duplicate asset IDs, and
invalid sources before spawning a worker or mutating the app. No public `Any`
downcasts or JSON configuration are necessary for ordinary Rust consumers.

For example, userland can register `MyTextureImporter` with `Output = Texture`
alongside `PngImporter` in the same texture registry. It can independently install
`AssetStore<MyDialogue>` using a user-defined output type. Neither requires an
implementation of a Nico trait on the output type. Retain one store/worker per
output type initially, with internal completion routing keyed by that type and
diagnostic service labels owned by store installation, not decoder selection.

Explicit catalog entries select the importer. Optional extension-based convenience
must reject ambiguous candidates rather than use first/last registration wins.
Settings are immutable per entry. Importing one path with two configurations uses
two asset IDs; requesting an existing ID continues to share its entry. Importer
IDs and versions are provenance, not persistent cache invalidation by themselves.
Persistent caches will need source/dependency fingerprints and a defined settings
fingerprint; they are deferred rather than based on unstable Rust hashes.

## Source access, limits, and diagnostics

The context borrows the primary source bytes and exposes cooperative
cancellation checks. Shared source reading enforces a primary input byte bound
before decoding. Keep trusted local-root routing; it is not a filesystem sandbox.

Begin with embedded PNG/GLB data and no external dependency reads. Reject external
references explicitly. When an importer needs sidecars, extend the context with
source-relative bounded reads that record dependencies, reject unsupported schemes
and lexical root escapes, and bound both read count and cumulative bytes. Do not
allow recursive asset-store requests or blocking waits on the same serial worker.
An importer may assemble its owned bundle from source bytes; a general asset
dependency graph is separate work. This keeps the initial interface small without
forcing importers to open arbitrary paths as their normal contract.

`StoreLimits` bounds resident entries; dispatch remains serial. `ImportBudget`
bounds input and decoded output. Error code/message/location fields have fixed
128/1024/1024-byte bounds and disclose truncation. Separate these from importer settings (dimensions, vertex counts,
joint counts, keyframes, channels, and format-specific policy). Validate overflow
and limits before allocating. A budget helper supports cooperative accounting;
it cannot enforce all memory allocations inside arbitrary Rust or third-party
decoder code. State which working buffers and decoder allocations each importer
actually bounds. Ch03 needs a deliberately chosen input budget above its current
22,157,716 bytes; do not silently disable the existing limits.

Import errors carry a category (I/O, malformed, unsupported, invalid settings,
limit exceeded, or cancelled), importer ID, stable diagnostic code,
and bounded message/location fields. Keep store errors such as unknown asset,
capacity, and worker unavailable distinct. Preserve structured failure in status
and explicit retry; do not require extending an engine enum for every new format.

Extension code is trusted in-process Rust. Cancellation is cooperative, and joining
shutdown may wait for an importer that does not return. Initially preserve the
worker-panic policy: detect worker loss and fail pending entries explicitly; do
not silently retry arbitrary user code. Document this limit and test it.

## Model and humanoid consumers

`ModelGlbImporter` now produces one validated model bundle containing nodes,
meshes/primitives, materials/encoded images, skin bindings, and clips for an explicit
GLB subset. `nico-animation` consumes that data for pose evaluation and canonical
humanoid conversion. Its profiles are configured independently of source decoding.
The [model/animation contract](2026-09-16-model-animation.md) owns the implemented
subset, mapping rules, and limitations. The native GPU preview is available; production playback and arena integration remain pending.

The bundle retains its related CPU data atomically. Independent subasset IDs,
cross-bundle sharing, and external dependency scheduling remain future ownership
work; no heterogeneous multi-store transaction is introduced.

## Features and migration

Identity, CPU asset contracts, and the import trait/context remain independent of
runtime and decoder packages. `png-import` and `gltf-import` enable built-in
importers independently; `runtime-loading` enables the store adapter. `loading`
remains a compatibility umbrella enabling all three.

PNG and restricted static GLB use ordinary importer implementations.
`TextureStore`/`MeshStore` aliases and existing `install` convenience methods are
retained; those constructors build registries internally from legacy `AssetLimits`.
`install_with_importers` accepts an explicit registry and `StoreLimits`. The sealed
`DecodeAsset` trait is removed. Public loading failures from decoding now use
`AssetError::Import { importer, error }`; legacy decoder error variants remain for
compatibility but are not the public registry failure contract. Static mesh format
restrictions remain unchanged.

Runtime publication, generation rejection, lease release, snapshot retention,
bounded dispatch, explicit retry, and joined shutdown remain unchanged. Keep
native/MCP transport in engine host code. Importer registration and errors should
be available as owned structured reports; any preview/import operations needed
for character development use bounded requests and the existing bridge path.

## Acceptance and validation

Public integration tests implement an importer outside the library module
for an existing engine type and one for a custom type. Exercise two importers
producing the same output type, different settings for the same source, explicit
selection, registration failures without partial installation, structured errors,
retry, cancellation/stale completion, worker failure, and shutdown. No renderer
or native window is needed to validate this interface.

Retain PNG/static-GLB regressions and verify feature combinations: base assets,
import contracts without runtime, each built-in importer independently, and the
compatibility loading feature. Run asset tests and strict package Clippy, then
workspace checks when the implementation changes public callers. Later model and
humanoid work adds its own bounded import and visual retargeting evidence.

Implementation order is extensible importer/store integration, migrated built-ins
and external-consumer tests, generic character import, then canonical humanoid
conversion and skinning. Concrete actions are maintained in
[TODO](../../TODO.md#next-client-humanoid-models-and-animation).
