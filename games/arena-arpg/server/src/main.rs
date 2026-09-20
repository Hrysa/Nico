use arena_arpg_shared::{ArenaPlugin, FIXED_STEP};
use clap::Parser;
use nico_launch::{
    CommonArgs, init_logging,
    server::{ServerArgs, ServerHost},
};
use nico_runtime::AppBuilder;
#[derive(Parser)]
#[command(about = "Arena combat test host and persistent multiplayer world server")]
struct Args {
    /// Saved Arena project; loads its authoritative world and character assets.
    #[arg(long, conflicts_with_all = ["arena", "world_asset", "item_asset", "logic_characters"])]
    project: Option<std::path::PathBuf>,
    #[command(flatten)]
    common: CommonArgs,
    /// Run the standalone arena combat test instead of the multiplayer world.
    #[arg(long)]
    arena: bool,
    #[arg(long, default_value = "127.0.0.1:47640")]
    listen: std::net::SocketAddr,
    #[arg(long, default_value = "target/world-data")]
    data_dir: std::path::PathBuf,
    #[arg(long, default_value = arena_arpg_shared::open_world::content::DEFAULT_WORLD)]
    world_asset: std::path::PathBuf,
    #[arg(long, default_value = arena_arpg_shared::open_world::content::DEFAULT_ITEM)]
    item_asset: std::path::PathBuf,
    #[arg(long, default_value = arena_arpg_shared::characters::DEFAULT_LOGIC_ROOT)]
    logic_characters: std::path::PathBuf,
    #[command(flatten)]
    host: ServerArgs,
}
fn main() -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let mut args = Args::parse();
    if args.host.tick_rate.get() != 60 {
        return Err("game simulation requires --tick-rate 60".into());
    }
    init_logging(args.common.log_level)?;
    let content = args
        .project
        .as_deref()
        .map(arena_arpg_shared::project::ProjectContent::open)
        .transpose()?;
    if let Some(content) = &content {
        args.logic_characters = content.asset("assets/logic/characters")?;
        args.world_asset = content.source("logic")?;
        args.item_asset = content.asset("assets/logic/items/iron-sword.item.toml")?;
    }
    let characters = arena_arpg_shared::characters::CharacterCatalog::load(&args.logic_characters)?;
    let (builder, tools) = if !args.arena {
        use arena_arpg_shared::open_world::{
            OpenWorld,
            content::{ItemDefinition, ZoneDefinition},
            server::WorldServer,
        };
        let world = OpenWorld::with_content(
            characters,
            ZoneDefinition::load(&args.world_asset)?,
            ItemDefinition::load(&args.item_asset)?,
        )?;
        let server = WorldServer::bind(args.listen, &args.data_dir, world)?;
        tracing::info!(address=%server.address()?, "world server listening");
        arena_arpg_shared::open_world::runtime::register(
            AppBuilder::new().with_fixed_step(FIXED_STEP),
            server,
        )?
    } else {
        arena_arpg_shared::tools::register(
            AppBuilder::new()
                .with_fixed_step(FIXED_STEP)
                .add_plugin(ArenaPlugin::with_characters(characters)),
        )?
    };
    let mut app = builder.build()?;
    tracing::info!(arena = args.arena, "game server starting");
    let mut host = ServerHost::new(args.host)
        .with_game_identity("arena_arpg", "1")
        .with_mcp_tools(tools);
    if let Some(content) = &content {
        content.verify()?;
        host = host.with_content_revision(content.revision.clone());
    }
    host.run(&mut app)?;
    tracing::info!("game server stopped");
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn world_is_default_and_arena_is_explicit() {
        let args = Args::try_parse_from(["server"]).unwrap();
        assert!(!args.arena);
        assert_eq!(args.listen, "127.0.0.1:47640".parse().unwrap());
        assert!(Args::try_parse_from(["server", "--arena"]).unwrap().arena);
    }
}
