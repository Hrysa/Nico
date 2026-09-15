# Rapier integration review — 2026-09-15

Status: both findings resolved in the uncommitted integration based on `de58b26`.
Review covered the new physics crate, arena migration, removal of the old slide
solver, manifests, tests, and documentation. Findings and source locations below
describe the reviewed version; subsequent fixes are recorded under
[resolution](#resolution).

## P1: Query refresh consumes dynamics registration changes

Location: `crates/nico-physics/src/world.rs`, `refresh_queries` (line 187).

The wrapper runs `CollisionPipeline::step` against the same body/collider sets used
by `PhysicsWorld::step`. In Rapier 0.35.3, that collision-only pipeline processes
body changes without registering them with the dynamics island manager, then
clears their modification flags. The subsequent dynamics step no longer sees the
new-body registration work.

Reproduction: create a dynamic ball at Y=3 with gravity -9.81, issue an unrelated
shape cast, then run 60 fixed steps. The ball remains at Y=3 with zero velocity.
The identical setup without the query reaches Y=-1.925437697 and velocity
Y=-9.810000196. A scene query must not change whether dynamics runs.

Refresh query acceleration without consuming dynamics bookkeeping, or explicitly
preserve the required provider updates. Add a regression comparing simulation with
and without queries after body creation and wake/teleport operations. Current tests
exercise query freshness and dynamics separately, so they miss this interaction.

## P2: Kinematic characters cannot activate fixed sensors

Location: `crates/nico-physics/src/world.rs`, collider construction (line 126).

Collider construction leaves Rapier's `ActiveCollisionTypes` at its default, which
only enables pairs involving a dynamic body. The wrapper exposes sensors and
kinematic bodies but provides no way to enable kinematic/fixed sensor detection.
Collision-group masks do not override this separate body-type filter.

Reproduction: create a fixed sensor sphere at the origin and a kinematic sphere at
X=-3, move the kinematic target to the origin, and step twice. The contact snapshot
is empty despite overlap. This prevents trigger zones from detecting kinematic
characters. Existing sensor coverage uses a dynamic body and therefore passes.

Enable the necessary sensor pair types or expose an explicit Nico-owned pair
policy. Cover kinematic/fixed and relevant kinematic/kinematic sensor combinations
through contact snapshots and the runtime event adapter.

## Original review verification and limits

Both reproductions used a temporary standalone harness under ignored
`target/physics-review`, with a copy of the workspace lockfile and an offline Cargo
run against the reviewed `nico-physics` source. Rapier was 0.35.3. The findings do not
depend on native windows, MCP timing, or performance measurements.

The arena's current query-only movement path does not step dynamic rigid bodies or
consume sensor contacts, so its earlier native smoke pass does not exercise either
failure. Engine/provider ownership is otherwise consistent with the intended thin
wrapper: Rapier types stay private and the runtime has no physics dependency.
Full workspace tests and native smoke were not rerun during the original review; targeted
reproductions establish the two missing cases.

## Resolution

On 2026-09-15, P1 was fixed by replacing the collision-only pipeline refresh with
`update_collision_geometry()`: it copies colliders, computes current world poses,
and builds a separate spatial tree. Lookups leave pending dynamics bookkeeping and
last-step contact observations untouched. Real dynamics steps invalidate the cache.

P2 was fixed by enabling all active body-type combinations for sensors. Bilateral
collision masks still apply, and solid colliders retain the provider defaults.

All 13 physics tests passed, including three new behavioral regressions: identical
420-tick dynamic trajectories with/without lookups across insertion, teleport,
sleep/wake, and replacement; kinematic entry/exit with fixed and kinematic sensors,
creation orders, and filtering; and trigger observations through runtime events.
Both original standalone reproductions also passed: queried and unqueried balls
now have identical trajectories, and the kinematic overlap yields a sensor contact.
Broader validation is recorded in the
[roadmap](../roadmap.md#rapier-integration-validation).
