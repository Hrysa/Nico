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

### Validation and completion criteria

- A script compilation failure does not corrupt metadata, scenes, the last successful artifact set, or the running Editor.
- Editor loose-file loading and Player package loading produce equivalent runtime resources from the same asset reference.

### Explicitly deferred

- Production texture, shader, model, audio, material, scene, and prefab importers beyond the contracts required to add them cleanly.
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

### Multi-window editor

- Prioritize detachable Scene and Game viewports, followed by Inspector, Hierarchy, FileSystem, profiler, debugger, and settings.
- Keep temporary workflows such as Open Scene as in-window modal overlays.

### Dynamic glyph generation and runtime cache

- Add selectable font faces, weights, and fallback chains to the existing on-demand glyph cache.
- Share compatible immutable glyph data safely across native windows and DPI contexts.

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
