# Third-person arena ARPG reference game

Status: shared simulation, MCP adapter, native hosts, camera, and combat presentation
implemented, including three waves and grunt/brute enemies. Initial human playtest
and balance approval are recorded; no tuning changes are requested. This replaces the earlier
collect-and-escape proposal. Scope targets belong in
[roadmap phase 5](../roadmap.md#5-make-a-playable-local-game); actions belong in
[TODO](../../TODO.md#next-client-humanoid-models-and-animation).

## First playable encounter

Control a melee fighter with a third-person camera and clear three waves in
one compact arena, with three monsters per wave. Prove positioning, melee attacks, dodging, readable monster
attacks, health, victory, defeat, and restart. The encounter has one hero, one weapon,
and two monster archetypes: a grunt and a slower, larger purple brute. Equipment, loot, progression, and more
abilities are later ARPG design work after the combat slice is playable.

Windows first, solo first, and later two-player online co-op remain the targets.
Numeric rules below remain initial choices. Scripted combat comparisons are recorded
in the roadmap; the user has accepted the current balance.

## Camera and controls

- A perspective camera follows behind and above the hero. Mouse movement orbits
  yaw and pitch; clamp pitch for floor visibility. Sweep its boom against arena
  geometry and shorten it to avoid clipping, restoring distance smoothly.
- WASD moves relative to camera yaw on the XZ floor, with normalized diagonals.
  Convert to world-space intent before submitting to shared simulation.
- The hero faces movement direction while moving. Left mouse starts a melee
  attack toward camera yaw, locking facing for the attack; no automatic targeting.
- Space dodges along movement intent, or current facing when stationary.
- R restarts. Escape releases pointer capture; clicking recaptures without also
  attacking. Focus loss clears held input and pending automation input. Simulation
  continues while unfocused. Camera and pointer state belong to the client.

## Arena and initial state

Use a flat 24-by-24-unit floor at Y=0 with X/Z=[-12,12], low perimeter walls,
and no interior obstacles. Spawn the hero at (0,-6), facing +Z. Spawn monsters
at (-5,5), (0,7), and (5,5), facing -Z, initially idle for 60 ticks. Coordinates
are (X,Z). The hero starts with 100 health. Grunts have 60 health; brutes have 100.

Distinct silhouettes, colors, and facing indicators identify actors. A roughly
five-minute encounter remains a later pacing target. The three-wave fixture takes
about 50 simulation seconds for the recorded reactive policy; human pacing remains
unverified. No fixed waiting time pads the run to that target.

### Wave progression

| Wave | Monsters in slots 1, 2, 3 |
| --- | --- |
| 1 | Grunt, grunt, grunt |
| 2 | Grunt, grunt, brute |
| 3 | Grunt, brute, brute |

Clearing waves one and two starts a 180-tick (three-second) intermission. Simulation
run ticks continue; actor positions and combat freeze. Movement/attack/dodge are
rejected with `intermission`; restart remains available. At the boundary, the hero
recovers 40 health capped at 100, actors return to the validated initial spawns,
all actions/cooldowns reset, and the next roster is installed. Monsters wait 60 ticks
at the start of every wave. Wave-local ticks reset while run ID and run ticks persist.
The client clears old hit flashes and suppresses held movement until release when a
wave changes. The HUD shows the current wave, countdown, and health recovery.
Actor slots are reused, so identify a monster by run ID, wave, and actor ID. Attack
IDs remain monotonic across waves and restarts. Only clearing wave three wins.

## Combat and simulation

Authoritative rules advance at 60 Hz. Movement, attack, dodge, and restart are
semantic actions used by both human input and MCP.

| Action | Initial rule |
| --- | --- |
| Hero movement | Up to 4 units/second; blocked during attack or dodge. |
| Hero melee | 12 windup ticks, 6 active ticks, 18 recovery ticks; 25 damage in a 90-degree frontal sector of radius 2. |
| Dodge | 18 ticks at 8 units/second; invulnerable for the first 12 ticks; 48-tick cooldown from dodge start. |
| Grunt movement | Chase at 2 units/second; start attack within 1.6 units center distance. |
| Grunt melee | 30 windup ticks, 6 active ticks, 36 recovery ticks; 20 damage in a 90-degree frontal sector of radius 1.8; facing locks at windup start. |
| Brute movement | Chase at 1.4 units/second; start attack within 2.2 units center distance. |
| Brute melee | 48 windup ticks, 6 active ticks, 48 recovery ticks; 30 damage in a 90-degree frontal sector of radius 2.4. |

Shared `ActorKind` combat data drives simulation, telegraphs, and MCP action timing.
Both enemy types use the same 0.4-unit collision footprint. Brute visual size does
not expand its collision body.

Each attack has an ID and hits each target at most once. A contact avoided through
dodge invulnerability consumes that swing's opportunity to hit that target, so it
cannot deal delayed damage when invulnerability ends. One hero swing may hit
multiple monsters. Test target centers against the sector each active tick; edge
contact counts. Telegraph visuals must communicate this reach. Contact alone deals
no damage. Dead monsters cannot attack or block. Damage does not interrupt a
surviving actor's action in this first slice.

Attacks begin only while idle and are not buffered. Dodge can begin while idle or
cancel hero attack recovery after the six active ticks. It cannot interrupt sword
windup/active frames. If the action lock or cooldown will end within nine ticks
(150 ms), a dodge press is retained and fires at the first eligible boundary. Earlier
presses are rejected; they do not linger until a later opening. The retained direction
is normalized at submission. A newer combat press replaces the pending dodge;
restart, wave clear, defeat, focus loss, and shutdown clear it. Dodge wins if attack
and dodge are requested together. Its cooldown and invulnerability are unchanged.

Monster AI reserves strike starts in chronological order, at least 30 ticks apart.
An enemy in range waits until its proposed strike (current run tick plus its windup)
meets that boundary. Once started, facing and timing remain locked. Reservations
persist if the attacker dies, avoiding an immediate replacement strike; wave reset
clears the reservation. This spaces active windows without shortening telegraphs.
Per tick: restart first if requested; otherwise accept actions, resolve movement,
evaluate active hits, apply damage simultaneously, resolve deaths/outcomes, then
advance phase timers. Invulnerability is evaluated for that tick. Actors alive at
hit evaluation may trade hits. Hero death takes priority if the final monster and
hero die together. Health clamps at zero.

## Movement and collision

The game chooses kinematic floor movement using `nico-physics` and Rapier's
character controller, with no jumping or gravity-driven actors. Actors have circular
footprints of radius 0.4. Sweep movement against
walls and living actors, slide at contact, and stop at corners. Dodge uses the
same path and cannot pass through actors or walls. Resolve actors in stable ID
order against latest positions; do not push actors. This introduces an explicit
ordering bias to assess in playtests. Use a 0.0001-unit contact margin and Rapier's
bounded controller iterations; discard unresolved motion when its bound is reached.
Reject overlapping or out-of-bounds spawns. The open arena needs direct pursuit,
not navigation around interior obstacles.

Fixed steps and stable ordering support repeatability on the tested build;
cross-platform floating-point determinism remains unverified for phase 6.

## Outcomes and feedback

The run is playing (including intermission), won, or lost. Clear all three waves
while alive to win; zero
hero health loses. Terminal states freeze gameplay and retain its final snapshot,
while host progress continues. Restart increments run ID, resets run tick, and
restores spawns, health, facing, timers, cooldowns, input, and encounter state.
It clears queued gameplay actions but preserves host diagnostics and command history.

Show hero/monster health, current wave, intermission countdown, remaining monsters
in this wave, dodge readiness, controls, and
result/restart feedback. Windup, active reach, recovery, dodge, damage, and death
must look distinct. Begin with mesh pose changes, attack telegraphs, and hit flashes;
imported skeletal animation is a later content decision. Camera collision, text
HUD, and combat presentation are implemented in the native arena client. The full
90-degree sector stays visible during windup; a brighter fill grows radially to its
boundary at activation, then turns yellow. During the final 12 windup ticks, the
sector pulses pale gold, the weapon brightens, and an overhead exclamation mark
appears. An INCOMING HUD cue warns when a nearby monster is about to strike. Telegraphs disappear during recovery and
terminal results. Weapon poses wind back, swing, and settle during recovery. The HUD
shows cooldown progress; idle actors animate walking only while their position moves.
Restart clears presentation hit flashes.

## MCP contract

Shared/client/server packages exist under `games/arena-arpg/`. Preserve minimal-game
rendering samples. Games own semantic extensions; engine crates retain transport,
registration, services, and built-in status/stop/diagnostics. Tool threads queue
bounded requests or read owned snapshots; fixed runtime boundaries apply actions.

Schemas reject extra fields and non-finite numbers. Mutations require an integer
current `run_id`, checked again at application.

| Tool | Arguments | Result |
| --- | --- | --- |
| `game_state` | Empty object | Snapshot sequence/age/closed state, run ID/tick/state, wave/total_waves/wave_tick/intermission_ticks, buffered_dodge direction or null, next_monster_strike_tick (earliest permitted next strike), actor IDs/kinds/positions/facing/health/max_health/attack_range/windup_ticks, action IDs/phases/timers, cooldowns, current-wave monster count, active movement command ID |
| `game_move` | run_id, world-space x/z in [-1,1], integer ticks in 1..120 | Accepted command ID; normalized movement lease |
| `game_attack` | run_id, yaw in radians in [-pi,pi] | Accepted command ID for one attack |
| `game_dodge` | run_id, nonzero x/z in [-1,1] | Accepted command ID; immediate or buffered within nine ticks of readiness; completes on dodge start |
| `game_restart` | run_id | Accepted command ID |
| `game_command` | Positive integer command_id | Pending/running/completed/cancelled/rejected, source/result run IDs, start/end ticks, applied tick count, reason code |

Allow one queued/active movement lease, one queued combat action, and one reserved
restart slot. Overload returns `busy`. Movement leases expire after the requested
ticks even when combat blocks locomotion. Combat commands complete on action start,
not damage; buffered dodges report `running` with zero applied ticks and a null
start tick until they start, and retain the combat slot; snapshots expose phases for observation. Reject incompatible actions
with `action_locked`, unavailable dodge with `cooldown`, terminal actions except
restart with `run_finished`, intermission actions except restart with `intermission`, and old-run requests with `stale_run`.

Human movement cancels automation movement with `human_input`. Human combat takes
priority over queued tool combat, cancelling it with the same reason. Focus loss
cancels queued combat and queued/active movement with `focus_lost`, without undoing
an attack/dodge already started. Restart cancels old requests with `restarted`; its
boundary does not advance the new run. Clearing a wave cancels remaining movement
lease ticks with `wave_cleared`, retaining the number already applied. Terminal state cancels remaining requests
with `run_finished`. Shutdown rejects requests and cancels outstanding ones with
`shutdown`.

Use monotonic process-local IDs and retain 128 terminal outcomes plus bounded
pending/active entries. Return `expired_command` for evicted IDs, `unknown_command`
for never-issued IDs, and explicit `not_ready`, `invalid_arguments`, or
`shutting_down` errors when applicable. History survives reconnect/reset, not exit.
Acceptance means queued: poll outcomes and inspect snapshots at or after the
reported boundary. Never blindly retry timed-out mutations. Discover instances and
schemas through the bridge. MCP directions are world-space; camera input stays local.

## Acceptance criteria

This table defines acceptance scope. Completed headless checks and their tested
environment are recorded in the roadmap, including handler and bridge-routing
coverage and isolated native scenarios. Initial human approval is recorded there;
current balance is accepted, while the separate manual lifecycle checks remain.

| Area | Required evidence |
| --- | --- |
| Movement | Camera-relative mapping, diagonal speed, walls/corners, actor blocking, and bounded dodge travel. |
| Combat | Phase boundaries, facing/range checks, one hit per target, multiple targets, cooldown/invulnerability edges, simultaneous damage, and action priority. |
| Monsters | Initial idle, pursuit, attack range, locked telegraph facing, recovery, and dead-actor removal from combat/collision. |
| Outcomes | Exact intermission timing, wave rosters/spawn reset/healing, restart during countdown, final-wave victory, defeat, simultaneous final deaths losing, terminal freeze, and full restart from every state. |
| Timing | Same command/tick sequence repeats on the tested build; host progress survives terminal gameplay. |
| Operations | Invalid schemas, overload, stale IDs, rejection, history expiry, human takeover, focus loss, reconnect, and shutdown. |
| Headless | Normal movement/attack/dodge commands win; an idle hero loses; both restart without presentation dependencies. |
| Native | Camera follow/orbit/collision, pointer lifecycle, readable telegraphs, matching health/results, and keyboard/mouse plus MCP play. |
| Lifecycle | Bridge reconnect retains run/history; bridge exit leaves game running; distinguish stop acceptance from completed shutdown. |

Record native platform/backend/adapter and encounter duration. Tune readability and
pacing through playtests before expanding ARPG content.

## Engine integration

The arena consumes engine camera control, quaternion coordinate helpers, procedural
meshes, cached bitmap text, Rapier movement, frame input accumulation, command
bookkeeping, and snapshot publication. The game supplies tuning, poses, bindings,
and authored geometry. Camera and mesh poses publish unit quaternions; planar facing
angles remain gameplay data. Native pointer operations and launch policy are
engine-owned. See [architecture](../architecture.md#assets-and-game-construction)
for ownership and [README](../../README.md#arena-operations) for client operations.

Geometry is authored once in shared gameplay: 0.4-wide walls have inner faces at
±11.8, so 0.4-radius actor centers stop at ±11.4.
