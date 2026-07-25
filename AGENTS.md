# AGENTS.md

## Project Overview

C# game engine built on **Silk.NET 2.23.0**, targeting **.NET 11.0**. "Editor as a Game" architecture: the Editor is a 3D world with an orthographic camera; the 3D Viewport renders to an FBO and displays as a textured quad inside the Editor world.

## Solution Structure

```
GameEngine.slnx
├── src/Engine.Core/         → pure abstractions (no Silk.NET): Node base class
├── src/Engine.Graphics/     → graphics abstractions (no Silk.NET): IGraphicsContext, IRenderer, etc.
├── src/Engine.Graphics.Silk/ → Silk.NET implementations (Silk.NET lives here)
├── src/Engine.UI/           → UI element types (UIElement, Panel, Button, ViewportPanel)
├── src/Engine/              → Silk.NET host (references Silk.NET directly)
├── src/Editor/              → Editor.csproj (editor app, uses Engine.UI for layout)
└── src/Player/              → Player.csproj (game runtime)
```

**Current state:** `src/` has working projects. Editor builds a 2D UI layout with multiple viewports (Scene, Game) rendered via the Vulkan pipeline. Each viewport has its own FBO with color attachment. Camera system with PerspectiveCamera (full) and OrthographicCamera (stubs).

## Dependency Rules (Iron Law)

```
Editor → Engine (Core + Graphics)
Player → Engine (Core + Graphics)
Engine.Graphics → Engine.Core
Engine.Graphics.Silk → Engine.Graphics
Engine.Graphics.Silk → Silk.NET (NuGet)   ← ONLY project that references Silk.NET directly
Engine.UI → Engine.Core
Engine.UI → Engine.Graphics
```

- `Engine.Core` contains pure abstractions and domain logic — no Silk.NET. Has `Node` base class.
- `Engine.Graphics` contains graphics abstractions (interfaces) — no Silk.NET. Has `Color`, `Vertex`, `IGraphicsContext`, `ICamera`, `IWindow`, etc.
- `Engine.Graphics.Silk` contains Silk.NET implementations of graphics abstractions.
- `Engine.UI` contains UI element types (`UIElement`, `Panel`, `Button`, `ViewportPanel`) — extends `Node` from `Engine.Core`.
- `Editor` and `Player` interact with graphics only through interfaces defined in `Engine.Graphics`.
- `Silk.NET` references are confined to `Engine.Graphics.Silk`.

## Rendering Pipeline

### Pass 1: Per-viewport FBO rendering
1. For each registered viewport:
   - Bind FBO framebuffer (color attachment)
   - Set viewport scissor to FBO dimensions
   - Call viewport render callback (draws 3D scene content via `DrawInViewport`)
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
- `PerspectiveCamera` — full implementation with FOV/aspect/near/far, euler-angle rotation, movement, pitch clamping
- `OrthographicCamera` — stubs with TODO methods (Pan, Zoom, Size)
- Both extend `Node` (get Position/Rotation/Scale for free)
- Vulkan Y-flip applied in `PerspectiveCamera.GetProjectionMatrix()`
- ViewportPanel has `ICamera? Camera` property

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
- `IWindow.SetViewportRenderCallback()` — callback receives `ViewportRenderContext`
- `IWindow.DrawInViewport()` — queues vertices for FBO pass
- `IWindow.SetViewportClearColor()` — per-viewport clear color

## Coding Conventions

- File-scoped namespaces: `namespace Engine;`
- Interfaces: `I` prefix + PascalCase (e.g., `IGraphicsContext`)
- Public methods: PascalCase
- Private fields: `_camelCase`
- GPU resources: implement `IDisposable`, use disposed-guard pattern
- `null!` for uninitialized nullable properties
- **XML documentation**: every public/private method must have `/// <summary>` doc comment with `<param>` tags for each parameter and `<returns>` for non-void methods

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
- Shaders: `src/Shaders/` (basic.vert/frag, texture.vert/frag) → compile SPIR-V to same directory

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
3. Wire up in `Editor/EditorApp.cs` or `Player/GameApp.cs`
4. Never modify `Engine.Core`-style pure logic (no graphics refs there)
