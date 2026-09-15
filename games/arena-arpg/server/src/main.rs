use arena_arpg_shared::{ArenaPlugin, FIXED_STEP};
use clap::Parser;
use nico_launch::{
    CommonArgs, init_logging,
    server::{ServerArgs, ServerHost},
};
use nico_runtime::AppBuilder;
#[derive(Parser)]
#[command(about = "Headless arena ARPG combat host (local simulation, not multiplayer)")]
struct Args {
    #[command(flatten)]
    common: CommonArgs,
    #[command(flatten)]
    host: ServerArgs,
}
fn main() -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let args = Args::parse();
    if args.host.tick_rate.get() != 60 {
        return Err("arena combat requires --tick-rate 60".into());
    }
    init_logging(args.common.log_level)?;
    let (builder, tools) = arena_arpg_shared::tools::register(
        AppBuilder::new()
            .with_fixed_step(FIXED_STEP)
            .add_plugin(ArenaPlugin),
    )?;
    let mut app = builder.build()?;
    tracing::info!("arena server starting");
    ServerHost::new(args.host)
        .with_game_identity("arena_arpg", "1")
        .with_mcp_tools(tools)
        .run(&mut app)?;
    tracing::info!("arena server stopped");
    Ok(())
}
