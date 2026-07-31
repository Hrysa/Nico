# Angled Scene Camera Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the Editor Scene viewport an elevated front-right default camera view so every world-aligned gizmo handle is readable.

**Architecture:** Add a reusable `LookAt` operation to `PerspectiveCamera`, using its existing yaw/pitch forward convention and dirty-view cache. Configure the Editor camera at `(4, 3, 6)` looking at the origin; gizmo code remains unchanged.

**Tech Stack:** C# 14, .NET 11.0, `System.Numerics`, xUnit 2.9.3.

## Global Constraints

- Keep gizmo layout and transform math world-aligned and unchanged.
- Keep camera roll at zero.
- Every new method requires XML documentation with all parameter tags.
- Verify with `Engine.Graphics.Tests`, the Editor project build, and `./run.sh`; ignore the unrelated empty Player project.
- Preserve `.mimocode/` untouched.

---

### Task 1: Perspective Camera LookAt

**Files:**
- Modify: `src/Engine.Graphics/PerspectiveCamera.cs`
- Create: `tests/Engine.Graphics.Tests/PerspectiveCameraTests.cs`

**Interfaces:**
- Consumes: `PerspectiveCamera.Position`, `GetForwardVector()`, and cached `GetViewMatrix()`.
- Produces: `public void LookAt(Vector3 target)`.

- [ ] **Step 1: Write failing direction and cache tests**

```csharp
[Theory]
[InlineData(4f, 3f, 6f)]
[InlineData(-3f, 2f, 8f)]
public void LookAt_PointsForwardAtTarget(float x, float y, float z)
{
    var camera = new PerspectiveCamera { Position = new Vector3(x, y, z) };
    camera.LookAt(Vector3.Zero);
    AssertVectorClose(Vector3.Normalize(-camera.Position), camera.GetForwardVector());
}

[Fact]
public void LookAt_InvalidatesCachedViewMatrix()
{
    var camera = new PerspectiveCamera { Position = new Vector3(0f, 0f, 6f) };
    var before = camera.GetViewMatrix();
    camera.LookAt(new Vector3(2f, 0f, 0f));
    Assert.NotEqual(before, camera.GetViewMatrix());
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --filter PerspectiveCameraTests --no-restore -m:1`

Expected: FAIL because `PerspectiveCamera.LookAt` does not exist.

- [ ] **Step 3: Implement the minimal LookAt operation**

Calculate `direction = Normalize(target - Position)`, reject a zero or non-finite direction without changing rotation, set pitch to `Asin(clamp(direction.Y))`, set yaw to `Atan2(direction.X, -direction.Z)`, set roll to zero, and set `_viewDirty = true`.

- [ ] **Step 4: Verify GREEN and regression tests**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --no-restore -m:1`

Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Engine.Graphics/PerspectiveCamera.cs tests/Engine.Graphics.Tests/PerspectiveCameraTests.cs
git commit -m "feat: aim perspective camera at target"
```

### Task 2: Editor Default Scene View

**Files:**
- Modify: `src/Editor/Program.cs`

**Interfaces:**
- Consumes: `PerspectiveCamera.LookAt(Vector3 target)` from Task 1.
- Produces: Editor Scene camera initialized at `(4, 3, 6)` and aimed at `Vector3.Zero`.

- [ ] **Step 1: Change the Editor camera initialization**

```csharp
sceneCamera.Position = new Vector3(4f, 3f, 6f);
sceneCamera.LookAt(Vector3.Zero);
```

- [ ] **Step 2: Run tests and build the Editor**

Run: `dotnet test tests/Engine.Graphics.Tests/Engine.Graphics.Tests.csproj --no-restore -m:1`

Expected: all tests PASS.

Run: `dotnet build src/Editor/Editor.csproj --no-restore -m:1`

Expected: build succeeds with zero errors.

- [ ] **Step 3: Launch the Editor smoke test**

Run: `./run.sh`

Verify: the cube remains centered; selecting it displays distinct projected X/Y/Z translation axes and three readable rotation rings; translation and rotation gestures still start and end normally.

- [ ] **Step 4: Commit and inspect scope**

```bash
git add src/Editor/Program.cs
git commit -m "feat: angle default editor scene camera"
git status --short
```

Expected: only `.mimocode/` remains untracked.
