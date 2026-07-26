# AGENTS.md

## Project Overview

C# game engine built on **Silk.NET 2.23.0**, targeting **.NET 11.0**. "Editor as a Game" architecture: the Editor is a 2D UI with multiple viewports (Scene, Game) each rendering to an FBO and displaying as a textured quad inside the Editor world.

## Solution Structure

```
GameEngine.slnx
├── src/Engine.Core/         → pure abstractions (no Silk.NET): Node base class
├── src/Engine.Graphics/     → graphics abstractions (no Silk.NET): ICamera, IWindow, Vertex, Color
├── src/Engine.Graphics.Silk/ → Silk.NET implementations: SilkWindow, RenderGraph, ViewportFbo
├── src/Engine.UI/           → UI element types (UIElement, Panel, Button, ViewportPanel)
├── src/Engine/              → empty Silk.NET package aggregator (no source)
├── src/Editor/              → editor app entry point (Program.cs, EditorUI.cs)
└── src/Player/              → game runtime (Player.csproj)
```

**Current state:** Working Vulkan renderer with 2-pass rendering (FBO pass + swapchain pass). Scene viewport renders a rotating 3D cube via PerspectiveCamera. Game viewport renders a static colored quad via orthographic projection. UI layout with menu bar, status bar, hierarchy, inspector panels.

## Dependency Rules (Iron Law)

```
Editor → Engine.Graphics, Engine.Graphics.Silk, Engine.UI
Player → Engine
Engine.Graphics → Engine.Core
Engine.Graphics.Silk → Engine.Graphics
Engine.Graphics.Silk → Silk.NET (NuGet)   ← ONLY project that references Silk.NET directly
Engine.UI → Engine.Core
Engine.UI → Engine.Graphics
```

- `Engine.Core` contains pure abstractions and domain logic — no Silk.NET. Has `Node` base class.
- `Engine.Graphics` contains graphics abstractions (interfaces) — no Silk.NET. Has `Color`, `Vertex`, `IGraphicsContext`, `ICamera`, `IWindow`, etc.
- `Engine.Graphics.Silk` contains Silk.NET implementations of graphics abstractions. Has `RenderGraph` for multi-pass command buffer management.
- `Engine.UI` contains UI element types (`UIElement`, `Panel`, `Button`, `ViewportPanel`) — extends `Node` from `Engine.Core`.
- `Editor` and `Player` interact with graphics only through interfaces defined in `Engine.Graphics`.
- `Silk.NET` references are confined to `Engine.Graphics.Silk`.

## Rendering Pipeline

### RenderGraph

`RenderGraph` (in `Engine.Graphics.Silk`) manages per-frame Vulkan synchronization:

- **Per-frame resources:** Command pool + fence (2 frames in flight)
- **Per-pass resources:** Allocated command buffer + semaphore (max 4 passes)
- **Frame lifecycle:** `BeginFrame()` waits/resets fence → `BeginPass()` allocates command buffer → `EndPass()` ends recording → `SubmitPass()` submits with semaphore chain → `EndFrame()` advances frame counter
- **Semaphore chain:** Pass N waits on pass N-1's semaphore (or imageAvailable for first pass); last pass's semaphore is waited on by present

### Pass 1: Per-viewport FBO rendering
1. For each registered viewport:
   - Bind FBO framebuffer (color attachment)
   - Set viewport scissor to FBO dimensions
   - Replay queued draw calls with `_fboGraphicsPipeline`
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
- `PerspectiveCamera` — full implementation with FOV/aspect/near/far, euler-angle rotation, movement, pitch clamping. Vulkan Y-flip and Z [0,1] remap applied in `GetProjectionMatrix()`.
- `OrthographicCamera` — stubs with TODO methods (Pan, Zoom, Size)
- Both extend `Node` (get Position/Rotation/Scale for free)
- ViewportPanel has `ICamera? Camera` property
- Scene viewport uses PerspectiveCamera with a rotating cube (36 vertices, 12 triangles)

## Debug System

Conditional compilation per subsystem. Define symbols in `Directory.Build.props`:

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

- `IWindow` events: `MouseMove`, `MouseDown`, `MouseUp`, `MouseDoubleClick`, `MouseScroll`, `KeyDown`, `KeyUp`
- `SilkWindow` subscribes to Silk.NET `IMouse` / `IKeyboard` after window creation
- Editor `Program.cs` does recursive hit testing against UI tree, dispatches to elements via `SetHover()` / `SetPressed()` / `InvokeClick()`

## Viewport System

- `ViewportPanel` extends `Panel` — tracks `ViewportId`, `Camera`, resize detection
- `ViewportFbo` — per-viewport Vulkan resources (color image, framebuffer, sampler, descriptor set)
- `IWindow.RegisterViewport()` / `UnregisterViewport()` / `ResizeViewport()`
- `IWindow.Update` — event fired each frame with delta time (logic goes here)
- `IWindow.DrawInViewport()` — queues vertices for FBO pass (call from Update handler)
- `IWindow.SetViewportClearColor()` — per-viewport clear color

## Coding Conventions

- File-scoped namespaces: `namespace Engine;`
- Interfaces: `I` prefix + PascalCase (e.g., `IGraphicsContext`)
- Public methods: PascalCase
- Private fields: `_camelCase`
- GPU resources: implement `IDisposable`, use disposed-guard pattern
- `null!` for uninitialized nullable properties
- **XML documentation**: every public/private method must have `/// <summary>` doc comment with `<param>` tags for each parameter and `<returns>` for non-void methods

## Known Pitfalls

### Matrix4x4 Push Constants (Row-Major vs Column-Major)

`System.Numerics.Matrix4x4` is **row-major**. GLSL `mat4` is **column-major**. When a C# `Matrix4x4` is pushed as raw bytes via `vkCmdPushConstants`, GLSL reads the memory as columns — effectively getting the **transpose** of the intended matrix.

**Why it matters:** This automatic transpose is actually **correct** for the MVP transform. The C# row-vector convention is `v' = v * M`. The column-vector equivalent is `v' = M^T * v`. Since GLSL naturally reads the transpose, no explicit `Transpose()` call is needed — just push the raw `Matrix4x4` bytes directly.

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
./run.sh                               # run editor (sets MoltenVK env on macOS)
dotnet run --project src/Editor        # run editor directly
dotnet run --project src/Player        # run game directly
```

### macOS Vulkan Setup

`run.sh` sets `VK_ICD_FILENAMES` → MoltenVK and `DYLD_FALLBACK_LIBRARY_PATH` → Homebrew lib. If Vulkan init fails, check these.

## Tech Stack

- .NET 11.0, nullable enabled, implicit usings
- `AllowUnsafeBlocks` only in `Engine.Graphics.Silk` (Vulkan interop)
- Silk.NET.Windowing/Input/Vulkan/Maths 2.23.0
- Microsoft.Extensions.Logging + Console (verbose dev logging)
- Vortice.Dxc 3.8.3 (HLSL → SPIR-V shader compilation)
- Shaders: `src/Shaders/` (basic.vert/frag, texture.vert/frag) → compile SPI-RV to same directory

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
