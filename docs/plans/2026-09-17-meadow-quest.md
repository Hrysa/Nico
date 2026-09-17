# Meadow Watch quest

Implemented as a single game-owned quest. The settlement warden is a stationary,
non-combatant, non-blocking NPC with a procedural gold-colored body and marker.
The zone logic configures `quest.warden`, `quest.camp`, and `quest.camp_radius_m`.
No model paths or presentation state enter authoritative character saves.

Talk within 2.5 metres to accept Meadow Watch. Defeat three camp monsters, then
return and talk for 50 XP and an iron sword. If a sword is already owned, it is
retained without adding a duplicate. Equip with F. Monsters count when their
server-owned home lies inside the configured camp, even if chased outside it.
Credit follows existing XP ownership: the killing player receives credit.
Kills before acceptance do not count; respawned camp monsters can count again.

`CharacterRecord.quest` stores available/active/ready/completed and a bounded
kill count. Old schema-1 saves default to available when the field is absent.
Record validation rejects inconsistent stage/count combinations. Progress and
reward changes use the existing autosave, disconnect, and orderly shutdown paths;
there is no extra durability guarantee against a crash before a save.

The server handles `PlayerInput.talk` at the simulation boundary, checks living
status and proximity, and commits the reward and completed state together in the
character record. Repeated interaction cannot pay twice. Quest state is private
to the player's snapshot. Wire protocol version 2 requires matching hosts.

E prioritizes nearby loot and otherwise talks; `world_action` with
`{"action":"talk"}` supplies the same authoritative operation for automation.
Inspect `world_client_state.authoritative.quest` and acknowledged input sequences.
HUD text shows progress, objective distance, and the nearby interaction prompt.

This is one fixed quest, not a general quest scripting or NPC dialogue engine.
Party credit, multiple quests, NPC pathfinding, and a harder unlocked encounter
remain outside this milestone.
