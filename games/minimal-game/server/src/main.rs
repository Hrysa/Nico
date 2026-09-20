mod project;
mod storage;
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
    /// Saved project to load for an editor-owned play session.
    #[arg(long)]
    project: Option<std::path::PathBuf>,
    /// Optional persistence directory; play profiles provide a disposable session directory.
    #[arg(long)]
    data_dir: Option<std::path::PathBuf>,
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

    let builder = AppBuilder::new()
        .with_fixed_step(tick_interval)
        .add_plugin(MinimalGamePlugin);
    let (mut builder, mut tools) = if args.server.bridge_address().is_some() {
        let (builder, tools) = minimal_game_shared::tools::register(builder)?;
        (builder, Some(tools))
    } else {
        (builder, None)
    };
    let revision = if let Some(root) = args.project {
        let (configured, revision) = project::register(builder, &mut tools, root)?;
        builder = configured;
        Some(revision)
    } else {
        None
    };
    let mut app = builder.build()?;
    if let Some(directory) = &args.data_dir {
        storage::load(
            directory,
            app.world_mut()
                .resource_mut::<minimal_game_shared::GameState>()?,
        )?;
    }
    let mut host = ServerHost::new(args.server).with_game_identity("minimal_game", "1");
    if let Some(revision) = revision {
        host = host.with_content_revision(revision);
    }
    if let Some(tools) = tools {
        host = host.with_mcp_tools(tools);
    }
    host.run(&mut app)?;
    if let Some(directory) = &args.data_dir {
        storage::save(
            directory,
            app.world().resource::<minimal_game_shared::GameState>()?,
        )?;
    }
    tracing::info!("minimal game server stopped");
    Ok(())
}
