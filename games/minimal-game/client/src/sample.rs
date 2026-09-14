//! Game-owned draw extraction and bounded sample controls; no transport threads.
use minimal_game_shared::Position;
use nico_assets::{
    AssetId, AssetLease, Handle, Mesh, Texture,
    loading::{MeshState, MeshStore, TextureLimits, TextureState, TextureStore},
};
use nico_ops::mcp::{CallToolResult, Tool, ToolExtensions};
use nico_presentation::{Camera2d, Camera3d, MeshInstance, Quad, Scene2d, Scene3d};
use nico_runtime::{AppBuilder, Plugin, RuntimeError, RuntimeResult, Stage};
use serde_json::{Map, Value, json};
use std::{
    collections::VecDeque,
    io,
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc::{self, Receiver, SyncSender},
    },
};

const TEXTURE: Handle<Texture> = Handle::new(AssetId::from_u128(1));
const CHECKER: Handle<Texture> = Handle::new(AssetId::from_u128(3));
const CAPACITY: usize = 32;
const MESH: Handle<Mesh> = Handle::new(AssetId::from_u128(2));

#[derive(Debug)]
enum Action {
    Position([f32; 2]),
    Camera([f32; 2]),
    Visible(bool),
    Texture(bool),
    Retry,
    Mesh(bool),
    RetryMesh,
    Camera3d([f32; 3]),
    Yaw(f32),
}
#[derive(Debug)]
struct Command {
    id: u64,
    action: Action,
}
struct Sender {
    channel: SyncSender<Command>,
    next: u64,
}
struct Sample {
    lease: Option<AssetLease<Texture>>,
    mesh_lease: Option<AssetLease<Mesh>>,
    checker_lease: Option<AssetLease<Texture>>,
    mode3d: bool,
    camera3d: Camera3d,
    yaw: f32,
    camera: Camera2d,
    visible: bool,
    applied: u64,
    error: Option<String>,
    results: VecDeque<(u64, Option<String>)>,
}

pub fn register(
    mut builder: AppBuilder,
    tools: &mut ToolExtensions,
    root: PathBuf,
    mode3d: bool,
) -> io::Result<AppBuilder> {
    TextureStore::install(
        &mut builder,
        &root,
        [
            (TEXTURE.id(), "textures/sample.png".into()),
            (CHECKER.id(), "textures/uv-checker.png".into()),
        ],
        TextureLimits::default(),
    )?;
    if mode3d {
        MeshStore::install(
            &mut builder,
            &root,
            [(MESH.id(), "meshes/cube.glb".into())],
            TextureLimits::default(),
        )?;
    }
    let (sender, receiver) = mpsc::sync_channel(CAPACITY);
    let sender = Mutex::new(Sender {
        channel: sender,
        next: 1,
    });
    let snapshot = Arc::new(Mutex::new(None::<Value>));
    let closed = Arc::new(AtomicBool::new(false));
    let observed = snapshot.clone();
    tools.register(Tool::new("sample_state", "Read the owned rendering sample snapshot. Texture readiness describes CPU pixels, not GPU completion. Compare last_applied_command with accepted command IDs.",
        json!({"type":"object","properties":{},"additionalProperties":false}).as_object().unwrap().clone())
        .with_raw_output_schema(json!({"type":"object","required":["frame","last_applied_command","command_results","texture_state","world_quads","hud_quads"],"properties":{
            "frame":{"type":"integer"},"last_applied_command":{"type":"integer"},
            "command_results":{"type":"array","maxItems":32,"items":{"type":"object","required":["command_id","error"],"properties":{"command_id":{"type":"integer"},"error":{"type":["string","null"]}},"additionalProperties":false}},
            "texture_state":{"enum":["loading","ready","failed","disabled"]},"world_quads":{"type":"integer"},"hud_quads":{"type":"integer"},
            "sample_mode":{"enum":["2d","3d"]},"mesh_state":{"enum":["unused","loading","ready","failed","disabled"]},
            "mesh_draws":{"type":"integer"},"mesh_vertices":{"type":"integer"},"mesh_indices":{"type":"integer"},
            "checker_state":{"enum":["unused","loading","ready","failed","disabled"]},"checker_error":{"type":["string","null"]},"checker_resident":{"type":"boolean"},
            "mesh_resident":{"type":"boolean"},"mesh_yaw":{"type":"number"},"camera3d_position":{"type":"array","items":{"type":"number"},"minItems":3,"maxItems":3}
        }}).as_object().unwrap().clone().into()), move |args| {
        if !args.is_empty() { return error("invalid_arguments", "sample_state accepts no arguments"); }
        observed.lock().unwrap().clone().map(CallToolResult::structured).unwrap_or_else(|| error("not_ready", "no sample frame published"))
    })?;
    let stopped = closed.clone();
    tools.register(Tool::new("sample_control", "Queue one sample edit at the runtime Update boundary. set_position teleports world sprites for validation. Acceptance is not application; inspect sample_state. Do not blindly retry timed-out mutations.",
        json!({"type":"object","oneOf":[
            {"required":["action","x","y"],"properties":{"action":{"enum":["set_position","set_camera"]},"x":{"type":"number","minimum":-10000,"maximum":10000},"y":{"type":"number","minimum":-10000,"maximum":10000}},"additionalProperties":false},
            {"required":["action","value"],"properties":{"action":{"enum":["set_sprite_visible","set_texture_enabled","set_mesh_enabled"]},"value":{"type":"boolean"}},"additionalProperties":false},
            {"required":["action"],"properties":{"action":{"enum":["retry_texture","retry_mesh"]}},"additionalProperties":false},
            {"required":["action","x","y","z"],"properties":{"action":{"const":"set_camera3d"},"x":{"type":"number","minimum":-10000,"maximum":10000},"y":{"type":"number","minimum":-10000,"maximum":10000},"z":{"type":"number","minimum":-10000,"maximum":10000}},"additionalProperties":false},
            {"required":["action","radians"],"properties":{"action":{"const":"set_mesh_yaw"},"radians":{"type":"number","minimum":-10000,"maximum":10000}},"additionalProperties":false}
        ]}).as_object().unwrap().clone())
        .with_raw_output_schema(json!({"type":"object","required":["accepted","command_id"],"properties":{"accepted":{"const":true},"command_id":{"type":"integer","minimum":1}},"additionalProperties":false}).as_object().unwrap().clone().into()), move |args| {
        let action = match parse(&args) { Ok(value) => value, Err(()) => return error("invalid_arguments", "arguments do not match an action schema") };
        if stopped.load(Ordering::Acquire) { return error("closed", "sample has stopped"); }
        let mut sender = sender.lock().unwrap();
        let id = sender.next;
        if id == u64::MAX { return error("closed", "sample command identity exhausted"); }
        match sender.channel.try_send(Command { id, action }) {
            Ok(()) => { sender.next += 1; CallToolResult::structured(json!({"accepted":true,"command_id":id})) },
            Err(mpsc::TrySendError::Full(_)) => error("overloaded", "sample command queue is full"),
            Err(mpsc::TrySendError::Disconnected(_)) => error("closed", "sample has stopped"),
        }
    })?;
    Ok(builder.add_plugin(SamplePlugin {
        mode3d,
        receiver: Arc::new(Mutex::new(receiver)),
        snapshot,
        closed,
    }))
}

fn error(code: &str, message: &str) -> CallToolResult {
    CallToolResult::structured_error(json!({"error":{"code":code,"message":message}}))
}

fn parse(args: &Map<String, Value>) -> Result<Action, ()> {
    match args.get("action").and_then(Value::as_str) {
        Some(action @ ("set_position" | "set_camera")) if args.len() == 3 => {
            let x = args.get("x").and_then(Value::as_f64).ok_or(())?;
            let y = args.get("y").and_then(Value::as_f64).ok_or(())?;
            if !x.is_finite() || !y.is_finite() || x.abs() > 10000.0 || y.abs() > 10000.0 {
                return Err(());
            }
            Ok(if action == "set_position" {
                Action::Position([x as f32, y as f32])
            } else {
                Action::Camera([x as f32, y as f32])
            })
        }
        Some(action @ ("set_sprite_visible" | "set_texture_enabled" | "set_mesh_enabled"))
            if args.len() == 2 =>
        {
            let value = args.get("value").and_then(Value::as_bool).ok_or(())?;
            Ok(if action == "set_sprite_visible" {
                Action::Visible(value)
            } else if action == "set_mesh_enabled" {
                Action::Mesh(value)
            } else {
                Action::Texture(value)
            })
        }
        Some("retry_texture") if args.len() == 1 => Ok(Action::Retry),
        Some("retry_mesh") if args.len() == 1 => Ok(Action::RetryMesh),
        Some("set_camera3d") if args.len() == 4 => {
            let values = ["x", "y", "z"].map(|key| args.get(key).and_then(Value::as_f64));
            let [Some(x), Some(y), Some(z)] = values else {
                return Err(());
            };
            if [x, y, z]
                .iter()
                .any(|v| !v.is_finite() || v.abs() > 10000.0)
            {
                return Err(());
            }
            let position = [x as f32, y as f32, z as f32];
            if !(Camera3d {
                position,
                ..Camera3d::default()
            })
            .has_valid_view_direction()
            {
                return Err(());
            }
            Ok(Action::Camera3d(position))
        }
        Some("set_mesh_yaw") if args.len() == 2 => {
            let value = args.get("radians").and_then(Value::as_f64).ok_or(())?;
            if !value.is_finite() || value.abs() > 10000.0 {
                return Err(());
            }
            Ok(Action::Yaw(value as f32))
        }
        _ => Err(()),
    }
}

struct SamplePlugin {
    mode3d: bool,
    receiver: Arc<Mutex<Receiver<Command>>>,
    snapshot: Arc<Mutex<Option<Value>>>,
    closed: Arc<AtomicBool>,
}

fn runtime_error(error: impl std::fmt::Display) -> RuntimeError {
    RuntimeError::System {
        stage: "Startup",
        name: "sample texture request".into(),
        message: error.to_string(),
    }
}

impl Plugin for SamplePlugin {
    fn build(&self, builder: &mut AppBuilder) -> RuntimeResult<()> {
        builder.insert_resource(Sample {
            lease: None,
            mesh_lease: None,
            checker_lease: None,
            mode3d: self.mode3d,
            camera3d: Camera3d::default(),
            yaw: 0.0,
            camera: Camera2d::default(),
            visible: true,
            applied: 0,
            error: None,
            results: VecDeque::new(),
        });
        builder.insert_resource(Scene2d::default());
        builder.insert_resource(Scene3d::default());
        builder.add_system(Stage::Startup, "sample::load", |context| {
            let lease = context
                .world
                .resource_mut::<TextureStore>()?
                .request(TEXTURE)
                .map_err(runtime_error)?;
            context.world.resource_mut::<Sample>()?.lease = Some(lease);
            if context.world.resource::<Sample>()?.mode3d {
                let checker = context
                    .world
                    .resource_mut::<TextureStore>()?
                    .request(CHECKER)
                    .map_err(runtime_error)?;
                context.world.resource_mut::<Sample>()?.checker_lease = Some(checker);
                let lease = context
                    .world
                    .resource_mut::<MeshStore>()?
                    .request(MESH)
                    .map_err(runtime_error)?;
                context.world.resource_mut::<Sample>()?.mesh_lease = Some(lease);
            }
            Ok(())
        });
        let receiver = self.receiver.clone();
        let snapshot = self.snapshot.clone();
        builder.add_system(Stage::Update, "sample::extract", move |context| {
            let commands: Vec<_> = receiver.lock().unwrap().try_iter().take(CAPACITY).collect();
            for command in commands {
                let mut failure = None;
                let mode3d = context.world.resource::<Sample>()?.mode3d;
                if !mode3d && matches!(command.action, Action::Mesh(_) | Action::RetryMesh | Action::Camera3d(_) | Action::Yaw(_)) {
                    failure = Some("3D sample is not selected".into());
                } else { match command.action {
                    Action::Position([x,y]) => for position in context.world.query::<&mut Position>().iter() { *position = Position::new(x,y); },
                    Action::Camera(center) => context.world.resource_mut::<Sample>()?.camera.center = center,
                    Action::Visible(value) => context.world.resource_mut::<Sample>()?.visible = value,
                    Action::Texture(false) => {
                        let sample = context.world.resource_mut::<Sample>()?;
                        sample.lease = None;
                        sample.checker_lease = None;
                    },
                    Action::Texture(true) => {
                        match context.world.resource_mut::<TextureStore>()?.request(TEXTURE) {
                            Ok(lease) => context.world.resource_mut::<Sample>()?.lease = Some(lease),
                            Err(error) => failure = Some(error.to_string()),
                        }
                        if mode3d {
                            match context.world.resource_mut::<TextureStore>()?.request(CHECKER) {
                                Ok(lease) => context.world.resource_mut::<Sample>()?.checker_lease = Some(lease),
                                Err(error) => failure = Some(error.to_string()),
                            }
                        }
                    }
                    Action::Retry => {
                        let store = context.world.resource_mut::<TextureStore>()?;
                        let failed: Vec<_> = [Some(TEXTURE), mode3d.then_some(CHECKER)].into_iter().flatten()
                            .filter(|&handle| matches!(store.state(handle), Some(TextureState::Failed(_)))).collect();
                        if failed.is_empty() { failure = Some("NotFailed".into()); }
                        for handle in failed {
                            if let Err(error) = store.retry(handle) { failure = Some(error.to_string()); }
                        }
                    },
                    Action::Mesh(false) => context.world.resource_mut::<Sample>()?.mesh_lease = None,
                    Action::Mesh(true) => match context.world.resource_mut::<MeshStore>()?.request(MESH) {
                        Ok(lease) => context.world.resource_mut::<Sample>()?.mesh_lease = Some(lease),
                        Err(error) => failure = Some(error.to_string()),
                    },
                    Action::RetryMesh => if let Err(error) = context.world.resource_mut::<MeshStore>()?.retry(MESH) { failure = Some(error.to_string()); },
                    Action::Camera3d(position) => context.world.resource_mut::<Sample>()?.camera3d.position = position,
                    Action::Yaw(radians) => context.world.resource_mut::<Sample>()?.yaw = radians,
                } }
                let sample = context.world.resource_mut::<Sample>()?;
                sample.applied = command.id;
                sample.error = failure.clone();
                if sample.results.len() == CAPACITY { sample.results.pop_front(); }
                sample.results.push_back((command.id, failure));
            }
            let sample = context.world.resource::<Sample>()?;
            let store = context.world.resource::<TextureStore>()?;
            let (texture_state, texture, texture_error) = if sample.lease.is_none() { ("disabled", None, None) } else {
                match store.state(TEXTURE) {
                    Some(TextureState::Ready(value)) => ("ready", Some(value.clone()), None),
                    Some(TextureState::Failed(error)) => ("failed", None, Some(error.to_string())),
                    _ => ("loading", None, None),
                }
            };
            let (checker_state, checker, checker_error) = if !sample.mode3d { ("unused", None, None) } else if sample.checker_lease.is_none() { ("disabled", None, None) } else {
                match store.state(CHECKER) {
                    Some(TextureState::Ready(value)) => ("ready", Some(value.clone()), None),
                    Some(TextureState::Failed(error)) => ("failed", None, Some(error.to_string())),
                    _ => ("loading", None, None),
                }
            };
            let positions: Vec<_> = context.world.query::<&Position>().iter().map(|p| [p.x(),p.y()]).collect();
            let world = if sample.visible && !sample.mode3d { positions.iter().map(|&center| Quad { center, size: [1.0,1.0], color: [1.0;4], texture: texture.clone() }).collect() } else { Vec::new() };
            let scene = Scene2d { camera: sample.camera, world,
                hud: vec![Quad { center: [48.0,48.0], size: [48.0,48.0], color: [1.0;4], texture }] };
            let mesh_store = context.world.resource::<MeshStore>().ok();
            let (mesh_state, mesh, mesh_error) = if !sample.mode3d { ("unused",None,None) } else if sample.mesh_lease.is_none() { ("disabled",None,None) } else {
                match mesh_store.and_then(|s| s.state(MESH)) {
                    Some(MeshState::Ready(value)) => ("ready",Some(value.clone()),None),
                    Some(MeshState::Failed(error)) => ("failed",None,Some(error.to_string())),
                    _ => ("loading",None,None),
                }
            };
            let scene3d = Scene3d { camera: sample.camera3d, meshes: if sample.mode3d && sample.visible {
                positions.iter().map(|p| MeshInstance { position: [p[0],p[1],0.0], yaw_radians: sample.yaw, scale: 1.0,
                    color: [1.0;4], mesh: mesh.clone(), texture: checker.clone() }).collect()
            } else { Vec::new() } };
            let value = json!({"frame":context.time.frame_number(),"last_applied_command":sample.applied,"command_error":sample.error,
                "sample_mode":if sample.mode3d {"3d"} else {"2d"}, "mesh_state":mesh_state,"mesh_error":mesh_error,
                "mesh_resident":mesh_store.is_some_and(|s| s.state(MESH).is_some()),"mesh_draws":scene3d.meshes.len(),
                "mesh_vertices":mesh.as_ref().map_or(0,|m| m.vertices().len()),"mesh_indices":mesh.as_ref().map_or(0,|m| m.indices().len()),
                "camera3d_position":sample.camera3d.position,"mesh_yaw":sample.yaw,
                "command_results":sample.results.iter().map(|(id,error)| json!({"command_id":id,"error":error})).collect::<Vec<_>>(),
                "checker_state":checker_state,"checker_error":checker_error,"checker_resident":store.state(CHECKER).is_some(),
                "texture_state":texture_state,"texture_error":texture_error,"texture_resident":store.state(TEXTURE).is_some(),
                "positions":positions,"camera_center":scene.camera.center,"pixels_per_unit":scene.camera.pixels_per_unit,
                "world_quads":scene.world.len(),"hud_quads":scene.hud.len(),"hud_center":[48,48],"hud_size":[48,48]});
            *context.world.resource_mut::<Scene2d>()? = scene;
            *context.world.resource_mut::<Scene3d>()? = scene3d;
            *snapshot.lock().unwrap() = Some(value);
            Ok(())
        });
        let closed = self.closed.clone();
        builder.add_system(Stage::Shutdown, "sample::release", move |context| {
            closed.store(true, Ordering::Release);
            context.world.resource_mut::<Sample>()?.lease = None;
            context.world.resource_mut::<Sample>()?.mesh_lease = None;
            context.world.resource_mut::<Sample>()?.checker_lease = None;
            *context.world.resource_mut::<Scene2d>()? = Scene2d::default();
            *context.world.resource_mut::<Scene3d>()? = Scene3d::default();
            Ok(())
        });
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use minimal_game_shared::{MinimalGamePlugin, MovementVector, PlayerCommand};
    use std::time::{Duration, Instant};
    fn app() -> nico_runtime::App {
        let builder = AppBuilder::new().add_plugin(MinimalGamePlugin);
        register(
            builder,
            &mut ToolExtensions::default(),
            PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../assets/presentation"),
            false,
        )
        .unwrap()
        .build()
        .unwrap()
    }
    #[test]
    fn sample_shares_loaded_texture_and_tracks_authoritative_movement_and_removal() {
        let mut app = app();
        app.start().unwrap();
        app.send_event(PlayerCommand::move_in(MovementVector::normalized(0.0, 1.0)));
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            app.tick(Duration::from_millis(17)).unwrap();
            if app.world().resource::<Scene2d>().unwrap().world[0]
                .texture
                .is_some()
            {
                break;
            }
            assert!(Instant::now() < deadline);
            std::thread::sleep(Duration::from_millis(2));
        }
        let scene = app.world().resource::<Scene2d>().unwrap();
        assert!(scene.world[0].center[1] > 0.0);
        assert_eq!(scene.hud[0].center, [48.0, 48.0]);
        assert!(Arc::ptr_eq(
            scene.world[0].texture.as_ref().unwrap(),
            scene.hud[0].texture.as_ref().unwrap()
        ));
        let entity = app.world().entities().iter().next().unwrap().entity();
        app.world_mut().despawn(entity).unwrap();
        app.tick(Duration::ZERO).unwrap();
        assert!(app.world().resource::<Scene2d>().unwrap().world.is_empty());
        app.world_mut().spawn((Position::new(2.0, 3.0),));
        app.tick(Duration::ZERO).unwrap();
        assert_eq!(
            app.world().resource::<Scene2d>().unwrap().world[0].center,
            [2.0, 3.0]
        );
        app.shutdown().unwrap();
        assert!(app.world().resource::<Scene2d>().unwrap().hud.is_empty());
    }
    #[test]
    fn sample_commands_reject_unknown_fields_and_out_of_range_values() {
        for value in [
            json!({"action":"set_camera","x":0,"y":1,"extra":true}),
            json!({"action":"set_position","x":10001,"y":0}),
            json!({"action":"set_texture_enabled","value":1}),
            json!({"action":"set_camera3d","x":0.001,"y":10000,"z":0}),
            json!({"action":"set_camera3d","x":0,"y":0,"z":0}),
            json!({"action":"set_camera3d","x":0.0001,"y":0,"z":0}),
        ] {
            assert!(parse(value.as_object().unwrap()).is_err());
        }
        assert!(matches!(
            parse(
                json!({"action":"set_camera","x":0,"y":1})
                    .as_object()
                    .unwrap()
            ),
            Ok(Action::Camera([0.0, 1.0]))
        ));
    }

    #[test]
    fn sample3d_uses_opaque_checker_and_preserves_transparent_hud() {
        let builder = AppBuilder::new().add_plugin(MinimalGamePlugin);
        let mut app = register(
            builder,
            &mut ToolExtensions::default(),
            PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../assets/presentation"),
            true,
        )
        .unwrap()
        .build()
        .unwrap();
        app.start().unwrap();
        app.send_event(PlayerCommand::move_in(MovementVector::normalized(0.0, 1.0)));
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            app.tick(Duration::from_millis(17)).unwrap();
            let scene = app.world().resource::<Scene3d>().unwrap();
            if scene.meshes[0].mesh.is_some() && scene.meshes[0].texture.is_some() {
                break;
            }
            assert!(Instant::now() < deadline);
            std::thread::sleep(Duration::from_millis(2));
        }
        let scene = app.world().resource::<Scene3d>().unwrap();
        let hud = app.world().resource::<Scene2d>().unwrap();
        assert!(scene.meshes[0].position[1] > 0.0);
        assert_eq!(scene.meshes[0].mesh.as_ref().unwrap().vertices().len(), 24);
        assert!(hud.world.is_empty());
        assert!(!Arc::ptr_eq(
            scene.meshes[0].texture.as_ref().unwrap(),
            hud.hud[0].texture.as_ref().unwrap()
        ));
        assert!(
            scene.meshes[0]
                .texture
                .as_ref()
                .unwrap()
                .pixels()
                .chunks_exact(4)
                .all(|p| p[3] == 255)
        );
        assert!(
            hud.hud[0]
                .texture
                .as_ref()
                .unwrap()
                .pixels()
                .chunks_exact(4)
                .any(|p| p[3] == 0)
        );
        let entity = app.world().entities().iter().next().unwrap().entity();
        app.world_mut().despawn(entity).unwrap();
        app.tick(Duration::ZERO).unwrap();
        assert!(app.world().resource::<Scene3d>().unwrap().meshes.is_empty());
        app.world_mut().spawn((Position::new(2.0, 3.0),));
        app.tick(Duration::ZERO).unwrap();
        assert_eq!(
            app.world().resource::<Scene3d>().unwrap().meshes[0].position,
            [2.0, 3.0, 0.0]
        );
        app.shutdown().unwrap();
        assert!(app.world().resource::<Scene3d>().unwrap().meshes.is_empty());
        assert!(app.world().resource::<Scene2d>().unwrap().hud.is_empty());
    }

    #[test]
    fn retry_only_failed_textures_preserves_healthy_content() {
        for (fail_hud, fail_checker) in [(false, true), (true, false), (true, true)] {
            let (sender, receiver) = mpsc::sync_channel(CAPACITY);
            let snapshot = Arc::new(Mutex::new(None));
            let mut builder = AppBuilder::new().add_plugin(MinimalGamePlugin);
            let root = PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../assets/presentation");
            TextureStore::install(
                &mut builder,
                &root,
                [
                    (
                        TEXTURE.id(),
                        if fail_hud {
                            "missing-hud.png"
                        } else {
                            "textures/sample.png"
                        }
                        .into(),
                    ),
                    (
                        CHECKER.id(),
                        if fail_checker {
                            "missing-checker.png"
                        } else {
                            "textures/uv-checker.png"
                        }
                        .into(),
                    ),
                ],
                TextureLimits::default(),
            )
            .unwrap();
            MeshStore::install(
                &mut builder,
                &root,
                [(MESH.id(), "meshes/cube.glb".into())],
                TextureLimits::default(),
            )
            .unwrap();
            let mut app = builder
                .add_plugin(SamplePlugin {
                    mode3d: true,
                    receiver: Arc::new(Mutex::new(receiver)),
                    snapshot: snapshot.clone(),
                    closed: Arc::new(AtomicBool::new(false)),
                })
                .build()
                .unwrap();
            app.start().unwrap();
            let deadline = Instant::now() + Duration::from_secs(5);
            loop {
                app.tick(Duration::ZERO).unwrap();
                let state = snapshot.lock().unwrap().clone().unwrap();
                if state["texture_state"] == if fail_hud { "failed" } else { "ready" }
                    && state["checker_state"] == if fail_checker { "failed" } else { "ready" }
                {
                    break;
                }
                assert!(Instant::now() < deadline);
                std::thread::sleep(Duration::from_millis(2));
            }
            sender
                .try_send(Command {
                    id: 1,
                    action: Action::Retry,
                })
                .unwrap();
            app.tick(Duration::ZERO).unwrap();
            let state = snapshot.lock().unwrap().clone().unwrap();
            assert!(state["command_error"].is_null(), "{state}");
            assert_eq!(
                state["texture_state"],
                if fail_hud { "loading" } else { "ready" }
            );
            assert_eq!(
                state["checker_state"],
                if fail_checker { "loading" } else { "ready" }
            );
            app.shutdown().unwrap();
        }
    }

    #[test]
    fn queued_controls_apply_at_update_and_texture_release_is_reconciled() {
        let (sender, receiver) = mpsc::sync_channel(CAPACITY);
        let snapshot = Arc::new(Mutex::new(None));
        let closed = Arc::new(AtomicBool::new(false));
        let mut builder = AppBuilder::new().add_plugin(MinimalGamePlugin);
        TextureStore::install(
            &mut builder,
            "missing-sample-directory",
            [(TEXTURE.id(), "sample.png".into())],
            TextureLimits::default(),
        )
        .unwrap();
        let mut app = builder
            .add_plugin(SamplePlugin {
                mode3d: false,
                receiver: Arc::new(Mutex::new(receiver)),
                snapshot: snapshot.clone(),
                closed: closed.clone(),
            })
            .build()
            .unwrap();
        app.start().unwrap();
        for (index, action) in [
            Action::Position([2.0, 3.0]),
            Action::Camera([1.0, 0.0]),
            Action::Visible(false),
            Action::Texture(false),
        ]
        .into_iter()
        .enumerate()
        {
            sender
                .try_send(Command {
                    id: index as u64 + 1,
                    action,
                })
                .unwrap();
        }
        assert!(snapshot.lock().unwrap().is_none());
        assert_eq!(
            app.world().query::<&Position>().iter().next().unwrap().x(),
            0.0
        );
        app.tick(Duration::ZERO).unwrap();
        let value = snapshot.lock().unwrap().clone().unwrap();
        assert_eq!(value["positions"], json!([[2.0, 3.0]]));
        assert_eq!(value["camera_center"], json!([1.0, 0.0]));
        assert_eq!(value["world_quads"], 0);
        assert_eq!(value["hud_quads"], 1);
        assert_eq!(value["last_applied_command"], 4);
        app.tick(Duration::ZERO).unwrap();
        assert!(
            app.world()
                .resource::<TextureStore>()
                .unwrap()
                .state(TEXTURE)
                .is_none()
        );
        sender
            .try_send(Command {
                id: 5,
                action: Action::Texture(true),
            })
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            app.tick(Duration::ZERO).unwrap();
            if snapshot.lock().unwrap().as_ref().unwrap()["texture_state"] == "failed" {
                break;
            }
            assert!(Instant::now() < deadline);
            std::thread::sleep(Duration::from_millis(2));
        }
        assert!(
            app.world().resource::<Scene2d>().unwrap().hud[0]
                .texture
                .is_none()
        );
        for (id, action) in [
            (6, Action::Retry),
            (7, Action::Retry),
            (8, Action::Camera([0.0, 0.0])),
        ] {
            sender.try_send(Command { id, action }).unwrap();
        }
        app.tick(Duration::ZERO).unwrap();
        let value = snapshot.lock().unwrap().clone().unwrap();
        assert!(value["command_error"].is_null());
        assert_eq!(
            value["command_results"]
                .as_array()
                .unwrap()
                .iter()
                .find(|v| v["command_id"] == 7)
                .unwrap()["error"],
            "NotFailed"
        );
        for id in 9..=40 {
            sender
                .try_send(Command {
                    id,
                    action: Action::Visible(false),
                })
                .unwrap();
        }
        assert!(matches!(
            sender.try_send(Command {
                id: 41,
                action: Action::Visible(true)
            }),
            Err(mpsc::TrySendError::Full(_))
        ));
        app.tick(Duration::ZERO).unwrap();
        sender
            .try_send(Command {
                id: 41,
                action: Action::Visible(true),
            })
            .unwrap();
        app.tick(Duration::ZERO).unwrap();
        let value = snapshot.lock().unwrap().clone().unwrap();
        let outcomes = value["command_results"].as_array().unwrap();
        assert_eq!(outcomes.len(), CAPACITY);
        assert_eq!(outcomes[0]["command_id"], 10);
        assert_eq!(outcomes.last().unwrap()["command_id"], 41);
        app.shutdown().unwrap();
        assert!(closed.load(Ordering::Acquire));
    }
}
