use crate::*;
use rapier::{
    control::{CharacterLength, KinematicCharacterController},
    prelude as r,
};
use std::collections::BTreeMap;
use std::time::Duration;

struct Entry {
    body: r::RigidBodyHandle,
    collider: r::ColliderHandle,
}

/// Single-owner physics state. Mutations are immediately visible to the next query;
/// only step advances dynamics. Contacts are owned, bounded, last-step observations.
pub struct PhysicsWorld {
    raw: r::PhysicsWorld,
    collision_geometry: r::ColliderSet,
    collision_tree: rapier::parry::partitioning::Bvh,
    entries: BTreeMap<BodyId, Entry>,
    next_id: u64,
    capacity: usize,
    dirty: bool,
    closed: bool,
    failed: bool,
    contacts: Vec<Contact>,
    contacts_truncated: bool,
}
impl std::fmt::Debug for PhysicsWorld {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PhysicsWorld")
            .field("bodies", &self.len())
            .field("closed", &self.closed)
            .finish()
    }
}
fn pose(p: Pose) -> r::Pose {
    r::Pose {
        translation: p.position.into(),
        rotation: r::Rotation::from_xyzw(
            p.orientation[0],
            p.orientation[1],
            p.orientation[2],
            p.orientation[3],
        )
        .normalize(),
    }
}
fn groups(g: CollisionGroups) -> r::InteractionGroups {
    r::InteractionGroups::new(
        r::Group::from_bits_retain(g.membership),
        r::Group::from_bits_retain(g.filter),
        r::InteractionTestMode::And,
    )
}
fn shape(s: Shape) -> r::SharedShape {
    match s {
        Shape::Ball { radius } => r::SharedShape::ball(radius),
        Shape::Cuboid {
            half_extents: [x, y, z],
        } => r::SharedShape::cuboid(x, y, z),
        Shape::Capsule {
            half_height,
            radius,
        } => r::SharedShape::capsule_y(half_height, radius),
    }
}
impl PhysicsWorld {
    pub fn new(gravity: [f64; 3], capacity: usize) -> PhysicsResult<Self> {
        if gravity.iter().any(|x| !x.is_finite()) || capacity == 0 {
            return Err(PhysicsError::InvalidInput);
        }
        let raw = r::PhysicsWorld {
            gravity: gravity.into(),
            ..Default::default()
        };
        Ok(Self {
            raw,
            collision_geometry: r::ColliderSet::new(),
            collision_tree: rapier::parry::partitioning::Bvh::new(),
            entries: BTreeMap::new(),
            next_id: 0,
            capacity,
            dirty: false,
            closed: false,
            failed: false,
            contacts: Vec::new(),
            contacts_truncated: false,
        })
    }
    fn open(&self) -> PhysicsResult<()> {
        if self.closed {
            Err(PhysicsError::Closed)
        } else if self.failed {
            Err(PhysicsError::SimulationFailed)
        } else {
            Ok(())
        }
    }
    fn entry(&self, id: BodyId) -> PhysicsResult<&Entry> {
        self.entries.get(&id).ok_or(PhysicsError::UnknownBody)
    }
    pub fn len(&self) -> usize {
        self.entries.len()
    }
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }
    pub fn is_closed(&self) -> bool {
        self.closed
    }
    pub fn insert(&mut self, desc: BodyDesc) -> PhysicsResult<BodyId> {
        self.open()?;
        if !desc.valid() {
            return Err(PhysicsError::InvalidInput);
        }
        if self.len() >= self.capacity {
            return Err(PhysicsError::Capacity);
        }
        let id = BodyId(self.next_id.checked_add(1).ok_or(PhysicsError::Capacity)?);
        let builder = match desc.kind {
            BodyKind::Fixed => r::RigidBodyBuilder::fixed(),
            BodyKind::Dynamic => r::RigidBodyBuilder::dynamic().ccd_enabled(true),
            BodyKind::Kinematic => r::RigidBodyBuilder::kinematic_position_based(),
        }
        .pose(pose(desc.pose))
        .linvel(desc.velocity.into());
        let collider = r::ColliderBuilder::new(shape(desc.shape))
            .friction(desc.friction)
            .restitution(desc.restitution)
            .density(desc.density)
            .sensor(desc.sensor)
            .active_collision_types(if desc.sensor {
                r::ActiveCollisionTypes::all()
            } else {
                r::ActiveCollisionTypes::default()
            })
            .collision_groups(groups(desc.groups));
        let (body, collider) = self.raw.insert(builder, collider);
        self.entries.insert(id, Entry { body, collider });
        self.next_id = id.0;
        self.dirty = true;
        Ok(id)
    }
    pub fn remove(&mut self, id: BodyId) -> PhysicsResult<()> {
        self.open()?;
        let entry = self.entries.remove(&id).ok_or(PhysicsError::UnknownBody)?;
        self.raw.remove_body(entry.body);
        self.dirty = true;
        Ok(())
    }
    pub fn state(&self, id: BodyId) -> PhysicsResult<BodyState> {
        let body = &self.raw.bodies[self.entry(id)?.body];
        Ok(BodyState {
            pose: Pose {
                position: body.translation().to_array(),
                orientation: body.rotation().to_array(),
            },
            velocity: body.linvel().to_array(),
            sleeping: body.is_sleeping(),
        })
    }
    /// Teleport a body; does not perform a collision sweep.
    pub fn set_pose(&mut self, id: BodyId, value: Pose) -> PhysicsResult<()> {
        self.open()?;
        if !value.valid() {
            return Err(PhysicsError::InvalidInput);
        }
        let handle = self.entry(id)?.body;
        self.raw.bodies[handle].set_position(pose(value), true);
        self.dirty = true;
        Ok(())
    }
    pub fn set_velocity(&mut self, id: BodyId, velocity: [f64; 3]) -> PhysicsResult<()> {
        self.open()?;
        if velocity.iter().any(|x| !x.is_finite()) {
            return Err(PhysicsError::InvalidInput);
        }
        let handle = self.entry(id)?.body;
        self.raw.bodies[handle].set_linvel(velocity.into(), true);
        Ok(())
    }
    /// Set a kinematic body's target for the next dynamics step, preserving its
    /// inferred velocity for contacts with dynamic bodies.
    pub fn set_kinematic_target(&mut self, id: BodyId, target: Pose) -> PhysicsResult<()> {
        self.open()?;
        let handle = self.entry(id)?.body;
        if !target.valid() || !self.raw.bodies[handle].is_kinematic() {
            return Err(PhysicsError::InvalidInput);
        }
        self.raw.bodies[handle].set_next_kinematic_position(pose(target));
        Ok(())
    }
    /// Build lookup geometry without consuming pending dynamics changes.
    fn update_collision_geometry(&mut self) {
        if !self.dirty {
            return;
        }
        self.collision_geometry = self.raw.colliders.clone();
        for (_, collider) in self.collision_geometry.iter_mut() {
            if let Some(parent) = collider.parent() {
                let position = *self.raw.bodies[parent].position()
                    * *collider
                        .position_wrt_parent()
                        .expect("attached collider pose");
                collider.set_position(position);
            }
        }
        self.collision_tree = rapier::parry::partitioning::Bvh::from_iter(
            rapier::parry::partitioning::BvhBuildStrategy::Binned,
            self.collision_geometry
                .iter()
                .filter(|(_, c)| c.is_enabled())
                .map(|(handle, collider)| {
                    (handle.into_raw_parts().0 as usize, collider.compute_aabb())
                }),
        );
        self.dirty = false;
    }
    fn collision_lookup<'a>(&'a self, filter: r::QueryFilter<'a>) -> r::QueryPipeline<'a> {
        r::QueryPipeline {
            dispatcher: self.raw.narrow_phase.query_dispatcher(),
            bvh: &self.collision_tree,
            bodies: &self.raw.bodies,
            colliders: &self.collision_geometry,
            filter,
        }
    }
    fn filter(&self, filter: QueryFilter) -> PhysicsResult<r::QueryFilter<'_>> {
        let mut result = r::QueryFilter::default().groups(groups(filter.groups));
        if !filter.include_sensors {
            result = result.exclude_sensors();
        }
        if let Some(id) = filter.exclude {
            result = result.exclude_rigid_body(self.entry(id)?.body);
        }
        Ok(result)
    }
    fn id_for(&self, collider: r::ColliderHandle) -> Option<BodyId> {
        self.entries
            .iter()
            .find_map(|(id, entry)| (entry.collider == collider).then_some(*id))
    }
    /// Sweep by displacement; fraction is in [0,1], normal is in world space.
    pub fn cast_shape(
        &mut self,
        shape_desc: Shape,
        from: Pose,
        travel: [f64; 3],
        filter: QueryFilter,
    ) -> PhysicsResult<Option<CastHit>> {
        self.open()?;
        if !shape_desc.valid() || !from.valid() || travel.iter().any(|x| !x.is_finite()) {
            return Err(PhysicsError::InvalidInput);
        }
        self.update_collision_geometry();
        let hit = self.collision_lookup(self.filter(filter)?).cast_shape(
            &pose(from),
            travel.into(),
            shape(shape_desc).as_ref(),
            rapier::parry::query::ShapeCastOptions {
                max_time_of_impact: 1.0,
                ..Default::default()
            },
        );
        Ok(hit.and_then(|(collider, hit)| {
            self.id_for(collider).map(|body| CastHit {
                body,
                fraction: hit.time_of_impact,
                normal: hit.normal1.to_array(),
            })
        }))
    }
    /// Compute movement only. The caller applies it with set_pose or a kinematic
    /// target. Does not push dynamic bodies; actor intent remains game policy.
    pub fn move_character(
        &mut self,
        id: BodyId,
        travel: [f64; 3],
        delta: Duration,
        settings: CharacterSettings,
    ) -> PhysicsResult<CharacterMovement> {
        self.open()?;
        if travel.iter().any(|x| !x.is_finite())
            || delta.is_zero()
            || delta.as_secs_f64() > 1.0
            || !settings.offset.is_finite()
            || settings.offset <= 0.0
            || !settings.max_slope_angle.is_finite()
            || !(0.0..=std::f64::consts::FRAC_PI_2).contains(&settings.max_slope_angle)
            || settings
                .snap_distance
                .is_some_and(|x| !x.is_finite() || x <= 0.0)
        {
            return Err(PhysicsError::InvalidInput);
        }
        self.update_collision_geometry();
        let entry = self.entry(id)?;
        let collider = &self.collision_geometry[entry.collider];
        let controller = KinematicCharacterController {
            offset: CharacterLength::Absolute(settings.offset),
            max_slope_climb_angle: settings.max_slope_angle,
            min_slope_slide_angle: settings.max_slope_angle,
            snap_to_ground: settings.snap_distance.map(CharacterLength::Absolute),
            ..Default::default()
        };
        let filter = r::QueryFilter::default()
            .exclude_rigid_body(entry.body)
            .exclude_sensors()
            .groups(collider.collision_groups());
        let movement = controller.move_shape(
            delta.as_secs_f64(),
            &self.collision_lookup(filter),
            collider.shape(),
            collider.position(),
            travel.into(),
            |_| {},
        );
        Ok(CharacterMovement {
            translation: movement.translation.to_array(),
            grounded: movement.grounded,
        })
    }
    pub fn step(&mut self, delta: Duration) -> PhysicsResult<()> {
        self.open()?;
        if delta.is_zero() || delta.as_secs_f64() > 1.0 {
            return Err(PhysicsError::InvalidInput);
        }
        self.raw.integration_parameters.dt = delta.as_secs_f64();
        self.raw.step();
        self.dirty = true;
        self.contacts.clear();
        self.contacts_truncated = false;
        if !self.raw.quarantine().is_empty() {
            self.failed = true;
            return Err(PhysicsError::SimulationFailed);
        }
        let pairs = self
            .raw
            .contact_pairs()
            .filter(|p| p.has_any_active_contact())
            .map(|p| (p.collider1, p.collider2, false))
            .chain(
                self.raw
                    .intersection_pairs()
                    .filter(|(_, _, _, _, active)| *active)
                    .map(|(a, _, b, _, _)| (a, b, true)),
            );
        let mut contacts = Vec::new();
        for (a, b, sensor) in pairs {
            if contacts.len() == 4096 {
                self.contacts_truncated = true;
                break;
            }
            if let (Some(a), Some(b)) = (self.id_for(a), self.id_for(b)) {
                contacts.push(Contact {
                    bodies: [a.min(b), a.max(b)],
                    sensor,
                });
            }
        }
        contacts.sort_unstable();
        self.contacts = contacts;
        Ok(())
    }
    pub fn contacts(&self) -> &[Contact] {
        &self.contacts
    }
    pub fn contacts_truncated(&self) -> bool {
        self.contacts_truncated
    }
    /// Release provider resources and reject future mutations. No worker threads.
    pub fn close(&mut self) {
        self.raw = r::PhysicsWorld::default();
        self.collision_geometry = r::ColliderSet::new();
        self.collision_tree = rapier::parry::partitioning::Bvh::new();
        self.entries.clear();
        self.contacts.clear();
        self.contacts_truncated = false;
        self.closed = true;
    }
}

#[cfg(test)]
mod failure_tests {
    use super::*;
    #[test]
    fn provider_failure_is_latched_and_shutdown_still_releases_resources() {
        let mut world = PhysicsWorld::new([0.0; 3], 1).unwrap();
        let id = world
            .insert(BodyDesc::new(
                BodyKind::Dynamic,
                Shape::Ball { radius: 0.5 },
                Pose::default(),
            ))
            .unwrap();
        // Fault injection below the validated public input boundary.
        let handle = world.entries[&id].body;
        world.raw.bodies[handle].set_linvel(r::Vector::splat(f64::NAN), true);
        assert_eq!(
            world.step(Duration::from_millis(16)),
            Err(PhysicsError::SimulationFailed)
        );
        assert_eq!(
            world.step(Duration::from_millis(16)),
            Err(PhysicsError::SimulationFailed)
        );
        world.close();
        assert!(world.is_empty() && world.is_closed());
    }
}
