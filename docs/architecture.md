# Architecture

The engine uses a layered "Editor as a Game" architecture. The Editor is retained 2D UI, while its Scene and Game viewports render into independent offscreen targets that are presented inside the UI.

## Projects and dependencies

```text
Engine.Core
    ↑
    ├── Engine.Graphics
    │       ↑
    │       ├── Engine.Graphics.Silk ── Silk.NET
    │       ├── Engine.Physics
    │       ├── Engine.Scripting
    │       └── Engine.UI
    └── Engine.Assets

Engine ── runtime composition used by Player
Editor ── editor composition and tooling
Player ── packaged/loose game runtime
```

- `Engine.Core` owns nodes, components, asset IDs, and renderer-independent state.
- `Engine.Graphics` owns transforms, cameras, meshes, materials, render queues, input contracts, and scene persistence.
- `Engine.Graphics.Silk` is the only project that references Silk.NET or Vulkan.
- `Engine.Physics` owns fixed-step simulation and depends on `Node3D` through `Engine.Graphics`.
- `Engine.Scripting` owns script lifecycle, observed-property contracts, scene queries, and gameplay input state.
- `Engine.UI` owns the retained UI tree, layout, routed input, controls, docking, accessibility, and UI scheduling.
- `Engine.Assets` owns asset metadata, importers, artifacts, and cache management.

Silk.NET types must never cross the public `Engine.Graphics` boundary.

## Scene model

`Node` owns hierarchy, local transform state, and an ordered `Component` collection. `Node3D` composes local transforms through its ancestors. A node may own multiple script and engine components.

Built-in component types currently include:

- `ScriptComponent`, which references a persistent script asset and stores authored property overrides;
- `RigidBodyComponent`, which stores motion mode, mass, velocity, gravity, and damping;
- `ColliderComponent`, which stores primitive collision geometry and contact material values.

Scene format 4 persists component collections. Format 3 remains readable through the legacy single-`scriptId` migration path. Entering Play mode clones the authored graph, including all components and material overrides. Runtime changes affect only that clone.

## Frame lifecycle

The client update order is:

```text
native input
    -> SceneScript.OnUpdate
    -> fixed physics steps
    -> interpolated presentation transforms
    -> SceneScript.OnLateUpdate
    -> viewport and UI submission
    -> Vulkan render/present
```

Gameplay scripts submit velocity and other intent before physics. Camera follow and other presentation work belongs in `OnLateUpdate`, after physics has published the current client pose.

`PhysicsWorld` defaults to authoritative, non-interpolated transforms. Editor Play mode and Player enable interpolation so a 60 Hz physics simulation renders smoothly at higher display rates. A headless server should leave interpolation disabled.

## Rendering

Each visible frame has two conceptual stages:

1. Registered Scene/Game viewports render meshes into per-view FBOs.
2. The Editor or Player UI renders to the swapchain and presents viewport textures as quads.

The Vulkan backend separates resource ownership:

- `FrameScheduler` owns per-frame command pools, fences, command buffers, and semaphore chaining.
- `SwapchainManager` owns swapchain images, views, extent, and framebuffers.
- `FrameVertexBuffers` owns frame-indexed mapped upload buffers.
- `PipelineResources` owns shaders, layouts, pipelines, and descriptors.
- `ViewportFbo` owns each viewport's color/depth attachments and texture descriptor.

Raw `System.Numerics.Matrix4x4` values are pushed without an explicit transpose. Perspective projection performs only the Vulkan Y-axis correction.

## UI and windows

Each native window owns one `UIHost`, dispatcher, input router, retained root, and rendering context. `DockSession` coordinates one authoritative dock workspace across the main and floating native windows.

UI hosts support four scheduling policies:

- `ExternallyManaged`: a game loop explicitly advances and refreshes UI;
- `EventDriven`: sleep until input or invalidation requests work;
- `Continuous`: update every frame;
- `Hybrid`: event-driven while idle and continuous while retained timers or interactions require ticks.

Hidden dock content sets `IsVisible = false`, which removes the subtree from layout, painting, hit testing, and retained time updates.

## Invariants

1. GPU resources have one explicit owner and idempotent cleanup.
2. CPU uploads target only resources safe for the active frame.
3. Runtime Play state never mutates the authored scene.
4. Dynamic physics owns position integration; scripts control it through motion state rather than per-frame transform assignment.
5. UI coordinates are parent-local; layout, painting, clipping, and hit testing resolve them through the retained tree.
6. Update, input, layout, and rendering hot paths avoid LINQ and interface-typed enumeration.

## Verification

```bash
dotnet build GameEngine.slnx
dotnet test GameEngine.slnx -m:1
./run.sh example_game
```

The final command is the macOS Vulkan integration smoke test.
