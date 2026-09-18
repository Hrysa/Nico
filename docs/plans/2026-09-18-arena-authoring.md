# Game-provided Arena world authoring

Status: implemented. Windows native capture and workspace-check evidence is recorded
in [roadmap phase 8](../roadmap.md#8-make-development-repeatable).

The engine's `nico-authoring` contract lets a registered game adapter supply a
validated document, immutable scene extraction, source persistence, and structured
inspection. The editor application explicitly registers adapters; manifest names
do not load arbitrary libraries or execute shell commands. UI, history, bounded
commands, bridge transport and runtime lifecycle remain engine/application-owned.

Arena's client and editor share `arena-arpg-presentation`. Its environment and
procedural landscape code are moved from the client without duplicating loaders.
The `authoring` feature adds the Arena adapter and does not bring egui into games.
`games/arena-arpg/nico.project.toml` selects the real meadow logic/visual sources.

The adapter previews ground, sky, hills, grass, mapped obstacles, and decorations.
The hierarchy exposes 13 obstacles and 86 decorations. Decorations support ground
position, Y rotation, canopy height, addition from the registered model set, and
removal. Obstacle transforms edit axis-aligned collider centers and uniform sizes;
canopy heights scale with the collider. Existing game validation rejects invalid
placements. Spawn and quest data remain visible in structured inspection, without
running gameplay simulation or networking.

Undo/redo uses editor-owned document history. Save writes the existing game TOML
files, preserving unrelated semantic fields; changed files are reformatted and
comments are not retained. Decoration-only changes preserve the logic file bytes.
External source changes block Save. Each file replacement is atomic and a failed
second replacement rolls back the first, but the pair is not crash-atomic. Reload
requires a clean document and resets history. Refresh assets reimports models while
retaining edits; automatic catalog updates alone do not rebuild the game adapter.

Acceptance: shared client/editor loaders; nonempty native rendered meadow;
document and source round trips; invalid edit atomicity; external-edit conflicts;
save rollback; existing game scenery tests; engine/editor tests and Clippy. Native
observation must distinguish GPU captures from user-confirmed desktop visibility.
