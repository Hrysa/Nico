# Nico tasks

This file owns concrete next actions. Phase goals, scope, completion criteria, and
validation evidence belong in the [roadmap](docs/roadmap.md); the
[architecture](docs/architecture.md) defines ownership and contracts.

## Client humanoid models and animation

Acceptance: [roadmap phase 7 character milestone](docs/roadmap.md#client-character-milestone).

- [ ] Finalize the supplied character selection: verify the RPG animation upstream
      source/license and finish visual acceptance of the currently bound clips; see the
      [asset inspection](docs/plans/2026-09-16-character-assets.md) and
      [first visual review](docs/reviews/2026-09-17-hero-clip-review.md).
- [ ] Visually validate RPG clips retargeted onto Ch03, including reference-pose
      alignment, root-motion policy, contacts, and proportions. Finish floor/foot
      contact and native strike-frame checks with denser samples; the
      [sword/grip review](docs/reviews/2026-09-17-sword-grip.md) records implemented
      grip, sword selection and numerical contact/weapon-clearance coverage. Tune
      alignment, playback and authored stride speed through the
      [character definitions](docs/plans/2026-09-17-character-definitions.md). Refine profiles
      and add twist handling only when observed poses require it.
- [ ] Extend preview controls with skeleton visualization,
      and a timeline widget; validate native reference pose and more motion samples.
- [ ] Broaden character-art acceptance across wave compositions and replacement
      assets, including Imp/Puglin stride and weapon-contact calibration; see the
      [Bestiary review](docs/reviews/2026-09-17-bestiary.md).

## Following: client presentation

Acceptance: [roadmap phase 7](docs/roadmap.md#7-complete-the-player-experience).

- [ ] Add the material and lighting support needed by the selected character,
      starting with base color, normals, and a simple lit scene.
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
