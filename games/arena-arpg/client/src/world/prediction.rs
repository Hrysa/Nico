//! Local motion prediction only. Health, inventory, hits and rewards are authoritative.
use arena_arpg_shared::{
    Vec2,
    characters::CharacterCatalog,
    open_world::{
        ObjectKind, ObjectSnapshot, PlayerInput, WorldAction, WorldSnapshot,
        content::ZoneDefinition,
    },
};
use nico_physics::{BodyDesc, BodyId, BodyKind, CharacterSettings, PhysicsWorld, Pose, Shape};
use std::{collections::VecDeque, sync::Arc};
pub struct Prediction {
    pub actor: ObjectSnapshot,
    pub pending: VecDeque<PlayerInput>,
    pub correction_m: f64,
    pub acknowledged: u64,
    pub tick: u64,
    physics: PhysicsWorld,
    body: BodyId,
    characters: Arc<CharacterCatalog>,
    zone: ZoneDefinition,
}
impl Prediction {
    pub fn new(
        snapshot: &WorldSnapshot,
        characters: Arc<CharacterCatalog>,
        zone: ZoneDefinition,
    ) -> Result<Self, String> {
        let actor = snapshot
            .objects
            .iter()
            .find(|o| o.id == snapshot.player)
            .ok_or("missing local player")?
            .clone();
        let (physics, body) = scene(snapshot, &characters, &zone)?;
        Ok(Self {
            actor,
            pending: VecDeque::new(),
            correction_m: 0.,
            acknowledged: snapshot.acknowledged_input,
            tick: snapshot.tick,
            physics,
            body,
            characters,
            zone,
        })
    }
    pub fn reconcile(&mut self, snapshot: &WorldSnapshot) -> Result<(), String> {
        if snapshot.acknowledged_input < self.acknowledged {
            return Err("input acknowledgement regressed".into());
        }
        let previous = self.actor.position;
        self.pending
            .retain(|i| i.sequence > snapshot.acknowledged_input);
        self.actor = snapshot
            .objects
            .iter()
            .find(|o| o.id == snapshot.player)
            .ok_or("missing local player")?
            .clone();
        (self.physics, self.body) = scene(snapshot, &self.characters, &self.zone)?;
        self.tick = snapshot.tick;
        self.acknowledged = snapshot.acknowledged_input;
        for input in self.pending.clone() {
            self.advance(&input);
        }
        self.correction_m =
            (previous.x - self.actor.position.x).hypot(previous.z - self.actor.position.z);
        Ok(())
    }
    pub fn push(&mut self, input: PlayerInput) -> Result<(), String> {
        if self.pending.len() >= 64 {
            return Err("server acknowledgement timeout".into());
        }
        self.advance(&input);
        self.pending.push_back(input);
        Ok(())
    }
    fn advance(&mut self, input: &PlayerInput) {
        self.tick += 1;
        if self.actor.health == 0 {
            return;
        }
        let def = self.characters.get(self.actor.kind.character());
        let a = &def.arena.attacks.primary;
        let d = &def.arena.dodge;
        self.actor.dodge_cooldown = self.actor.dodge_cooldown.saturating_sub(1);
        let recovery = matches!(self.actor.action,WorldAction::Attack{elapsed,..} if elapsed>=a.windup_ticks+a.active_ticks);
        if (matches!(self.actor.action, WorldAction::Idle) || recovery)
            && self.actor.dodge_cooldown == 0
            && let Some(direction) = input.dodge
        {
            self.actor.action = WorldAction::Dodge {
                elapsed: 0,
                direction: unit(direction),
            };
            self.actor.dodge_cooldown = d.cooldown_ticks;
            self.actor.facing = unit(direction);
        } else if matches!(self.actor.action, WorldAction::Idle)
            && let Some(yaw) = input.attack_yaw
        {
            self.actor.action = WorldAction::Attack {
                id: self.tick,
                elapsed: 0,
            };
            self.actor.facing = Vec2::new(yaw.sin(), yaw.cos());
        }
        let travel = match self.actor.action {
            WorldAction::Idle => {
                if input.movement.x != 0. || input.movement.z != 0. {
                    self.actor.facing = unit(input.movement);
                }
                Vec2::new(
                    input.movement.x * def.arena.movement.speed_mps / 60.,
                    input.movement.z * def.arena.movement.speed_mps / 60.,
                )
            }
            WorldAction::Dodge { direction, .. } => Vec2::new(
                direction.x * d.speed_mps / 60.,
                direction.z * d.speed_mps / 60.,
            ),
            _ => Vec2::default(),
        };
        let motion = self
            .physics
            .move_character(
                self.body,
                [travel.x, 0., travel.z],
                arena_arpg_shared::FIXED_STEP,
                CharacterSettings {
                    offset: 0.0001,
                    max_slope_angle: 0.,
                    snap_distance: None,
                },
            )
            .expect("validated prediction geometry");
        let radius = def.core.collision.radius_m;
        let limit = self.zone.half_extent_m - radius;
        self.actor.position = Vec2::new(
            (self.actor.position.x + motion.translation[0]).clamp(-limit, limit),
            (self.actor.position.z + motion.translation[2]).clamp(-limit, limit),
        );
        self.physics
            .set_pose(
                self.body,
                Pose::at([self.actor.position.x, radius, self.actor.position.z]),
            )
            .unwrap();
        self.actor.action = match self.actor.action {
            WorldAction::Attack { id, elapsed }
                if elapsed + 1 < a.windup_ticks + a.active_ticks + a.recovery_ticks =>
            {
                WorldAction::Attack {
                    id,
                    elapsed: elapsed + 1,
                }
            }
            WorldAction::Dodge { elapsed, direction } if elapsed + 1 < d.duration_ticks => {
                WorldAction::Dodge {
                    elapsed: elapsed + 1,
                    direction,
                }
            }
            _ => WorldAction::Idle,
        };
    }
}
fn scene(
    snapshot: &WorldSnapshot,
    characters: &CharacterCatalog,
    zone: &ZoneDefinition,
) -> Result<(PhysicsWorld, BodyId), String> {
    let mut physics = PhysicsWorld::new([0.; 3], 300).map_err(|e| format!("{e:?}"))?;
    for obstacle in &zone.obstacles {
        physics
            .insert(BodyDesc::new(
                BodyKind::Fixed,
                Shape::Cuboid {
                    half_extents: obstacle.size.map(|x| x / 2.),
                },
                Pose::at(obstacle.center),
            ))
            .map_err(|e| format!("{e:?}"))?;
    }
    let mut local = None;
    for object in &snapshot.objects {
        if object.kind == ObjectKind::Loot || (object.health == 0 && object.id != snapshot.player) {
            continue;
        }
        let radius = characters
            .get(object.kind.character())
            .core
            .collision
            .radius_m;
        let body = physics
            .insert(BodyDesc::new(
                BodyKind::Kinematic,
                Shape::Ball { radius },
                Pose::at([object.position.x, radius, object.position.z]),
            ))
            .map_err(|e| format!("{e:?}"))?;
        if object.id == snapshot.player {
            local = Some(body);
        }
    }
    Ok((physics, local.ok_or("local collider missing")?))
}
fn unit(v: Vec2) -> Vec2 {
    let length = v.x.hypot(v.z);
    if length > 0. {
        Vec2::new(v.x / length, v.z / length)
    } else {
        v
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use arena_arpg_shared::open_world::{CharacterRecord, OpenWorld};
    #[test]
    fn acknowledgements_replay_unprocessed_inputs_without_double_movement() {
        let characters = CharacterCatalog::builtin();
        let mut server = OpenWorld::new(characters.clone());
        let id = server
            .connect(CharacterRecord::new("alice".into(), 100))
            .unwrap();
        let initial = server.snapshot(id).unwrap();
        let mut predicted = Prediction::new(&initial, characters, server.zone.clone()).unwrap();
        for sequence in 1..=6 {
            let input = PlayerInput {
                sequence,
                movement: Vec2::new(1., 0.),
                ..Default::default()
            };
            predicted.push(input.clone()).unwrap();
            server.submit(id, input).unwrap();
            if sequence <= 3 {
                server.step();
            }
        }
        let before = predicted.actor.position;
        predicted.reconcile(&server.snapshot(id).unwrap()).unwrap();
        assert_eq!(predicted.pending.len(), 3);
        assert!((predicted.actor.position.x - before.x).abs() < 1e-6);
        for _ in 0..3 {
            server.step();
        }
        predicted.reconcile(&server.snapshot(id).unwrap()).unwrap();
        assert!(predicted.pending.is_empty());
        assert!((predicted.actor.position.x - server.record(id).unwrap().position.x).abs() < 1e-6);
    }
}
