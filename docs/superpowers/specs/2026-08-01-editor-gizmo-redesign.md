# Editor Gizmo Redesign

## Purpose

Replace the current experimental gizmo with a deterministic, testable transform gizmo for the Scene viewport. The gizmo appears automatically when an object is selected and displays translation axes and rotation rings together.

The first version supports smooth world-space translation and rotation. It does not support scale, snapping, local-space transforms, keyboard mode switching, or toolbar mode switching.

## Goals

- Keep the gizmo a constant visual size in screen pixels as the camera moves.
- Display translation and rotation controls simultaneously.
- Give rendering, hover, and click handling one shared definition of handle geometry and layer order.
- Prevent hover/click conflicts between overlapping translation axes and rotation rings.
- Calculate every drag result from immutable mouse-down state to prevent accumulated drift.
- Keep all gizmo logic independent of Silk.NET and concrete scene-object types.
- Handle degenerate camera and projection conditions without jumps or invalid transforms.

## Non-Goals

- Scale handles.
- Local-space orientation.
- Transform snapping.
- Keyboard shortcuts or explicit transform-mode selection.
- Multi-object transforms.
- Undo/redo integration.
- GPU ID-buffer picking.

## Architecture

`EditorGizmo` is the public facade owned by the Editor. It accepts transform state, camera matrices, viewport bounds, and pointer input. It exposes hover/drag state, generated overlay vertices, and transform updates without depending on `MeshInstance3D`.

The implementation is divided into focused components:

- `GizmoLayout` creates a screen-consistent representation of all visible handles for one target, camera, and viewport.
- `GizmoPicker` hit-tests the generated layout using its explicit interaction layers.
- `GizmoDragSession` captures immutable mouse-down state and calculates updated transforms.
- `GizmoOverlayBuilder` converts the generated layout into colored overlay triangles.
- `GizmoProjection` contains validated world/screen projection, unprojection, and constant-size conversion helpers.

These components belong in `Engine.Graphics`. They use `System.Numerics`, graphics-domain value types, and no Silk.NET APIs. The Editor remains responsible for selection, pointer-event routing, and applying returned transforms to the selected object.

The old `AxisGizmo` and `RotationGizmo`, plus the current experimental monolithic `EditorGizmo`, are superseded by this system. There will be only one active gizmo implementation.

## Handle Model and Layering

Handles use explicit semantic identities rather than integer axes:

- `TranslateX`, `TranslateY`, `TranslateZ`
- `RotateX`, `RotateY`, `RotateZ`

Every handle contains its semantic identity, interaction layer, display geometry, hit geometry, color, and interaction validity. Rendering proceeds from back to front:

1. Rotation rings.
2. Translation axes and arrowheads.
3. The hovered or actively dragged handle highlight.

Picking traverses the same handle collection from front to back. Translation therefore wins only when the pointer lies inside a translation handle's hit region. Otherwise, a rotation ring remains available even where its projected bounds overlap an axis. The handle returned by hover is exactly the handle that mouse-down starts; the two operations cannot disagree.

While dragging, the active handle remains selected regardless of pointer position. Hover is not recomputed and another handle cannot steal the operation.

## Constant Screen Size

The layout uses a configurable target size in pixels. For a perspective camera, `GizmoProjection` calculates the world-units-per-pixel value at the target's view-space depth and scales world-oriented handle geometry accordingly. Projection therefore preserves the requested screen size as camera distance changes.

The calculation is repeated when the target, camera, or viewport changes. It is not accumulated across frames. Invalid viewport dimensions, a target on or behind the camera plane, an invalid clip `W`, or a non-finite result makes the layout unavailable for that frame.

All handles remain aligned to the world X, Y, and Z axes. The selected object's rotation does not rotate the gizmo.

## Interaction Flow

### Selection and Hover

When the Editor has a selected object inside the Scene viewport, it requests a layout using the object's world position and the Scene camera. No selection produces no gizmo overlay.

Pointer movement inside the Scene viewport updates hover through `GizmoPicker`. Hit regions are measured in pixels and may be slightly wider than visible strokes for usability. Points outside the viewport cannot hover a handle.

### Drag Start

On primary mouse-down, the Editor asks the gizmo to begin a drag before attempting scene-object selection. A successful start captures:

- The selected handle.
- Original position and rotation.
- Mouse-down pointer position.
- Target position and relevant world axis.
- View and projection matrices.
- Viewport bounds.
- Stable operation-specific reference values.

The drag session owns no reference to the selected scene object.

### World-Space Translation

Translation projects a known world-space length along the selected axis into screen space. Pointer displacement from mouse-down is projected onto that screen direction and converted back to world distance using the captured ratio between projected pixels and that known world length. This accounts for perspective and axis foreshortening. The result is added to the original position along the selected world axis.

Because every update uses the original position and mouse-down pointer, event frequency does not change the result and moving the pointer back to its starting point restores the original position.

If the axis has an unusably short screen projection, the translation handle is marked non-interactive for that layout. It is not pickable and cannot produce a large or discontinuous movement.

### World-Space Rotation

Rotation first intersects the pointer ray with the plane through the target whose normal is the selected world axis. The signed angle between the captured start vector and current vector produces the rotation delta around that axis.

When ray/plane intersection is numerically unstable because the plane is nearly edge-on, the drag session uses a captured screen-space tangent reference. Pointer displacement along that tangent maps continuously to an angle delta. The fallback is selected at drag start and remains fixed for the session, preventing an interaction from switching algorithms mid-drag.

The engine convention is fixed as the row-vector matrix order `Rz * Ry * Rx`, where `Rotation.X`, `.Y`, and `.Z` are radians around their named axes. The delta is converted to an axis-angle rotation around the selected world axis and post-multiplied onto the original orientation, which applies the delta in world-space order under the row-vector convention. The resulting orientation is converted back to Euler angles in that same `Rz * Ry * Rx` convention only when returning the transform update. Conversion returns the canonical solution with Y in `[-PI/2, PI/2]`; at the Euler singularity it sets Z to zero and solves the equivalent X angle. This version does not change the public transform representation, but drag calculations do not add directly to an Euler component.

`Node3D.GetModelMatrix()` must use `Scale * Rz * Ry * Rx * Translation`. Its current omission of Z rotation must be corrected as part of integration so the visible object transform matches the gizmo result. Tests lock down this composition and conversion order, including already-rotated targets and the Euler singularity behavior.

### Drag End and Cancellation

Primary mouse-up commits the current result and clears the session. Deselecting the object, losing the Scene viewport, receiving invalid camera/viewport data, or explicit cancellation clears the session without applying additional changes. A failed calculation during an otherwise valid drag returns no update and preserves the last valid transform.

## Rendering

The gizmo remains a 2D swapchain overlay drawn above viewport textures. `GizmoOverlayBuilder` tessellates the same line, ring-segment, and arrowhead shapes used by picking. Overlay vertices use screen-pixel coordinates and the existing `IWindow.DrawOverlay` path.

Default axis colors are red for X, green for Y, and blue for Z. Hovered and active handles use a distinct bright highlight. The active highlight renders last. Overlay primitives are CPU-clipped to the Scene viewport rectangle before tessellation, and picking uses the same clipped shapes. The gizmo cannot render or interact inside adjacent Editor panels.

## Validation and Failure Handling

Public operations validate viewport dimensions and matrix inversion results. Projection and drag calculations reject NaN and Infinity values. Invalid conditions have inert outcomes:

- Layout failure produces no handles and no overlay vertices.
- Picking failure produces no hovered handle.
- Drag-start failure does not create a session.
- Drag-update failure produces no transform update.
- Overlay generation never emits non-finite vertices.

Degenerate geometry is omitted individually when possible so one camera-aligned axis does not disable unrelated handles.

## Editor Integration

`Editor/Program.cs` continues to own the selected `MeshInstance3D`. It creates one `EditorGizmo`, passes Scene viewport input to it before object selection, applies successful transform updates, and clears/cancels the gizmo when selection changes.

Object selection may continue using the existing temporary screen-distance implementation; mesh-accurate object picking is outside this redesign. The gizmo consumes pointer input only when a handle begins or owns a drag. Other Scene and UI input remains available otherwise.

## Testing

Add `Engine.Graphics.Tests` as a test project with no Silk.NET dependency. Unit tests cover:

- Constant pixel dimensions at multiple camera distances and viewport sizes.
- World-axis orientation despite target rotation.
- Render order and inverse picking order.
- Translation winning at true translation/ring overlaps.
- Rotation remaining pickable near, but outside, translation hit geometry.
- Hover and mouse-down resolving to the same handle.
- Translation calculated from original state without accumulated drift.
- Positive and negative signed rotation around each world axis.
- World-axis rotation of an already-rotated target, plus Euler/matrix round trips under the engine's chosen convention.
- Stable edge-on rotation fallback.
- Camera-aligned translation axes becoming non-interactive.
- Viewport offsets and rejection outside viewport bounds.
- Invalid viewport, non-invertible matrix, behind-camera target, and non-finite input behavior.
- Overlay output containing only finite vertices.
- Drag persistence outside handle bounds and termination on mouse-up or cancellation.

Editor-level integration coverage verifies that selection shows the gizmo, deselection hides it, gizmo mouse-down takes priority over object selection, translation changes only position, and rotation changes only orientation.

## Acceptance Criteria

- Selecting an object displays world-aligned translation axes and rotation rings together.
- The gizmo remains visually constant in size as camera distance changes.
- Hover highlight always identifies the operation that a click starts.
- Translation controls take foreground priority only inside their actual hit areas.
- Smooth translation and rotation work on all three world axes without snapping, including targets that already have rotation.
- Dragging does not jump, accumulate event-dependent error, or switch handles.
- Degenerate views fail inertly and never introduce NaN/Infinity into object transforms or overlay vertices.
- Gizmo logic is covered by deterministic tests and does not reference Silk.NET.
