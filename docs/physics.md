# Physics

`Engine.Physics` provides a renderer-independent fixed-step 3D simulation. Physics data lives in components from `Engine.Core`, so scenes and game scripts do not depend on a native backend.

## Components

A falling object needs both motion and collision components:

```csharp
node.AddComponent(new RigidBodyComponent
{
    MotionType = RigidBodyMotionType.Dynamic,
    Mass = 1f
});

node.AddComponent(new ColliderComponent
{
    Shape = ColliderShape.Box,
    Size = Vector3.One
});
```

A static floor needs only a collider:

```csharp
floor.AddComponent(new ColliderComponent
{
    Shape = ColliderShape.Plane
});
```

Supported motion types are static, dynamic, and kinematic. Supported collider shapes are box, sphere, capsule, cylinder, and infinite plane. Colliders expose center, dimensions, trigger state, friction, and restitution.

## Simulation ownership

- Dynamic: physics integrates position from velocity and gravity.
- Kinematic: game code owns the transform; the body participates in contacts without receiving dynamic integration.
- Static or collider-only: the transform is expected to remain fixed.

Do not assign a dynamic body's position every frame. Change `LinearVelocity`, or use a kinematic body for authored movement. Explicit force, impulse, kinematic-target, and teleport APIs remain future work.

## Runtime behavior

`PhysicsWorld` defaults to a 60 Hz fixed step with bounded catch-up. It applies gravity, linear damping, positional correction, restitution, and friction. Trigger pairs raise `Contact` without physical response.

Finite non-plane shapes currently use conservative world-space bounds for collision detection. Angular velocity, torque, sleeping, continuous collision detection, and exact capsule/cylinder contact solving are not implemented.

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

The current floating-point solver is not guaranteed to be bit-identical across architectures. Networking should use authoritative snapshots and reconciliation rather than deterministic lockstep.

Still required for networked physics:

- a headless server host and fixed-tick scheduler;
- tick-indexed input queues;
- stable replicated-object IDs;
- compact snapshot serialization;
- client interpolation buffers;
- optional prediction and reconciliation;
- separation between authoritative simulation state and render-only network poses.
