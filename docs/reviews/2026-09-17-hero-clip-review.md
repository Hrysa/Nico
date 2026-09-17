# Hero clip visual review

Historical review of the build and content described below. For current models,
bindings, and ownership, see the [character contract](../plans/2026-09-17-character-definitions.md)
and [Bestiary integration review](2026-09-17-bestiary.md).

Follow-up: the later [sword/grip implementation review](2026-09-17-sword-grip.md)
records the fix and replacement sword clips. Findings below describe the original
unarmed selection before that change.

## Conclusion

The six selected RPG clips produce recognizable actions on Ch03 in the inspected
poses. This review does not close character-art acceptance. Open reference-pose
fingers are conspicuous during combat, and the arena blade follows an open hand
rather than a convincing grip. Resolve the hand pose and weapon fit before treating
the unarmed attack as an accepted sword animation. The captures do not demonstrate
a need for a general twist-bone solution.

## Tested configuration and evidence

Reviewed on 2026-09-17 at commit `5ee89ac`, using Cargo's development build on
Windows, NVIDIA GeForce GTX 1660, Vulkan, with 1920x1080 GPU-readback PNGs. Both
executables used the game-owned Ch03 model and the six files listed in the
[asset selection](../plans/2026-09-16-character-assets.md#candidate-clip-mapping).
The preview was launched with six repeated `--animation` arguments; the arena
used its default asset paths. No asset or executable source was modified.

| Host | PID | Bridge instance | Shutdown |
| --- | --- | --- | --- |
| Character preview | 25596 | `18480-18d5f9a486be14ec-1` | MCP stop, 7,845 steps/presentations, process exit 0 |
| Arena client | 14316 | `18480-18d5f9a486be14ec-2` | MCP stop, 3,378 steps, 3,377 presentations, process exit 0 |

Ignored local evidence is retained in `target/clip-review-2026-09-17/`: 28 PNGs,
`evidence.json` with before/after publication snapshots, capture request IDs,
host diagnostics and game command outcomes, and `asset-hashes.json`. Each capture
was requested through the corresponding instance's `window_snapshot` tool and
copied before the next capture overwrote the host's result file.

Preview controls were polled for successful terminal outcomes. Stationary samples
used zero crossfade, one-shot mode, and seek/pause. Each clip also ran at 0.1 speed
for at least 1.5 seconds before a capture request while playing. Three transition
captures used normal playback speed and a 0.2-second crossfade. Their preceding
snapshots reported nonzero fade weights; captures show intermediate body poses.
This is sparse visual sampling, not a recorded continuous-motion review.

Published state and GPU images are separate observations. In moving cases the
after-snapshot can already describe the next action. In particular, the arena
attack's preceding state reported the 0.326666647-second contact marker, but that
does not establish the captured image as the exact contact frame. The preview
file `attack-0408.png` is a sample at 0.4083333 seconds, not that marker.

## Clip findings

| Clip | Inspected output | Acceptance limit |
| --- | --- | --- |
| Idle | Bent-knee guard at start and during playback; arena blade follows the right hand | Open fingers do not grip the blade; inspect the full loop seam |
| Run forward | Alternating leg poses at 0, 0.2 and 0.4 seconds; rendered running during arena movement | No foot-lock or stride-speed acceptance from sparse captures |
| Attack R1 | Recognizable torso/arm extension; arena blade remains attached during the attack | Open palm; exact contact-frame alignment and suitability as a sword attack remain open |
| Roll forward | Side samples at 0.2, 0.43 and 0.65 seconds show tuck, recovery and standing; arena capture shows inversion during dodge | Floor clearance and held-weapon clearance require denser sampling |
| GetHit F1 | Forward torso bend and lowered arms at 0.2 seconds and during playback | No new isolated arena hit-transition/contact acceptance |
| Death1 | Backward collapse in playback, horizontal final pose from front and side; arena terminal death state holds the endpoint | Enemies obscure the arena corpse; floor intersection remains unverified |

The reference-pose capture shows the intact textured T-pose. No obvious exploding
geometry or gross limb collapse was seen in these samples. This does not validate
every frame, every view, or replacement character proportions. Preview state
reported `in_place=true`; the preview lacks a floor, and this review did not compare
preserved-root playback against in-place conversion.

Arena restart, attack, dodge and a 120-tick movement request completed through
runtime-owned commands. The first movement capture shows running; the second,
despite its `arena-run-later.png` filename, shows return to idle after the movement
lease. It must not be counted as a second running sample. The arena initially
reached defeat while unattended; this was an art inspection, not a victory test.
Camera command 1 was observed applied. Both hosts reported no failure; diagnostics
included the existing OBS Vulkan hook warning.

## Remaining acceptance

Use the existing character pipeline to resolve the demonstrated grip issue, then
recheck the six actions with the weapon, including roll clearance. A denser floor
and contact review is needed before claiming foot locking or combat calibration.
Do not infer source-clip finger mappings or a desired sword grip solely from joint
names; compare source and target hand poses first. Concrete work is tracked in
[TODO](../../TODO.md#client-humanoid-models-and-animation).

No user-watched demonstration was requested or confirmed, and no desktop visibility
claim is made. Source/license verification, broad art acceptance, automated test
suite results and other platforms are outside this review. Earlier test evidence
remains in the [milestone](../roadmap.md#client-character-milestone).
