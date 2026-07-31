# Editor Gizmo Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the experimental gizmo with a constant-screen-size, layered world-space translation and rotation gizmo whose rendering, hover, and dragging agree.

**Architecture:** `EditorGizmo` is a small public facade over focused projection, layout, picking, drag-session, transform-math, and overlay-building units in `Engine.Graphics`. Layout produces the single ordered geometry model consumed by both picking and rendering; the Editor owns selection and applies immutable drag results.

**Tech Stack:** C# 14, .NET 11.0, `System.Numerics`, xUnit 2.9.3, Microsoft.NET.Test.Sdk 18.0.1, existing `IWindow.DrawOverlay` renderer.

## Global Constraints

- `Engine.Graphics` and its tests must not reference Silk.NET.
- Translation and rotation handles display together automatically for the selected object.
- Handles are aligned to world X/Y/Z and remain a constant pixel size.
- Rotation rings are the background interaction layer; translation axes and arrowheads are the foreground layer.
- Smooth dragging only: no snapping, scale handles, local mode, keyboard mode switch, or toolbar mode switch.
- Every drag result is calculated from immutable mouse-down state.
- `System.Numerics.Matrix4x4` stays in row-vector convention; model order is `Scale * Rz * Ry * Rx * Translation`.
- All public and private methods require XML `summary` documentation, `param` tags, and `returns` for non-void methods.
- Preserve unrelated `.mimocode/` files and do not stage them.
- The current uncommitted `EditorGizmo.cs`, `Program.cs`, `AxisGizmo.cs`, and `RotationGizmo.cs` changes are the superseded experiment; replace only those changes deliberately.

## File Structure

- Create `src/Engine.Graphics/Gizmos/GizmoTypes.cs`: public handle/viewport/transform types plus internal geometric primitives.
- Create `src/Engine.Graphics/Gizmos/GizmoTransformMath.cs`: Euler/matrix conversion and world-axis rotation composition.
- Create `src/Engine.Graphics/Gizmos/GizmoProjection.cs`: validated projection, unprojection, depth scaling, and rectangle clipping.
- Create `src/Engine.Graphics/Gizmos/GizmoLayout.cs`: constant-size layered handle geometry.
- Create `src/Engine.Graphics/Gizmos/GizmoPicker.cs`: front-to-back picking against layout geometry.
- Create `src/Engine.Graphics/Gizmos/GizmoDragSession.cs`: immutable translation/rotation drag calculations.
- Create `src/Engine.Graphics/Gizmos/GizmoOverlayBuilder.cs`: tessellation of clipped layout geometry into `Vertex[]`.
- Replace `src/Engine.Graphics/EditorGizmo.cs`: public facade coordinating the focused units.
- Modify `src/Engine.Graphics/Node3D.cs`: use the documented three-axis rotation convention.
- Delete `src/Engine.Graphics/AxisGizmo.cs` and `src/Engine.Graphics/RotationGizmo.cs`: remove superseded implementations.
- Modify `src/Editor/Program.cs`: route selection, hover, drag, cancellation, and overlay generation through the facade.
- Create `tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj` and focused test files mirroring each unit.
- Create `src/Engine.Graphics/Properties/AssemblyInfo.cs`: expose internal gizmo units to `Engine.Graphics.Tests` only.
- Modify `GameEngine.slnx`: add the test project under `/tests/`.

---

### Task 1: Test Project and Transform Convention

**Files:**
- Create: `tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj`
- Create: `src/Engine.Graphics/Properties/AssemblyInfo.cs`
- Create: `tests/Engine.Graphics.Tests/GizmoTransformMathTests.cs`
- Create: `src/Engine.Graphics/Gizmos/GizmoTransformMath.cs`
- Modify: `src/Engine.Graphics/Node3D.cs`
- Modify: `GameEngine.slnx`

**Interfaces:**
- Consumes: `Node3D.Rotation`, `Node3D.GetModelMatrix()`.
- Produces: `internal static Matrix4x4 ToRotationMatrix(Vector3 euler)`, `internal static Vector3 ToEuler(Matrix4x4 rotation)`, and `internal static Vector3 RotateWorld(Vector3 originalEuler, Vector3 worldAxis, float radians)` on `GizmoTransformMath`.

- [ ] **Step 1: Add the xUnit project and solution entry**

Use this project body and add `<Folder Name="/tests/"><Project Path="tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj" /></Folder>` to `GameEngine.slnx`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net11.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.0.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5"><PrivateAssets>all</PrivateAssets></PackageReference>
    <ProjectReference Include="../../src/Engine.Graphics/Engine.Graphics.csproj" />
  </ItemGroup>
</Project>
```

Add `[assembly: InternalsVisibleTo("Engine.Graphics.Tests")]` in `AssemblyInfo.cs` so focused units remain internal to the production assembly.

- [ ] **Step 2: Write failing convention tests**

Add tests that compare `Node3D.GetModelMatrix()` with `Scale * Rz * Ry * Rx * Translation`, round-trip nonsingular Euler samples, verify canonical Y range, verify the singularity rule (`Z == 0`), and verify a world Y delta is post-multiplied:

```csharp
[Fact]
public void RotateWorld_PostMultipliesWorldAxisDelta()
{
    var original = new Vector3(0.3f, -0.2f, 0.4f);
    var actual = GizmoTransformMath.ToRotationMatrix(
        GizmoTransformMath.RotateWorld(original, Vector3.UnitY, 0.25f));
    var expected = GizmoTransformMath.ToRotationMatrix(original)
        * Matrix4x4.CreateFromAxisAngle(Vector3.UnitY, 0.25f);
    AssertMatrixClose(expected, actual, 0.0001f);
}
```

- [ ] **Step 3: Run the focused tests and confirm failure**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --filter GizmoTransformMathTests`

Expected: FAIL because `GizmoTransformMath` does not exist and `Node3D` omits Z rotation.

- [ ] **Step 4: Implement the convention minimally**

Implement `ToRotationMatrix` as `Rz * Ry * Rx`; implement explicit matrix-to-Euler extraction for that order with clamped trigonometric input, canonical Y in `[-PI/2, PI/2]`, and the documented `Z = 0` singularity branch. Implement `RotateWorld` by normalizing the world axis, post-multiplying the axis-angle matrix, and converting back. Change `Node3D.GetModelMatrix()` to:

```csharp
return Matrix4x4.CreateScale(Scale)
     * GizmoTransformMath.ToRotationMatrix(Rotation)
     * Matrix4x4.CreateTranslation(Position);
```

- [ ] **Step 5: Run tests and build the solution**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --filter GizmoTransformMathTests`

Expected: PASS.

Run: `dotnet build GameEngine.slnx`

Expected: build succeeds with zero errors.

- [ ] **Step 6: Commit the transform foundation**

```bash
git add GameEngine.slnx src/Engine.Graphics/Node3D.cs src/Engine.Graphics/Gizmos/GizmoTransformMath.cs src/Engine.Graphics/Properties/AssemblyInfo.cs tests/Engine.Graphics.Tests
git commit -m "test: define gizmo transform convention"
```

### Task 2: Validated Projection and Shared Handle Layout

**Files:**
- Create: `src/Engine.Graphics/Gizmos/GizmoTypes.cs`
- Create: `src/Engine.Graphics/Gizmos/GizmoProjection.cs`
- Create: `src/Engine.Graphics/Gizmos/GizmoLayout.cs`
- Create: `tests/Engine.Graphics.Tests/GizmoProjectionTests.cs`
- Create: `tests/Engine.Graphics.Tests/GizmoLayoutTests.cs`

**Interfaces:**
- Consumes: row-vector `view * projection` matrices.
- Produces: `GizmoHandleKind`, `GizmoViewport`, `GizmoTransform`, `GizmoLayoutResult`, `GizmoHandleGeometry`, `GizmoSegment`, `GizmoTriangle`; `GizmoLayout.Create(Vector3 target, Matrix4x4 view, Matrix4x4 projection, GizmoViewport viewport)`.

- [ ] **Step 1: Define failing projection and constant-size tests**

Use a 45-degree perspective camera and assert: world origin projects to viewport center; screen-to-ray reverses projection; invalid/zero viewport and singular matrices return `false`; behind-camera targets produce `GizmoLayoutResult.Empty`; and the projected translation-axis maximum extent stays within one pixel at camera Z positions 5 and 10.

```csharp
[Theory]
[InlineData(5f)]
[InlineData(10f)]
public void Create_KeepsTargetPixelSizeAcrossCameraDistance(float cameraZ)
{
    var result = GizmoLayout.Create(Vector3.Zero, ViewAt(cameraZ), Projection(), Viewport);
    var x = result.Handles.Single(h => h.Kind == GizmoHandleKind.TranslateX);
    Assert.InRange(x.ScreenExtent, 95f, 97f);
}
```

- [ ] **Step 2: Run tests and confirm they fail**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --filter "GizmoProjectionTests|GizmoLayoutTests"`

Expected: FAIL because the types and layout do not exist.

- [ ] **Step 3: Implement the value types and validated projection helpers**

Define public immutable inputs:

```csharp
public enum GizmoHandleKind { None, RotateX, RotateY, RotateZ, TranslateX, TranslateY, TranslateZ }
public readonly record struct GizmoViewport(float X, float Y, float Width, float Height);
public readonly record struct GizmoTransform(Vector3 Position, Vector3 Rotation);
```

Define internal immutable segments/triangles and handle geometry with `Kind`, `Layer`, `Color`, `Interactive`, `Segments`, `Triangles`, and computed `ScreenExtent`. Define `GizmoLayoutResult` with `IsValid`, `Viewport`, captured `View`/`Projection`, target position, and ordered handles. Implement `TryWorldToScreen`, `TryScreenToRay`, `TryWorldUnitsPerPixel`, Liang-Barsky segment clipping, and Sutherland-Hodgman triangle clipping. Every helper returns `false` for invalid dimensions, failed inversion, `clip.W <= epsilon`, or non-finite results.

- [ ] **Step 4: Implement constant-size layout and explicit layers**

Use constants `AxisPixels = 96`, `RingPixels = 72`, `VisibleLinePixels = 2`, `HitLinePixels = 8`, `ArrowLengthPixels = 12`, and `RingSegments = 64`. Build rings first at layer 0 and translation segments/arrowheads second at layer 1. Derive world scale at target depth, keep axes world-aligned, clip every primitive to the viewport, and mark a translation handle non-interactive when its projected axis is shorter than four pixels.

- [ ] **Step 5: Run focused tests**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --filter "GizmoProjectionTests|GizmoLayoutTests"`

Expected: PASS, including finite-geometry, viewport-offset, clipping, world-alignment, and camera-aligned-axis cases.

- [ ] **Step 6: Commit projection and layout**

```bash
git add src/Engine.Graphics/Gizmos/GizmoTypes.cs src/Engine.Graphics/Gizmos/GizmoProjection.cs src/Engine.Graphics/Gizmos/GizmoLayout.cs tests/Engine.Graphics.Tests/GizmoProjectionTests.cs tests/Engine.Graphics.Tests/GizmoLayoutTests.cs
git commit -m "feat: build layered gizmo layout"
```

### Task 3: Deterministic Layered Picking

**Files:**
- Create: `src/Engine.Graphics/Gizmos/GizmoPicker.cs`
- Create: `tests/Engine.Graphics.Tests/GizmoPickerTests.cs`

**Interfaces:**
- Consumes: `GizmoLayoutResult.Handles` ordered back-to-front and its clipped segments/triangles.
- Produces: `internal static GizmoHandleKind Pick(GizmoLayoutResult layout, Vector2 pointer)`.

- [ ] **Step 1: Write failing priority and geometry tests**

Construct synthetic overlapping handles so tests do not depend on camera math. Assert a foreground translation triangle wins over a background ring segment at true overlap; a ring wins just outside the translation hit area; non-interactive geometry is ignored; outside-viewport pointers return `None`; and equal-layer candidates choose the shortest geometric distance then stable layout order.

```csharp
[Fact]
public void Pick_ForegroundTranslationWinsTrueOverlap()
{
    var layout = TestLayout.Overlap(GizmoHandleKind.RotateZ, GizmoHandleKind.TranslateX);
    Assert.Equal(GizmoHandleKind.TranslateX, GizmoPicker.Pick(layout, new Vector2(50, 50)));
}
```

- [ ] **Step 2: Run tests and confirm failure**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --filter GizmoPickerTests`

Expected: FAIL because `GizmoPicker` does not exist.

- [ ] **Step 3: Implement picking against shared shapes**

Traverse layers from highest to lowest. For each interactive handle, use point-to-segment distance with the segment's hit width and barycentric point-in-triangle for arrowheads. Within one layer choose minimum distance; preserve list order for exact ties. Do not introduce separate approximate ring or axis geometry.

- [ ] **Step 4: Run focused tests**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --filter GizmoPickerTests`

Expected: PASS.

- [ ] **Step 5: Commit picking**

```bash
git add src/Engine.Graphics/Gizmos/GizmoPicker.cs tests/Engine.Graphics.Tests/GizmoPickerTests.cs
git commit -m "feat: add layered gizmo picking"
```

### Task 4: Immutable Translation and Rotation Drag Sessions

**Files:**
- Create: `src/Engine.Graphics/Gizmos/GizmoDragSession.cs`
- Create: `tests/Engine.Graphics.Tests/GizmoDragSessionTests.cs`

**Interfaces:**
- Consumes: `GizmoHandleKind`, original `GizmoTransform`, layout, camera matrices, viewport, and mouse-down position.
- Produces: `internal static bool TryStart(GizmoHandleKind handle, Vector2 pointer, GizmoTransform original, GizmoLayoutResult layout, out GizmoDragSession? session)`; `internal bool TryUpdate(Vector2 pointer, out GizmoTransform transform)`.

- [ ] **Step 1: Write failing translation-session tests**

Assert X/Y/Z translation changes only position along the selected world axis; two updates at the same pointer produce identical results; returning to mouse-down restores the original transform; camera foreshortening uses the captured projected-pixels/world-distance ratio; and a collapsed projected axis cannot start.

- [ ] **Step 2: Write failing rotation-session tests**

Assert positive and negative signed angles for X/Y/Z, world-axis rotation of an already-rotated target matches `GizmoTransformMath.RotateWorld`, orientation changes without position changes, repeated updates do not accumulate, and an invalid current ray returns `false` without a non-finite transform.

Include an edge-on case that selects the fallback at drag start and verifies `radians = tangentPixels / RingPixels` remains continuous even if later ray/plane intersections become valid:

```csharp
[Fact]
public void Rotation_EdgeOnFallbackIsFixedForWholeSession()
{
    var session = StartEdgeOnRotation(GizmoHandleKind.RotateY, new Vector2(100, 100));
    Assert.True(session.TryUpdate(new Vector2(100 + MathF.PI * 36, 100), out var result));
    AssertOrientationClose(ExpectedWorldYRotation(MathF.PI / 2), result.Rotation);
}
```

- [ ] **Step 3: Run tests and confirm failure**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --filter GizmoDragSessionTests`

Expected: FAIL because `GizmoDragSession` does not exist.

- [ ] **Step 4: Implement immutable translation sessions**

Capture original transform, normalized world axis, mouse-down pointer, and projected pixels per world unit. Compute every translation result from those captured values. Reject start when the ratio is non-finite or below the layout threshold.

- [ ] **Step 5: Implement plane and fallback rotation sessions**

At start, try ray/plane intersection and require a finite radial vector above epsilon. Otherwise capture the displayed ring tangent at the picked segment and `RadiansPerPixel = 1 / RingPixels`. Store the chosen strategy in a private enum and never switch it. For plane mode use `atan2(dot(axis, cross(start,current)), dot(start,current))`; for tangent mode project mouse displacement onto the captured tangent. Apply the delta using `GizmoTransformMath.RotateWorld`.

- [ ] **Step 6: Run focused tests**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --filter GizmoDragSessionTests`

Expected: PASS.

- [ ] **Step 7: Commit drag sessions**

```bash
git add src/Engine.Graphics/Gizmos/GizmoDragSession.cs tests/Engine.Graphics.Tests/GizmoDragSessionTests.cs
git commit -m "feat: add stable gizmo drag sessions"
```

### Task 5: Overlay Tessellation from Shared Layout

**Files:**
- Create: `src/Engine.Graphics/Gizmos/GizmoOverlayBuilder.cs`
- Create: `tests/Engine.Graphics.Tests/GizmoOverlayBuilderTests.cs`

**Interfaces:**
- Consumes: `GizmoLayoutResult`, hovered handle, active handle.
- Produces: `internal static Vertex[] Build(GizmoLayoutResult layout, GizmoHandleKind hovered, GizmoHandleKind active)`.

- [ ] **Step 1: Write failing overlay tests**

Assert empty layout returns an empty array; all output positions are finite and inside viewport bounds; rotation triangles precede translation triangles; active highlight is emitted last; active overrides hovered color; and each thick segment produces two consistently wound triangles.

- [ ] **Step 2: Run tests and confirm failure**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --filter GizmoOverlayBuilderTests`

Expected: FAIL because `GizmoOverlayBuilder` does not exist.

- [ ] **Step 3: Implement tessellation**

Tessellate each already-clipped segment into six `Vertex` values using its visible width. Tessellate clipped triangles directly. Emit handles by ascending layer and emit the hovered/active handle once more with bright yellow only as the final highlight pass. Filter any non-finite vertex defensively and return `Array.Empty<Vertex>()` if the layout is invalid.

- [ ] **Step 4: Run focused tests**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --filter GizmoOverlayBuilderTests`

Expected: PASS.

- [ ] **Step 5: Commit overlay building**

```bash
git add src/Engine.Graphics/Gizmos/GizmoOverlayBuilder.cs tests/Engine.Graphics.Tests/GizmoOverlayBuilderTests.cs
git commit -m "feat: render gizmo from shared layout"
```

### Task 6: Public Facade and Atomic Editor Migration

**Files:**
- Replace: `src/Engine.Graphics/EditorGizmo.cs`
- Modify: `src/Editor/Program.cs`
- Create: `tests/Engine.Graphics.Tests/EditorGizmoTests.cs`
- Create: `tests/Engine.Graphics.Tests/EditorGizmoIntegrationTests.cs`
- Delete: `src/Engine.Graphics/AxisGizmo.cs`
- Delete: `src/Engine.Graphics/RotationGizmo.cs`

**Interfaces:**
- Consumes: layout, picker, drag session, overlay builder.
- Produces: the final `EditorGizmo` API and compiling Editor call sites.

- [ ] **Step 1: Write failing facade-state tests**

Target this API:

```csharp
public GizmoHandleKind HoveredHandle { get; }
public GizmoHandleKind ActiveHandle { get; }
public bool IsDragging { get; }
public void UpdateLayout(GizmoTransform target, Matrix4x4 view, Matrix4x4 projection, GizmoViewport viewport);
public bool UpdateHover(Vector2 pointer);
public bool BeginDrag(Vector2 pointer, GizmoTransform target);
public bool TryUpdateDrag(Vector2 pointer, out GizmoTransform transform);
public void EndDrag();
public void CancelDrag();
public Vertex[] BuildOverlay();
```

Assert hover and begin-drag resolve the same handle, active handle persists outside bounds, `UpdateHover` cannot steal an active drag, `EndDrag` and `CancelDrag` clear state, invalid layout cancels a session, and `BuildOverlay` returns no vertices without a valid layout. In `EditorGizmoIntegrationTests`, drive the same selection/event order as `Program.cs` and assert automatic overlay display, gizmo mouse-down priority, UI-click suppression for a consumed gesture, transform isolation, mouse-up termination, and cancellation on selection loss.

- [ ] **Step 2: Run tests and confirm failure**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --filter "EditorGizmoTests|EditorGizmoIntegrationTests"`

Expected: FAIL because the experimental facade does not provide this API or the required deterministic event sequence.

- [ ] **Step 3: Replace the facade and remove legacy classes**

Make `EditorGizmo` coordinate only the current immutable layout, hovered handle, and optional drag session. `BeginDrag` must use `HoveredHandle` after verifying the pointer still picks that same handle. `UpdateLayout` must cancel dragging on invalid matrices/viewport; otherwise it updates display geometry without replacing captured session state. Remove `AxisGizmo.cs` and `RotationGizmo.cs` rather than keeping parallel systems.

- [ ] **Step 4: Migrate Editor event routing in the same atomic change**

Create `GizmoViewport` from `sceneViewport.Position`, `Width`, and `Height`. On mouse move, call `TryUpdateDrag` when dragging and otherwise `UpdateHover`. On primary mouse-down inside the Scene viewport, try `BeginDrag` before `FindObjectAtScreen`. When selection changes, call `CancelDrag`. On mouse-up call `EndDrag`. During update, call `UpdateLayout` with the selected object's current transform and draw `BuildOverlay()`; without selection, cancel and call `window.DrawOverlay([])`. Remove obsolete integer-axis logging, duplicate projection/drag helpers, `GizmoMode`, and unused `sceneAngle`. Keep UI focus behavior, but track whether the gizmo consumed primary mouse-down so mouse-up does not invoke a UI click for that gesture.

- [ ] **Step 5: Run tests and build**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --filter "EditorGizmoTests|EditorGizmoIntegrationTests"`

Expected: PASS.

Run: `dotnet build GameEngine.slnx`

Expected: the complete solution builds with zero errors and no old facade call sites.

- [ ] **Step 6: Commit facade replacement and Editor migration**

```bash
git add src/Engine.Graphics/EditorGizmo.cs src/Engine.Graphics/AxisGizmo.cs src/Engine.Graphics/RotationGizmo.cs src/Editor/Program.cs tests/Engine.Graphics.Tests/EditorGizmoTests.cs tests/Engine.Graphics.Tests/EditorGizmoIntegrationTests.cs
git commit -m "refactor: replace legacy editor gizmos"
```

### Task 7: Final Verification and Documentation Check

**Files:**
- Modify only files requiring corrections discovered by verification.

**Interfaces:**
- Consumes: complete implementation from Tasks 1–6.
- Produces: verified solution with no legacy gizmo references or accidental files staged.

- [ ] **Step 1: Scan dependency and legacy boundaries**

Run: `rg -n "Silk.NET|AxisGizmo|RotationGizmo|GizmoMode|DraggedAxis" src/Engine.Graphics tests/Engine.Graphics.Tests src/Editor`

Expected: no Silk.NET references in `Engine.Graphics` or its tests; no legacy gizmo type/mode references anywhere.

- [ ] **Step 2: Verify XML documentation and formatting**

Run: `dotnet format GameEngine.slnx --verify-no-changes`

Expected: exit 0. Inspect every new public/private method for required XML `summary`, `param`, and `returns` tags; add any missing documentation and rerun.

- [ ] **Step 3: Run the full automated suite**

Run: `dotnet test GameEngine.slnx --no-restore`

Expected: all tests PASS with zero failures.

Run: `dotnet build GameEngine.slnx --no-restore`

Expected: build succeeds with zero errors.

- [ ] **Step 4: Perform a macOS editor smoke test**

Run: `./run.sh`

Verify manually: selecting the cube shows all six handles; camera distance does not change apparent gizmo size; axes win only at their visible/hit geometry; rings remain selectable elsewhere; X/Y/Z translation and rotation are smooth; dragging outside a handle stays active; mouse-up ends the drag; and no overlay appears in adjacent panels. Close the editor normally.

- [ ] **Step 5: Inspect repository scope**

Run: `git status --short && git diff --check && git log --oneline -8`

Expected: `.mimocode/` remains untracked and unstaged; only intended gizmo/test changes exist; no whitespace errors; each task has a focused commit.

- [ ] **Step 6: Commit verification fixes if needed**

If verification required tracked-file corrections, inspect `git diff --name-only`, confirm every listed path belongs to the gizmo work, then stage tracked corrections and commit:

```bash
git add -u
git commit -m "fix: harden editor gizmo verification"
```

If no corrections were needed, do not create an empty commit.
