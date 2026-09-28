//! Arena collision policy over the engine's Rapier integration.
use crate::{Actor, Vec2, geometry};
const RADIUS_DEFAULT: f64 = geometry::ACTOR_RADIUS;
use nico_physics::{BodyDesc, BodyId, BodyKind, CharacterSettings, PhysicsWorld, Pose, Shape};
#[cfg(test)]
pub(crate) const RADIUS: f64 = geometry::ACTOR_RADIUS;
#[cfg(test)]
pub(crate) fn valid_position(p: Vec2) -> bool {
    valid_position_with_radius(p, RADIUS)
}
pub(crate) fn valid_position_with_radius(p: Vec2, radius: f64) -> bool {
    let limit = geometry::INNER_FACE - radius;
    p.finite() && p.x.abs() <= limit && p.z.abs() <= limit
}
/// Reuses a collision scene across ticks. Gameplay supplies actor positions and
/// liveness; later actor slots observe earlier slots' movement at the same tick.
#[derive(Debug)]
pub(crate) struct CollisionWorld {
    physics: PhysicsWorld,
    actors: [Option<BodyId>; 4],
    radii: [f64; 4],
    shapes: [Shape; 4],
    centers: [f64; 4],
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
            radii: [0.0; 4],
            shapes: [Shape::Ball {
                radius: RADIUS_DEFAULT,
            }; 4],
            centers: [RADIUS_DEFAULT; 4],
        }
    }
}
fn actor_pose(p: Vec2, radius: f64) -> Pose {
    Pose::at([p.x, radius, p.z])
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
                .set_pose(id, actor_pose(p, self.centers[slot]))
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
                            self.shapes[slot],
                            actor_pose(p, self.centers[slot]),
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
            let radius = actor.definition().core.collision.radius_m;
            let shape = actor.definition().physics_shape();
            if self.shapes[i] != shape {
                self.set_actor(i, None);
                self.shapes[i] = shape;
            }
            self.radii[i] = radius;
            self.centers[i] = actor.definition().core.collision.center_height_m();
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
        let movement = move_on_floor(
            &mut self.physics,
            id,
            self.shapes[slot],
            actor_pose(position, self.centers[slot]),
            travel,
        )
        .expect("validated arena motion");
        // The arena explicitly constrains locomotion to its floor plane.
        let next = Vec2::new(
            (position.x + movement.translation[0]).clamp(
                -(geometry::INNER_FACE - self.radii[slot]),
                geometry::INNER_FACE - self.radii[slot],
            ),
            (position.z + movement.translation[2]).clamp(
                -(geometry::INNER_FACE - self.radii[slot]),
                geometry::INNER_FACE - self.radii[slot],
            ),
        );
        self.physics
            .set_pose(id, actor_pose(next, self.centers[slot]))
            .expect("live actor");
        next
    }
}

#[cfg(test)]
pub(crate) fn slide(p: Vec2, travel: Vec2, blockers: &[Vec2]) -> Vec2 {
    let mut world = CollisionWorld {
        radii: [RADIUS; 4],
        ..Default::default()
    };
    world.set_actor(0, Some(p));
    for (i, p) in blockers.iter().enumerate() {
        world.set_actor(i + 1, Some(*p));
    }
    world.slide(0, p, travel)
}

/// Preserve the ground plane without projecting a capsule through an obstacle.
pub fn move_on_floor(
    physics: &mut PhysicsWorld,
    id: BodyId,
    shape: Shape,
    from: Pose,
    travel: Vec2,
) -> nico_physics::PhysicsResult<nico_physics::CharacterMovement> {
    let mut movement = physics.move_character(
        id,
        [travel.x, 0., travel.z],
        crate::FIXED_STEP,
        CharacterSettings {
            offset: 0.0001,
            max_slope_angle: 0.,
            snap_distance: None,
        },
    )?;
    movement.translation[1] = 0.;
    // Rapier may slide vertically around rounded caps. Recheck the projected path.
    let length = movement.translation[0].hypot(movement.translation[2]);
    if length > 0.
        && let Some(hit) = physics.cast_shape(
            shape,
            from,
            movement.translation,
            nico_physics::QueryFilter {
                exclude: Some(id),
                ..Default::default()
            },
        )?
    {
        let approach =
            movement.translation[0] * hit.normal[0] + movement.translation[2] * hit.normal[2];
        if hit.fraction == 0. && approach >= -1e-10 {
            return Ok(movement);
        }
        let fraction = (hit.fraction - 0.0001 / length).max(0.);
        movement.translation[0] *= fraction;
        movement.translation[2] *= fraction;
    }
    Ok(movement)
}

#[cfg(test)]
mod capsule_tests {
    use super::*;
    #[test]
    fn capsule_blocks_upper_body_obstacles_that_a_foot_sphere_misses() {
        let travel = |shape, height| {
            let mut physics = PhysicsWorld::new([0.; 3], 2).unwrap();
            physics
                .insert(BodyDesc::new(
                    BodyKind::Fixed,
                    Shape::Cuboid {
                        half_extents: [1., 0.2, 0.2],
                    },
                    Pose::at([0., 1.5, 2.]),
                ))
                .unwrap();
            let pose = Pose::at([0., height, 0.]);
            let id = physics
                .insert(BodyDesc::new(BodyKind::Kinematic, shape, pose))
                .unwrap();
            move_on_floor(&mut physics, id, shape, pose, Vec2::new(0., 3.))
                .unwrap()
                .translation
        };
        let hero = crate::characters::CharacterCatalog::builtin();
        let definition = hero.get(crate::ActorKind::Hero);
        let capsule = travel(
            definition.physics_shape(),
            definition.core.collision.center_height_m(),
        );
        let sphere = travel(Shape::Ball { radius: 0.4 }, 0.4);
        assert!(capsule[2] < 1.8);
        assert_eq!(capsule[1], 0.);
        assert!((sphere[2] - 3.).abs() < 1e-6);
    }
}
