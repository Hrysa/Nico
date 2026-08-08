# Server-side physics

## Authority model

Gameplay physics should be authoritative on the server. Clients send input commands rather than final transforms; the server applies gameplay logic, advances physics at a fixed rate, and publishes resulting state snapshots.

```text
Client input
    -> server gameplay
    -> fixed physics step
    -> authoritative snapshot
    -> client interpolation and rendering
```

The server should reject impossible movement instead of trusting a position supplied by a client.

## What the client needs

A client does not need to simulate physics for remote objects. It sees server physics by rendering the transforms contained in snapshots. Remote objects interpolate between two buffered snapshots:

```csharp
var position = Vector3.Lerp(previous.Position, next.Position, alpha);
```

Interpolation displays the result of physics; it does not determine whether movement is physically valid. That decision belongs to the authoritative server.

Client physics remains useful for:

- predicting the local player to hide round-trip latency;
- local camera and movement collision queries;
- visual-only debris, particles, and effects;
- smoothing interactive objects between snapshots.

Remote replicas should be network-controlled or kinematic. They should not be dynamic bodies whose transforms are also overwritten by snapshots.

## Snapshot state

Each replicated physics object should have a stable network ID. A snapshot should normally include:

```csharp
public readonly record struct NetworkTransform(
    long Tick,
    Vector3 Position,
    Quaternion Rotation,
    Vector3 LinearVelocity,
    Vector3 AngularVelocity);
```

Interpolation only requires positions, rotations, and timestamps. Velocity is still valuable for animation and short extrapolation:

```csharp
var predictedPosition = snapshot.Position
    + snapshot.LinearVelocity * secondsSinceSnapshot;
```

When velocity is not transmitted, the client can estimate it from consecutive snapshots:

```csharp
var velocity = (next.Position - previous.Position)
    / (nextTime - previousTime);
```

Sending server-computed velocity is preferred because it remains stable when snapshot arrival times vary.

## Local-player prediction

Pure interpolation is correct but makes local input feel delayed by network round-trip time. Client prediction can be added for the locally controlled object:

1. Number and retain each local input command.
2. Apply it immediately to the predicted client state.
3. Send the command and its sequence number to the server.
4. Receive the authoritative state and last processed input number.
5. Restore that state and replay any newer unacknowledged inputs.

The server remains authoritative even when the client predicts.

## Engine direction

`Engine.Physics` has no Silk.NET or Vulkan dependency and can run without a window. A headless server can attach a `PhysicsWorld` to a loaded scene and advance it from a fixed-tick loop.

The current solver uses floating-point calculations and is not guaranteed to produce bit-identical results on every architecture. Networking should therefore use authoritative snapshots and reconciliation rather than deterministic lockstep.

The networking layer still needs:

- a headless server host and fixed-tick scheduler;
- tick-indexed input queues;
- stable replicated-object IDs;
- transform and velocity snapshot serialization;
- client interpolation buffers;
- optional local prediction and reconciliation;
- explicit force, impulse, kinematic-target, and teleport commands;
- separation between simulation transforms and interpolated render transforms.
