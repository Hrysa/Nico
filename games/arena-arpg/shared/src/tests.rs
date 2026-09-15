use super::*;

#[test]
fn buffered_dodge_cannot_leak_through_restart_wave_clear_or_defeat() {
    for boundary in ["restart", "wave", "defeat"] {
        let mut a = Arena::default();
        a.snapshot.actors[0].dodge_cooldown = 9;
        let mut input = TickInput::idle(1);
        input.dodge = Some(Vec2::new(1.0, 0.0));
        assert!(a.step(input).dodge_buffered);
        let mut input = TickInput::idle(1);
        match boundary {
            "restart" => input.restart = true,
            "wave" => {
                for actor in &mut a.snapshot.actors[1..] {
                    actor.health = 0;
                }
            }
            _ => a.snapshot.actors[0].health = 0,
        }
        let report = a.step(input);
        assert!(!report.dodge_buffered && !report.dodge_started);
        assert!(a.snapshot.buffered_dodge.is_none());
    }
}

#[test]
fn dodge_cancels_recovery_and_buffers_only_within_nine_ticks_of_readiness() {
    for (elapsed, accepted, wait) in [(8, false, 10), (9, true, 9), (17, true, 1), (18, true, 0)] {
        let mut a = Arena::default();
        a.snapshot.actors[0].action = Action::Attack {
            id: 1,
            elapsed,
            hit_mask: 0,
        };
        let mut input = TickInput::idle(1);
        input.dodge = Some(Vec2::new(1.0, 0.0));
        let report = a.step(input);
        assert_eq!(report.rejection.is_none(), accepted);
        if wait == 0 {
            assert!(report.dodge_started);
        } else if accepted {
            assert!(report.dodge_buffered);
            idle(&mut a, wait - 1);
            assert!(matches!(a.snapshot.actors[0].action, Action::Attack { .. }));
            let report = a.step(TickInput::idle(1));
            assert!(report.dodge_started);
        } else {
            idle(&mut a, 40);
        }
        assert_eq!(
            matches!(a.snapshot.actors[0].action, Action::Dodge { .. }),
            accepted
        );
        assert!(a.snapshot.buffered_dodge.is_none());
    }
    let mut a = Arena::default();
    a.snapshot.actors[0].dodge_cooldown = 9;
    let mut input = TickInput::idle(1);
    input.dodge = Some(Vec2::new(-1.0, 0.0));
    assert!(a.step(input).dodge_buffered);
    idle(&mut a, 8);
    assert!(a.step(TickInput::idle(1)).dodge_started);
    assert_eq!(a.snapshot.actors[0].facing, Vec2::new(-1.0, 0.0));
}

#[test]
fn mixed_monsters_schedule_strikes_at_least_thirty_ticks_apart() {
    let mut a = Arena::default();
    a.snapshot.wave_tick = 60;
    a.snapshot.actors[0].position = Vec2::default();
    for (i, position) in [
        Vec2::new(-1.0, 0.0),
        Vec2::new(1.0, 0.0),
        Vec2::new(0.0, 1.0),
    ]
    .into_iter()
    .enumerate()
    {
        a.snapshot.actors[i + 1].position = position;
    }
    a.snapshot.actors[3].kind = ActorKind::Brute;
    let mut previous = None;
    let mut count = 0;
    for _ in 0..600 {
        a.snapshot.actors[0].health = 100;
        a.step(TickInput::idle(1));
        for actor in &a.snapshot.actors[1..] {
            if matches!(actor.action, Action::Attack { elapsed: 1, .. }) {
                let strike = a.snapshot.tick - 1 + u64::from(actor.kind.stats().windup);
                if let Some(last) = previous {
                    assert!(strike >= last + 30);
                }
                previous = Some(strike);
                count += 1;
            }
        }
    }
    assert!(count > 10);
}

#[test]
fn wave_break_is_exact_resets_positions_and_heals_without_resetting_run_time() {
    let mut a = Arena::default();
    a.snapshot.actors[0].health = 30;
    a.snapshot.actors[0].position = Vec2::new(9.0, 9.0);
    for actor in &mut a.snapshot.actors[1..] {
        actor.health = 0;
    }
    idle(&mut a, 1);
    assert_eq!(a.snapshot.state, RunState::Playing);
    assert_eq!(a.snapshot.intermission_ticks, INTERMISSION_TICKS);
    let positions = a.snapshot.actors.clone();
    assert_eq!(attack(&mut a, 0.0).rejection, Some(Rejection::Intermission));
    idle(&mut a, 178);
    assert_eq!(a.snapshot.intermission_ticks, 1);
    assert_eq!(a.snapshot.actors, positions);
    idle(&mut a, 1);
    assert_eq!(
        (a.snapshot.run_id, a.snapshot.tick, a.snapshot.wave_tick),
        (1, 181, 0)
    );
    assert_eq!(a.snapshot.wave, 2);
    assert_eq!(a.snapshot.actors[0].health, 70);
    assert_eq!(a.snapshot.actors[0].position, Level::default().spawns[0]);
    assert_eq!(
        a.snapshot
            .actors
            .iter()
            .filter(|a| a.kind == ActorKind::Brute)
            .count(),
        1
    );
    assert_eq!(a.snapshot.actors[3].health, 100);
    idle(&mut a, 60);
    assert_eq!(a.snapshot.actors[3].position, Level::default().spawns[3]);
    idle(&mut a, 1);
    assert_ne!(a.snapshot.actors[3].position, Level::default().spawns[3]);
    for actor in &mut a.snapshot.actors[1..] {
        actor.health = 0;
    }
    idle(&mut a, 181);
    assert_eq!(a.snapshot.wave, 3);
    assert_eq!(a.snapshot.actors[0].health, 100);
    assert_eq!(
        a.snapshot
            .actors
            .iter()
            .filter(|a| a.kind == ActorKind::Brute)
            .count(),
        2
    );
    for actor in &mut a.snapshot.actors[1..] {
        actor.health = 0;
    }
    idle(&mut a, 1);
    assert_eq!(a.snapshot.state, RunState::Won);
    assert_eq!(a.snapshot.intermission_ticks, 0);
}

#[test]
fn restart_during_wave_break_restores_first_wave_and_full_health() {
    let mut a = Arena::default();
    a.snapshot.wave = 2;
    a.snapshot.intermission_ticks = 120;
    a.snapshot.actors[0].health = 10;
    let mut input = TickInput::idle(1);
    input.restart = true;
    assert!(a.step(input).restarted);
    let mut expected = Arena::default().snapshot;
    expected.run_id = 2;
    assert_eq!(a.snapshot, expected);
}

#[test]
fn brute_has_longer_reach_windup_and_recovery_and_hits_once() {
    let mut a = duel();
    a.snapshot.actors[1].kind = ActorKind::Brute;
    a.snapshot.actors[1].health = 100;
    a.snapshot.actors[1].position.z = 2.3;
    a.snapshot.actors[1].action = Action::Attack {
        id: 1,
        elapsed: 0,
        hit_mask: 0,
    };
    idle(&mut a, 48);
    assert_eq!(a.snapshot.actors[0].health, 100);
    idle(&mut a, 1);
    assert_eq!(a.snapshot.actors[0].health, 70);
    idle(&mut a, 53);
    assert_eq!(a.snapshot.actors[0].health, 70);
    assert_eq!(a.snapshot.actors[1].action, Action::Idle);
}

fn idle(arena: &mut Arena, ticks: usize) {
    for _ in 0..ticks {
        arena.step(TickInput::idle(arena.snapshot.run_id));
    }
}
fn duel() -> Arena {
    let mut arena = Arena::default();
    arena.snapshot.actors[0].position = Vec2::default();
    arena.snapshot.actors[1].position = Vec2::new(0.0, 1.5);
    arena.snapshot.actors[2].health = 0;
    arena.snapshot.actors[3].health = 0;
    arena
}
fn attack(arena: &mut Arena, yaw: f64) -> StepReport {
    let mut input = TickInput::idle(arena.snapshot.run_id);
    input.attack_yaw = Some(yaw);
    arena.step(input)
}
#[test]
fn validates_spawns_and_rejects_nonfinite_coordinates() {
    let mut level = Level::default();
    assert_eq!(level.validate(), Ok(()));
    level.spawns[0] = level.spawns[1];
    assert_eq!(level.validate(), Err(LevelError::OverlappingSpawns));
    level.spawns[0].x = f64::NAN;
    assert_eq!(level.validate(), Err(LevelError::OutOfBounds));
    level.spawns[0] = Vec2::new(11.7, 0.0);
    assert_eq!(level.validate(), Err(LevelError::OutOfBounds));
}
#[test]
fn normalized_movement_and_idle_stop() {
    let mut a = Arena::default();
    let start = a.snapshot.actors[0].position;
    let mut input = TickInput::idle(1);
    input.movement = Vec2::new(1.0, 1.0);
    a.step(input);
    let delta = a.snapshot.actors[0].position.sub(start);
    assert!((delta.dot(delta).sqrt() - 4.0 / 60.0).abs() < 1e-12);
    let position = a.snapshot.actors[0].position;
    idle(&mut a, 1);
    assert_eq!(a.snapshot.actors[0].position, position);
}
#[test]
fn sweeps_block_tunneling_and_allow_wall_sliding() {
    let p = collision::slide(Vec2::new(11.3, 0.0), Vec2::new(100.0, 2.0), &[]);
    assert!(p.x <= geometry::CENTER_LIMIT && p.x > geometry::CENTER_LIMIT - 0.01);
    assert!((p.z - 2.0).abs() < 1e-8);
    let corner = collision::slide(Vec2::new(11.3, 11.3), Vec2::new(5.0, 5.0), &[]);
    assert!(corner.x <= geometry::CENTER_LIMIT && corner.z <= geometry::CENTER_LIMIT);
    let p = collision::slide(
        Vec2::new(-2.0, 0.0),
        Vec2::new(10.0, 0.0),
        &[Vec2::default()],
    );
    assert!(p.x <= -0.8 && p.x > -0.801);
    let tangent = collision::slide(
        Vec2::new(-0.8, 0.0),
        Vec2::new(0.0, 1.0),
        &[Vec2::default()],
    );
    assert_eq!(tangent, Vec2::new(-0.8, 1.0));
}
#[test]
fn attack_windup_single_hit_and_recovery_boundaries() {
    let mut a = duel();
    attack(&mut a, 0.0);
    idle(&mut a, 11);
    assert_eq!(a.snapshot.actors[1].health, 60);
    idle(&mut a, 1);
    assert_eq!(a.snapshot.actors[1].health, 35);
    idle(&mut a, 22);
    assert_eq!(a.snapshot.actors[1].health, 35);
    assert!(matches!(
        a.snapshot.actors[0].action,
        Action::Attack { elapsed: 35, .. }
    ));
    assert_eq!(attack(&mut a, 0.0).rejection, Some(Rejection::ActionLocked));
    assert_eq!(a.snapshot.actors[0].action, Action::Idle);
    assert_eq!(attack(&mut a, 0.0).rejection, None);
}
#[test]
fn sector_rejects_behind_and_out_of_range_but_hits_multiple_targets() {
    let mut a = duel();
    a.snapshot.actors[2].health = 60;
    a.snapshot.actors[2].position = Vec2::new(1.0, 1.0);
    a.snapshot.actors[3].health = 60;
    a.snapshot.actors[3].position = Vec2::new(0.0, -1.0);
    attack(&mut a, 0.0);
    idle(&mut a, 12);
    assert_eq!(
        [
            a.snapshot.actors[1].health,
            a.snapshot.actors[2].health,
            a.snapshot.actors[3].health
        ],
        [35, 35, 60]
    );
    let mut a = duel();
    a.snapshot.actors[1].position.z = 2.01;
    attack(&mut a, 0.0);
    idle(&mut a, 17);
    assert_eq!(a.snapshot.actors[1].health, 60);
}
#[test]
fn dodge_has_priority_cooldown_and_cannot_cross_actors() {
    let mut a = duel();
    let mut input = TickInput::idle(1);
    input.attack_yaw = Some(0.0);
    input.dodge = Some(Vec2::new(0.0, 1.0));
    a.step(input);
    assert!(matches!(
        a.snapshot.actors[0].action,
        Action::Dodge { elapsed: 1, .. }
    ));
    idle(&mut a, 17);
    assert!(a.snapshot.actors[0].position.z <= 0.7);
    assert_eq!(a.snapshot.actors[0].action, Action::Idle);
    assert_eq!(a.step(input).rejection, Some(Rejection::Cooldown));
    idle(&mut a, 29);
    assert_eq!(a.snapshot.actors[0].dodge_cooldown, 0);
    assert_eq!(a.step(input).rejection, None);
}
#[test]
fn dodge_invulnerability_ends_exactly_at_tick_twelve() {
    for elapsed in [11, 12] {
        let mut a = duel();
        a.snapshot.actors[0].action = Action::Dodge {
            elapsed,
            direction: Vec2::default(),
        };
        a.snapshot.actors[1].action = Action::Attack {
            id: 7,
            elapsed: 30,
            hit_mask: 0,
        };
        idle(&mut a, 1);
        assert_eq!(
            a.snapshot.actors[0].health,
            if elapsed == 11 { 100 } else { 80 }
        );
        idle(&mut a, 1);
        assert_eq!(
            a.snapshot.actors[0].health,
            if elapsed == 11 { 100 } else { 80 }
        );
    }
}
#[test]
fn monsters_idle_then_chase_and_lock_attack_facing() {
    let mut a = duel();
    a.snapshot.actors[1].position.z = 2.0;
    idle(&mut a, 60);
    assert_eq!(a.snapshot.actors[1].position.z, 2.0);
    idle(&mut a, 1);
    assert!(a.snapshot.actors[1].position.z < 2.0);
    idle(&mut a, 15);
    assert!(matches!(a.snapshot.actors[1].action, Action::Attack { .. }));
    let facing = a.snapshot.actors[1].facing;
    let mut input = TickInput::idle(1);
    input.movement = Vec2::new(1.0, 0.0);
    a.step(input);
    assert_eq!(a.snapshot.actors[1].facing, facing);
}
#[test]
fn simultaneous_final_deaths_lose_and_dead_actors_do_not_block() {
    let mut a = duel();
    a.snapshot.actors[0].health = 20;
    a.snapshot.actors[1].health = 25;
    a.snapshot.actors[0].action = Action::Attack {
        id: 1,
        elapsed: 12,
        hit_mask: 0,
    };
    a.snapshot.actors[1].action = Action::Attack {
        id: 2,
        elapsed: 30,
        hit_mask: 0,
    };
    idle(&mut a, 1);
    assert_eq!(a.snapshot.state, RunState::Lost);
    assert_eq!(a.snapshot.actors[0].health, 0);
    assert_eq!(a.snapshot.actors[1].health, 0);
    let mut a = duel();
    a.snapshot.actors[1].health = 0;
    a.snapshot.actors[2].health = 60;
    let mut input = TickInput::idle(1);
    input.movement = Vec2::new(0.0, 1.0);
    for _ in 0..30 {
        a.step(input);
    }
    assert!(a.snapshot.actors[0].position.z > 1.5);
}
#[test]
fn reset_restores_every_run_state_and_rejects_stale_or_invalid_input() {
    for state in [RunState::Playing, RunState::Won, RunState::Lost] {
        let mut a = duel();
        a.snapshot.state = state;
        a.snapshot.tick = 100;
        let mut input = TickInput::idle(1);
        input.restart = true;
        input.attack_yaw = Some(0.0);
        input.movement = Vec2::new(1.0, 0.0);
        assert!(a.step(input).restarted);
        let mut expected = Arena::default().snapshot;
        expected.run_id = 2;
        assert_eq!(a.snapshot, expected);
        assert_eq!(a.step(input).rejection, Some(Rejection::StaleRun));
        assert_eq!(a.snapshot.actors[0].position, expected.actors[0].position);
        let mut invalid = TickInput::idle(2);
        invalid.movement.x = f64::NAN;
        assert_eq!(a.step(invalid).rejection, Some(Rejection::InvalidArguments));
    }
}
#[test]
fn idle_encounter_loses_and_terminal_state_freezes() {
    let mut a = Arena::default();
    idle(&mut a, 3000);
    assert_eq!(a.snapshot.state, RunState::Lost);
    let terminal = a.snapshot.clone();
    idle(&mut a, 10);
    assert_eq!(a.snapshot, terminal);
    let mut input = TickInput::idle(1);
    input.attack_yaw = Some(0.0);
    assert_eq!(a.step(input).rejection, Some(Rejection::RunFinished));
}
fn play_encounter(a: &mut Arena) {
    let mut opening = TickInput::idle(a.snapshot.run_id);
    opening.dodge = Some(Vec2::new(1.0, 0.0));
    a.step(opening);
    for _ in 0..6000 {
        if a.snapshot.state != RunState::Playing {
            break;
        }
        if a.snapshot.intermission_ticks > 0 {
            idle(a, 1);
            continue;
        }
        let hero = &a.snapshot.actors[0];
        let target = a.snapshot.actors[1..]
            .iter()
            .filter(|m| m.health > 0)
            .min_by(|a, b| {
                a.position
                    .sub(hero.position)
                    .dot(a.position.sub(hero.position))
                    .total_cmp(
                        &b.position
                            .sub(hero.position)
                            .dot(b.position.sub(hero.position)),
                    )
            })
            .unwrap();
        let toward = target.position.sub(hero.position);
        let mut input = TickInput::idle(a.snapshot.run_id);
        if hero.action == Action::Idle {
            let threats: Vec<_> = a.snapshot.actors[1..].iter().filter(|m| m.health > 0
                && m.position.sub(hero.position).dot(m.position.sub(hero.position)) < (m.kind.stats().range + 0.8).powi(2)
                && matches!(m.action, Action::Attack { elapsed, .. } if elapsed < m.kind.stats().windup)).collect();
            if hero.dodge_cooldown == 0 && let Some(threat) = threats.iter().find(|m| matches!(m.action, Action::Attack { elapsed, .. } if elapsed >= m.kind.stats().windup - 12)) {
                input.dodge = Some(Vec2::new(threat.facing.z, -threat.facing.x));
            } else if toward.dot(toward) <= 4.0 && threats.is_empty() {
                input.attack_yaw = Some(toward.x.atan2(toward.z));
            } else if toward.dot(toward) > 4.0 {
                input.movement = toward.unit();
            }
        }
        a.step(input);
    }
}
#[test]
fn normal_commands_win_repeatably_and_restart() {
    let mut a = Arena::default();
    let mut b = Arena::default();
    play_encounter(&mut a);
    play_encounter(&mut b);
    assert_eq!(a.snapshot, b.snapshot);
    assert_eq!(a.snapshot.state, RunState::Won, "{:?}", a.snapshot);
    let mut input = TickInput::idle(a.snapshot.run_id);
    input.restart = true;
    assert!(a.step(input).restarted);
    assert_eq!(a.snapshot.tick, 0);
    assert_eq!(a.snapshot.monsters_remaining(), 3);
}
#[test]
fn runtime_applies_only_at_fixed_boundaries_and_prioritizes_restart()
-> nico_runtime::RuntimeResult<()> {
    let mut app = nico_runtime::AppBuilder::new()
        .add_plugin(ArenaPlugin)
        .build()?;
    app.start()?;
    let mut input = TickInput::idle(1);
    input.movement = Vec2::new(1.0, 0.0);
    app.send_event(input);
    app.tick(std::time::Duration::ZERO)?;
    assert_eq!(app.world().resource::<Arena>()?.snapshot.tick, 0);
    app.tick(FIXED_STEP)?;
    assert!(
        app.world().resource::<Arena>()?.snapshot.actors[0]
            .position
            .x
            > 0.0
    );
    let mut reset = input;
    reset.restart = true;
    app.send_event(reset);
    app.send_event(input);
    app.tick(FIXED_STEP)?;
    assert_eq!(app.world().resource::<Arena>()?.snapshot.run_id, 2);
    assert_eq!(app.world().resource::<Arena>()?.snapshot.tick, 0);
    app.tick(FIXED_STEP)?;
    assert_eq!(
        app.world().resource::<Arena>()?.snapshot.actors[0]
            .position
            .x,
        0.0
    );
    app.shutdown()?;
    Ok(())
}
#[test]
fn runtime_rejects_incompatible_fixed_rate() -> nico_runtime::RuntimeResult<()> {
    let mut app = nico_runtime::AppBuilder::new()
        .with_fixed_step(std::time::Duration::from_millis(20))
        .add_plugin(ArenaPlugin)
        .build()?;
    app.start()?;
    assert!(app.tick(std::time::Duration::from_millis(20)).is_err());
    Ok(())
}

#[test]
fn monster_active_window_and_recovery_are_exact() {
    let mut a = duel();
    a.snapshot.tick = 60;
    a.snapshot.wave_tick = 60;
    idle(&mut a, 30);
    assert_eq!(a.snapshot.actors[0].health, 100);
    idle(&mut a, 1);
    assert_eq!(a.snapshot.actors[0].health, 80);
    idle(&mut a, 41);
    assert_eq!(a.snapshot.actors[0].health, 80);
    assert_eq!(a.snapshot.actors[1].action, Action::Idle);
    idle(&mut a, 1);
    assert!(matches!(
        a.snapshot.actors[1].action,
        Action::Attack { elapsed: 1, .. }
    ));
}

#[test]
fn runtime_continues_host_frames_after_defeat_and_shuts_down_cleanly()
-> nico_runtime::RuntimeResult<()> {
    let mut app = nico_runtime::AppBuilder::new()
        .add_plugin(ArenaPlugin)
        .build()?;
    app.start()?;
    for _ in 0..3000 {
        app.tick(FIXED_STEP)?;
    }
    let terminal = app.world().resource::<Arena>()?.snapshot().clone();
    assert_eq!(terminal.state, RunState::Lost);
    for _ in 0..10 {
        app.tick(FIXED_STEP)?;
    }
    assert_eq!(app.world().resource::<Arena>()?.snapshot(), &terminal);
    assert_eq!(app.state(), nico_runtime::AppState::Running);
    app.shutdown()?;
    assert_eq!(app.state(), nico_runtime::AppState::Stopped);
    assert_eq!(app.world().resource::<Arena>()?.snapshot(), &terminal);
    Ok(())
}

#[test]
fn simulated_actors_remain_inside_arena_and_never_overlap() {
    let mut a = Arena::default();
    for tick in 0..1800 {
        let angle = tick as f64 * 0.017;
        let mut input = TickInput::idle(a.snapshot.run_id);
        input.movement = Vec2::new(angle.sin(), angle.cos());
        if tick % 60 == 0 {
            input.dodge = Some(input.movement);
        }
        a.step(input);
        for (i, actor) in a.snapshot.actors.iter().enumerate() {
            assert!(collision::valid_position(actor.position));
            if actor.health == 0 {
                continue;
            }
            for other in &a.snapshot.actors[..i] {
                if other.health > 0 {
                    let d = actor.position.sub(other.position);
                    assert!(d.dot(d) >= 0.8_f64.powi(2) - 1e-8);
                }
            }
        }
    }
}
