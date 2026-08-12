# Physics

`Engine.Physics` provides a renderer-independent fixed-step 3D simulation backed by BepuPhysics v2. Physics data lives in components from `Engine.Core`, so scenes and game scripts do not depend directly on Bepu types.

## Components

A falling object needs both motion and collision components:

```csharp
node.AddComponent(new RigidBodyComponent
{
    MotionType = RigidBodyMotionType.Dynamic,
    Mass = 1f
});

node.AddComponent(new BoxColliderComponent
{
    Size = Vector3.One
});
```

A static floor needs only a collider:

```csharp
floor.AddComponent(new PlaneColliderComponent
{
    Size = new Vector2(100f, 100f)
});
```

Supported motion types are static, dynamic, and kinematic. Concrete collider components are box, sphere, capsule, cylinder, finite XZ plane, static triangle mesh, and heightfield terrain. Colliders expose center, dimensions or an asset reference, trigger state, collision layer/mask, friction, and restitution.

## Simulation ownership

- Dynamic: physics integrates position from velocity and gravity.
- Kinematic: game code owns the transform; the body participates in contacts without receiving dynamic integration.
- Static or collider-only: the transform is expected to remain fixed.

Do not assign a dynamic body's position every frame. Change `LinearVelocity`, or use a kinematic body for authored movement. Explicit force, impulse, kinematic-target, and teleport APIs remain future work.

## Runtime behavior

`PhysicsWorld` defaults to a 60 Hz fixed step with bounded catch-up. Bepu owns broad-phase and narrow-phase collision detection, sleeping, contact constraints, and friction response. The engine adapter applies per-component gravity and linear damping before each Bepu step. Trigger pairs raise `Contact` without physical response.

Boxes, spheres, capsules, and cylinders use Bepu's exact primitive collision shapes. `PlaneColliderComponent` is a configurable finite thin static box. `MeshColliderComponent` consumes explicitly imported/chunked static triangle collision data; adding a collider to a visual GLB does not implicitly reuse render geometry. `TerrainColliderComponent` consumes imported heightfield samples. Angular motion remains locked until angular velocity and torque are exposed by the engine component API.

Client runtimes enable transform interpolation between completed steps. Headless and authoritative simulations should leave interpolation disabled so node transforms expose the latest completed simulation state.

## Server authority

Gameplay physics should be authoritative on the server:

```text
client input
    -> server gameplay
    -> fixed physics step
    -> authoritative snapshot
    -> client interpolation and rendering
```

Remote client objects do not need local gameplay physics. They can interpolate snapshots containing a stable network object ID, simulation tick, position, rotation, linear velocity, and angular velocity. Velocity supports animation and short extrapolation but is not required for interpolation.

The local player may predict inputs and later reconcile with an authoritative snapshot:

1. number and retain local input commands;
2. apply each command immediately to predicted state;
3. send the command to the server;
4. restore the returned authoritative state;
5. replay inputs newer than the server's acknowledged sequence.

Bepu's floating-point simulation is not guaranteed to be bit-identical across architectures. Networking should use authoritative snapshots and reconciliation rather than deterministic lockstep.

Still required for networked physics:

- a headless server host and fixed-tick scheduler;
- tick-indexed input queues;
- stable replicated-object IDs;
- compact snapshot serialization;
- client interpolation buffers;
- optional prediction and reconciliation;
- separation between authoritative simulation state and render-only network poses.
