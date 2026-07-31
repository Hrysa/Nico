# Angled Scene Camera Design

## Purpose

Make the world-aligned editor gizmo easier to read by changing the Scene viewport's default camera angle. The gizmo itself remains truthful to world X/Y/Z and does not receive a cosmetic orientation that disagrees with transform behavior.

## Design

- Add a `PerspectiveCamera.LookAt(Vector3 target)` operation that computes yaw and pitch for the camera's existing forward-vector convention and marks the view matrix dirty.
- Initialize the Editor Scene camera at `(4, 3, 6)` and aim it at the world origin.
- Keep the camera roll at zero.
- Do not change gizmo layout, picking, layering, translation, or rotation calculations.
- Do not change the Game viewport camera.

The elevated front-right view projects positive world axes toward distinct screen directions and prevents the X/Y rotation rings from both appearing edge-on. Objects remain centered because the camera explicitly targets the origin.

## Validation

- Unit-test that `LookAt(Vector3.Zero)` makes the camera forward vector equal the normalized target direction from multiple camera positions.
- Unit-test that `LookAt` refreshes an already-cached view matrix.
- Verify all existing gizmo tests still pass.
- Build the Editor and launch `./run.sh` to confirm the selected cube and all gizmo axes/rings are readable from the new default view.
