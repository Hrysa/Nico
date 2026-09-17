//! Repeatable policy comparison, not a human playtest or CPU benchmark.
use arena_arpg_shared::{Action, Arena, RunState, TickInput, Vec2};

fn main() {
    println!("policy,outcome,ticks,simulation_seconds,health,wave,monsters,attacks,dodges");
    for policy in ["idle", "rush", "reactive"] {
        let mut arena = Arena::default();
        let (mut attacks, mut dodges) = (0, 0);
        for _ in 0..60 * 300 {
            let state = arena.snapshot();
            if state.state != RunState::Playing {
                break;
            }
            let mut input = TickInput::idle(state.run_id);
            let hero = &state.actors[0];
            if policy != "idle" && hero.action == Action::Idle && state.intermission_ticks == 0 {
                let distance = |p: Vec2| (p.x - hero.position.x).hypot(p.z - hero.position.z);
                let target = state.actors[1..]
                    .iter()
                    .filter(|a| a.health > 0)
                    .min_by(|a, b| distance(a.position).total_cmp(&distance(b.position)))
                    .unwrap();
                let threat = state.actors[1..].iter().find(|a| {
                    let stats = a.stats();
                    a.health > 0 && distance(a.position) < stats.range + 0.8
                        && matches!(a.action, Action::Attack { elapsed, .. } if elapsed >= stats.windup - 12 && elapsed < stats.windup)
                });
                // React in the last 200 ms of a visible windup, using owned state.
                if policy == "reactive"
                    && hero.dodge_cooldown == 0
                    && let Some(threat) = threat
                {
                    input.dodge = Some(Vec2::new(threat.facing.z, -threat.facing.x));
                    dodges += 1;
                } else {
                    let dx = target.position.x - hero.position.x;
                    let dz = target.position.z - hero.position.z;
                    let d = dx.hypot(dz);
                    if d <= 2.0 {
                        // Wait through a nearby windup so recovery does not prevent dodging.
                        let waiting = policy == "reactive"
                            && state.actors[1..].iter().any(|a| {
                                a.health > 0
                                    && distance(a.position) < a.stats().range + 0.8
                                    && matches!(a.action, Action::Attack { elapsed, .. } if elapsed < a.stats().windup)
                            });
                        if !waiting {
                            input.attack_yaw = Some(dx.atan2(dz));
                            attacks += 1;
                        }
                    } else {
                        input.movement = Vec2::new(dx / d, dz / d);
                    }
                }
            }
            let report = arena.step(input);
            assert!(
                report.rejection.is_none(),
                "policy sent invalid intent: {report:?}"
            );
        }
        let state = arena.snapshot();
        println!(
            "{policy},{:?},{},{:.3},{},{},{},{attacks},{dodges}",
            state.state,
            state.tick,
            state.tick as f64 / 60.0,
            state.actors[0].health,
            state.wave,
            state.monsters_remaining()
        );
    }
}
