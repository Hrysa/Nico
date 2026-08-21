# Nico TODO

## Portable asynchronous I/O

The runtime and game-facing APIs must not depend directly on Tokio or another
platform executor. Native, web, console, and test hosts select their own backend.
The authoritative world remains owned and mutated by the runtime thread.

### Architecture

- [ ] Define the minimum asynchronous I/O boundary without selecting a library.
- [ ] Define owned request IDs, completion values, cancellation, and error types.
- [ ] Specify when completions become visible to systems: stage boundary or next
      frame.
- [ ] Specify queue capacity, backpressure, overflow, and shutdown behavior.
- [ ] Ensure asynchronous tasks cannot capture or mutate `World` directly.
- [ ] Define the system that drains completions and applies them to `World` or
      publishes runtime events.
- [ ] Validate entity-targeted completions against generational entity IDs before
      applying them.

### Service boundaries

- [ ] Keep domain contracts separate instead of exposing one universal I/O enum.
- [ ] Define an asset-loading service when the asset pipeline is designed.
- [ ] Define a network transport service when networking is designed.
- [ ] Define save-storage and platform-service contracts when required.
- [ ] Keep CPU-bound jobs separate from asynchronous I/O operations.

### Platform adapters

- [ ] Design host-side backend injection without exposing backend-specific types
      through `nico-runtime`.
- [ ] Evaluate Tokio as the first native client/server backend.
- [ ] Provide a deterministic immediate or manually driven backend for tests.
- [ ] Evaluate browser APIs and the JavaScript event loop for a web backend.
- [ ] Implement console backends only with access to each platform SDK; do not
      assume Tokio support.

### Verification

- [ ] Test request submission, completion delivery, cancellation, and errors.
- [ ] Test that late completions cannot update destroyed entities.
- [ ] Test orderly shutdown with pending operations.
- [ ] Test bounded queues under producer overload.
- [ ] Test that runtime behavior remains deterministic when completion order is
      controlled by a test backend.

## Asset import pipeline

An asset is a stable logical resource. An artifact is a reproducible,
target-specific runtime representation. Source files and authoring metadata must
not be runtime dependencies.

### Identity and metadata

- [ ] Define path-independent `AssetId` and stable IDs for subassets emitted from
      compound sources such as glTF or FBX files.
- [ ] Define canonical `TargetProfile` identity for desktop, mobile, server, and
      other capability groups without encoding individual GPU models.
- [ ] Keep only authored state in version-controlled `.meta` files: asset ID,
      importer selection, import settings, explicit dependencies, and overrides.
- [ ] Store source hashes, importer versions, discovered dependencies, generated
      variants, and artifact hashes in a derived import database rather than
      `.meta` files.

### Artifact keys and formats

- [ ] Define `ArtifactKey` as a canonical hash of source content, settings, target
      profile, artifact schema, importer implementation, toolchain, and dependency
      artifact keys.
- [ ] Define `ObjectId` separately as the hash of completed artifact bytes.
- [ ] Define a build index mapping `ArtifactKey` to `ObjectId` and store artifact
      objects under `objects/<ObjectId>`.
- [ ] Define an artifact header containing type, schema version, compatibility,
      payload integrity information, and required dependencies.
- [ ] Keep runtime format compatibility separate from importer versioning.

### Import graph and execution

- [ ] Define the minimal `ImportRecipe` and `Importer` contracts without choosing
      importer libraries or file formats.
- [ ] Build a dependency graph, invalidate consumers through dependency artifact
      keys, and report dependency cycles.
- [ ] Generate variants on demand for requested target profiles instead of
      eagerly importing every possible variant.
- [ ] Specify deterministic output requirements and diagnose unequal outputs
      produced from an identical `ArtifactKey`.
- [ ] Define cancellation, failure cleanup, progress reporting, and concurrent
      import coordination.

### Storage and distribution

- [ ] Define `ArtifactStore` and `BuildIndex` contracts usable by local and remote
      backends.
- [ ] Use atomic object writes and verify downloaded content against `ObjectId`.
- [ ] Define remote authentication, upload policy, cache retention, and garbage
      collection based on artifacts reachable from manifests.
- [ ] Keep driver-, GPU-, OS-, and device-specific pipeline caches local.
- [ ] Support packing content-addressed artifacts into shipping bundles without
      changing their logical identities.

### Runtime boundary

- [ ] Generate a target-specific resolved manifest mapping `AssetId` to artifact
      descriptors, object IDs, dependencies, and package locations.
- [ ] Make runtime loading consume only resolved manifests and artifacts, never
      source files, `.meta` files, import recipes, or target-profile logic.
- [ ] Preserve the distinction between shared logic assets and client-only
      presentation assets when producing client and server manifests.
- [ ] Connect runtime asset loading to the portable asynchronous I/O service after
      both contracts are reviewed.

### Verification

- [ ] Test cache hits, misses, corruption detection, and transitive invalidation.
- [ ] Test stable asset and subasset identity across moves and reimports.
- [ ] Test profile-specific resolution and client/server manifest separation.
- [ ] Test concurrent imports, interrupted writes, remote failures, and recovery.
- [ ] Test that shipping runtime packages contain no authoring-only dependencies.

## Deferred decisions

- Async runtime or executor library.
- Channel implementation.
- Native networking library.
- Web and console implementation details.
- Whether the low-level abstraction needs to be a standalone crate; a new crate
  requires a demonstrated dependency boundary.
- Importer and source-format libraries.
- Artifact serialization and shipping bundle formats.
- Local import database implementation.
- Remote object-store and build-index providers.
- Whether import contracts justify a new crate beyond `nico-assets`.
