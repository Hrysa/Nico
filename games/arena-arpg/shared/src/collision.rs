//! Arena collision policy over the engine's Rapier integration.
use crate::{Actor, Vec2, geometry};
use nico_physics::{BodyDesc, BodyId, BodyKind, CharacterSettings, PhysicsWorld, Pose, Shape};
pub(crate) const RADIUS: f64 = geometry::ACTOR_RADIUS;
pub(crate) fn valid_position(p: Vec2) -> bool {
    p.finite() && p.x.abs() <= geometry::CENTER_LIMIT && p.z.abs() <= geometry::CENTER_LIMIT
}
/// Reuses a collision scene across ticks. Gameplay supplies actor positions and
/// liveness; later actor slots observe earlier slots' movement at the same tick.
#[derive(Debug)]
pub(crate) struct CollisionWorld {
    physics: PhysicsWorld,
    actors: [Option<BodyId>; 4],
}
impl Default for CollisionWorld {
    fn default() -> Self {
        let mut physics = PhysicsWorld::new([0.0; 3], 8).expect("arena physics config");
        for wall in geometry::WALLS {
            physics
                .insert(BodyDesc::new(
                    BodyKind::Fixed,
                    Shape::Cuboid {
                        half_extents: wall.size.map(|x| x / 2.0),
                    },
                    Pose::at(wall.center),
                ))
                .expect("authored wall");
        }
        Self {
            physics,
            actors: [None; 4],
        }
    }
}
fn actor_pose(p: Vec2) -> Pose {
    Pose::at([p.x, RADIUS, p.z])
}
impl CollisionWorld {
    pub fn close(&mut self) {
        self.physics.close();
        self.actors = [None; 4];
    }
    fn set_actor(&mut self, slot: usize, position: Option<Vec2>) {
        match (self.actors[slot], position) {
            (Some(id), Some(p)) => self
                .physics
                .set_pose(id, actor_pose(p))
                .expect("live actor"),
            (Some(id), None) => {
                self.physics.remove(id).expect("live actor");
                self.actors[slot] = None;
            }
            (None, Some(p)) => {
                self.actors[slot] = Some(
                    self.physics
                        .insert(BodyDesc::new(
                            BodyKind::Kinematic,
                            Shape::Ball { radius: RADIUS },
                            actor_pose(p),
                        ))
                        .expect("bounded arena actors"),
                );
            }
            (None, None) => {}
        }
    }
    pub fn sync(&mut self, actors: &[Actor; 4]) {
        for (i, actor) in actors.iter().enumerate() {
            if actor.health == 0 {
                self.set_actor(i, None);
            }
        }
        for (i, actor) in actors.iter().enumerate() {
            if actor.health > 0 {
                self.set_actor(i, Some(actor.position));
            }
        }
    }
    pub fn slide(&mut self, slot: usize, position: Vec2, travel: Vec2) -> Vec2 {
        if travel.dot(travel) == 0.0 {
            return position;
        }
        let id = self.actors[slot].expect("live actor synchronized");
        let movement = self
            .physics
            .move_character(
                id,
                [travel.x, 0.0, travel.z],
                crate::FIXED_STEP,
                CharacterSettings {
                    offset: 0.0001,
                    max_slope_angle: 0.0,
                    snap_distance: None,
                },
            )
            .expect("validated arena motion");
        // The arena explicitly constrains locomotion to its floor plane.
        let next = Vec2::new(
            (position.x + movement.translation[0])
                .clamp(-geometry::CENTER_LIMIT, geometry::CENTER_LIMIT),
            (position.z + movement.translation[2])
                .clamp(-geometry::CENTER_LIMIT, geometry::CENTER_LIMIT),
        );
        self.physics
            .set_pose(id, actor_pose(next))
            .expect("live actor");
        next
    }
}

#[cfg(test)]
pub(crate) fn slide(p: Vec2, travel: Vec2, blockers: &[Vec2]) -> Vec2 {
    let mut world = CollisionWorld::default();
    world.set_actor(0, Some(p));
    for (i, p) in blockers.iter().enumerate() {
        world.set_actor(i + 1, Some(*p));
    }
    world.slide(0, p, travel)
}
