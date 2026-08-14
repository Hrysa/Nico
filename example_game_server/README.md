# Example Game UDP Server

The example server loads `example_game/scenes/scene.node`, imports its referenced assets, and
runs the authored colliders in headless BEPU. The scene's `TerrainColliderComponent` is also used
by the lightweight authoritative player motor. Its asset reference resolves to
`maps/island.nterrain`, so the server and client use the same height samples, 100×100 placement,
5-unit height scale, center, and node transform.

Run it from the repository root:

```powershell
dotnet run --project example_game_server -- --port 7777
```

Start this process before pressing Play in the Editor or launching the standalone Player. The
example game's `ThirdPersonController` requires a successful handshake with `127.0.0.1:7777` by
default; without it, Play startup fails instead of falling back to local movement. The host and port
are editable on the script component. If an established server stops responding, Play is stopped.

Useful options:

```text
--scene <path>                 Scene containing the authoritative terrain collider
--tick-rate <hz>               Fixed simulation rate; default 60
--port <udp-port>              IPv4 UDP port; default 7777, zero selects an ephemeral port
--network-snapshot-rate <hz>   Snapshot rate; default 20
--client-timeout <seconds>     Silent-session timeout; default 10
--ticks <count>                Optional finite tick count for smoke tests
--no-delay                     Run fixed ticks without wall-clock pacing
```

## Protocol version 1

Every integer is little-endian and every floating-point value is an IEEE-754 32-bit single.
Datagrams start with the ASCII bytes `NICO`, followed by version `1` and a one-byte message type.
Messages have exact sizes; malformed, oversized, stale, unauthenticated, and nonfinite inputs are
ignored.

| Type | Direction | Payload after header |
|---|---|---|
| `1` Hello | Client → Server | `uint32 nonce` |
| `2` Welcome | Server → Client | `uint32 echoedNonce`, `uint32 clientId`, `uint64 token`, `uint16 tickRate`, `int64 tick`, `float3 spawn` |
| `3` Input | Client → Server | `uint32 clientId`, `uint64 token`, `uint32 sequence`, `float2 moveXZ`, `float facingYaw`, `byte jump` |
| `4` Snapshot | Server → Client | `uint32 clientId`, `uint64 token`, `uint32 acknowledgedInput`, `int64 tick`, `float3 position`, `float3 velocity`, `float facingYaw`, `byte grounded` |
| `5` Disconnect | Client → Server | `uint32 clientId`, `uint64 token` |

The client begins with `Hello` and accepts a welcome only when `echoedNonce` matches. A repeated
hello from the same endpoint returns the existing session, which makes a lost welcome harmless.
Subsequent messages must contain the assigned ID and unpredictable token. Input sequence numbers
use wrapping unsigned ordering, allowing the server to discard reordered or duplicated inputs.

Clients send intent only. Position, velocity, and grounded state in snapshots are authoritative.
The current lightweight motor provides acceleration-limited movement, gravity, jumping, terrain
grounding, ground snapping, surface normals, and a 45-degree walkable-slope limit. It intentionally
does not simulate dynamic-body pushing or moving platforms.
