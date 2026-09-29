use super::*;
use std::sync::{
    Arc,
    atomic::{AtomicUsize, Ordering},
};

struct DropCount(Arc<AtomicUsize>);
impl Drop for DropCount {
    fn drop(&mut self) {
        self.0.fetch_add(1, Ordering::SeqCst);
    }
}

#[test]
fn preparation_failure_and_exhausted_ids_preserve_the_active_scene() -> RuntimeResult<()> {
    let mut initial = AppBuilder::new();
    initial.insert_resource(42_u32);
    let mut app = AppBuilder::new().build()?;
    app.switch_scene(initial.build_scene()?)?;
    app.start()?;
    assert!(
        AppBuilder::new()
            .with_fixed_step(Duration::ZERO)
            .build_scene()
            .is_err()
    );
    assert_eq!(*app.world().resource::<u32>()?, 42);
    app.scene_generation = u64::MAX;
    assert_eq!(
        app.switch_scene(AppBuilder::new().build_scene()?),
        Err(RuntimeError::SceneGenerationExhausted)
    );
    assert_eq!(*app.world().resource::<u32>()?, 42);
    app.shutdown()
}

#[test]
fn initial_scene_failure_cleans_scene_then_root_once() -> RuntimeResult<()> {
    let order = Arc::new(std::sync::Mutex::new(Vec::new()));
    let mut root = AppBuilder::new();
    let observed = order.clone();
    root.add_system(Stage::Shutdown, "root close", move |_| {
        observed.lock().unwrap().push("root");
        Ok(())
    });
    let mut scene = AppBuilder::new();
    scene.add_system(Stage::Startup, "failure", |_| {
        Err(RuntimeError::MissingResource("fixture"))
    });
    let observed = order.clone();
    scene.add_system(Stage::Shutdown, "scene close", move |_| {
        observed.lock().unwrap().push("scene");
        Ok(())
    });
    let mut app = root.build()?;
    app.switch_scene(scene.build_scene()?)?;
    assert!(app.start().is_err());
    assert_eq!(app.state(), AppState::Stopped);
    assert_eq!(app.scene_id(), None);
    assert_eq!(*order.lock().unwrap(), ["scene", "root"]);
    Ok(())
}

#[test]
fn scene_switch_preserves_root_systems_resources_events_and_time() -> RuntimeResult<()> {
    let mut root = AppBuilder::new();
    root.insert_resource(Vec::<(u64, Duration)>::new());
    root.insert_resource(0_u32);
    root.add_system(Stage::Startup, "root start", |ctx| {
        *ctx.world.resource_mut::<u32>()? += 1;
        Ok(())
    });
    root.add_system(Stage::Update, "root update", |ctx| {
        ctx.world
            .resource_mut::<Vec<(u64, Duration)>>()?
            .push((ctx.time.frame_number(), ctx.time.elapsed()));
        Ok(())
    });
    let mut app = root.build()?;
    let scene = || {
        let mut scene = AppBuilder::new();
        scene.add_system(Stage::Startup, "scene start", |ctx| {
            *ctx.app_world.as_mut().unwrap().resource_mut::<u32>()? += 10;
            ctx.world.spawn((1_u8,));
            ctx.world.insert_resource(Vec::<u64>::new());
            Ok(())
        });
        scene.add_system(Stage::Update, "scene update", |ctx| {
            ctx.world
                .resource_mut::<Vec<u64>>()?
                .push(ctx.time.frame_number());
            Ok(())
        });
        scene.build_scene()
    };
    let first = app.switch_scene(scene()?)?;
    app.start()?;
    app.send_app_event(77_u32);
    app.send_event(55_u8);
    app.tick(Duration::from_millis(20))?;
    let second = app.switch_scene(scene()?)?;
    assert_ne!(first, second);
    assert_eq!(app.state(), AppState::Running);
    assert_eq!(*app.app_world().resource::<u32>()?, 21);
    assert_eq!(app.world().entities().len(), 1);
    assert_eq!(
        app.events()
            .read(&mut events::EventReader::<u8>::new())
            .count(),
        0
    );
    assert_eq!(
        app.app_events()
            .read(&mut events::EventReader::<u32>::new())
            .copied()
            .collect::<Vec<_>>(),
        [77]
    );
    app.tick(Duration::from_millis(20))?;
    assert_eq!(app.world().resource::<Vec<u64>>()?, &[0]);
    assert_eq!(
        app.app_world().resource::<Vec<(u64, Duration)>>()?,
        &[
            (0, Duration::from_millis(20)),
            (1, Duration::from_millis(40))
        ]
    );
    app.shutdown()
}

#[test]
fn scene_cleanup_drops_dynamic_entities_resources_and_system_captures() -> RuntimeResult<()> {
    let drops = Arc::new(AtomicUsize::new(0));
    let mut scene = AppBuilder::new();
    scene.insert_resource(DropCount(drops.clone()));
    let count = drops.clone();
    scene.add_system(Stage::Startup, "spawn", move |ctx| {
        ctx.world.spawn((DropCount(count.clone()),));
        ctx.commands.spawn((DropCount(count.clone()),));
        Ok(())
    });
    let held = DropCount(drops.clone());
    scene.add_system(Stage::Update, "owned closure", move |_| {
        let _ = &held;
        Ok(())
    });
    let mut app = AppBuilder::new().build()?;
    app.switch_scene(scene.build_scene()?)?;
    app.start()?;
    assert_eq!(app.world().entities().len(), 2);
    app.switch_scene(AppBuilder::new().build_scene()?)?;
    assert_eq!(drops.load(Ordering::SeqCst), 4);
    assert_eq!(app.world().entities().len(), 0);
    app.shutdown()
}

#[test]
fn failed_scene_cleanup_still_closes_services_and_keeps_root_running() -> RuntimeResult<()> {
    let (service, _backend) = services::service_channel::<(), ()>(4).unwrap();
    let mut scene = AppBuilder::new();
    scene.add_system(Stage::Shutdown, "failure", |_| {
        Err(RuntimeError::MissingResource("fixture"))
    });
    scene.add_service("worker", service.clone());
    let mut app = AppBuilder::new().build()?;
    app.switch_scene(scene.build_scene()?)?;
    app.start()?;
    assert!(app.switch_scene(AppBuilder::new().build_scene()?).is_err());
    assert!(service.is_closed());
    assert_eq!(app.state(), AppState::Running);
    assert_eq!(app.scene_id(), None);
    app.switch_scene(AppBuilder::new().build_scene()?)?;
    app.shutdown()
}

#[test]
fn failed_scene_startup_cleans_up_once_without_stopping_root() -> RuntimeResult<()> {
    let stops = Arc::new(AtomicUsize::new(0));
    let observed = stops.clone();
    let mut scene = AppBuilder::new();
    scene.add_system(Stage::Startup, "failure", |_| {
        Err(RuntimeError::MissingResource("fixture"))
    });
    scene.add_system(Stage::Shutdown, "cleanup", move |_| {
        observed.fetch_add(1, Ordering::SeqCst);
        Ok(())
    });
    let mut app = AppBuilder::new().build()?;
    app.start()?;
    assert!(app.switch_scene(scene.build_scene()?).is_err());
    assert_eq!(stops.load(Ordering::SeqCst), 1);
    assert_eq!(app.state(), AppState::Running);
    app.shutdown()?;
    assert_eq!(stops.load(Ordering::SeqCst), 1);
    assert!(app.switch_scene(AppBuilder::new().build_scene()?).is_err());
    Ok(())
}
