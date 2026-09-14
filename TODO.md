# Nico tasks

This file owns concrete next actions. Phase goals, scope, completion criteria, and
validation evidence belong in the [roadmap](docs/roadmap.md); the
[architecture](docs/architecture.md) defines ownership and contracts.

## Next: paired rendering samples

Selected scope and acceptance: [roadmap phase 4](docs/roadmap.md#4-display-the-game-world).

- [ ] Define the full reference game's mechanic, platforms, and content scale
      before expanding beyond these samples.

## Native host validation

Acceptance criteria: [roadmap phase 1](docs/roadmap.md#1-run-a-native-client).

- [ ] Windows: resize repeatedly, maximize/restore, minimize/restore, and close
      after transitions; record OS, backend, adapter, and results.
- [ ] macOS: repeat the same checks and record the environment and results.
- [ ] Check rendering recovery, GPU validation errors, frame timing after restore,
      focus-loss input release, and clean shutdown; fix observed failures.

Use successful presentation counts alongside session-frame counts when checking
rendering. Desktop minimization and Winit suspension require separate checks.

## Conditional follow-ups

Only take these up when a concrete consumer needs them:

- [ ] Add persistent bridge schema caching if discovery across bridge restarts is
      required; schemas currently remain cached only for the bridge's lifetime.
- [ ] Add game operations through registered MCP tools as tasks require them;
      apply simulation commands at runtime-owned boundaries.
- [ ] Connect a native gamepad provider through `nico-input` when a target device
      requires it.
- [ ] Add Slang reflection and asset-backed shaders when the first rendered game
      object needs them.
- [ ] Extract a provider-neutral host contract only if a second provider reveals
      shared requirements.

Profiling tasks are not active; see [phase 9](docs/roadmap.md#9-validate-performance).
