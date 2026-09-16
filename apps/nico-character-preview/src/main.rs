//! Standalone native GPU-skinned model preview using the normal engine host.
mod controls;
mod frame_rate;
mod model;
use clap::Parser;
use glam::Vec3;
use model::{Character, CharacterAssets, spawn_character};
use nico_launch::{
    CommonArgs,
    client::{ClientArgs, ClientHost},
    init_logging,
};
use nico_presentation::{Camera3d, Scene2d, Scene3d};
use nico_presentation_control::text::BitmapFont;
use nico_runtime::{AppBuilder, RuntimeError, Stage, events::EventReader};
use nico_winit::NativeClientConfig;
use serde_json::json;
use std::{
    path::PathBuf,
    sync::{Arc, Mutex},
};
type Result<T> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;
#[derive(Parser)]
struct Args {
    #[command(flatten)]
    common: CommonArgs,
    #[command(flatten)]
    host: ClientArgs,
    /// Local GLB to preview; never copied into shipping assets.
    #[arg(long)]
    model: PathBuf,
    /// RPG skeleton GLB retargeted to the Mixamo model; repeat for multiple files.
    #[arg(long)]
    animation: Vec<PathBuf>,
    /// Start paused at the reference pose.
    #[arg(long)]
    bind_pose: bool,
    /// Play selected clips once and hold their final pose.
    #[arg(long)]
    once: bool,
    /// Shared-asset character instances, bounded by the renderer draw budget.
    #[arg(long, default_value_t = 1, value_parser = clap::value_parser!(u16).range(1..=64))]
    characters: u16,
    /// Pose evaluation cap; zero evaluates every Update. Playback time is unchanged.
    #[arg(long, default_value_t = 0, value_parser = clap::value_parser!(u16).range(0..=120))]
    update_hz: u16,
}
fn runtime_error(error: impl std::fmt::Display) -> RuntimeError {
    RuntimeError::System {
        stage: "Update",
        name: "character preview".into(),
        message: error.to_string(),
    }
}
fn main() -> Result<()> {
    let args = Args::parse();
    init_logging(args.common.log_level)?;
    let assets = Arc::new(CharacterAssets::load(&args.model, &args.animation)?);
    let count = usize::from(args.characters);
    if assets.primitive_count() * count > 256 {
        return Err("character count exceeds 256 total model draws".into());
    }
    let interval = if args.update_hz == 0 {
        std::time::Duration::ZERO
    } else {
        std::time::Duration::from_secs_f64(1. / f64::from(args.update_hz))
    };
    let queue = Arc::new(Mutex::new(controls::Queue::new(32)));
    let publication = Arc::new(Mutex::new(nico_ops::publication::Publication::default()));
    let tools = controls::register(queue.clone(), publication.clone())?;
    let mut builder = AppBuilder::new();
    builder.insert_resource(Scene3d::default());
    builder.insert_resource(Scene2d::default());
    let initial_bind = args.bind_pose;
    let play_mode = if args.once {
        nico_animation::playback::PlayMode::Once
    } else {
        nico_animation::playback::PlayMode::Loop
    };
    builder.add_system(Stage::Startup, "preview::spawn", move |ctx| {
        let columns = (count as f32).sqrt().ceil() as usize;
        for id in 0..count {
            let entity = spawn_character(ctx.world, assets.clone()).map_err(runtime_error)?;
            let mut character = ctx.world.entities().get::<&mut Character>(entity).unwrap();
            character.id = id;
            character.position = [
                ((id % columns) as f32 - (columns - 1) as f32 * 0.5) * 2.5,
                0.,
                (id / columns) as f32 * -2.5,
            ];
            character.evaluation_interval = interval;
            character.bind_pose = initial_bind;
            character.play_mode = play_mode;
            character.player.set_mode(play_mode);
            if !args.once && character.player.clip().is_some() {
                let offset = character.duration() * id as f64 / count as f64;
                character.player.seek(offset).map_err(runtime_error)?;
            }
            character.player.set_paused(initial_bind);
        }
        Ok(())
    });
    let mut input = EventReader::<controls::Input>::new();
    let mut camera = controls::Orbit::default();
    let mut font = BitmapFont::default();
    let mut selected = 0usize;
    let mut last_camera = None;
    let mut framed = false;
    let mut frame_rate = frame_rate::FrameRate::default();
    let q = queue.clone();
    let p = publication.clone();
    builder.add_system(Stage::Update, "preview::extract", move |ctx| {
        frame_rate.observe(std::time::Instant::now());
        let mut query = ctx.world.query::<&mut Character>();
        let mut characters: Vec<_> = query.iter().collect();
        characters.sort_by_key(|c| c.id);
        if characters.is_empty() { return Err(runtime_error("missing characters")); }
        if !framed {
            let mut low = Vec3::splat(f32::INFINITY);
            let mut high = Vec3::splat(f32::NEG_INFINITY);
            for c in &mut characters {
                c.draws().map_err(runtime_error)?;
                let bounds = c.bounds().map_err(runtime_error)?;
                low = low.min(bounds.min.into()); high = high.max(bounds.max.into());
            }
            camera.target = (low + high) * 0.5;
            camera.distance = ((high - low).length().max(0.1) * 1.5).min(90.);
            framed = true;
        }
        for input in ctx.events.read(&mut input) {
            let c = &mut characters[selected];
            if input.toggle || input.reset || input.clip_step != 0 {
                c.scheduled_advance(std::time::Duration::ZERO, true).map_err(runtime_error)?;
                if input.toggle { c.player.set_paused(!c.player.paused()); }
                if input.reset && c.player.clip().is_some() { c.player.seek(0.).map_err(runtime_error)?; }
                if input.clip_step != 0 && !c.assets.clips().is_empty() {
                    let next = (c.player.clip().unwrap_or(0) as isize + isize::from(input.clip_step)).rem_euclid(c.assets.clips().len() as isize) as usize;
                    controls::apply(controls::Action::Clip(next), c, &mut camera).map_err(runtime_error)?;
                }
                c.invalidate();
            }
            camera.yaw += input.orbit[0] * ctx.time.delta().as_secs_f32();
            camera.pitch = (camera.pitch + input.orbit[1] * ctx.time.delta().as_secs_f32()).clamp(-1.4, 1.4);
            camera.distance = (camera.distance * (1. + input.zoom * ctx.time.delta().as_secs_f32())).clamp(0.1, 100.);
        }
        {
            let mut q = q.lock().unwrap();
            while let Some((id, action)) = q.pop() {
                let result = if let controls::Action::Select(index) = action {
                    if index < characters.len() { selected = index; Ok(()) } else { Err("instance index out of range".into()) }
                } else {
                    let c = &mut characters[selected];
                    c.scheduled_advance(std::time::Duration::ZERO, true).map_err(runtime_error)?;
                    let result = controls::apply(action, c, &mut camera);
                    if result.is_ok() { c.invalidate(); }
                    result
                };
                q.record(json!({"command_id": id, "error": result.err()}));
            }
        }
        let camera_key = (camera.yaw, camera.pitch, camera.distance, camera.target.to_array());
        let camera_changed = last_camera != Some(camera_key);
        last_camera = Some(camera_key);
        let position = camera.target + Vec3::new(camera.yaw.sin() * camera.pitch.cos(), camera.pitch.sin(), camera.yaw.cos() * camera.pitch.cos()) * camera.distance;
        let mut scene = Scene3d {
            camera: Camera3d::looking_at(position.to_array(), camera.target.to_array(), [0., 1., 0.]).ok_or_else(|| runtime_error("invalid preview camera"))?,
            meshes: Vec::new(),
        };
        let window = ctx.world.resource::<nico_winit::NativeWindowState>().cloned().unwrap_or_default();
        let view_projection = scene.camera.view_projection(window.logical_size[0] / window.logical_size[1]);
        let mut instances = Vec::with_capacity(characters.len());
        let mut evaluated = 0usize;
        let mut visible_count = 0usize;
        for c in &mut characters {
            evaluated += usize::from(c.scheduled_advance(ctx.time.delta(), camera_changed).map_err(runtime_error)?);
            c.draws().map_err(runtime_error)?;
            let bounds = c.bounds().map_err(runtime_error)?;
            let visible = view_projection.is_none_or(|matrix| bounds.intersects_clip(matrix));
            if visible { visible_count += 1; scene.meshes.extend_from_slice(c.draws().map_err(runtime_error)?); }
            instances.push(json!({"id":c.id,"position":c.position,"clip":c.player.clip(),"time":c.player.time(),"pending_seconds":c.pending.as_secs_f64(),"playing":!c.player.paused(),"finished":c.player.finished(),"visible":visible,"render_bounds":{"min":bounds.min,"max":bounds.max},"update_hz":if c.evaluation_interval.is_zero() {0.} else {1./c.evaluation_interval.as_secs_f64()}}));
        }
        let character = &characters[selected];
        let bounds = character.bounds().map_err(runtime_error)?;
        let visible = instances[selected]["visible"].as_bool().unwrap();
        let name = character.player.clip().and_then(|i| character.assets.clips().get(i)).map_or("REFERENCE", |c| c.name());
        let mut hud = Scene2d::default();
        let heading = format!(
            "GPU SKIN PREVIEW  {}\n{}\nSPACE PLAY PAUSE   R RESTART\nARROWS ORBIT   W S ZOOM   A D CLIP\n{}  CLIP {}  TIME {:.0} / {:.0} MS",
            if !character.player.paused() { "PLAY" } else { "PAUSE" },
            frame_rate.hud(),
            if character.play_mode == nico_animation::playback::PlayMode::Loop { "LOOP" } else if character.player.finished() { "ONCE DONE" } else { "ONCE" },
            character.player.clip().unwrap_or(0), character.player.time() * 1000., character.duration() * 1000.,
        );
        let heading = format!("{heading}\nCHAR {} OF {}   VISIBLE {}   EVALUATED {}", selected + 1, count, visible_count, evaluated);
        font.draw(&mut hud.hud, &heading, [16., 16.], 2., [1.; 4]);
        let clips: Vec<_> = character.assets.clips().iter().enumerate()
            .map(|(i,c)| json!({"index": i, "name": c.name(), "duration": c.duration()})).collect();
        let value = json!({
            "frame": ctx.time.frame_number(),
            "selected":selected, "instance_count":count, "visible_count":visible_count, "evaluated_instances":evaluated, "instances":instances,
            "frame_timing": frame_rate.json(),
            "mode": "gpu_skinned_preview",
            "clip": character.player.clip(),
            "clip_name": name,
            "clips": clips,
            "time": character.player.time(),
            "duration": character.duration(),
            "playing": !character.player.paused(),
            "speed": character.player.speed(),
            "looping": character.play_mode == nico_animation::playback::PlayMode::Loop,
            "finished": character.player.finished(),
            "fade_seconds": character.fade_seconds,
            "fade_weight": character.player.fade_weight(),
            "in_place": character.in_place,
            "bind_pose": character.bind_pose,
            "retargeted": character.assets.retargeted,
            "draws": scene.meshes.len(),
            "visible":visible,
            "render_bounds": {"min":bounds.min, "max":bounds.max},
            "camera": {"yaw": camera.yaw, "pitch": camera.pitch, "distance": camera.distance, "target":camera.target.to_array()},
            "omitted_extension_count": character.assets.model.data().omitted_extensions.len(),
            "omitted_extensions": character.assets.model.data().omitted_extensions.iter().take(32).map(|name| name.chars().take(64).collect::<String>()).collect::<Vec<_>>(),
            "omitted_extensions_truncated":character.assets.model.data().omitted_extensions.len()>32 || character.assets.model.data().omitted_extensions.iter().take(32).any(|name|name.chars().count()>64),
            "command_results": q.lock().unwrap().history(),
        });
        drop(characters);
        drop(query);
        *ctx.world.resource_mut::<Scene3d>()? = scene;
        *ctx.world.resource_mut::<Scene2d>()? = hud;
        p.lock().unwrap().publish(value);
        Ok(())
    });
    builder.add_system(Stage::Shutdown, "preview::release", move |ctx| {
        let mut q = queue.lock().unwrap();
        q.close_with(|(id, _)| {
            json!({"command_id":id,
"error":"shutdown"})
        });
        let mut p = publication.lock().unwrap();
        if let Some(mut value) = p.get().cloned() {
            value["command_results"] = json!(q.history());
            p.publish(value);
        }
        p.close();
        let entities: Vec<_> = ctx
            .world
            .query::<(nico_ecs::Entity, &Character)>()
            .iter()
            .map(|(e, _)| e)
            .collect();
        for e in entities {
            ctx.world.despawn(e).map_err(runtime_error)?;
        }
        *ctx.world.resource_mut::<Scene3d>()? = Scene3d::default();
        *ctx.world.resource_mut::<Scene2d>()? = Scene2d::default();
        Ok(())
    });
    let config = NativeClientConfig::new(
        "Nico character preview",
        "assets/presentation/shaders/generated/wgpu/bootstrap.wgsl",
    )
    .with_mesh_shaders(
        "assets/presentation/shaders/generated/wgpu/meshes.wgsl",
        "assets/presentation/shaders/generated/wgpu/quads.wgsl",
    )
    .with_skin_shader("assets/presentation/shaders/generated/wgpu/skinned_meshes.wgsl");
    ClientHost::new(args.host)
        .with_game_identity("character_preview", "1")
        .with_mcp_tools(tools)
        .run(builder.build()?, config, controls::map_input)?;
    Ok(())
}

#[cfg(test)]
mod cli_tests {
    use super::*;
    #[test]
    fn crowd_and_update_rate_limits_are_validated_before_loading() {
        for (flag, value) in [
            ("--characters", "0"),
            ("--characters", "65"),
            ("--update-hz", "121"),
        ] {
            assert!(
                Args::try_parse_from(["preview", "--model", "unused.glb", flag, value]).is_err()
            );
        }
        let args = Args::try_parse_from([
            "preview",
            "--model",
            "unused.glb",
            "--characters",
            "64",
            "--update-hz",
            "15",
        ])
        .unwrap();
        assert_eq!(args.characters, 64);
        assert_eq!(args.update_hz, 15);
    }
}
