# Nico tasks

This file owns concrete next actions. Phase goals, scope, completion criteria, and
validation evidence belong in the [roadmap](docs/roadmap.md); the
[architecture](docs/architecture.md) defines ownership and contracts.

## Deferred rendering optimization

- [ ] Follow-up optimization (deferred): reduce the remaining moving-camera frame-time
      gap against expanded grass without changing visible results. The common instance
      feature is complete without LOD; further performance investigation is deferred
      by the user. See [phase 8](docs/roadmap.md#8-make-development-repeatable).

## World camera stutter

- [ ] Validate left/right camera rotation in the rebuilt world client, including
      first visibility and repeated tree/rock reentry, with the updated debug
      dependency optimization and native driver-validation defaults. The residency fix
      eliminates repeated uploads in the GPU regression; native frame-time impact
      and user-observed smoothness remain unverified. See
      [phase 4 evidence](docs/roadmap.md#4-display-the-game-world).

## Client humanoid models and animation

Acceptance: [roadmap phase 7 character milestone](docs/roadmap.md#client-character-milestone).

- [ ] Verify the RPG animation source and license. Finish visual checks for the selected clips.
- [ ] Check RPG clips retargeted onto Ch03 for reference pose, root motion, contacts, and body shape.
      Use denser samples for floor contact and native strike frames.
      Tune alignment, playback, and stride speed through the character definition files.
      Add twist handling only when observed poses require it.
- [ ] Extend preview controls with skeleton visualization,
      and a timeline widget; validate native reference pose and more motion samples.
- [ ] Check character art across wave groups and replacement assets.
      Include Imp/Puglin stride and weapon contact.

## Following: client presentation

Acceptance: [roadmap phase 7](docs/roadmap.md#7-complete-the-player-experience).

- [ ] Add grounded shadows and combat feedback: weapon trails, impact effects,
      hit flashes, and attack/hit/dodge audio with bounded lifetimes.
- [ ] Add client settings for camera sensitivity, audio volume, and input rebinding,
      plus readable loading/error states and restart controls with automation access.

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
