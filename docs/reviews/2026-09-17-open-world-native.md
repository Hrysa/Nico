# Open-world native validation checkpoint

The initial checkpoint below preceded completion; the final audit follows it. This records a Windows
debug-build session on Vulkan / NVIDIA GTX 1660, not MMO capacity, Internet
deployment readiness, or a user-confirmed desktop demonstration.

## Observations

The first native run exposed server timer oversleep accumulating into a slower
simulation. The server runner now uses cumulative deadlines with bounded catch-up.
A later run still accumulated client input backlog from clock drift. The client
now limits unacknowledged prediction to eight inputs, retaining human button edges
and queued tool actions while waiting. Movement commands count submitted inputs.
Disconnect reasons remain inspectable after reconnect.

The rebuilt Alice client was PID 29560, bridge instance
`18480-18d5f9a486be14ec-18`; Bob was PID 30908, instance `-19` with the same prefix.
Server PIDs 24404 (`-15`), 29652 (`-20`), and 25096 (`-21`) used the same local
character store across orderly restarts. Both clients remained on their first
connection during the initial movement/combat checks; subsequent epoch changes
were explicitly requested reconnects and server restarts.

- Captures from each client show its moving imported character. Alice's capture
  also shows Bob in the same world, alongside the settlement and camp.
- The camp fight killed Alice. The death capture shows the corpse and respawn HUD;
  a subsequent acknowledged respawn restored 100 health at the settlement.
- A separately spawned grunt was defeated through client attack inputs. Alice
  received 10 XP, picked up its `iron_sword`, and equipped it. The captured HUD and
  separately sampled authoritative state agree on the item and progression.
- An explicit reconnect restored position, inventory, equipment and XP. Position
  comparison used a clear location: joining into an occupied location can move a
  character to a nearby free position by design.
- A clean server restart restored Alice at approximately (-3.6, -30.8), with
  100 health, 10 XP, the sword in inventory and equipped. Assertions compared the
  complete position, health, inventory, equipment and XP before and after restart.
- All three final test processes exited after structured stop requests. The
  pre-existing bridge was left running. Automated control has stopped and the
  test windows are closed.

Local artifacts are under `target/open-world-2026-09-17/`: `v3-evidence.json`
contains capture request results and separately sampled before/after snapshots;
`v3-alice-moving.png`, `v3-bob-moving.png`, `v3-sword-combat.png`, `v3-death.png`,
`v3-loot-equipped.png`, and `v3-restored-equipment.png` contain GPU readbacks.
Snapshots are not represented as the exact captured frame. Earlier v1/v2 captures
are diagnostic evidence of incomplete runs, not acceptance evidence.

## Regression checks and remaining work

All 34 client tests passed, including a real loopback test running the client twice
as fast as the server without reconnecting, prediction acknowledgement replay,
and cancellation of active/queued tool commands in the final shutdown publication.
Client all-target Clippy passed with warnings denied.

The repeatable two-client native test script, focused failure-path coverage,
remote interpolation and render-budget review, world-default CLI migration,
public documentation and full workspace checks remain required. This session did
not prove simultaneous cooperative damage or native dodge acceptance. See the
[milestone](../plans/2026-09-17-open-world.md) for the unchanged completion scope.

## Final implementation and completion audit (2026-09-17)

The complete local playable milestone is implemented. World mode is now the
default; `--arena` selects the original independent combat scenario. Public launch
commands and tool semantics live in [README](../../README.md#run-and-validate);
[architecture](../architecture.md#multiplayer-world-ownership) owns dependency,
authority and persistence contracts.

`python apps/nico-bridge/tests/world_native_smoke.py --bin-dir target/debug
--output-dir target/world-native-evidence-v2` passed. The isolated bridge was
PID 5764; initial server PID 32516 was instance `5764-18d60cb74573fc74-1`.
Alice PID 29028 and Bob PID 5760 were instances `-2` and `-3` with that prefix.
After clean server shutdown, replacement server PID 29124 was instance `-4`.
The JSON report records all five process exit codes as zero. This test's bridge
was stopped; the pre-existing user bridge was untouched.

The run saved seven PNGs, inspected from the named instances: both moving players,
dodge, cooperative sword combat, equipped loot, the death transition/HUD and
restored equipment after restart. Before/after snapshots remain explicitly
separate from captured frames. The first script run exposed a test race between
server death publication and the client's next XP snapshot; the final script waits
for the award instead of assuming both observations happen together.

| Accepted requirement | Current implementation and evidence |
| --- | --- |
| Outdoor zone, settlement and monster camp | Authored Meadow world asset; collision regression; native captures show houses, path and camp monsters. |
| Two native clients on one authoritative server | Isolated native script verifies registered PIDs, both players' interest views and shared monster outcomes. |
| Independent ECS actors and immutable definitions | `shared/src/open_world/mod.rs` composes identity, position, combat, player, monster and loot; independent-player and reconnect tests verify separate IDs/state. |
| Validated movement, sword combat and dodge | Bounded sequenced inputs, collision and fixed action phases; shared authority tests, native cooperative kill and authoritative dodge observation/capture. |
| AI, death and respawn | Shared deterministic respawn tests; native idle player dies to a respawned monster and returns at full health after the delay. |
| Loot, inventory, equipment and progression | Exclusive-claim/proximity/ownership tests; native kill grants 10 XP, followed by pickup and equipped-sword state/HUD. |
| Nearby replication | 32-metre per-player views; distant-object and private-inventory regression; native players share the same entities. |
| Prediction, reconciliation and interpolation | Acknowledgement replay test; real loopback client running twice the server rate retains an eight-input bound; remote motion/action clock and respawn/teleport regression. |
| Reconnect and clean restart restore character | Native script compares position, health, inventory, equipment and XP exactly; store/session restart integration tests also pass. |
| Versioning, input and transport bounds | Protocol mismatch and duplicate-session rejection; finite/unit input, stale sequence and queue-limit tests; transport fragmented/oversized frames, backpressure and independent disconnect tests. |
| Observable persistence failures | Corrupt load rejects that session without overwriting the file or stopping healthy players; failed disconnect save retains the authoritative record and succeeds after retry. |
| Structured world/session/entity/action inspection | `world_state`, `world_spawn`, `world_client_state`, `world_action`; runtime test verifies fixed-boundary execution, rejection and shutdown cancellation; real MCP native scenario exercises routing. |
| Arena retained explicitly | CLI parsing tests; legacy native script now passes `--arena`; bounded imported-hero native launch exits successfully after three session frames. |
| Native automation and rendered inspection | `world_native_smoke.py` plus inspected PNGs and `target/world-native-evidence-v2/report.json`; final test processes exited cleanly. |
| Documentation and regression gates | Commands below passed; README, architecture, plan, roadmap and TODO updated. |

Validation commands:

```text
cargo test --workspace --exclude nico-bridge
cargo test -p nico-bridge --target-dir target/bridge-validation
cargo test -p arena-arpg-shared --all-features
cargo check --workspace
cargo clippy --workspace --all-targets -- -D warnings
cargo fmt --all -- --check
git diff --check
cargo run -p arena-arpg-client -- --arena --smoke-frames 3 --no-bridge --background --log-level info
```

Bridge tests use a separate output directory to avoid the user's running bridge
locking the default binary. The shared all-feature suite has 62 tests; the client
suite has 35 and the server CLI suite has one. Rust emitted incremental-cache
finalization notes on Windows, but the listed checks completed successfully.

The result remains a local prototype with simple scenery and procedural monsters.
TCP/loopback validation does not establish Internet suitability or MMO capacity.
GPU captures do not prove desktop visibility, and user-watched acceptance was not
requested or claimed. Large worlds, account security and population scaling remain
outside the accepted milestone.
