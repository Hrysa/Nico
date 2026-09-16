use super::*;
use crate::{ArenaPlugin, FIXED_STEP, InputFocusLost};
use nico_runtime::App;

fn harness() -> (App, Operations) {
    let (builder, _catalog) = register(AppBuilder::new().add_plugin(ArenaPlugin)).unwrap();
    let app = builder.build().unwrap();
    let endpoint = app.world().resource::<Operations>().unwrap().clone();
    (app, endpoint)
}
fn call(endpoint: &Operations, name: &str, args: Value) -> Value {
    endpoint
        .call(name, args.as_object().unwrap().clone())
        .structured_content
        .unwrap()
}
fn movement(endpoint: &Operations, run: u64, ticks: u16) -> u64 {
    call(
        endpoint,
        "game_move",
        json!({"run_id":run,"x":1,"z":0,"ticks":ticks}),
    )["command_id"]
        .as_u64()
        .unwrap()
}
fn poll(endpoint: &Operations, id: u64) -> Value {
    call(endpoint, "game_command", json!({"command_id":id}))
}
fn snapshot(endpoint: &Operations) -> Value {
    call(endpoint, "game_state", json!({}))
}
fn tick(app: &mut App, count: usize) {
    for _ in 0..count {
        app.tick(FIXED_STEP).unwrap();
    }
}

fn hold(endpoint: &Operations, lease: u64, x: f64, z: f64, ticks: u16) -> u64 {
    call(
        endpoint,
        "game_move_hold",
        json!({"run_id":1,"lease_id":lease,"x":x,"z":z,"ticks":ticks}),
    )["command_id"]
        .as_u64()
        .unwrap()
}

#[test]
fn hold_renewal_has_no_idle_boundary_and_release_stops_on_next_tick() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    let lease = hold(&endpoint, 0, 1., 0., 3);
    tick(&mut app, 2);
    let renewal = hold(&endpoint, lease, 1., 0., 3);
    tick(&mut app, 2);
    assert_eq!(poll(&endpoint, renewal)["state"], "completed");
    assert_eq!(poll(&endpoint, lease)["applied_ticks"], 4);
    assert_eq!(snapshot(&endpoint)["movement_hold"]["remaining_ticks"], 1);
    assert!(
        (snapshot(&endpoint)["actors"][0]["position"]["x"]
            .as_f64()
            .unwrap()
            - 4. / 15.)
            .abs()
            < 1e-10
    );
    let release = call(
        &endpoint,
        "game_move_release",
        json!({"run_id":1,"lease_id":lease}),
    )["command_id"]
        .as_u64()
        .unwrap();
    tick(&mut app, 2);
    assert_eq!(poll(&endpoint, release)["state"], "completed");
    assert_eq!(poll(&endpoint, lease)["reason"], "released");
    assert!(snapshot(&endpoint)["movement_hold"].is_null());
    assert!(
        (snapshot(&endpoint)["actors"][0]["position"]["x"]
            .as_f64()
            .unwrap()
            - 4. / 15.)
            .abs()
            < 1e-10
    );
    app.shutdown().unwrap();
}

#[test]
fn hold_can_steer_and_expires_without_renewal_while_stale_edits_cannot_revive_it() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    let lease = hold(&endpoint, 0, 1., 0., 2);
    tick(&mut app, 1);
    hold(&endpoint, lease, 0., 1., 2);
    tick(&mut app, 3);
    let p = snapshot(&endpoint)["actors"][0]["position"].clone();
    assert!((p["x"].as_f64().unwrap() - 1. / 15.).abs() < 1e-10);
    assert!((p["z"].as_f64().unwrap() + 6. - 2. / 15.).abs() < 1e-10);
    assert_eq!(poll(&endpoint, lease)["reason"], "lease_expired");
    let newer = hold(&endpoint, 0, -1., 0., 10);
    let stale = hold(&endpoint, lease, 1., 0., 120);
    tick(&mut app, 1);
    assert_eq!(poll(&endpoint, stale)["reason"], "inactive_lease");
    let release = call(
        &endpoint,
        "game_move_release",
        json!({"run_id":1,"lease_id":lease}),
    )["command_id"]
        .as_u64()
        .unwrap();
    tick(&mut app, 1);
    assert_eq!(poll(&endpoint, release)["reason"], "inactive_lease");
    assert_eq!(snapshot(&endpoint)["active_movement_command_id"], newer);
    app.shutdown().unwrap();
}

#[test]
fn hold_and_pending_renewal_obey_cancellation_boundaries() {
    for reason in [
        "focus_lost",
        "human_input",
        "restarted",
        "shutdown",
        "wave_cleared",
        "run_finished",
    ] {
        let (mut app, endpoint) = harness();
        app.start().unwrap();
        let lease = hold(&endpoint, 0, 1., 0., 120);
        tick(&mut app, 1);
        let renewal = hold(&endpoint, lease, 0., 1., 120);
        match reason {
            "focus_lost" => app.send_event(InputFocusLost),
            "human_input" => {
                let mut input = TickInput::idle(1);
                input.movement = Vec2::new(-1., 0.);
                app.send_event(input);
            }
            "restarted" => {
                call(&endpoint, "game_restart", json!({"run_id":1}));
            }
            "shutdown" => {}
            "wave_cleared" => {
                for actor in &mut app
                    .world_mut()
                    .resource_mut::<Arena>()
                    .unwrap()
                    .snapshot
                    .actors[1..]
                {
                    actor.health = 0;
                }
            }
            "run_finished" => {
                app.world_mut()
                    .resource_mut::<Arena>()
                    .unwrap()
                    .snapshot
                    .actors[0]
                    .health = 0;
            }
            _ => unreachable!(),
        }
        if reason == "shutdown" {
            app.shutdown().unwrap();
        } else {
            tick(&mut app, 1);
        }
        assert_eq!(poll(&endpoint, lease)["reason"], reason);
        assert!(snapshot(&endpoint)["movement_hold"].is_null());
        assert_eq!(
            poll(&endpoint, renewal)["state"],
            if matches!(reason, "wave_cleared" | "run_finished") {
                "completed"
            } else {
                "cancelled"
            }
        );
        if reason != "shutdown" {
            app.shutdown().unwrap();
        }
    }
}

#[test]
fn hold_slots_are_bounded_and_do_not_replace_finite_moves() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    let finite = movement(&endpoint, 1, 3);
    assert_eq!(
        call(
            &endpoint,
            "game_move_hold",
            json!({"run_id":1,"lease_id":0,"x":1,"z":0,"ticks":120})
        )["error"]["code"],
        "busy"
    );
    let edit = hold(&endpoint, finite, 1., 0., 120);
    assert_eq!(
        call(
            &endpoint,
            "game_move_release",
            json!({"run_id":1,"lease_id":finite})
        )["error"]["code"],
        "busy"
    );
    tick(&mut app, 3);
    assert_eq!(poll(&endpoint, edit)["reason"], "inactive_lease");
    assert_eq!(poll(&endpoint, finite)["state"], "completed");
    for args in [
        json!({"run_id":1,"lease_id":0,"x":1,"z":0,"ticks":0}),
        json!({"run_id":1,"lease_id":0,"x":1,"z":0,"ticks":121}),
        json!({"run_id":1,"lease_id":0,"x":2,"z":0,"ticks":30}),
    ] {
        assert_eq!(
            call(&endpoint, "game_move_hold", args)["error"]["code"],
            "invalid_arguments"
        );
    }
    app.shutdown().unwrap();
}

#[test]
fn shutdown_cancels_buffered_tool_dodge_and_publishes_cleared_input() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    app.world_mut()
        .resource_mut::<Arena>()
        .unwrap()
        .snapshot
        .actors[0]
        .dodge_cooldown = 9;
    let id = call(&endpoint, "game_dodge", json!({"run_id":1,"x":1,"z":0}))["command_id"]
        .as_u64()
        .unwrap();
    tick(&mut app, 1);
    assert_eq!(poll(&endpoint, id)["state"], "running");
    app.shutdown().unwrap();
    assert_eq!(poll(&endpoint, id)["state"], "cancelled");
    assert_eq!(poll(&endpoint, id)["reason"], "shutdown");
    assert!(snapshot(&endpoint)["buffered_dodge"].is_null());
}

#[test]
fn buffered_tool_dodge_completes_on_start_and_focus_loss_cancels_it() {
    for cancel in [false, true] {
        let (mut app, endpoint) = harness();
        app.start().unwrap();
        app.world_mut()
            .resource_mut::<Arena>()
            .unwrap()
            .snapshot
            .actors[0]
            .dodge_cooldown = 9;
        let id = call(&endpoint, "game_dodge", json!({"run_id":1,"x":1,"z":0}))["command_id"]
            .as_u64()
            .unwrap();
        tick(&mut app, 1);
        assert_eq!(poll(&endpoint, id)["state"], "running");
        assert!(poll(&endpoint, id)["start_tick"].is_null());
        assert_eq!(snapshot(&endpoint)["buffered_dodge"]["x"], 1.0);
        if cancel {
            app.send_event(InputFocusLost);
        }
        tick(&mut app, 9);
        assert_eq!(
            poll(&endpoint, id)["state"],
            if cancel { "cancelled" } else { "completed" }
        );
        if cancel {
            assert_eq!(poll(&endpoint, id)["reason"], "focus_lost");
        } else {
            assert_eq!(poll(&endpoint, id)["start_tick"], 10);
        }
        assert!(snapshot(&endpoint)["buffered_dodge"].is_null());
        app.shutdown().unwrap();
    }
}

#[test]
fn wave_clear_cancels_movement_and_break_rejects_combat_but_accepts_restart() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    let id = movement(&endpoint, 1, 120);
    for actor in &mut app
        .world_mut()
        .resource_mut::<Arena>()
        .unwrap()
        .snapshot
        .actors[1..]
    {
        actor.health = 0;
    }
    tick(&mut app, 1);
    assert_eq!(poll(&endpoint, id)["state"], "cancelled");
    assert_eq!(poll(&endpoint, id)["reason"], "wave_cleared");
    assert_eq!(poll(&endpoint, id)["applied_ticks"], 1);
    assert_eq!(snapshot(&endpoint)["wave"], 1);
    assert_eq!(snapshot(&endpoint)["intermission_ticks"], 180);
    assert_eq!(
        call(&endpoint, "game_attack", json!({"run_id":1,"yaw":0}))["error"]["code"],
        "intermission"
    );
    tick(&mut app, 180);
    let next = snapshot(&endpoint);
    assert_eq!(next["wave"], 2);
    assert_eq!(next["actors"][3]["kind"], "brute");
    assert_eq!(next["actors"][3]["windup_ticks"], 48);
    assert_eq!(next["actors"][3]["max_health"], 100);
    for actor in &mut app
        .world_mut()
        .resource_mut::<Arena>()
        .unwrap()
        .snapshot
        .actors[1..]
    {
        actor.health = 0;
    }
    tick(&mut app, 1);
    let restart = call(&endpoint, "game_restart", json!({"run_id":1}))["command_id"]
        .as_u64()
        .unwrap();
    tick(&mut app, 1);
    assert_eq!(poll(&endpoint, restart)["state"], "completed");
    assert_eq!(snapshot(&endpoint)["wave"], 1);
    assert_eq!(snapshot(&endpoint)["intermission_ticks"], 0);
    assert_eq!(snapshot(&endpoint)["run_id"], 2);
    app.shutdown().unwrap();
}

#[test]
fn publication_and_movement_are_atomic_at_fixed_boundaries() {
    let (mut app, endpoint) = harness();
    assert_eq!(snapshot(&endpoint)["error"]["code"], "not_ready");
    assert_eq!(
        call(&endpoint, "game_restart", json!({"run_id":1}))["error"]["code"],
        "not_ready"
    );
    app.start().unwrap();
    let initial = snapshot(&endpoint);
    let id = movement(&endpoint, 1, 3);
    assert_eq!(poll(&endpoint, id)["state"], "pending");
    app.tick(std::time::Duration::ZERO).unwrap();
    assert_eq!(snapshot(&endpoint)["tick"], 0);
    tick(&mut app, 1);
    assert_eq!(poll(&endpoint, id)["state"], "running");
    assert_eq!(snapshot(&endpoint)["active_movement_command_id"], id);
    tick(&mut app, 2);
    let outcome = poll(&endpoint, id);
    assert_eq!(outcome["state"], "completed");
    assert_eq!(outcome["applied_ticks"], 3);
    assert_eq!(outcome["start_tick"], 1);
    assert_eq!(outcome["end_tick"], 3);
    let position = snapshot(&endpoint)["actors"][0]["position"].clone();
    tick(&mut app, 1);
    assert_eq!(snapshot(&endpoint)["actors"][0]["position"], position);
    assert_eq!(initial["actors"][0]["position"]["x"], 0.0);
    assert!(snapshot(&endpoint)["active_movement_command_id"].is_null());
    app.shutdown().unwrap();
}
#[test]
fn malformed_arguments_are_rejected_without_allocating_commands() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    for (name, args) in [
        ("game_state", json!({"extra":1})),
        ("game_command", json!({"command_id":0})),
        ("game_move", json!({"run_id":1,"x":2,"z":0,"ticks":1})),
        ("game_move", json!({"run_id":1,"x":0,"z":0,"ticks":121})),
        ("game_move", json!({"run_id":1,"x":0,"z":0,"ticks":0})),
        ("game_move", json!({"run_id":1,"x":0,"z":0,"ticks":1.5})),
        ("game_attack", json!({"run_id":1,"yaw":4})),
        ("game_attack", json!({"run_id":1,"yaw":null})),
        ("game_dodge", json!({"run_id":1,"x":0,"z":0})),
        ("game_restart", json!({"run_id":1,"extra":true})),
        ("game_restart", json!({"run_id":-1})),
    ] {
        assert_eq!(
            call(&endpoint, name, args)["error"]["code"],
            "invalid_arguments",
            "{name}"
        );
    }
    assert_eq!(movement(&endpoint, 1, 1), 1);
    app.shutdown().unwrap();
}
#[test]
fn restart_has_reserved_capacity_cancels_actions_and_preserves_history() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    let move_id = movement(&endpoint, 1, 120);
    let attack_id = call(&endpoint, "game_attack", json!({"run_id":1,"yaw":0}))["command_id"]
        .as_u64()
        .unwrap();
    assert_eq!(
        call(
            &endpoint,
            "game_move",
            json!({"run_id":1,"x":0,"z":0,"ticks":1})
        )["error"]["code"],
        "busy"
    );
    assert_eq!(
        call(&endpoint, "game_dodge", json!({"run_id":1,"x":1,"z":0}))["error"]["code"],
        "busy"
    );
    let restart = call(&endpoint, "game_restart", json!({"run_id":1}))["command_id"]
        .as_u64()
        .unwrap();
    assert_eq!(
        call(&endpoint, "game_restart", json!({"run_id":1}))["error"]["code"],
        "busy"
    );
    tick(&mut app, 1);
    for id in [move_id, attack_id] {
        assert_eq!(poll(&endpoint, id)["reason"], "restarted");
    }
    assert_eq!(poll(&endpoint, restart)["state"], "completed");
    assert_eq!(poll(&endpoint, restart)["result_run_id"], 2);
    assert_eq!(snapshot(&endpoint)["tick"], 0);
    assert_eq!(snapshot(&endpoint)["run_id"], 2);
    assert_eq!(
        call(&endpoint, "game_restart", json!({"run_id":1}))["error"]["code"],
        "stale_run"
    );
    tick(&mut app, 1);
    assert_eq!(snapshot(&endpoint)["actors"][0]["position"]["x"], 0.0);
    app.shutdown().unwrap();
}
#[test]
fn combat_completion_means_started_and_failures_are_deferred() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    let id = call(&endpoint, "game_attack", json!({"run_id":1,"yaw":0}))["command_id"]
        .as_u64()
        .unwrap();
    tick(&mut app, 1);
    assert_eq!(poll(&endpoint, id)["state"], "completed");
    assert_eq!(
        snapshot(&endpoint)["actors"][0]["action"]["phase"],
        "windup"
    );
    let dodge = call(&endpoint, "game_dodge", json!({"run_id":1,"x":1,"z":0}))["command_id"]
        .as_u64()
        .unwrap();
    tick(&mut app, 1);
    assert_eq!(poll(&endpoint, dodge)["state"], "rejected");
    assert_eq!(poll(&endpoint, dodge)["reason"], "action_locked");
    tick(&mut app, 34);
    let dodge = call(&endpoint, "game_dodge", json!({"run_id":1,"x":1,"z":0}))["command_id"]
        .as_u64()
        .unwrap();
    tick(&mut app, 18);
    assert_eq!(poll(&endpoint, dodge)["state"], "completed");
    let again = call(&endpoint, "game_dodge", json!({"run_id":1,"x":1,"z":0}))["command_id"]
        .as_u64()
        .unwrap();
    tick(&mut app, 1);
    assert_eq!(poll(&endpoint, again)["reason"], "cooldown");
    app.shutdown().unwrap();
}
#[test]
fn human_input_and_focus_loss_cancel_only_pending_automation() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    let movement_id = movement(&endpoint, 1, 120);
    let attack_id = call(&endpoint, "game_attack", json!({"run_id":1,"yaw":0}))["command_id"]
        .as_u64()
        .unwrap();
    let mut human = TickInput::idle(1);
    human.movement = Vec2::new(-1.0, 0.0);
    human.dodge = Some(Vec2::new(-1.0, 0.0));
    app.send_event(human);
    tick(&mut app, 1);
    for id in [movement_id, attack_id] {
        assert_eq!(poll(&endpoint, id)["reason"], "human_input");
    }
    let movement_id = movement(&endpoint, 1, 120);
    let attack_id = call(&endpoint, "game_attack", json!({"run_id":1,"yaw":0}))["command_id"]
        .as_u64()
        .unwrap();
    app.send_event(InputFocusLost);
    tick(&mut app, 1);
    for id in [movement_id, attack_id] {
        assert_eq!(poll(&endpoint, id)["reason"], "focus_lost");
    }
    assert_eq!(snapshot(&endpoint)["actors"][0]["action"]["kind"], "dodge");
    app.shutdown().unwrap();
}
#[test]
fn movement_lease_counts_ticks_even_when_attack_blocks_locomotion() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    let initial = snapshot(&endpoint)["actors"][0]["position"].clone();
    let id = movement(&endpoint, 1, 3);
    call(&endpoint, "game_attack", json!({"run_id":1,"yaw":0}));
    tick(&mut app, 3);
    assert_eq!(poll(&endpoint, id)["state"], "completed");
    assert_eq!(poll(&endpoint, id)["applied_ticks"], 3);
    assert_eq!(snapshot(&endpoint)["actors"][0]["position"], initial);
    app.shutdown().unwrap();
}
#[test]
fn shutdown_rejects_mutations_and_retains_terminal_outcomes() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    let id = movement(&endpoint, 1, 120);
    tick(&mut app, 1);
    app.shutdown().unwrap();
    assert_eq!(poll(&endpoint, id)["reason"], "shutdown");
    assert_eq!(poll(&endpoint, id)["applied_ticks"], 1);
    assert_eq!(
        call(&endpoint, "game_restart", json!({"run_id":1}))["error"]["code"],
        "shutting_down"
    );
    assert_eq!(snapshot(&endpoint)["tick"], 1);
}
#[test]
fn history_is_bounded_with_distinct_expired_and_unknown_ids() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    for run in 1..=129 {
        call(&endpoint, "game_restart", json!({"run_id":run}));
        tick(&mut app, 1);
    }
    assert_eq!(poll(&endpoint, 1)["error"]["code"], "expired_command");
    assert_eq!(poll(&endpoint, 2)["state"], "completed");
    assert_eq!(poll(&endpoint, 130)["error"]["code"], "unknown_command");
    assert_eq!(endpoint.0.lock().unwrap().commands.history().len(), 128);
    let reconnected = endpoint.clone();
    assert_eq!(poll(&endpoint, 129), poll(&reconnected, 129));
    app.shutdown().unwrap();
}
#[test]
fn revalidates_run_id_at_application_and_rejects_terminal_mutations() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    let id = movement(&endpoint, 1, 120);
    // Simulate another runtime-owned reset after publication but before application.
    let mut reset = TickInput::idle(1);
    reset.restart = true;
    app.world_mut().resource_mut::<Arena>().unwrap().step(reset);
    tick(&mut app, 1);
    assert_eq!(poll(&endpoint, id)["reason"], "stale_run");
    tick(&mut app, 3000);
    assert_eq!(snapshot(&endpoint)["state"], "lost");
    assert_eq!(
        call(&endpoint, "game_attack", json!({"run_id":2,"yaw":0}))["error"]["code"],
        "run_finished"
    );
    app.shutdown().unwrap();
}

#[test]
fn tool_commands_complete_a_real_encounter_and_restart_after_victory() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    let opening = call(&endpoint, "game_dodge", json!({"run_id":1,"x":1,"z":0}))["command_id"]
        .as_u64()
        .unwrap();
    tick(&mut app, 1);
    assert_eq!(poll(&endpoint, opening)["state"], "completed");
    for _ in 0..6000 {
        let state = snapshot(&endpoint);
        if state["state"] != "playing" {
            break;
        }
        if state["intermission_ticks"] == 0 && state["actors"][0]["action"]["kind"] == "idle" {
            let position = |actor: &Value| {
                Vec2::new(
                    actor["position"]["x"].as_f64().unwrap(),
                    actor["position"]["z"].as_f64().unwrap(),
                )
            };
            let hero = position(&state["actors"][0]);
            let target = state["actors"].as_array().unwrap()[1..]
                .iter()
                .filter(|m| m["health"].as_u64().unwrap() > 0)
                .min_by(|a, b| {
                    let a = position(a).sub(hero);
                    let b = position(b).sub(hero);
                    a.dot(a).total_cmp(&b.dot(b))
                })
                .unwrap();
            let toward = position(target).sub(hero);
            let threats: Vec<_> = state["actors"].as_array().unwrap()[1..]
                .iter()
                .filter(|m| {
                    let d = position(m).sub(hero);
                    m["health"].as_u64().unwrap() > 0
                        && d.dot(d) < (m["attack_range"].as_f64().unwrap() + 0.8).powi(2)
                        && m["action"]["phase"] == "windup"
                })
                .collect();
            let threat = threats
                .iter()
                .find(|m| m["action"]["phase_ticks_remaining"].as_u64().unwrap() <= 12);
            let response = if state["actors"][0]["dodge_cooldown"] == 0
                && let Some(m) = threat
            {
                call(
                    &endpoint,
                    "game_dodge",
                    json!({"run_id":1,"x":m["facing"]["z"],"z":-m["facing"]["x"].as_f64().unwrap()}),
                )
            } else if toward.dot(toward) <= 4.0 && !threats.is_empty() {
                tick(&mut app, 1);
                continue;
            } else if toward.dot(toward) <= 4.0 {
                call(
                    &endpoint,
                    "game_attack",
                    json!({"run_id":1,"yaw":toward.x.atan2(toward.z)}),
                )
            } else {
                let d = toward.unit();
                call(
                    &endpoint,
                    "game_move",
                    json!({"run_id":1,"x":d.x,"z":d.z,"ticks":1}),
                )
            };
            assert_eq!(response["accepted"], true);
        }
        tick(&mut app, 1);
    }
    assert_eq!(snapshot(&endpoint)["state"], "won");
    let reset = call(&endpoint, "game_restart", json!({"run_id":1}))["command_id"]
        .as_u64()
        .unwrap();
    tick(&mut app, 1);
    assert_eq!(poll(&endpoint, reset)["state"], "completed");
    assert_eq!(snapshot(&endpoint)["run_id"], 2);
    assert_eq!(snapshot(&endpoint)["monsters_remaining"], 3);
    app.shutdown().unwrap();
}
#[test]
fn terminal_transition_cancels_remaining_movement_ticks() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    let id = movement(&endpoint, 1, 120);
    let arena = app.world_mut().resource_mut::<Arena>().unwrap();
    arena.snapshot.actors[0].health = 20;
    arena.snapshot.actors[1].position = Vec2::new(0.0, -4.5);
    arena.snapshot.actors[1].action = Action::Attack {
        id: 1,
        elapsed: 30,
        hit_mask: 0,
    };
    tick(&mut app, 1);
    assert_eq!(snapshot(&endpoint)["state"], "lost");
    assert_eq!(poll(&endpoint, id)["state"], "cancelled");
    assert_eq!(poll(&endpoint, id)["reason"], "run_finished");
    assert_eq!(poll(&endpoint, id)["applied_ticks"], 1);
    app.shutdown().unwrap();
}
#[test]
fn concurrent_submissions_cannot_overfill_movement_slot() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    let responses = std::thread::scope(|scope| {
        let handles: Vec<_> = (0..8)
            .map(|_| {
                let endpoint = &endpoint;
                scope.spawn(move || {
                    call(
                        endpoint,
                        "game_move",
                        json!({"run_id":1,"x":1,"z":0,"ticks":1}),
                    )
                })
            })
            .collect();
        handles
            .into_iter()
            .map(|h| h.join().unwrap())
            .collect::<Vec<_>>()
    });
    assert_eq!(
        responses.iter().filter(|r| r["accepted"] == true).count(),
        1
    );
    assert_eq!(
        responses
            .iter()
            .filter(|r| r["error"]["code"] == "busy")
            .count(),
        7
    );
    tick(&mut app, 1);
    assert_eq!(poll(&endpoint, 1)["state"], "completed");
    app.shutdown().unwrap();
}
#[test]
fn focus_notifications_are_drained_and_do_not_cancel_a_later_command() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    app.send_event(InputFocusLost);
    app.send_event(InputFocusLost);
    tick(&mut app, 1);
    let id = movement(&endpoint, 1, 2);
    tick(&mut app, 2);
    assert_eq!(poll(&endpoint, id)["state"], "completed");
    let mut reset = TickInput::idle(1);
    reset.restart = true;
    app.send_event(reset);
    app.send_event(InputFocusLost);
    tick(&mut app, 1);
    assert_eq!(snapshot(&endpoint)["run_id"], 2);
    app.shutdown().unwrap();
}

#[test]
fn invalid_human_input_remains_observable_with_tools_installed() {
    let (mut app, endpoint) = harness();
    app.start().unwrap();
    let mut invalid = TickInput::idle(1);
    invalid.movement.x = f64::NAN;
    app.send_event(invalid);
    tick(&mut app, 1);
    assert_eq!(
        app.world().resource::<StepReport>().unwrap().rejection,
        Some(crate::Rejection::InvalidArguments)
    );
    assert_eq!(snapshot(&endpoint)["actors"][0]["position"]["x"], 0.0);
    app.shutdown().unwrap();
}
