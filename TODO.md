# Nico tasks

This file owns concrete next actions. Phase goals, scope, completion criteria, and
validation evidence belong in the [roadmap](docs/roadmap.md); the
[architecture](docs/architecture.md) defines ownership and contracts.

## Next: networked co-op design

Acceptance: [roadmap phase 6](docs/roadmap.md#6-play-over-a-network).

- [ ] Define player/session identity, authoritative input handling, state snapshots,
      and disconnect/rejoin behavior for two-player arena co-op.
- [ ] Choose interpolation/prediction and reconciliation requirements; assess Rapier
      repeatability against the chosen model before assuming lockstep or rollback.

## Native host validation

Acceptance criteria: [roadmap phase 1](docs/roadmap.md#1-run-a-native-client).

- [ ] Windows: verify physical keyboard/mouse play and close-button shutdown after
      transitions; automated MCP resize/maximize/minimize/restore and stop passed
      in the recorded phase 1 environment.
- [ ] macOS: repeat the same checks and record the environment and results.
- [ ] Validate OS-originated suspend/resume on a supported desktop. The shared
      Winit suspension path and wakeup/stop handling have unit coverage; native
      Windows minimization is recorded separately from OS suspension.
- [ ] Measure individual frame timing after restore and validate held physical-key
      release across focus loss. Automated presentation recovery, pointer release,
      movement-command cancellation, and orderly stop passed on Windows.
- [ ] Check GPU recovery/failure paths beyond the successful Windows transitions.

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
- [ ] Add Slang reflection and asset-backed shader loading when reference-game
      materials or content iteration require them.
- [ ] Extract a provider-neutral host contract only if a second provider reveals
      shared requirements.

Profiling tasks are not active; see [phase 9](docs/roadmap.md#9-validate-performance).
