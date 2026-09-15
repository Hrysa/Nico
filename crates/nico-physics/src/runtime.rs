use crate::*;
use nico_ecs::Entity;
use nico_runtime::{AppBuilder, Plugin, RuntimeError, RuntimeResult, Stage};
use std::collections::BTreeMap;

/// Attach to an entity to register one body/collider. Pose and velocity are written
/// back after each step. Changes made before physics run apply at that boundary;
/// kinematic pose edits are motion targets, other pose edits are teleports.
/// Removing this component or despawning the entity removes its provider body.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct PhysicsBody(pub BodyDesc);

/// Owned contact observation emitted after every physics step. This is a snapshot
/// of touching pairs, not a damage event or contact-start notification.
#[derive(Clone, Debug)]
pub struct ContactFrame {
    pub contacts: Vec<EntityContact>,
    pub truncated: bool,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct EntityContact {
    pub entities: [Entity; 2],
    pub sensor: bool,
}

/// Runtime-owned mapping. Provider handles are never ECS components.
#[derive(Default)]
pub struct PhysicsEntities {
    entries: BTreeMap<Entity, (BodyId, BodyDesc)>,
}
impl PhysicsEntities {
    pub fn body(&self, entity: Entity) -> Option<BodyId> {
        self.entries.get(&entity).map(|e| e.0)
    }
    pub fn entity(&self, body: BodyId) -> Option<Entity> {
        self.entries
            .iter()
            .find_map(|(entity, entry)| (entry.0 == body).then_some(*entity))
    }
    fn sync(
        &mut self,
        physics: &mut PhysicsWorld,
        desired: &[(Entity, BodyDesc)],
    ) -> PhysicsResult<()> {
        // Validate all authored input before changing registrations.
        if desired.iter().any(|(_, desc)| !desc.valid()) {
            return Err(PhysicsError::InvalidInput);
        }
        let removed: Vec<_> = self
            .entries
            .keys()
            .filter(|entity| desired.binary_search_by_key(*entity, |(e, _)| *e).is_err())
            .copied()
            .collect();
        for entity in removed {
            let (body, _) = self.entries.remove(&entity).unwrap();
            physics.remove(body)?;
        }
        for &(entity, desc) in desired {
            if let Some(&(body, previous)) = self.entries.get(&entity) {
                let configuration = BodyDesc {
                    pose: previous.pose,
                    velocity: previous.velocity,
                    ..desc
                };
                if configuration != previous {
                    physics.remove(body)?;
                    self.entries.remove(&entity);
                } else {
                    if desc.pose != previous.pose {
                        if desc.kind == BodyKind::Kinematic {
                            physics.set_kinematic_target(body, desc.pose)?;
                        } else {
                            physics.set_pose(body, desc.pose)?;
                        }
                    }
                    if desc.velocity != previous.velocity {
                        physics.set_velocity(body, desc.velocity)?;
                    }
                    continue;
                }
            }
            let body = physics.insert(desc)?;
            self.entries.insert(entity, (body, desc));
        }
        Ok(())
    }
}

/// Register before gameplay plugins whose systems consume physics results, and
/// after plugins whose systems submit intent. Systems execute in registration order.
/// This plugin steps once per FixedUpdate using the runtime delta, with no worker.
pub struct PhysicsPlugin {
    pub gravity: [f64; 3],
    pub capacity: usize,
}
impl Default for PhysicsPlugin {
    fn default() -> Self {
        Self {
            gravity: [0.0, -9.81, 0.0],
            capacity: 1024,
        }
    }
}
fn failure(error: PhysicsError) -> RuntimeError {
    RuntimeError::System {
        stage: "FixedUpdate",
        name: "physics::step".into(),
        message: error.to_string(),
    }
}
impl Plugin for PhysicsPlugin {
    fn build(&self, app: &mut AppBuilder) -> RuntimeResult<()> {
        app.insert_resource(PhysicsWorld::new(self.gravity, self.capacity).map_err(failure)?);
        app.insert_resource(PhysicsEntities::default());
        app.add_system(Stage::FixedUpdate, "physics::step", |context| {
            let mut desired: Vec<_> = context
                .world
                .query::<(Entity, &PhysicsBody)>()
                .iter()
                .map(|(e, b)| (e, b.0))
                .collect();
            desired.sort_by_key(|(e, _)| *e);
            let mut mapping = context
                .world
                .remove_resource::<PhysicsEntities>()
                .expect("physics mapping installed");
            let result = (|| {
                let physics = context.world.resource_mut::<PhysicsWorld>()?;
                mapping.sync(physics, &desired).map_err(failure)?;
                physics.step(context.time.delta()).map_err(failure)?;
                let frame = ContactFrame {
                    contacts: physics
                        .contacts()
                        .iter()
                        .filter_map(|contact| {
                            Some(EntityContact {
                                entities: [
                                    mapping.entity(contact.bodies[0])?,
                                    mapping.entity(contact.bodies[1])?,
                                ],
                                sensor: contact.sensor,
                            })
                        })
                        .collect(),
                    truncated: physics.contacts_truncated(),
                };
                let mut updates = Vec::with_capacity(mapping.entries.len());
                for (entity, (id, desc)) in &mut mapping.entries {
                    let state = physics.state(*id).map_err(failure)?;
                    desc.pose = state.pose;
                    desc.velocity = state.velocity;
                    updates.push((*entity, *desc));
                }
                for (entity, desc) in updates {
                    context
                        .world
                        .entities()
                        .get::<&mut PhysicsBody>(entity)
                        .expect("entity collected at this boundary")
                        .0 = desc;
                }
                context.events.send(frame);
                Ok(())
            })();
            context.world.insert_resource(mapping);
            result
        });
        app.add_system(Stage::Shutdown, "physics::close", |context| {
            context.world.resource_mut::<PhysicsWorld>()?.close();
            context
                .world
                .resource_mut::<PhysicsEntities>()?
                .entries
                .clear();
            Ok(())
        });
        Ok(())
    }
}
