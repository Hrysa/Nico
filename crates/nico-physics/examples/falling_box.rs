//! Bounded headless runtime example; no presentation or host transport.
use nico_physics::{BodyDesc, BodyKind, PhysicsBody, PhysicsPlugin, Pose, Shape};
use nico_runtime::AppBuilder;
use std::time::Duration;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let dt = Duration::from_nanos(16_666_667);
    let mut app = AppBuilder::new()
        .with_fixed_step(dt)
        .add_plugin(PhysicsPlugin::default())
        .build()?;
    app.world_mut().spawn((PhysicsBody(BodyDesc::new(
        BodyKind::Fixed,
        Shape::Cuboid {
            half_extents: [5.0, 0.5, 5.0],
        },
        Pose::at([0.0, -0.5, 0.0]),
    )),));
    let entity = app.world_mut().spawn((PhysicsBody(BodyDesc::new(
        BodyKind::Dynamic,
        Shape::Cuboid {
            half_extents: [0.5; 3],
        },
        Pose::at([0.0, 3.0, 0.0]),
    )),));
    app.start()?;
    for _ in 0..180 {
        app.tick(dt)?;
    }
    let position = app
        .world()
        .entities()
        .get::<&PhysicsBody>(entity)?
        .0
        .pose
        .position;
    println!("{{\"ticks\":180,\"position\":{position:?}}}");
    app.shutdown()?;
    Ok(())
}
