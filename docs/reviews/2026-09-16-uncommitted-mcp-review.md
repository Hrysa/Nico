# Uncommitted changes: independent review and MCP validation

Reviewed 2026-09-16 against HEAD, covering the 63 changed tracked files (including
staged additions), plus locally supplied GLBs under `tmp/`. Previous native/MCP
acceptance claims were not used as evidence. No production source was changed by
this review. The startup-path defect remains open and is explicitly deferred by
the user. Documentation corrections and a maintained regression harness follow below.

## Findings

### P2: skin shader bypasses native asset-path resolution

In `crates/nico-winit/src/lib.rs:295`, `skin_shader_path` is copied directly from
configuration, while mesh and bootstrap paths pass through `resolve_asset_path`.
The skin shader is subsequently opened relative to the working directory at
line 708. The resolver otherwise supports finding repository assets relative to
the executable's ancestors.

Reproduced with the freshly built
`target/review-validation/debug/arena-arpg-client.exe --no-bridge --smoke-frames 3`,
using `target/review-evidence` as the working directory. It exited 1 with
`failed to read skin shader assets/presentation/shaders/generated/wgpu/skinned_meshes.wgsl`
and OS error 3. The arena enables this shader unconditionally, so even procedural
mode regresses. The preview uses the same configuration path.

Resolve the skin shader alongside the mesh shader during host construction and
add a regression for a launch outside the repository root. This defect is not
covered by successful repository-root MCP sessions or current unit tests.

### P3: current implementation documents contradict one another (resolved)

The original model/animation plan called arena integration, crossfades and
synchronization subsequent work. The asset-import review likewise called playback
and arena integration pending, contradicting implementation and newer documents.
These status paragraphs now link to the implemented production pipeline. README,
roadmap and production documents distinguish fresh validation from the historical
MCP claims, including the unrevalidated 16/64-character and contact observations.

## Fresh checks

All following checks passed on this Windows workspace:

- `cargo test --workspace --all-features --target-dir target/review-validation`
- `cargo clippy --workspace --all-targets --all-features --target-dir target/review-validation -- -D warnings`
- `cargo fmt --all -- --check`
- `cargo run -p nico-shaderc -- --check`
- `cargo test -p nico-rhi-wgpu --target-dir target/review-validation -- --ignored`
  (four real-GPU tests, including weighted skinning versus CPU reference)
- Separate `cargo check -p nico-assets --no-default-features` checks with no
  features, `png-import`, `gltf-import`, and `runtime-loading`, using the isolated target.
- `git diff HEAD --check`

Review covered importer extension and worker ownership, model validation,
sampling/retargeting/playback, attachments, immutable presentation and bounds,
GPU palette validation and static/skinned interoperability, preview controls,
arena snapshot-driven animation, movement leases, and documentation status.
No additional actionable correctness defect was established in those paths.

## Connected client: commands, rendering, and user observation

The user identified PID **20952**, executable
`E:\repos\Nico\target\debug\arena-arpg-client.exe`, as the watched window.
Its bridge instance was `31384-18d5c1e2a6ab63bc-1`; its published state confirmed
the imported character. This was the user's existing binary, distinct from the
fresh isolated binaries tested below.

Discovery used `list_instances` and `list_game_tools`; interaction used
`call_game_tool`, without relying on dynamic tool refresh. Mutations were queued
through the registered APIs and command outcomes were polled.

- Command results: restart succeeded; a movement hold ran for 674 simulation ticks,
  changed direction and ended `cancelled/released`; six attacks and four dodges
  each completed their request by starting the action. These command completions
  do not by themselves establish damage or rendering.
- Rendered output: the first capture polling windows were too short for asynchronous
  PNG encoding. The retained dodge request subsequently completed and its PNG
  showed a roll. A second movement pass renewed the lease while polling captures
  to completion. Both images were inspected while the hold remained running and
  showed distinct run poses and the blade. That hold ran for 308 ticks and ended
  `cancelled/released`. State and PNG observations are separate samples, not a
  frame-exact correspondence. GPU readback does not establish desktop scanout.
- User observation: after the first pass the user answered **yes** when asked
  whether restart, right/left movement, repeated attacks, and dodges were visible.
  This confirms that particular demonstration, not general art/contact acceptance.
- Automated control ended and the user's window was left open. No host stop was
  sent to that instance.

Diagnostics identified NVIDIA GeForce GTX 1660 / Vulkan. The log included an OBS
Vulkan-layer API-version warning; no host failure was reported in the exercised
session. This review does not establish the cause of the earlier reported MCP
problem. The short capture timeout was a limitation of this review's first test
pass and was corrected by longer polling, not a proven game defect.

## Fresh isolated native MCP run

The prepared Python harness used the existing native-smoke MCP transport helper,
an independent bridge on an ephemeral loopback port, and freshly built binaries
in `target/review-validation/debug`. It discovered schemas and verified registration
PIDs before control. All interaction after launch went through bridge MCP.

| Host | PID | Instance suffix | Fresh evidence |
| --- | --- | --- | --- |
| Character preview | 27792 | `-1` | Two shared-asset characters; pause/seek independence, clip change/resume, captured running pose, offscreen culling and re-entry |
| Arena server | 21452 | `-2` | Restart, hold renewal/direction changes, movement samples, release |
| Imported-hero arena client | 27828 | `-3` | Same movement checks, captured running poses, attack/dodge command outcomes and captures |

Instance prefix: `29408-18d5c2526fbf9f24`. Preview and both arena hosts accepted
MCP stop and exited 0. Only test-owned processes were closed. The additional
outside-root launch intentionally exposed the startup defect and exited 1.
The preview, movement, attack and dodge PNGs were visually inspected. The fresh
client's captures show a rendered skinned character and attached blade; the
attack image shows windup geometry and the dodge image shows a roll transition.

## Evidence and remaining limits

Local ignored artifacts are in `target/review-evidence/`: `mcp_review.py`,
`mcp-report.json`, per-host logs/catalogs, `live-mcp.json`, `live-captures.json`,
and PNG captures. These record the new runs rather than reusing previous evidence.

This review does not revalidate the previous 16/64-character acceptance or release
cadence numbers, every supplied clip, frame-exact weapon contact, all-wave visual
quality, or platform/backend portability. Previous broad completion statements in
the production acceptance document must not be read as newly verified by this
review; fresh native scope is the scenarios above. Local GLBs remain user-supplied
inputs and were not staged or copied into shipping content.

## Follow-up: complete three-wave playthrough

The user correctly identified that the initial control checks did not constitute
a complete gameplay review. A new full encounter test was run on 2026-09-16 with
the same freshly built binaries and the imported Ch03 hero. It used real bridge
MCP for movement, attacks, dodges, command polling, snapshots, restarts and stop;
it did not change health, damage, simulation speed, or wave state directly.

Fresh bridge instance prefix: `29056-18d5c29ea35f1688`. Server suffix `-1` was
PID 16872; imported-hero client suffix `-2` was PID 31648. The user confirmed
that the new client window was visible and they were watching it.

| Host | Waves completed | Victory tick | Final health | Wall time to victory |
| --- | --- | --- | --- | --- |
| Server | 1, 2, 3 | 2879 | 100 | 89.875 seconds |
| Imported-hero client | 1, 2, 3 | 3025 | 100 | 50.454 seconds |

The test asserted terminal `won` at wave 3 and observed both intermissions.
It also exercised buffered dodge after attack, restart after victory, natural
defeat without input, restart after defeat, and orderly MCP shutdown. Both hosts
exited 0. The final pre-stop client status reported no host failure and 6,810
presentation API successes; this is not a GPU-completion or scanout count.

While automated control was active, the reviewer inspected captures from the same
client showing wave 1 cleared, wave 2 and its brute/grunt composition, wave 2
cleared, and wave 3 with two brutes. The final PNG was also inspected and explicitly
shows **ARENA CLEARED**, wave 3 of 3, zero monsters, and 100 health. Defeat output
was captured and inspected separately. These images and authoritative snapshots
are separate samples, not claimed to be the same frame.

The full-run artifacts are `target/review-evidence/full-levels/report.json`,
per-host logs, and `arena-wave-1-cleared.png`, `arena-wave-2.png`,
`arena-wave-2-cleared.png`, `arena-wave-3.png`, `arena-victory.png`, and
`arena-defeat.png`. The repeatable harness is
`target/review-evidence/full_levels.py`, adapted from the existing arena native
smoke test to load the imported character and report instance/PID and wave progress.
Automated control stopped and the test-owned window closed after shutdown.

MCP outcomes and rendered victory are verified. The user separately confirmed:
"Yes, I saw all three waves and victory." The watched full playthrough therefore
has command, rendered-output, and user-observation evidence.
The startup-path finding remains open, explicitly skipped at the user's request.
The documentation finding is resolved.

## Maintained regression harness

The imported-hero scenario now lives in
`apps/nico-bridge/tests/arena_native_smoke.py`, using paired `--character-model`
and `--character-animations` options. At that validation point, procedural mode
remained the default; the subsequent asset-location update below changes it.
The harness checks registration PIDs against its own processes, verifies imported
animation is active, records both intermissions and all three waves, victory,
natural defeat, and restart. Captures include instance identity and separately
sampled game/client state. The report explicitly does not claim user observation.
README documents the invocation. At that validation point, model/animation inputs
were still external to the game folder.

Fresh maintained-harness validation passed using `target/review-validation/debug`
with the local Ch03 model and RPG animation directory, in `--combat-only` mode.
Private bridge instances `21592-18d5c349d87c3944-1` (server PID 31508) and
`21592-18d5c349d87c3944-2` (client PID 28704) both completed waves 1–3 and both
intermissions, reaching victory at ticks 2879 and 3063 respectively. Defeat,
restart, buffered dodge and orderly stop passed; both processes exited 0.
The client reported 6,739 presentation API successes. Wave 2 and wave 3 captures
were inspected while control was active; the victory capture showed ARENA CLEARED,
wave 3/3, zero monsters and 100 health. Recorded post-capture snapshots are separate
samples. This repeat collected no user-observation confirmation and did not exercise
window transitions. Artifacts: `target/review-evidence/maintained-imported/`.

Three invalid character-argument combinations were rejected before launch.
Relative documentation file links and `git diff --check` passed. No Rust production
code changed in this follow-up; the earlier fresh Cargo checks were not repeated.

## Game asset location update

The selected model and six clips were subsequently copied byte-for-byte into
`games/arena-arpg/assets/presentation/characters/hero/`, with provenance recorded
in `LICENSE.md`. Original experimental inputs remain unchanged. The arena client
and MCP harness now use the game-owned imported hero by default; explicit paired
overrides and `--procedural-hero` are supported. This changes the earlier default
described above. Shader loading remains unchanged.

The updated client passed all 20 unit tests, strict package/all-target Clippy,
formatting, and the isolated build. The first default-path MCP run loaded and
rendered the hero, and its server cleared all three waves, but the automated client
lost during wave 1 (tick 482). No asset-loading or host error appeared in its log.
This is a failed playthrough, not a validation pass; its cause is not established.
Evidence is retained in `target/review-evidence/game-owned-hero/`.

The next run reached client wave 2 but failed on an explicitly cancelled movement
command (`focus_lost`, zero applied ticks), retained under
`target/review-evidence/game-owned-hero-repeat/`. The gameplay policy now records
that terminal cancellation and samples fresh state before choosing another action.
It does not retry timeouts or accept other cancellation reasons. The dedicated
window/focus-loss test retains its strict cancellation assertion. Five focused
command-outcome checks covered completion, allowed focus cancellation, and rejected
unexpected cancellations, with one mutation submission per command.

The final run passed in `--combat-only` mode without character-path overrides:
server PID 21020 / instance `6900-18d5c43015856bbc-1` and client PID 27404 /
instance `6900-18d5c43015856bbc-2` cleared waves 1–3 and both intermissions.
Victory ticks were 2879 and 3517 respectively; client health was 80. Buffered dodge,
natural defeat, restart and orderly shutdown passed, with both hosts exiting 0.
The client recorded 7,075 presentation API successes. Wave 2 and wave 3 captures
were inspected during active control, followed by the ARENA CLEARED capture.
Post-capture snapshots are separate samples; no user-observed demonstration is
claimed for this run. Evidence: `target/review-evidence/game-owned-hero-final/`.
The successful repeat does not establish the cause of the first failed playthrough.
