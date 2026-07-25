# AGENTS.md

## Project Overview

C# game engine built on **Silk.NET 2.23.0**, targeting **.NET 11.0**. "Editor as a Game" architecture: the Editor is a 3D world with an orthographic camera; the 3D Viewport renders to an FBO and displays as a textured quad inside the Editor world.

## Solution Structure

```
GameEngine.slnx
├── src/Engine.Core/         → pure abstractions (no Silk.NET)
├── src/Engine.Graphics/     → graphics abstractions (no Silk.NET)
├── src/Engine.Graphics.Silk/ → Silk.NET implementations (Silk.NET lives here)
├── src/Engine.Headless/     → engine without display (no Silk.NET, no graphics)
├── src/Editor/              → Editor.csproj (editor app)
└── src/Player/              → Player.csproj (game runtime)
```

**Current state:** `src/` does not exist yet. Only the solution file, `run.sh`, and `.gitignore` are present.

## Dependency Rules (Iron Law)

```
Editor → Engine (Core + Graphics)
Player → Engine (Core + Graphics)
Engine.Headless → Engine.Core
Engine.Graphics → Engine.Core
Engine.Graphics.Silk → Engine.Graphics
Engine.Graphics.Silk → Silk.NET (NuGet)   ← ONLY project that references Silk.NET directly
```

- `Engine.Core` contains pure abstractions and domain logic — no Silk.NET.
- `Engine.Graphics` contains graphics abstractions (interfaces) — no Silk.NET.
- `Engine.Graphics.Silk` contains Silk.NET implementations of graphics abstractions.
- `Engine.Headless` contains game logic without display — no Silk.NET, no graphics.
- `Editor` and `Player` interact with graphics only through interfaces defined in `Engine.Graphics`.
- `Silk.NET` references are confined to `Engine.Graphics.Silk`.

## Rendering Pipeline (Design, Not Yet Implemented)

### Pass 1: Game world → FBO
1. Bind FBO (offscreen texture)
2. Use perspective camera
3. Submit game object draw commands
4. Unbind FBO

### Pass 2: Editor world → Screen
1. Restore default framebuffer
2. Use orthographic camera
3. Draw editor UI
4. Draw a quad textured with the Pass 1 FBO result
5. Draw gizmos on top

## Coding Conventions

- File-scoped namespaces: `namespace Engine;`
- Interfaces: `I` prefix + PascalCase (e.g., `IGraphicsContext`)
- Public methods: PascalCase
- Private fields: `_camelCase`
- GPU resources: implement `IDisposable`, use disposed-guard pattern
- `null!` for uninitialized nullable properties

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
- Shaders: `src/Engine.Graphics/Shaders/` (source) → compile SPIR-V to same directory

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
