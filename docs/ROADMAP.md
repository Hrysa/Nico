# Roadmap

## Version 0.2.0

### Asset identity and content pipeline

Version 0.2.0 completes the remaining script validation, packaged runtime catalog, cooking, and Player-loading work on top of the implemented general asset pipeline. Metadata, identity, storage, dependencies, and runtime loading remain independent of C# and rendering APIs.

The persistent-to-runtime flow is:

```text
AssetId
  -> AssetDatabase record
  -> imported artifact location
  -> virtual filesystem stream
  -> runtime resource handle or script Type
```

An `AssetId` answers which persistent project asset is referenced. A project-relative path locates editable source content. A package entry locates cooked bytes. A runtime handle identifies a loaded CPU or GPU resource. These identities must not be collapsed into one type.

### Phase 6: Script asset importer and catalog

- Analyze and compile against the generated game script project so editor discovery uses the same references, defines, nullable settings, and language version as Play builds.
- Preserve the previous successful runtime catalog when compilation fails while exposing current diagnostics and marking affected entries stale or invalid.
- Display the resolved script name and validation state in the Inspector while persisting only the asset reference.
- Reject invalid C# assets during drag-and-drop instead of waiting for Play compilation.

### Phase 7: Packaged runtime scripting

- Let Player builds consume a precompiled/generated catalog without invoking the SDK or scanning source files.

### Phase 8: Cooking and packaged Player loading

- Traverse scene and asset dependencies from configured build roots and include only reachable artifacts unless explicitly marked always-include.
- Cook platform-specific artifacts before package assembly and produce deterministic manifests and package ordering.
- Map each asset ID and sub-asset key to a package entry without exposing package offsets to scene, material, script, or gameplay data.
- Bundle the compiled game script assembly and generated script catalog as build artifacts.
- Load Player resources through the same resource-manager API used by the Editor, differing only in asset resolver and storage implementations.
- Validate artifact versions, checksums, required engine versions, missing dependencies, and duplicate package entries before starting the game.
- Leave room for package splitting, streaming groups, patches, DLC, encryption/signing, and mod mounts without making them requirements for the first package format.

### Asset pipeline validation and completion criteria

- A script compilation failure does not corrupt metadata, scenes, the last successful artifact set, or the running Editor.
- Editor loose-file loading and Player package loading produce equivalent runtime resources from the same asset reference.

### Explicitly deferred

- Audio, shader-source, prefab, and production platform-compression importers. GLB models/animations/materials/textures, image textures, standard materials, animation sets, collision meshes, terrain, and scenes already have current import or persistence paths.
- Distributed asset processing and remote artifact caches.
- A final Source 2 VPK-compatible format; 0.2.0 requires package-ready abstractions and a minimal Nico package, not wire compatibility with VPK.
- Advanced package encryption, signing, differential patch generation, and network streaming.
- Multiple attachable script classes per source file unless stable type-level sub-assets are designed.
- Automatic migration of arbitrary third-party path- or type-name-based serialized formats.

## Version 0.3.0

### Retained transient geometry and frame-allocation control

- Make unchanged Editor and game frames allocate no managed memory, or remain within a measured near-zero budget, without attempting to remove event-driven allocations from loading, compilation, serialization, or UI construction.
- Begin with gizmos, selection overlays, debug drawing, text geometry, sprite batches, particle staging, and other geometry regenerated during sustained rendering.
- Reuse ordinary pre-sized `List<T>` instances and retained arrays by clearing and refilling them; do not introduce a custom list implementation without profiler evidence that the standard collection itself is a bottleneck.
- Avoid `ToArray()`, LINQ iterators, temporary result wrappers, and newly allocated intermediate primitive collections in continuous update and render paths.
- Allow tessellators to append into caller-owned buffers and expose completed geometry through spans or explicit written counts without transferring ownership or copying.
- Track the input generation of cached geometry and rebuild only when relevant semantic inputs change.
- For gizmos, include selection identity, object transform, camera matrices, viewport dimensions, DPI/window context, interaction mode, hovered handle, and active handle in invalidation state.
- Preserve viewport- and native-window-specific caches where projection, dimensions, or DPI differ while sharing immutable semantic data where valid.
- Grow retained buffers geometrically when required and keep their capacity for later frames; add bounded trimming only if profiling shows long-lived pathological peaks.
- Upload regenerated ranges through the existing transient arena without creating a second managed snapshot of identical geometry.
- Keep `DEBUG_GC_ALLOC` instrumentation available for measuring Update + Render allocation bytes per frame, compiled out when disabled.
- Validate three distinct budgets: idle with no selection, idle with a selected object, and active gizmo dragging. Optimize sustained allocations first; selection-change and other one-frame spikes are secondary.
- Target approximately 0–1 KB per idle Editor frame, 0–2 KB for an unchanged active viewport, and less than 10 KB per gizmo-drag frame, then revise budgets from representative profiling data.

### Typography and bounded glyph cache

The current renderer already provides on-demand TrueType glyph generation, DirectWrite hinted RGB coverage on Windows, Unicode fallback, and per-window atlas textures. Remaining work is:

- Add user-selectable font faces, weights, and ordered fallback chains.
- Add bounded atlas pages with usage tracking and fence-safe eviction for long sessions.
- Persist reusable glyph pages and metadata when font source and rasterization settings match.
- Share compatible immutable glyph data across native windows and DPI contexts while retaining per-window GPU descriptors and synchronization.
- Add cache hit-rate, atlas occupancy, generation, upload, and eviction diagnostics.

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

## Version 0.4.0

### Temporal anti-aliasing

Replace viewport MSAA with a full temporal anti-aliasing pipeline while preserving the existing `RenderView` → offscreen render target → viewport texture → presentation architecture.

- Split viewport rendering into explicit scene, temporal resolve, and presentation stages so post-processing can be extended without changing public viewport APIs.
- Render each view with a sub-pixel jittered projection using a stable low-discrepancy sequence, while keeping unjittered camera matrices available for input, gizmos, culling, and editor overlays.
- Produce per-pixel motion vectors from current and previous camera and object transforms, including support for static geometry, moving objects, and camera-only motion.
- Store per-view color history, depth history, previous matrices, jitter state, and frame validity; never share mutable temporal state between viewports or native windows.
- Reproject history with motion vectors and reject invalid samples using depth, bounds, disocclusion, and camera-cut tests.
- Add neighborhood clipping or variance clipping, responsive weighting, and configurable feedback to limit ghosting, trails, flicker, and history poisoning.
- Invalidate or reset history after viewport resize, DPI or render-scale changes, scene switches, camera cuts, projection changes, long frame gaps, device-resource recreation, and incompatible rendering-setting changes.
- Handle transparent geometry, particles, emissive surfaces, animated materials, editor gizmos, selection outlines, and UI overlays explicitly instead of allowing them to contaminate temporal history.
- Support render-scale-aware reconstruction so TAA can later evolve into temporal upsampling without changing asset, scene, viewport, or presentation contracts.
- Add debug views and metrics for motion vectors, jitter, history weight, rejected history, disocclusion, and resolve cost.
- Retain MSAA as a temporary fallback during development, then remove the multisampled viewport attachments and resolve path once TAA meets the completion criteria.

### TAA validation and completion criteria

- Static and moving scenes remain stable at native resolution with visibly less edge and shader aliasing than the current MSAA path.
- Camera movement, object motion, newly revealed surfaces, animation, particles, and viewport resizing do not produce persistent ghosting or invalid history.
- Scene and Game viewports maintain independent history across different sizes, cameras, render scales, and native windows.
- Picking, gizmos, editor overlays, and UI remain spatially aligned despite projection jitter.
- TAA works with both perspective and orthographic cameras and becomes the default viewport anti-aliasing mode before MSAA is removed.
