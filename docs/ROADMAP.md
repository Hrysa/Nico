# Roadmap

## Next Version

### Generation-based UI geometry caching

- Assign a monotonically increasing generation whenever UI layout or visual state changes.
- Traverse the UI tree and rebuild semantic draw commands and vertices only for a new generation.
- Track the uploaded generation independently for each frame-in-flight UI buffer.
- Upload a generation at most once to each frame buffer, then reuse its mapped GPU geometry while the UI remains unchanged.
- Preserve full-frame UI drawing and the existing content/overlay ordering; the optimization targets CPU traversal, tessellation, font vertex generation, and redundant memory copies.
- Keep component-level dirty rectangles and partial swapchain redraw outside this milestone unless profiling demonstrates a need.

### Multi-window editor

- Add a reusable `UIHost` boundary so editor component trees can be hosted by either the main dock layout or an independent native tool window.
- Support multiple native presentation windows from one renderer and shared Vulkan device.
- Give each native window its own surface, swapchain, framebuffers, input router, UI root, DPI scale, resize lifecycle, and presentation lifecycle.
- Allow persistent editor tools to detach, move to another monitor, and dock again.
- Prioritize detachable Scene and Game viewports, followed by Inspector, Hierarchy, FileSystem, profiler, debugger, and settings.
- Keep temporary workflows such as Open Scene as in-window modal overlays.
- Avoid creating a separate engine or Vulkan device for each tool window; share queues, pipelines, fonts, and immutable GPU assets where valid.

## Following Version

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
