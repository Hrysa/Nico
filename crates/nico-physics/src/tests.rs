use super::*;
use std::time::Duration;
const DT: Duration = Duration::from_nanos(16_666_667);

#[test]
fn collision_lookups_preserve_dynamics_through_insert_teleport_sleep_wake_and_remove() {
    fn run(lookups: bool) -> Vec<BodyState> {
        let mut world = PhysicsWorld::new([0.0, -9.81, 0.0], 4).unwrap();
        floor(&mut world);
        let mut id = world
            .insert(ball(BodyKind::Dynamic, [0.0, 3.0, 0.0]))
            .unwrap();
        let character = world
            .insert(ball(BodyKind::Kinematic, [8.0, 0.501, 0.0]))
            .unwrap();
        let mut states = Vec::new();
        for tick in 0..420 {
            if tick == 60 {
                world.set_pose(id, Pose::at([0.0, 2.0, 0.0])).unwrap();
            }
            if tick == 300 {
                assert!(
                    world.state(id).unwrap().sleeping,
                    "body must sleep before wake test"
                );
                world.set_velocity(id, [0.0, 3.0, 0.0]).unwrap();
            }
            if tick == 360 {
                world.remove(id).unwrap();
                id = world
                    .insert(ball(BodyKind::Dynamic, [0.0, 4.0, 0.0]))
                    .unwrap();
            }
            if lookups {
                let before = world.state(id).unwrap();
                let contacts = world.contacts().to_vec();
                let hit = world
                    .cast_shape(
                        Shape::Ball { radius: 0.1 },
                        Pose::at([0.0, 10.0, 0.0]),
                        [0.0, -10.0, 0.0],
                        QueryFilter::default(),
                    )
                    .unwrap()
                    .unwrap();
                assert_eq!(hit.body, id);
                assert!(
                    (hit.fraction - (10.0 - before.pose.position[1] - 0.6) / 10.0).abs() < 1e-6
                );
                world
                    .move_character(
                        character,
                        [0.01, 0.0, 0.0],
                        DT,
                        CharacterSettings::default(),
                    )
                    .unwrap();
                assert_eq!(world.state(id).unwrap(), before);
                assert_eq!(world.contacts(), contacts);
            }
            world.step(DT).unwrap();
            states.push(world.state(id).unwrap());
        }
        states
    }
    assert_eq!(run(false), run(true));
}

#[test]
fn kinematic_characters_enter_and_leave_fixed_and_kinematic_sensors_with_filtering() {
    for sensor_kind in [BodyKind::Fixed, BodyKind::Kinematic] {
        for sensor_first in [false, true] {
            for permitted in [false, true] {
                let mut world = PhysicsWorld::new([0.0; 3], 2).unwrap();
                let mut sensor = ball(sensor_kind, [0.0; 3]);
                sensor.sensor = true;
                sensor.groups = CollisionGroups {
                    membership: 1,
                    filter: if permitted { 2 } else { 4 },
                };
                let mut actor = ball(BodyKind::Kinematic, [-3.0, 0.0, 0.0]);
                actor.groups = CollisionGroups {
                    membership: 2,
                    filter: 1,
                };
                let (trigger, character) = if sensor_first {
                    (world.insert(sensor).unwrap(), world.insert(actor).unwrap())
                } else {
                    let character = world.insert(actor).unwrap();
                    (world.insert(sensor).unwrap(), character)
                };
                world.step(DT).unwrap();
                assert!(world.contacts().is_empty());
                world
                    .set_kinematic_target(character, Pose::default())
                    .unwrap();
                world.step(DT).unwrap();
                world.step(DT).unwrap();
                let contact = Contact {
                    bodies: [trigger.min(character), trigger.max(character)],
                    sensor: true,
                };
                assert_eq!(world.contacts().contains(&contact), permitted);
                world
                    .set_kinematic_target(character, Pose::at([3.0, 0.0, 0.0]))
                    .unwrap();
                world.step(DT).unwrap();
                world.step(DT).unwrap();
                assert!(world.contacts().is_empty());
            }
        }
    }
}

#[cfg(feature = "runtime")]
#[test]
fn runtime_contact_frames_report_kinematic_trigger_entry_and_exit()
-> nico_runtime::RuntimeResult<()> {
    use nico_runtime::{AppBuilder, events::EventReader};
    for sensor_kind in [BodyKind::Fixed, BodyKind::Kinematic] {
        let mut app = AppBuilder::new()
            .with_fixed_step(DT)
            .add_plugin(PhysicsPlugin {
                gravity: [0.0; 3],
                capacity: 2,
            })
            .build()?;
        let mut desc = ball(sensor_kind, [0.0; 3]);
        desc.sensor = true;
        let sensor = app.world_mut().spawn((PhysicsBody(desc),));
        let character = app
            .world_mut()
            .spawn((PhysicsBody(ball(BodyKind::Kinematic, [-3.0, 0.0, 0.0])),));
        app.start()?;
        app.tick(DT)?;
        let mut reader = EventReader::<ContactFrame>::new();
        assert!(
            app.events()
                .read(&mut reader)
                .last()
                .unwrap()
                .contacts
                .is_empty()
        );
        app.world()
            .entities()
            .get::<&mut PhysicsBody>(character)
            .unwrap()
            .0
            .pose = Pose::default();
        app.tick(DT)?;
        app.tick(DT)?;
        let frame = app.events().read(&mut reader).last().unwrap();
        assert!(!frame.truncated);
        assert_eq!(frame.contacts.len(), 1);
        assert!(
            frame.contacts[0].sensor
                && frame.contacts[0].entities.contains(&sensor)
                && frame.contacts[0].entities.contains(&character)
        );
        app.world()
            .entities()
            .get::<&mut PhysicsBody>(character)
            .unwrap()
            .0
            .pose = Pose::at([3.0, 0.0, 0.0]);
        app.tick(DT)?;
        app.tick(DT)?;
        assert!(
            app.events()
                .read(&mut reader)
                .last()
                .unwrap()
                .contacts
                .is_empty()
        );
        app.shutdown()?;
    }
    Ok(())
}
fn ball(kind: BodyKind, p: [f64; 3]) -> BodyDesc {
    BodyDesc::new(kind, Shape::Ball { radius: 0.5 }, Pose::at(p))
}
fn floor(world: &mut PhysicsWorld) -> BodyId {
    world
        .insert(BodyDesc::new(
            BodyKind::Fixed,
            Shape::Cuboid {
                half_extents: [10.0, 0.5, 10.0],
            },
            Pose::at([0.0, -0.5, 0.0]),
        ))
        .unwrap()
}
#[test]
fn gravity_contacts_and_fixed_steps_repeat() {
    fn run() -> (BodyState, Vec<Contact>) {
        let mut world = PhysicsWorld::new([0.0, -9.81, 0.0], 8).unwrap();
        let ground = floor(&mut world);
        let id = world
            .insert(ball(BodyKind::Dynamic, [0.0, 3.0, 0.0]))
            .unwrap();
        for _ in 0..180 {
            world.step(DT).unwrap();
        }
        let state = world.state(id).unwrap();
        assert!((state.pose.position[1] - 0.5).abs() < 0.01, "{state:?}");
        assert!(world.contacts().contains(&Contact {
            bodies: [ground, id],
            sensor: false
        }));
        assert!(!world.contacts_truncated());
        (state, world.contacts().to_vec())
    }
    assert_eq!(run(), run());
}
#[test]
fn queries_see_insert_teleport_remove_and_filter_without_advancing_time() {
    let mut world = PhysicsWorld::new([0.0, -9.81, 0.0], 8).unwrap();
    let mut desc = ball(BodyKind::Fixed, [2.0, 0.0, 0.0]);
    desc.groups = CollisionGroups {
        membership: 2,
        filter: 1,
    };
    let id = world.insert(desc).unwrap();
    let cast = |world: &mut PhysicsWorld, groups| {
        world
            .cast_shape(
                Shape::Ball { radius: 0.5 },
                Pose::default(),
                [4.0, 0.0, 0.0],
                QueryFilter {
                    groups,
                    ..Default::default()
                },
            )
            .unwrap()
    };
    let hit = cast(
        &mut world,
        CollisionGroups {
            membership: 1,
            filter: 2,
        },
    )
    .unwrap();
    assert_eq!(hit.body, id);
    assert!((hit.fraction - 0.25).abs() < 1e-5);
    assert!(hit.normal[0] < -0.99);
    assert!(
        cast(
            &mut world,
            CollisionGroups {
                membership: 4,
                filter: 2
            }
        )
        .is_none()
    );
    world.set_pose(id, Pose::at([20.0, 0.0, 0.0])).unwrap();
    assert!(cast(&mut world, CollisionGroups::default()).is_none());
    world.set_pose(id, Pose::at([2.0, 0.0, 0.0])).unwrap();
    assert!(cast(&mut world, CollisionGroups::default()).is_some());
    world.remove(id).unwrap();
    assert!(cast(&mut world, CollisionGroups::default()).is_none());
    let replacement = world.insert(desc).unwrap();
    assert_ne!(id, replacement);
    assert_eq!(world.state(id), Err(PhysicsError::UnknownBody));
}
#[test]
fn character_slides_and_climbs_a_rotated_ramp() {
    let mut world = PhysicsWorld::new([0.0; 3], 8).unwrap();
    let angle: f64 = 0.25;
    let ramp = BodyDesc::new(
        BodyKind::Fixed,
        Shape::Cuboid {
            half_extents: [3.0, 0.2, 2.0],
        },
        Pose {
            position: [0.0, 0.0, 0.0],
            orientation: [0.0, 0.0, (angle / 2.0).sin(), (angle / 2.0).cos()],
        },
    );
    world.insert(ramp).unwrap();
    let id = world
        .insert(BodyDesc::new(
            BodyKind::Kinematic,
            Shape::Capsule {
                half_height: 0.4,
                radius: 0.3,
            },
            Pose::at([-2.0, 0.5, 0.0]),
        ))
        .unwrap();
    let start = world.state(id).unwrap().pose.position;
    for _ in 0..80 {
        let movement = world
            .move_character(id, [0.04, -0.04, 0.0], DT, CharacterSettings::default())
            .unwrap();
        let mut next = world.state(id).unwrap().pose;
        for (x, delta) in next.position.iter_mut().zip(movement.translation) {
            *x += delta;
        }
        world.set_pose(id, next).unwrap();
    }
    let end = world.state(id).unwrap().pose.position;
    assert!(
        end[0] > start[0] + 1.0 && end[1] > start[1] + 0.2,
        "{start:?} -> {end:?}"
    );
}
#[test]
fn sensors_report_contacts_without_blocking_and_queries_opt_in() {
    let mut world = PhysicsWorld::new([0.0; 3], 8).unwrap();
    let mut sensor = ball(BodyKind::Fixed, [0.0; 3]);
    sensor.sensor = true;
    let a = world.insert(sensor).unwrap();
    let b = world.insert(ball(BodyKind::Dynamic, [0.0; 3])).unwrap();
    world.step(DT).unwrap();
    assert!(world.contacts().contains(&Contact {
        bodies: [a, b],
        sensor: true
    }));
    let filter = QueryFilter {
        exclude: Some(b),
        ..Default::default()
    };
    assert!(
        world
            .cast_shape(
                Shape::Ball { radius: 0.1 },
                Pose::at([-2.0, 0.0, 0.0]),
                [4.0, 0.0, 0.0],
                filter
            )
            .unwrap()
            .is_none()
    );
    assert!(
        world
            .cast_shape(
                Shape::Ball { radius: 0.1 },
                Pose::at([-2.0, 0.0, 0.0]),
                [4.0, 0.0, 0.0],
                QueryFilter {
                    include_sensors: true,
                    ..filter
                }
            )
            .unwrap()
            .is_some()
    );
}
#[test]
fn invalid_inputs_capacity_and_close_are_explicit() {
    let mut world = PhysicsWorld::new([0.0; 3], 1).unwrap();
    let mut invalid = ball(BodyKind::Dynamic, [0.0; 3]);
    invalid.pose.orientation = [0.0; 4];
    assert_eq!(world.insert(invalid), Err(PhysicsError::InvalidInput));
    let id = world.insert(ball(BodyKind::Dynamic, [0.0; 3])).unwrap();
    assert_eq!(
        world.insert(ball(BodyKind::Fixed, [1.0; 3])),
        Err(PhysicsError::Capacity)
    );
    assert_eq!(
        world.set_velocity(id, [f64::NAN; 3]),
        Err(PhysicsError::InvalidInput)
    );
    assert_eq!(world.step(Duration::ZERO), Err(PhysicsError::InvalidInput));
    assert_eq!(
        world.set_kinematic_target(id, Pose::default()),
        Err(PhysicsError::InvalidInput)
    );
    world.close();
    assert!(world.is_empty() && world.is_closed());
    assert_eq!(world.step(DT), Err(PhysicsError::Closed));
    assert_eq!(
        world.insert(ball(BodyKind::Dynamic, [0.0; 3])),
        Err(PhysicsError::Closed)
    );
}

#[test]
fn dense_sensor_contacts_report_truncation() {
    let mut world = PhysicsWorld::new([0.0; 3], 93).unwrap();
    let mut desc = ball(BodyKind::Dynamic, [0.0; 3]);
    desc.sensor = true;
    for _ in 0..93 {
        world.insert(desc).unwrap();
    }
    world.step(DT).unwrap();
    assert_eq!(world.contacts().len(), 4096);
    assert!(world.contacts_truncated());
    assert!(world.contacts().iter().all(|contact| contact.sensor));
}

#[test]
fn kinematic_target_pushes_a_dynamic_box() {
    let mut world = PhysicsWorld::new([0.0; 3], 4).unwrap();
    let shape = Shape::Cuboid {
        half_extents: [0.5; 3],
    };
    let pusher = world
        .insert(BodyDesc::new(
            BodyKind::Kinematic,
            shape,
            Pose::at([-2.0, 0.0, 0.0]),
        ))
        .unwrap();
    let box_id = world
        .insert(BodyDesc::new(BodyKind::Dynamic, shape, Pose::default()))
        .unwrap();
    for tick in 1..=90 {
        world
            .set_kinematic_target(pusher, Pose::at([-2.0 + tick as f64 * 0.03, 0.0, 0.0]))
            .unwrap();
        world.step(DT).unwrap();
    }
    assert!(world.state(box_id).unwrap().pose.position[0] > 1.5);
}

#[cfg(feature = "runtime")]
#[test]
fn runtime_replaces_edited_shapes_and_cleans_component_removal_and_failed_steps()
-> nico_runtime::RuntimeResult<()> {
    use nico_runtime::AppBuilder;
    let mut app = AppBuilder::new()
        .add_plugin(PhysicsPlugin::default())
        .build()?;
    let entity = app
        .world_mut()
        .spawn((PhysicsBody(ball(BodyKind::Fixed, [0.0; 3])),));
    app.start()?;
    app.tick(DT)?;
    let first = app
        .world()
        .resource::<PhysicsEntities>()?
        .body(entity)
        .unwrap();
    app.world()
        .entities()
        .get::<&mut PhysicsBody>(entity)
        .unwrap()
        .0
        .shape = Shape::Cuboid {
        half_extents: [1.0; 3],
    };
    app.tick(DT)?;
    let second = app
        .world()
        .resource::<PhysicsEntities>()?
        .body(entity)
        .unwrap();
    assert_ne!(first, second);
    assert_eq!(
        app.world().resource::<PhysicsWorld>()?.state(first),
        Err(PhysicsError::UnknownBody)
    );
    app.world_mut()
        .entities_mut()
        .remove_one::<PhysicsBody>(entity)
        .unwrap();
    app.tick(DT)?;
    assert!(app.world().resource::<PhysicsWorld>()?.is_empty());
    app.world_mut()
        .entities_mut()
        .insert_one(
            entity,
            PhysicsBody(ball(BodyKind::Dynamic, [f64::NAN, 0.0, 0.0])),
        )
        .unwrap();
    assert!(app.tick(DT).is_err());
    assert!(
        app.world()
            .resource::<PhysicsEntities>()?
            .body(entity)
            .is_none()
    );
    app.shutdown()?;
    assert!(app.world().resource::<PhysicsWorld>()?.is_closed());
    Ok(())
}

#[cfg(feature = "runtime")]
#[test]
fn runtime_syncs_entities_edits_despawns_contacts_and_shutdown() -> nico_runtime::RuntimeResult<()>
{
    use nico_runtime::{AppBuilder, events::EventReader};
    let mut app = AppBuilder::new()
        .with_fixed_step(DT)
        .add_plugin(PhysicsPlugin::default())
        .build()?;
    let ground = app.world_mut().spawn((PhysicsBody(BodyDesc::new(
        BodyKind::Fixed,
        Shape::Cuboid {
            half_extents: [10.0, 0.5, 10.0],
        },
        Pose::at([0.0, -0.5, 0.0]),
    )),));
    let entity = app
        .world_mut()
        .spawn((PhysicsBody(ball(BodyKind::Dynamic, [0.0, 2.0, 0.0])),));
    app.start()?;
    for _ in 0..180 {
        app.tick(DT)?;
    }
    let id = app
        .world()
        .resource::<PhysicsEntities>()?
        .body(entity)
        .unwrap();
    let body = *app.world().entities().get::<&PhysicsBody>(entity).unwrap();
    assert!((body.0.pose.position[1] - 0.5).abs() < 0.01);
    let mut reader = EventReader::<ContactFrame>::new();
    assert!(app.events().read(&mut reader).any(|frame| {
        frame
            .contacts
            .iter()
            .any(|c| c.entities.contains(&ground) && c.entities.contains(&entity))
    }));
    app.world()
        .entities()
        .get::<&mut PhysicsBody>(entity)
        .unwrap()
        .0
        .pose = Pose::at([0.0, 4.0, 0.0]);
    app.tick(DT)?;
    assert!(
        app.world()
            .resource::<PhysicsWorld>()?
            .state(id)
            .unwrap()
            .pose
            .position[1]
            > 3.9
    );
    app.world_mut().despawn(entity).unwrap();
    app.tick(DT)?;
    assert!(
        app.world()
            .resource::<PhysicsEntities>()?
            .body(entity)
            .is_none()
    );
    assert_eq!(
        app.world().resource::<PhysicsWorld>()?.state(id),
        Err(PhysicsError::UnknownBody)
    );
    app.shutdown()?;
    assert!(app.world().resource::<PhysicsWorld>()?.is_closed());
    Ok(())
}

#[test]
fn queries_support_one_and_two_remaining_sparse_collider_indices() {
    let mut world = PhysicsWorld::new([0.; 3], 8).unwrap();
    let first = world.insert(ball(BodyKind::Fixed, [-10., 0., 0.])).unwrap();
    let second = world.insert(ball(BodyKind::Fixed, [-5., 0., 0.])).unwrap();
    let target = world.insert(ball(BodyKind::Fixed, [3., 0., 0.])).unwrap();
    let actor = world
        .insert(ball(BodyKind::Kinematic, [0., 0., 0.]))
        .unwrap();
    world.remove(first).unwrap();
    world.remove(second).unwrap();
    let movement = world
        .move_character(actor, [5., 0., 0.], DT, CharacterSettings::default())
        .unwrap();
    assert!(movement.translation[0] < 3.);
    world.remove(target).unwrap();
    let movement = world
        .move_character(actor, [5., 0., 0.], DT, CharacterSettings::default())
        .unwrap();
    assert!((movement.translation[0] - 5.).abs() < 1e-6);
}
