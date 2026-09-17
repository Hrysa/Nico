use serde_json::{Value, json};

pub(super) fn description(name: &str) -> &'static str {
    match name {
        "game_characters" => {
            "Inspect immutable authoritative character definitions loaded at startup, without model or animation data."
        }
        "game_state" => {
            "Read an owned arena snapshot with age, run/tick, health, action phases and movement command ID. No live world access."
        }
        "game_move" => {
            "Queue world-space movement for 1..120 fixed ticks. Zero direction waits. One movement lease at a time. Acceptance is not execution; poll game_command. Never blindly retry after timeout."
        }
        "game_move_hold" => {
            "Hold world-space movement for 1..120 simulation ticks. lease_id 0 starts a hold: its command_id is the lease ID. Renew/change direction using that lease ID before expiry; renewals return separate command IDs. No release gap on renewal. Poll game_command and game_state.movement_hold. Expiry counts simulation ticks, not wall time. Never blindly retry timeouts."
        }
        "game_move_release" => {
            "Release the named movement hold at the next fixed boundary. Poll game_command for application; the hold ends cancelled/released. A stale lease cannot stop a newer hold."
        }
        "game_attack" => {
            "Queue one melee attack facing yaw radians (zero is +Z). Completes when the action starts, not when damage lands. Poll game_command and game_state; never blindly retry after timeout."
        }
        "game_dodge" => {
            "Queue one dodge in a nonzero world-space direction. Completes when dodge starts. Action lock/cooldown may reject at the runtime boundary. Never blindly retry after timeout."
        }
        "game_restart" => {
            "Queue restart for the current run. Reserved slot; cancels old-run actions and increments run ID. Acceptance is not execution. Never blindly retry after timeout."
        }
        "game_command" => {
            "Poll a process-local command outcome. History survives run reset and bridge reconnect; latest 128 terminal outcomes retained. A completed combat request means action started."
        }
        _ => unreachable!(),
    }
}
fn object(properties: Value) -> Value {
    let keys: Vec<_> = properties.as_object().unwrap().keys().cloned().collect();
    json!({"type":"object","properties":properties,"required":keys,"additionalProperties":false})
}
fn integer(min: u64) -> Value {
    json!({"type":"integer","minimum":min})
}
fn nullable_integer() -> Value {
    json!({"type":["integer","null"],"minimum":0})
}
pub(super) fn input(name: &str) -> Value {
    let axis = json!({"type":"number","minimum":-1,"maximum":1});
    object(match name {
        "game_state" | "game_characters" => json!({}),
        "game_command" => json!({"command_id":integer(1)}),
        "game_restart" => json!({"run_id":integer(1)}),
        "game_attack" => {
            json!({"run_id":integer(1),"yaw":{"type":"number","minimum":-std::f64::consts::PI,"maximum":std::f64::consts::PI}})
        }
        "game_move" => {
            json!({"run_id":integer(1),"x":axis,"z":axis,"ticks":{"type":"integer","minimum":1,"maximum":120}})
        }
        "game_move_hold" => {
            json!({"run_id":integer(1),"lease_id":integer(0),"x":axis,"z":axis,"ticks":{"type":"integer","minimum":1,"maximum":120}})
        }
        "game_move_release" => json!({"run_id":integer(1),"lease_id":integer(1)}),
        "game_dodge" => json!({"run_id":integer(1),"x":axis,"z":axis}),
        _ => unreachable!(),
    })
}
pub(super) fn output(name: &str) -> Value {
    match name {
        "game_characters" => object(
            json!({"schema_version":{"const":1},"definitions":{"type":"array","minItems":3,"maxItems":3,"items":{"type":"object"}},"closed":{"type":"boolean"}}),
        ),
        "game_state" => {
            let vector = object(json!({"x":{"type":"number"},"z":{"type":"number"}}));
            let action = object(
                json!({"kind":{"enum":["idle","attack","dodge"]},"phase":{"enum":["idle","windup","active","recovery","dodge"]},
                "id":nullable_integer(),"elapsed_ticks":integer(0),"phase_ticks_remaining":integer(0)}),
            );
            let actor = object(
                json!({"id":integer(0),"kind":{"enum":["hero","grunt","brute"]},"max_health":integer(1),"attack_range":{"type":"number"},"windup_ticks":integer(1),"position":vector,"facing":vector,"health":integer(0),"dodge_cooldown":integer(0),"action":action}),
            );
            object(
                json!({"closed":{"type":"boolean"},"snapshot_sequence":integer(1),"snapshot_age_ms":integer(0),"run_id":integer(1),"tick":integer(0),
                "movement_hold":{"anyOf":[{"type":"null"},object(json!({"lease_id":integer(1),"x":{"type":"number"},"z":{"type":"number"},"remaining_ticks":{"type":"integer","minimum":1,"maximum":120},"state":{"enum":["pending","running"]}}))]},
                "state":{"enum":["playing","won","lost"]},"actors":{"type":"array","items":actor,"minItems":4,"maxItems":4},
                "buffered_dodge":{"anyOf":[vector,{"type":"null"}]},"next_monster_strike_tick":integer(0),"wave":{"type":"integer","minimum":1,"maximum":3},"total_waves":{"const":3},"wave_tick":integer(0),"intermission_ticks":{"type":"integer","minimum":0,"maximum":180},"monsters_remaining":{"type":"integer","minimum":0,"maximum":3},"active_movement_command_id":nullable_integer()}),
            )
        }
        "game_command" => object(
            json!({"command_id":integer(1),"source_run_id":integer(1),"result_run_id":integer(1),
            "state":{"enum":["pending","running","completed","cancelled","rejected"]},"start_tick":nullable_integer(),"end_tick":nullable_integer(),
            "applied_ticks":integer(0),"reason":{"type":["string","null"]}}),
        ),
        _ => object(json!({"accepted":{"const":true},"command_id":integer(1),"run_id":integer(1)})),
    }
}
