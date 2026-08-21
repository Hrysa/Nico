use std::{error::Error, time::Duration};

use nico_devtools::{DevtoolsPlugin, DevtoolsState};
use nico_presentation::Presentation;
use nico_render::RenderFrame;
use nico_runtime::{AppBuilder, Plugin, RuntimeResult, Stage};

#[derive(Default)]
struct SandboxGame {
    started: bool,
    updates: u64,
}

struct SandboxGamePlugin;

impl Plugin for SandboxGamePlugin {
    fn build(&self, app: &mut AppBuilder) -> RuntimeResult<()> {
        app.insert_resource(SandboxGame::default());
        app.add_system(Stage::Startup, "sandbox::setup_game", setup_game);
        app.add_system(Stage::Update, "sandbox::update_game", update_game);
        Ok(())
    }
}

fn setup_game(context: &mut nico_runtime::SystemContext<'_>) -> RuntimeResult<()> {
    context.world.resource_mut::<SandboxGame>()?.started = true;
    Ok(())
}

fn update_game(context: &mut nico_runtime::SystemContext<'_>) -> RuntimeResult<()> {
    let game = context.world.resource_mut::<SandboxGame>()?;
    game.updates = game.updates.saturating_add(1);
    Ok(())
}

fn main() -> Result<(), Box<dyn Error>> {
    let mut app = AppBuilder::new()
        .add_plugin(SandboxGamePlugin)
        .add_plugin(DevtoolsPlugin)
        .build()?;
    let mut presentation = Presentation::null();

    presentation.start()?;
    app.start()?;
    app.tick(Duration::from_millis(16))?;
    presentation.present(
        app.world(),
        RenderFrame {
            frame_number: 0,
            interpolation: 0.0,
        },
    )?;
    app.shutdown()?;
    presentation.shutdown()?;

    let game = app.world().resource::<SandboxGame>()?;
    let devtools = app.world().resource::<DevtoolsState>()?;
    println!(
        "Nico sandbox: started={}, updates={}, observed_frames={}.",
        game.started,
        game.updates,
        devtools.observed_frames()
    );
    Ok(())
}
