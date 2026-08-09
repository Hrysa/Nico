# Example Game Server

This headless process loads `example_game/scenes/scene.node` and advances its
`PhysicsWorld` through the engine's BEPU backend at an authoritative fixed tick.
Render interpolation stays disabled, so published node transforms always represent
the latest completed server simulation step.

Run continuously at 60 Hz:

```bash
dotnet run --project example_game_server
```

Run a finite accelerated simulation for testing:

```bash
dotnet run --project example_game_server -- \
  --ticks 180 --no-delay --snapshot-interval 60
```

Available options are `--scene`, `--tick-rate`, `--ticks`,
`--snapshot-interval`, and `--no-delay`. Networking, replicated IDs, input queues,
and snapshot serialization are intentionally separate future layers over this
authoritative simulation host.
