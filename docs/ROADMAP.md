# Roadmap

## Version 0.2.0

### Asset identity and content pipeline

Version 0.2.0 establishes a general asset pipeline before script UUID attachment is introduced. Scripts are the first imported asset type, but metadata, identity, storage, dependencies, and runtime loading must remain independent of C# and rendering APIs.

The persistent-to-runtime flow is:

```text
AssetId
  -> AssetDatabase record
  -> imported artifact location
  -> virtual filesystem stream
  -> runtime resource handle or script Type
```

An `AssetId` answers which persistent project asset is referenced. A project-relative path locates editable source content. A package entry locates cooked bytes. A runtime handle identifies a loaded CPU or GPU resource. These identities must not be collapsed into one type.

### Phase 1: General asset metadata foundation

- Add an immutable `AssetId` value type wrapping a UUIDv7 `Guid`, with parsing, formatting, equality, and JSON serialization.
- Keep asset identity separate from scene-object identity (`NodeId`), sub-assets, native windows, and transient renderer handles.
- Store authoritative metadata beside each source asset as `<asset-name>.<extension>.meta`.
- Use a versioned, importer-neutral metadata envelope containing `version`, `id`, `importer`, and importer-owned `settings`.
- Treat compiler results, resolved type names, diagnostics, hashes, and artifact paths as derived cache data rather than authoritative metadata.
- Generate metadata for newly discovered supported assets and write it atomically.
- Preserve an ID when an asset and its sidecar move or are renamed together.
- Detect duplicate IDs caused by copying source and sidecar files; preserve the original asset and assign a new ID to the copy deterministically within the scan transaction.
- Report missing, malformed, unsupported-version, orphaned, and duplicate metadata through structured diagnostics instead of silently discarding identity.
- Restrict metadata and source operations to normalized project-relative paths beneath the opened project root.

Initial metadata shape:

```json
{
  "version": 1,
  "id": "9cd98fe4-55eb-46ad-a856-78c0900ef530",
  "importer": "csharp-script",
  "settings": {}
}
```

### Phase 2: Asset database and editor file operations

- Add an `AssetDatabase` that owns the in-memory `AssetId <-> normalized project-relative path` index.
- Expose lookup by ID and path, asset enumeration, importer identity, import status, diagnostics, and asset-change events.
- Make the index authoritative only for the current editor session; any central on-disk index is a disposable startup cache regenerated from sidecars and import results.
- Scan file metadata first so the FileSystem panel can appear without waiting for compilation or resource import.
- Incrementally process filesystem watcher changes and reconcile events with editor-initiated operations.
- Move, rename, duplicate, and delete source assets and sidecars as one editor transaction.
- Update the FileSystem tree through asset database events instead of independently rebuilding filesystem state.
- Add collision, external move, lost-sidecar, case-sensitivity, interrupted-write, and project-root escape tests on Windows, macOS, and Linux path semantics.

### Phase 3: Importer and artifact contracts

- Add importer registration independent of asset extensions and runtime resource types.
- Define importer contexts, versioned settings, source fingerprints, artifacts, stable sub-asset keys, dependencies, warnings, and errors.
- Select importers explicitly from metadata after initial extension-based metadata creation.
- Store generated artifacts under a disposable project cache keyed by asset ID, importer version, settings, target platform, and source/dependency fingerprint.
- Write artifact manifests atomically and retain the last successful artifact when a new import fails.
- Reimport only changed assets and invalidate transitive dependents through a dependency graph.
- Support cancellation and bounded parallel importing without allowing two jobs to publish the same asset generation.
- Begin with `csharp-script` and `raw` importers; leave texture, shader, model, audio, material, scene, and prefab importers on the same contracts.
- Represent outputs within a multi-output source using stable importer-defined sub-asset keys, such as `mesh/Body` or `animation/Walk`.

### Phase 4: Virtual filesystem and package-ready storage

- Define a read-oriented virtual filesystem with directory, package, memory, and built-in engine mounts.
- Keep virtual paths human-readable for browsing, diagnostics, console commands, and mod tools, but never treat them as persistent asset identity.
- Separate asset resolution from storage: `AssetId` resolves to an artifact location, and storage opens that location as a stream.
- Use loose imported artifacts in the Editor and allow the same resource loaders to read cooked package entries in Player builds.
- Design the package index for primary lookup by `AssetId` plus sub-asset key, with an optional virtual-path-to-ID alias table.
- Record entry offset, stored size, original size, compression, checksum, artifact type, and target-platform version.
- Support mount priority so project overrides, DLC, patches, and mods can shadow virtual paths without changing serialized asset references.
- Keep package writing and final content cooking in the build layer; the asset database must not depend on a particular archive format.

### Phase 5: Runtime resource boundary

- Add a resource manager that translates persistent asset references into stable runtime handles.
- Keep `Engine.Graphics` and `Engine.Graphics.Silk` unaware of `.meta` files, source paths, package indexes, and GUID resolution.
- Continue using `TextureHandle`, `MeshHandle`, material handles, and other focused runtime identifiers in render queues and Vulkan code.
- Return fallback resources while asynchronous loads are pending or failed.
- Preserve stable runtime handles while replacing their underlying resource after asset reimport or hot reload.
- Cache loaded resources by asset ID, sub-asset key, artifact generation, and load policy.
- Release CPU, GPU, and streaming resources through explicit ownership and fence-safe lifetimes.

### Phase 6: Script asset importer and catalog

- Treat a C# source file as a normal asset with the `csharp-script` importer.
- Initially require exactly one public, concrete, non-generic `SceneScript` class with a public parameterless constructor per attachable source asset.
- Use compiler semantic analysis rather than string or regular-expression matching to discover script symbols, inheritance, source locations, and diagnostics.
- Analyze and compile against the generated game script project so editor discovery uses the same references, defines, nullable settings, and language version as Play builds.
- Generate a catalog mapping `AssetId` to compiled `Type`; do not serialize assembly-qualified type names into scenes.
- Preserve the previous successful runtime catalog when compilation fails while exposing current diagnostics and marking affected entries stale or invalid.
- Share `Engine.Core` and `Engine.Scripting` from the default load context so collectible game assemblies retain compatible `Node` and `SceneScript` type identity.
- Make dragging a valid script asset from FileSystem onto the Inspector script field assign its `AssetId`.
- Display the resolved script name and validation state in the Inspector while persisting only the asset reference.
- Reject non-script sources, ambiguous multi-script files, abstract classes, open generic classes, and types without an accessible parameterless constructor with actionable diagnostics.

### Phase 7: Runtime scripting split

- Replace `Node.ScriptType` with an optional script asset reference and update scene serialization and example content without a long-term type-name compatibility layer.
- Extract script attachment and lifecycle execution from the Editor-owned `GameScriptHost` into an `Engine.Scripting` runtime host.
- Define an `IScriptTypeCatalog` contract that resolves script asset IDs to validated runtime types.
- Keep project watching, `dotnet build`, compiler diagnostics, progress UI, and collectible assembly loading in the Editor.
- Let Player builds consume a precompiled/generated catalog without invoking the SDK or scanning source files.
- Resolve each node's script ID through the catalog, instantiate the script, bind `Owner` and `Scene`, then invoke `OnReady`, `OnUpdate`, and `OnDestroy` through the shared runtime host.
- Unload the Editor's collectible script context on Stop after script destruction and disposal, while packaged Player assemblies use their normal application lifetime.

### Phase 8: Cooking and packaged Player loading

- Traverse scene and asset dependencies from configured build roots and include only reachable artifacts unless explicitly marked always-include.
- Cook platform-specific artifacts before package assembly and produce deterministic manifests and package ordering.
- Map each asset ID and sub-asset key to a package entry without exposing package offsets to scene, material, script, or gameplay data.
- Bundle the compiled game script assembly and generated script catalog as build artifacts.
- Load Player resources through the same resource-manager API used by the Editor, differing only in asset resolver and storage implementations.
- Validate artifact versions, checksums, required engine versions, missing dependencies, and duplicate package entries before starting the game.
- Leave room for package splitting, streaming groups, patches, DLC, encryption/signing, and mod mounts without making them requirements for the first package format.

### Validation and completion criteria

- Renaming or moving an asset with its sidecar does not change its ID or break a scene reference.
- Copying an asset with its sidecar produces a distinct ID without modifying the original asset.
- Deleting an asset produces a stable missing-asset diagnostic while preserving references for potential restoration.
- Script class and namespace renames do not require scene rewrites.
- A script compilation failure does not corrupt metadata, scenes, the last successful artifact set, or the running Editor.
- Editor loose-file loading and Player package loading produce equivalent runtime resources from the same asset reference.
- Rendering consumes runtime handles only and contains no metadata, GUID, VFS, or package lookup logic.
- Asset imports are incremental, cancellable, atomic at publication, and covered by dependency invalidation tests.
- Sidecar files are the source of truth; central indexes, compiler catalogs, and imported artifacts can be deleted and regenerated.

### Explicitly deferred

- Production texture, shader, model, audio, material, scene, and prefab importers beyond the contracts required to add them cleanly.
- Distributed asset processing and remote artifact caches.
- A final Source 2 VPK-compatible format; 0.2.0 requires package-ready abstractions and a minimal Nico package, not wire compatibility with VPK.
- Advanced package encryption, signing, differential patch generation, and network streaming.
- Multiple attachable script classes per source file unless stable type-level sub-assets are designed.
- Automatic migration of arbitrary third-party path- or type-name-based serialized formats.

## Version 0.3.0

### Implemented foundations

Persistent tool headers detach on double-click and dock again when their native window closes. The same `UIHost`/`DetachedToolWindow` boundary applies to future profiler, debugger, and settings tools when those components are introduced.

### Generation-based UI geometry caching

- Assign a monotonically increasing generation whenever UI layout or visual state changes.
- Traverse the UI tree and rebuild semantic draw commands and vertices only for a new generation.
- Track the uploaded generation independently for each frame-in-flight UI buffer.
- Upload a generation at most once to each frame buffer, then reuse its mapped GPU geometry while the UI remains unchanged.
- Preserve full-frame UI drawing and the existing content/overlay ordering; the optimization targets CPU traversal, tessellation, font vertex generation, and redundant memory copies.
- Keep component-level dirty rectangles and partial swapchain redraw outside this work unless profiling demonstrates a need.

### Multi-window editor

- Add a reusable `UIHost` boundary so editor component trees can be hosted by either the main dock layout or an independent native tool window.
- Support multiple native presentation windows from one renderer and shared Vulkan device.
- Give each native window its own surface, swapchain, framebuffers, input router, UI root, DPI scale, resize lifecycle, and presentation lifecycle.
- Allow persistent editor tools to detach, move to another monitor, and dock again.
- Prioritize detachable Scene and Game viewports, followed by Inspector, Hierarchy, FileSystem, profiler, debugger, and settings.
- Keep temporary workflows such as Open Scene as in-window modal overlays.
- Avoid creating a separate engine or Vulkan device for each tool window; share queues, pipelines, fonts, and immutable GPU assets where valid.

### Dynamic glyph generation and runtime cache

- Generate glyphs on demand for the codepoints, font faces, sizes, weights, and DPI scales actually requested by visible UI.
- Cache rasterized glyph metrics and atlas placements so unchanged text reuses existing CPU and GPU resources.
- Upload only newly generated or replaced atlas regions instead of rebuilding or transferring the complete atlas.
- Keep glyph generation independent of layout and window ownership so multiple windows can share immutable font data safely.
- Preserve antialiasing quality with oversampled rasterization and filtered atlas sampling.

### AI and automation interface

- Assign persistent IDs to scene nodes so automation clients can retain stable references across saves and reloads.
- Extract project, scene, selection, scripting, and play-mode operations from `Program.cs` into a typed `EditorSession` command service shared by the UI and automation clients.
- Define versioned command, result, snapshot, validation-error, and event contracts; generate JSON schemas from the C# contract types.
- Cover project and asset queries, scene open/save, node inspection and mutation, hierarchy operations, script creation and attachment, builds, and play-mode control.
- Require an expected scene revision for mutations so stale clients cannot silently overwrite newer user changes.
- Add transactions that group related commands into one undo/redo operation.
- Implement a CLI first for testing, CI, headless workflows, and one-shot human operations.
- Add local JSON-RPC over Unix sockets on macOS/Linux and named pipes on Windows for controlling the currently running Editor without exposing a network port.
- Make the CLI forward commands to a running Editor when available and use the same command service in headless mode when no Editor is running.
- Add an MCP adapter as a thin schema-driven layer over the local command API for AI clients.
- Publish scene, selection, build, and play-state events for interactive clients and progress reporting.
- Restrict all asset and filesystem operations to the opened project root; do not expose unrestricted filesystem access or arbitrary command execution.

### Bounded glyph cache and font fallback

- Add bounded atlas pages with usage tracking and fence-safe glyph eviction so long editor sessions cannot exhaust a fixed atlas.
- Persist reusable glyph-cache pages and metadata between editor sessions when the font source and rasterization settings still match.
- Support fallback font chains and dynamically generated Unicode glyphs without baking every supported codepoint at startup.
- Share compatible glyph-cache pages across native windows while keeping per-window descriptors and synchronization explicit.
- Add cache hit-rate, atlas occupancy, generation, upload, and eviction diagnostics for profiling.
