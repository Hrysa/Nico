use std::error::Error;

use clap::Parser;
use minimal_game_shared::MinimalGamePlugin;
use nico_launch::server::{ServerArgs, ServerHost};
use nico_launch::{CommonArgs, init_logging};
use nico_runtime::AppBuilder;

#[derive(Debug, Parser)]
#[command(
    name = "minimal-game-server",
    about = "Runs the minimal Nico dedicated server"
)]
struct GameArgs {
    #[command(flatten)]
    common: CommonArgs,

    #[command(flatten)]
    server: ServerArgs,
}

fn main() -> Result<(), Box<dyn Error + Send + Sync>> {
    let args = GameArgs::parse();
    init_logging(args.common.log_level)?;

    let tick_interval = args.server.tick_interval();
    tracing::info!(
        tick_rate = args.server.tick_rate.get(),
        "minimal game server starting"
    );

    let mut app = AppBuilder::new()
        .with_fixed_step(tick_interval)
        .add_plugin(MinimalGamePlugin)
        .build()?;
    ServerHost::new(args.server).run(&mut app)?;
    tracing::info!("minimal game server stopped");
    Ok(())
}
