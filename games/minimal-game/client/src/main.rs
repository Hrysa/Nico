use std::{error::Error, time::Duration};

use clap::Parser;
use minimal_game_shared::{GameState, MinimalGamePlugin, Position};
use nico_launch::{CommonArgs, init_logging};
use nico_presentation::{Presentation, RenderFrame};
use nico_runtime::AppBuilder;

#[derive(Debug, Parser)]
#[command(
    name = "minimal-game-client",
    about = "Runs the minimal Nico game client"
)]
struct ClientArgs {
    #[command(flatten)]
    common: CommonArgs,
}

fn main() -> Result<(), Box<dyn Error + Send + Sync>> {
    let args = ClientArgs::parse();
    init_logging(args.common.log_level)?;

    tracing::info!("minimal game client starting");
    let mut app = AppBuilder::new().add_plugin(MinimalGamePlugin).build()?;
    let mut presentation = Presentation::null();

    presentation.start()?;
    app.start()?;
    app.tick(Duration::from_nanos(16_666_667))?;
    presentation.present(
        app.world(),
        RenderFrame {
            frame_number: 0,
            interpolation: 0.0,
        },
    )?;
    app.shutdown()?;
    presentation.shutdown()?;

    let state = app.world().resource::<GameState>()?;
    let simulated_entities = app.world().query::<&Position>().iter().count();
    tracing::info!(
        fixed_updates = state.fixed_updates(),
        frame_updates = state.frame_updates(),
        simulated_entities,
        "minimal game client stopped"
    );
    Ok(())
}
