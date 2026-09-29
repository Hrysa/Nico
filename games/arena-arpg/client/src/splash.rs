//! Splash presentation and owned loading progress. Host lifecycle stays in nico-launch.
use crate::{Args, PreparedGame, Result};
use arena_arpg_shared::{project::StartupScene, scene::Splash};
use nico_assets::batch::Batch;
use nico_launch::client::ClientHost;
use nico_ops::mcp::{CallToolResult, Tool, ToolAccess, ToolExtensions};
use nico_presentation::{Quad, UiScene};
use nico_presentation_control::text::BitmapFont;
use nico_runtime::{AppBuilder, Stage};
use nico_winit::{NativeScene, NativeWindowState};
use serde::Serialize;
use serde_json::json;
use std::sync::{Arc, Mutex};

#[derive(Clone, Debug, Serialize)]
struct LoadingState {
    phase: &'static str,
    scene: String,
    completed: usize,
    total: usize,
    elapsed_seconds: f64,
    application_frames: u64,
    active_scene_generation: Option<u64>,
    error: Option<String>,
}
type Shared = Arc<Mutex<LoadingState>>;
// Application-owned loading survives scene cleanup. Dropping its batch cancels and joins workers.
struct Loader {
    args: Option<Args>,
    batch: Option<Batch<PreparedGame>>,
    state: Shared,
}
fn state(splash: &Splash) -> Shared {
    Arc::new(Mutex::new(LoadingState {
        phase: "splash",
        scene: splash.next_scene.display().to_string(),
        completed: 0,
        total: 0,
        elapsed_seconds: 0.,
        application_frames: 0,
        active_scene_generation: None,
        error: None,
    }))
}
fn register(tools: &mut ToolExtensions, state: Shared) -> Result<()> {
    tools.register(Tool::new("scene_loading", "Read scene loading phase and completed CPU asset counts. Loaded does not mean GPU presentation or server connection.",
        json!({"type":"object","properties":{},"additionalProperties":false}).as_object().unwrap().clone()),
        move |args| {
            if !args.is_empty() { return CallToolResult::structured_error(json!({"error":"No arguments expected"})); }
            CallToolResult::structured(json!(*state.lock().unwrap()))
        })?;
    tools.set_access("scene_loading", ToolAccess::Inspect)?;
    Ok(())
}

pub fn run(mut args: Args, entry: StartupScene) -> Result<()> {
    let splash = entry.splash.as_ref().unwrap().clone();
    args.scene = Some(splash.next_scene.clone());
    let state = state(&splash);
    let mut builder = splash_app(splash, state.clone());
    let mut prepared = Some(arena_arpg_shared::scene::registry()?.prepare(
        &entry.scene,
        nico_scene::HostRole::Client,
        |p| entry.project.resolve_asset(p),
    )?);
    builder.add_system(Stage::Startup, "splash::instantiate", move |ctx| {
        prepared.take().unwrap().instantiate(ctx.world);
        Ok(())
    });
    let mut tools = ToolExtensions::default();
    register(&mut tools, state.clone())?;
    let host = ClientHost::new(args.host.clone())
        .with_game_identity("arena_arpg", "1")
        .with_mcp_tools(tools);
    let mut root = AppBuilder::new();
    let observed = state.clone();
    root.add_system(
        Stage::Update,
        "scene_loader::application_progress",
        move |ctx| {
            observed.lock().unwrap().application_frames = ctx.time.frame_number().saturating_add(1);
            Ok(())
        },
    );
    root.insert_resource(Mutex::new(Loader {
        args: Some(args),
        batch: None,
        state,
    }));
    root.add_system(Stage::Shutdown, "scene_loader::close", |ctx| {
        ctx.world.remove_resource::<Mutex<Loader>>();
        Ok(())
    });
    host.run_scenes(
        root.build()?,
        NativeScene::new(builder.build_scene()?, |_, _: &mut Vec<()>| {}),
        crate::native_config(false),
        |app| {
            let mut loader = app.app_world().resource::<Mutex<Loader>>()?.lock().unwrap();
            let Loader { args, batch, state } = &mut *loader;
            state.lock().unwrap().active_scene_generation =
                app.scene_id().map(|id| id.generation());
            poll_loading(args, batch, state)
        },
    )?;
    Ok(())
}
fn poll_loading(
    args: &mut Option<Args>,
    batch: &mut Option<Batch<PreparedGame>>,
    state: &Shared,
) -> Result<Option<nico_launch::client::ClientScene>> {
    if state.lock().unwrap().phase == "loading"
        && let Some(args) = args.take()
    {
        let observed = state.clone();
        match Batch::start(vec![args], move |args, cancelled| {
            let observed = observed.clone();
            let _observer = nico_assets::progress::observe_progress(move |update| {
                let mut state = observed.lock().unwrap();
                state.completed = update.completed;
                state.total = update.total;
                if update.finished {
                    state.phase = "preparing";
                }
            });
            crate::prepare_game(args, cancelled.clone())
        }) {
            Ok(started) => *batch = Some(started),
            Err(error) => fail(state, error.to_string()),
        }
    }
    let Some(result) = batch.as_mut().and_then(Batch::try_next) else {
        return Ok(None);
    };
    batch.take();
    match result.and_then(|(_, prepared)| crate::compose_game(prepared)) {
        Ok(mut next) => {
            state.lock().unwrap().phase = "loaded";
            register(&mut next.tools, state.clone())?;
            Ok(Some(next))
        }
        Err(error) => {
            fail(state, error.to_string());
            Ok(None)
        }
    }
}
fn fail(state: &Shared, error: String) {
    tracing::error!(%error, "scene loading failed");
    let mut state = state.lock().unwrap();
    state.phase = "failed";
    state.error = Some(error);
}

fn splash_app(splash: Splash, state: Shared) -> AppBuilder {
    let mut builder = AppBuilder::new();
    let mut font = BitmapFont::default();
    let white = Arc::new(nico_assets::Texture::rgba8(1, 1, vec![255; 4]).unwrap());
    builder.add_system(Stage::Update, "splash::progress", move |ctx| {
        let mut state = state.lock().unwrap();
        state.elapsed_seconds = ctx.time.elapsed().as_secs_f64();
        if state.phase == "splash" && state.elapsed_seconds >= splash.duration_seconds {
            state.phase = "loading";
        }
        let size = ctx
            .world
            .resource::<NativeWindowState>()
            .map_or([1280., 720.], |s| s.logical_size);
        let mut ui = UiScene::default();
        ui.quads.push(Quad {
            center: [size[0] / 2., size[1] / 2.],
            size,
            color: [0.012, 0.018, 0.027, 1.],
            texture: Some(white.clone()),
        });
        let scale = (size[0] / 160.).clamp(2., 8.);
        let title_size = BitmapFont::measure(&splash.title);
        font.draw(
            &mut ui.quads,
            &splash.title,
            [(size[0] - title_size[0] * scale) / 2., size[1] * 0.38],
            scale,
            [0.82, 0.94, 0.88, 1.],
        );
        if state.phase != "splash" {
            let label = match state.phase {
                "failed" => "LOAD FAILED".to_owned(),
                "preparing" => "PREPARING MEADOW".to_owned(),
                _ if state.total == 0 => "LOADING MEADOW".to_owned(),
                _ => format!("IMPORTING ASSETS  {}/{}", state.completed, state.total),
            };
            let text_scale = (size[0] / 640.).clamp(1., 2.);
            let text_size = BitmapFont::measure(&label);
            font.draw(
                &mut ui.quads,
                &label,
                [(size[0] - text_size[0] * text_scale) / 2., size[1] * 0.57],
                text_scale,
                [0.65, 0.74, 0.72, 1.],
            );
            let width = (size[0] * 0.4).min(480.);
            let origin = [(size[0] - width) / 2., size[1] * 0.63];
            nico_presentation_control::text::rectangle(
                &mut ui,
                origin,
                [width, 4.],
                [0.07, 0.11, 0.12, 1.],
                white.clone(),
            );
            if state.phase == "loading" && state.total > 0 {
                let fraction = (state.completed as f32 / state.total as f32).clamp(0., 1.);
                nico_presentation_control::text::rectangle(
                    &mut ui,
                    origin,
                    [width * fraction, 4.],
                    [0.26, 0.72, 0.52, 1.],
                    white.clone(),
                );
            }
            if state.error.is_some() {
                let message = "SEE LOG FOR DETAILS";
                let extent = BitmapFont::measure(message);
                font.draw(
                    &mut ui.quads,
                    message,
                    [(size[0] - extent[0] * text_scale) / 2., size[1] * 0.69],
                    text_scale,
                    [0.9, 0.45, 0.4, 1.],
                );
            }
        }
        ctx.world.insert_resource(ui);
        Ok(())
    });
    builder
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;
    #[test]
    fn background_load_publishes_counts_and_switches_only_after_preparation() -> Result<()> {
        use clap::Parser;
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("..");
        let entry = StartupScene::load(&root, None)?;
        let splash = entry.splash.unwrap();
        let state = state(&splash);
        let mut args = Args::parse_from(["client"]);
        args.project = Some(root);
        args.scene = Some(splash.next_scene.clone());
        let mut args = Some(args);
        let mut batch = None;
        assert!(poll_loading(&mut args, &mut batch, &state)?.is_none());
        assert!(args.is_some() && batch.is_none());
        let mut app = splash_app(splash, state.clone()).build()?;
        app.start()?;
        app.tick(Duration::from_secs(1))?;
        let deadline = std::time::Instant::now() + Duration::from_secs(30);
        let next = loop {
            if let Some(next) = poll_loading(&mut args, &mut batch, &state)? {
                break next;
            }
            assert!(
                std::time::Instant::now() < deadline,
                "loading timed out: {:?}",
                state.lock().unwrap()
            );
            assert_ne!(state.lock().unwrap().phase, "failed");
            app.tick(Duration::from_millis(16))?;
            std::thread::sleep(Duration::from_millis(2));
        };
        let observed = state.lock().unwrap().clone();
        assert_eq!(observed.phase, "loaded");
        assert!(observed.total > 0);
        assert_eq!(observed.completed, observed.total);
        assert!(next.content_revision.is_none());
        assert_eq!(
            next.tools.access("scene_loading"),
            Some(ToolAccess::Inspect)
        );
        assert!(poll_loading(&mut args, &mut batch, &state)?.is_none());
        app.shutdown()?;
        Ok(())
    }
    #[test]
    fn failed_background_load_keeps_the_splash_and_error() -> Result<()> {
        use clap::Parser;
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("..");
        let entry = StartupScene::load(&root, None)?;
        let state = state(&entry.splash.unwrap());
        state.lock().unwrap().phase = "loading";
        let mut args = Args::parse_from(["client"]);
        args.project = Some(root);
        args.scene = Some("assets/scenes/missing.scene.toml".into());
        let mut args = Some(args);
        let mut batch = None;
        let deadline = std::time::Instant::now() + Duration::from_secs(5);
        while state.lock().unwrap().phase != "failed" {
            assert!(poll_loading(&mut args, &mut batch, &state)?.is_none());
            assert!(std::time::Instant::now() < deadline);
            std::thread::sleep(Duration::from_millis(1));
        }
        assert!(state.lock().unwrap().error.is_some());
        assert!(args.is_none() && batch.is_none());
        Ok(())
    }
    #[test]
    fn splash_waits_one_second_then_shows_loading_and_failure_without_entering_game() {
        let splash = Splash {
            title: "NICO".into(),
            next_scene: "assets/scenes/meadow.scene.toml".into(),
            duration_seconds: 1.,
        };
        let state = state(&splash);
        let mut app: nico_runtime::App = splash_app(splash, state.clone()).build().unwrap();
        app.start().unwrap();
        app.tick(Duration::from_millis(999)).unwrap();
        assert_eq!(state.lock().unwrap().phase, "splash");
        let splash_quads = app.world().resource::<UiScene>().unwrap().quads.len();
        app.tick(Duration::from_millis(1)).unwrap();
        assert_eq!(state.lock().unwrap().phase, "loading");
        assert!(app.world().resource::<UiScene>().unwrap().quads.len() > splash_quads);
        {
            let mut progress = state.lock().unwrap();
            progress.total = 24;
            progress.completed = 24;
        }
        app.tick(Duration::from_millis(16)).unwrap();
        let has_import_fill = |app: &nico_runtime::App| {
            app.world()
                .resource::<UiScene>()
                .unwrap()
                .quads
                .iter()
                .any(|quad| quad.color == [0.26, 0.72, 0.52, 1.])
        };
        assert!(has_import_fill(&app));
        state.lock().unwrap().phase = "preparing";
        app.tick(Duration::from_millis(16)).unwrap();
        assert!(!has_import_fill(&app));
        fail(&state, "missing model".into());
        app.tick(Duration::from_secs(1)).unwrap();
        assert_eq!(state.lock().unwrap().phase, "failed");
        assert_eq!(
            state.lock().unwrap().error.as_deref(),
            Some("missing model")
        );
        app.shutdown().unwrap();
    }
}
