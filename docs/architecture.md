# Architecture

The engine uses a layered, in-place architecture. Rebuilding as a separate project is unnecessary: the existing project boundaries are sound now, and the remaining Vulkan complexity is isolated inside the backend.

## Dependency direction

```text
Player ──> Engine ──> Engine.Core
                  ├─> Engine.Graphics ──> Engine.Core
                  ├─> Engine.Scripting ──> Engine.Core, Engine.Graphics
                  ├─> Engine.UI ────────> Engine.Core, Engine.Graphics
                  └─> Engine.Graphics.Silk ──> Engine.Graphics, Silk.NET

Editor ──> Engine.Graphics, Engine.Scripting, Engine.UI, Engine.Graphics.Silk
```

Only `Engine.Graphics.Silk` may reference Silk.NET. `Engine.Core`, `Engine.Graphics`, `Engine.Scripting`, and `Engine.UI` remain renderer-independent.

## Runtime contracts

- `IWindow` owns the platform window lifecycle and resize/update events.
- `IInputSource` exposes renderer-independent keyboard and pointer events.
- `IRenderer` accepts viewport, grid, UI, and overlay render data.
- `EngineHost` is the runtime composition facade used by Player.
- `SceneScript`, `SceneContext`, and `SceneScriptRuntime` form the renderer-independent game scripting API.

The current Silk implementation implements all three contracts on `SilkWindow`; consumers depend on the narrow contract they need.

## Editor composition

`Program` is the composition root. Behavior is split into focused collaborators:

- `EditorView` builds and lays out the UI tree.
- `UIEventRouter` owns hit testing and UI input state.
- `FlyCameraController` owns scene-camera input.
- `SceneSelectionController` owns viewport picking and selection.
- `EditorViewportRenderer` builds per-frame render submissions.
- `GameScriptHost` builds the generated game project and owns its collectible assembly context.

UI produces semantic `UIDrawCommand` values. Only the Silk backend translates those commands into GPU vertices.

## Scene and rendering

`Node` owns hierarchy invariants and local transforms. `Node3D` composes parent transforms. Cameras implement `ICamera`; selection uses `MeshPicker`; `RenderQueue` separates scene submission from backend execution.

A node may persist one assembly-qualified `ScriptType`. Entering play mode clones the authored graph, builds the game-owned script project, resolves those types from its output assembly, binds one `SceneScript` instance per runtime node, and invokes its lifecycle before rendering. Stopping play discards the clone, leaving authored state unchanged. Script assemblies are loaded into a collectible context; engine contract assemblies remain shared with the default context so node and script type identity is stable.

The Vulkan backend separates resource lifetimes even though `SilkWindow` remains its facade:

- `FrameScheduler` owns per-frame command pools, fences, command buffers, and semaphore chaining.
- `SwapchainManager` owns swapchain selection, images, views, extent, and framebuffers.
- `FrameVertexBuffers` owns frame-indexed mapped upload buffers.
- `PipelineResources` owns shader modules, pipeline layouts, pipelines, and texture descriptors.
- `ViewportFbo` owns each viewport's offscreen attachments and descriptor set.

## Invariants

1. No Silk.NET types cross the `Engine.Graphics` public boundary.
2. GPU resources have one explicit owner and idempotent cleanup.
3. CPU writes target only the active frame's upload buffers.
4. Viewport resize updates both FBO resources and camera projection state.
5. UI coordinates are parent-local; drawing and hit testing resolve them through the tree.
6. Raw `Matrix4x4` push constants are not explicitly transposed.

## Verification

Run:

```bash
dotnet build GameEngine.slnx
dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj
dotnet test tests/Editor.Tests/Editor.Tests.csproj
./run.sh
```

The unit suites cover hierarchy/transforms, cameras, render queues/helpers, mesh picking, UI routing, fly-camera input, selection, and viewport rendering orchestration. `./run.sh` is the Vulkan integration smoke test.
