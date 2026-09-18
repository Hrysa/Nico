# Editor projects consumed by game code

Status: implemented and verified on Windows. Validation evidence is recorded in
[roadmap phase 8](../roadmap.md#8-make-development-repeatable).

The game directory becomes the project root. `nico.project.toml` declares a name,
version, project-relative asset roots, default scene, and Cargo client/server target
names. Opening a project does not launch targets or execute project commands.

`nico-scene` owns the runtime-free manifest and scene serialization/validation and
ECS instantiation contract. The initial scene contains the model identities and
transforms already authored by the editor. Editor history and UI stay in the app.
`nico-presentation-control` owns shared model scene extraction so the editor and
minimal game interpret the same transforms and imported geometry.

The editor honors the manifest's asset roots and default scene. Existing loose
content directories remain supported for compatibility. The minimal client gains
`--project PATH`, loads the declared scene, and instantiates its objects at startup;
its normal sample modes remain available. Structured state exposes authored object
identity and transforms, imported asset errors, and draw readiness.

Acceptance: manifest/path/version validation and ECS instantiation tests; one scene
saved by the editor reopened by the game, with matching scene state and inspected
rendered captures; isolated native sessions and workspace checks. General custom
component reflection and executable game-code embedding are later contracts, not
implicit behavior of a Cargo target name.
