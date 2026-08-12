# AGENTS.md

## Project Overview

C# game engine built on **Silk.NET 2.23.0**, targeting **.NET 11.0**. "Editor as a Game" architecture: the Editor is a 2D UI with multiple viewports (Scene, Game) each rendering to an FBO and displaying as a textured quad inside the Editor world.

## Solution Structure

```
GameEngine.slnx
├── src/Engine.Core/         → nodes, components, asset identity, persistent material data
├── src/Engine.Assets/       → metadata, importers, artifacts, VFS, runtime resources
├── src/Engine.Graphics/     → renderer-independent scene, animation, render pipeline, queues
├── src/Engine.Graphics.Silk/ → Silk.NET/Vulkan, native windows, text rasterization
├── src/Engine.Physics/      → BepuPhysics adapter
├── src/Engine.Scripting/    → script lifecycle, observed properties, scene services
├── src/Engine.UI/           → retained UI, routing, controls, docking, accessibility
├── src/Engine/              → runtime composition facade (EngineHost)
├── src/Editor/              → editor composition and tooling
├── src/Player/              → game runtime
└── tools/                   → generators, profiling, benchmarks, asset authoring
```

**Current state:** The Editor loads versioned scenes, GLB/static/skinned meshes, materials, textures, animation sets, scripts, physics, HUDs, and configurable render pipelines. Scene and Game views render independently, can move to detached native windows, and remain embedded in the retained dock workspace when docked.

## Dependency Rules (Iron Law)

```
Editor → Engine.Assets, Engine.Graphics, Engine.Graphics.Silk, Engine.Physics, Engine.Scripting, Engine.UI
Player → Engine
Engine.Graphics → Engine.Core
Engine.Assets → Engine.Core
Engine.Graphics.Silk → Engine.Graphics
Engine.Graphics.Silk → Silk.NET (NuGet)   ← ONLY project that references Silk.NET directly
Engine.UI → Engine.Core
Engine.UI → Engine.Graphics
Engine.Physics → Engine.Core, Engine.Graphics
Engine.Scripting → Engine.Core, Engine.Graphics
```

- `Engine.Core` contains backend-independent domain state and codecs; it has no graphics or asset-import dependency.
- `Engine.Assets` may reference format libraries, but it does not reference rendering or Silk.NET.
- `Engine.Graphics` contains scene/render/animation abstractions and data; it has no Silk.NET dependency.
- `Engine.Graphics.Silk` contains the only Silk.NET and Vulkan implementation code.
- `Engine.UI` contains renderer-independent retained UI elements extending `Node`.
- `Editor`, `Engine`, and `Player` compose lower layers; gameplay and tools must not depend on concrete Vulkan types.
- `Silk.NET` references are confined to `Engine.Graphics.Silk`.

## Rendering Pipeline

Game/Scene code prepares a `RenderQueue`. A renderer-independent `RenderPipeline` executes ordered `RenderPipelinePass` instances and must call `SubmitScene()` exactly once. `BasicForwardRenderPipeline` exposes Shadows, DepthPrepass, Opaque, Transparent, and PostProcess stages. Empty stages are extension points; the opaque pass currently performs scene submission. Output effects such as grayscale are expressed through `RenderOutputSettings`.

### FrameScheduler

`FrameScheduler` (in `Engine.Graphics.Silk`) manages per-frame Vulkan synchronization:

- **Per-frame resources:** Command pool + fence (2 frames in flight)
- **Per-pass resources:** Allocated command buffer + semaphore (max 4 passes)
- **Frame lifecycle:** `BeginFrame()` waits/resets fence → `BeginPass()` allocates command buffer → `EndPass()` ends recording → `SubmitPass()` submits with semaphore chain → `EndFrame()` advances frame counter
- **Semaphore chain:** Pass N waits on pass N-1's semaphore (or imageAvailable for first pass); last pass's semaphore is waited on by present

### Pass 1: Per-render-view offscreen rendering
1. For each registered viewport:
   - Bind FBO framebuffer (color attachment)
   - Set viewport scissor to FBO dimensions
   - Replay the submitted render queue with the appropriate retained mesh pipelines
   - End render pass

### Pass 2: Editor UI → Screen
1. Bind swapchain framebuffer
2. Draw editor UI (colored quads for panels, buttons, separators)
3. For each viewport:
   - Bind texture pipeline
   - Bind viewport's FBO texture descriptor set
   - Bind viewport's textured quad vertex buffer
   - Draw textured quad (6 vertices)
4. End render pass

## Camera System

- `ICamera` interface in `Engine.Graphics` — `GetViewMatrix()`, `GetProjectionMatrix()`, `GetPushConstants(model)`, `UpdateViewport(w, h)`
- `PerspectiveCamera` — FOV/aspect/near/far, Euler-authored orientation resolved through quaternions for movement/orbit math, and pitch clamping. Vulkan correction is applied in `GetProjectionMatrix()`.
- `OrthographicCamera` — orthographic projection with viewport updates, panning, and zooming
- Both extend `Node3D`.
- ViewportPanel has `ICamera? Camera` property
- Scene navigation uses `FlyCameraController`; game cameras remain ordinary root scene nodes and are not forced under a camera rig.

## Debug System

Conditional compilation is available per subsystem. `Directory.Build.props` currently enables Core, Graphics, Graphics.Silk, UI, and Editor logging; add `DEBUG_INPUT` temporarily when native input tracing is required.

```csharp
Debug.Core(LogLevel.Debug, "message {0}", arg);
Debug.Graphics(LogLevel.Trace, "buffer created");
Debug.GraphicsSilk(LogLevel.Information, "Vulkan init");
Debug.UI(LogLevel.Debug, "hover: {Name}", name);
Debug.Editor(LogLevel.Information, "starting");
Debug.Input(LogLevel.Trace, "Mouse: ({X}, {Y})", x, y);
```

| Symbol | System |
|---|---|
| `DEBUG_CORE` | Engine.Core |
| `DEBUG_GRAPHICS` | Engine.Graphics |
| `DEBUG_GRAPHICS_SILK` | Engine.Graphics.Silk |
| `DEBUG_UI` | Engine.UI |
| `DEBUG_EDITOR` | Editor |
| `DEBUG_INPUT` | Editor input handling |

## Input System

- `IInputSource` events: `MouseMove`, `MouseDown`, `MouseUp`, `MouseDoubleClick`, `MouseScroll`, `KeyDown`, `KeyUp`
- `SilkWindow` subscribes to Silk.NET `IMouse` / `IKeyboard` after window creation
- `UIEventRouter` performs clipped hit testing and preview/target/bubble routing for pointer, keyboard, text/composition, commands, focus, and drag/drop.

## Viewport System

- `ViewportPanel` extends `Panel` and presents a `RenderViewHandle`.
- `ViewportFbo` owns per-view color/depth images, framebuffer, sampler, and descriptor state.
- `IRenderer.CreateRenderView()` / `DestroyRenderView()` / `ResizeRenderView()`
- `IWindow.Update` — event fired each frame with delta time (logic goes here)
- `RenderPipeline.Render()` submits a prepared `RenderQueue` to a view.
- `IRenderer.SetViewportClearColor()` — per-viewport clear color

## Coding Conventions

- File-scoped namespaces: `namespace Engine;`
- Interfaces: `I` prefix + PascalCase (e.g., `IGraphicsContext`)
- Public methods: PascalCase
- Private fields: `_camelCase`
- GPU resources: implement `IDisposable`, use disposed-guard pattern
- `null!` for uninitialized nullable properties
- **Property storage**: prefer auto-properties when no accessor logic is required. When a property
  needs validation, equality checks, invalidation, or change notification, use the C# 14 `field`
  keyword instead of declaring a manual `_camelCase` backing field. Use an explicit backing field
  only when storage must be accessed outside the property, shared by multiple properties, passed by
  `ref`, used with `Interlocked`/`volatile`, or has a lifecycle distinct from the property.
- **XML documentation**: every public/private method must have `/// <summary>` doc comment with `<param>` tags for each parameter and `<returns>` for non-void methods
- **GC-free enumeration**: every loop in frame, render, update, paint, and input hot paths must be allocation-free.
  - Prefer direct `foreach` for arrays, spans, and variables statically typed as a concrete `List<T>`; these enumerators are value types and do not allocate.
  - Do not use `foreach` through `IEnumerable<T>`, `ICollection<T>`, or `IReadOnlyList<T>` in hot paths because a value-type enumerator may be boxed behind the interface.
  - Do not use LINQ in hot paths; operators such as `Where`, `Select`, and `OfType` create iterator objects or delegates.
  - Do not call `ToArray()` unless ownership or an external API strictly requires an array and no reusable buffer, span, or existing collection can satisfy the contract. In hot paths, treat `ToArray()` as a last resort because it always allocates and copies.
  - Use an indexed `for` loop or expose a `ReadOnlySpan<T>` when a hot-path collection is interface-typed.
  - A `for` loop over `List<T>` can be marginally faster by avoiding the enumerator version check, but prefer the clearer direct `foreach` unless profiling proves loop overhead matters.
  - Consider `CollectionsMarshal.AsSpan(list)` only for a proven bottleneck where the list cannot change during traversal.
  - Add an allocation regression test when introducing a new hot-path collection or enumeration pattern.

## Asset and Inspector Architecture Principles

- **No compatibility by default**: do not retain legacy asset formats, migration readers, deprecated
  APIs, or parallel code paths unless compatibility is explicitly requested. Replace obsolete formats
  and update all producers and consumers together.
- **Assets own their properties**: properties intrinsic to an asset belong in that asset. Scene nodes
  store asset references and must not silently duplicate referenced asset data as scene overrides.
  Per-object overrides or unique instances must be explicit user-facing features.
- **One editor per concept**: implement a single reusable Inspector content component for each domain
  concept. When a scene object references an asset, embed the same asset editor used for direct
  filesystem inspection; never copy its controls or property-update logic into the object Inspector.
- **Inspector is a host**: the Inspector shell must not contain switches for every asset type.
  Selection-specific providers create composable Inspector content through registries and shared
  contexts. Adding an asset type should require registration, not edits throughout `Program.cs`, the
  filesystem tree, and `SceneInspector`.
- **Use shared asset documents**: load, cache, dirty-track, atomically save, reload, notify, and
  invalidate runtime resources through reusable typed asset-document services. Direct asset selection
  and embedded object views of the same asset must share one document and remain synchronized.
- **Compose common property editors**: reuse typed bindings and standard editors for numbers, vectors,
  colors, booleans, enums, collections, and asset references. Domain editors compose these controls;
  they do not reimplement parsing, validation, undo, refresh, or persistence.
- **Asset references are generic UI infrastructure**: use one filtered asset-reference field and one
  drag/drop resolution path for physical files and imported sub-assets. Do not add specialized PNG,
  material, animation, or other asset-drop branches for each Inspector consumer.
- **Prefer general mechanisms over local fixes**: before adding type checks or duplicated handlers,
  identify the reusable lifecycle, binding, routing, or UI abstraction. A new implementation should
  make the next asset type cheaper to add without copy-paste changes.

## Known Pitfalls

### Matrix4x4 Push Constants (Row-Major vs Column-Major)

`System.Numerics.Matrix4x4` is **row-major**. The Slang shaders declare `row_major` matrices and compile with `-matrix-layout-row-major`, which intentionally produces the existing SPIR-V `ColMajor` storage decorations. When a C# `Matrix4x4` is pushed as raw bytes via `vkCmdPushConstants`, the shader reads the memory as columns — effectively getting the **transpose** of the intended matrix.

**Why it matters:** This automatic transpose is actually **correct** for the MVP transform. The C# row-vector convention is `v' = v * M`. The column-vector equivalent is `v' = M^T * v`. Since the SPIR-V storage contract supplies the transpose, no explicit `Transpose()` call is needed — just push the raw `Matrix4x4` bytes directly.

**Rule:** Push `Matrix4x4` values directly to `PushConstants` without calling `Transpose()`. The game viewport's orthographic projection already does this correctly. The `PerspectiveCamera.GetPushConstants` must follow the same pattern.

```csharp
// Correct — no transpose needed:
return new PushConstants
{
    Model = model,
    View = view,
    Projection = projection
};
```

The only Vulkan-specific correction is the **Y-flip** (`M22 = -M22`) on the perspective projection matrix, to account for Vulkan's Y-down screen coordinates.

## Build & Run

```bash
dotnet build GameEngine.slnx           # build all
./run.sh example_game                  # run editor (sets MoltenVK env on macOS)
dotnet run --project src/Editor -- example_game
dotnet run --project src/Player -- example_game example_game/scenes/scene.node
```

### macOS Vulkan Setup

`run.sh` sets `VK_ICD_FILENAMES` → MoltenVK and `DYLD_FALLBACK_LIBRARY_PATH` → Homebrew lib. If Vulkan init fails, check these.

## Tech Stack

- .NET 11.0, nullable enabled, implicit usings
- `AllowUnsafeBlocks` only in `Engine.Graphics.Silk` (Vulkan interop)
- Silk.NET.Windowing/Input/Vulkan/Maths 2.23.0
- Microsoft.Extensions.Logging + Console (verbose dev logging)
- Slang 2026.5.2 (`slangc`, native x64/ARM64 Slang → SPIR-V shader compilation)
- Shaders: `src/Shaders/*.slang` → run `./compile_shaders.sh` to regenerate embedded `.spv` files

## Logging

Use `Microsoft.Extensions.Logging` everywhere. Create `ILoggerFactory` at entry point, pass to constructors.

```csharp
var loggerFactory = LoggerFactory.Create(b => { b.AddConsole(); b.SetMinimumLevel(LogLevel.Trace); });
var logger = loggerFactory.CreateLogger<MyClass>();
```

- `Engine.Core` — has `ILogger` via `Microsoft.Extensions.Logging.Abstractions`
- `Engine.Graphics.Silk` — receives `ILoggerFactory` via constructor
- `Editor` / `Player` — create `ILoggerFactory` in `Program.cs`

## Rendering Modification Checklist

When modifying rendering code:
1. Define/modify interfaces in `Engine.Graphics`
2. Implement concrete logic in `Engine.Graphics.Silk`
3. Wire up in `Editor/Program.cs` or `Player/` entry point
4. Never modify `Engine.Core`-style pure logic (no graphics refs there)
