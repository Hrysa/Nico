# Roadmap

## Next Version

### Multi-window editor

- Add a reusable `UIHost` boundary so editor component trees can be hosted by either the main dock layout or an independent native tool window.
- Support multiple native presentation windows from one renderer and shared Vulkan device.
- Give each native window its own surface, swapchain, framebuffers, input router, UI root, DPI scale, resize lifecycle, and presentation lifecycle.
- Allow persistent editor tools to detach, move to another monitor, and dock again.
- Prioritize detachable Scene and Game viewports, followed by Inspector, Hierarchy, FileSystem, profiler, debugger, and settings.
- Keep temporary workflows such as Open Scene as in-window modal overlays.
- Avoid creating a separate engine or Vulkan device for each tool window; share queues, pipelines, fonts, and immutable GPU assets where valid.
