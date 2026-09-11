mod controls;

use std::error::Error;

use clap::Parser;
use controls::map_player_input;
use minimal_game_shared::{GameState, MinimalGamePlugin, Position};
use nico_launch::client::{ClientArgs as HostArgs, ClientHost};
use nico_launch::{CommonArgs, init_logging};
use nico_runtime::AppBuilder;
use nico_winit::NativeClientConfig;

#[derive(Debug, Parser)]
#[command(
    name = "minimal-game-client",
    about = "Runs the minimal Nico game client"
)]
struct GameArgs {
    #[command(flatten)]
    common: CommonArgs,

    #[command(flatten)]
    host: HostArgs,
}

fn main() -> Result<(), Box<dyn Error + Send + Sync>> {
    let args = GameArgs::parse();
    init_logging(args.common.log_level)?;

    tracing::info!(
        smoke_frames = args.host.smoke_frames,
        "minimal game client starting"
    );
    let builder = AppBuilder::new().add_plugin(MinimalGamePlugin);
    let (builder, tools) = if args.host.bridge_address().is_some() {
        let (builder, tools) = minimal_game_shared::tools::register(builder)?;
        (builder, Some(tools))
    } else {
        (builder, None)
    };
    let app = builder.build()?;
    let config = NativeClientConfig::new(
        "Nico minimal game",
        "assets/presentation/shaders/generated/wgpu/bootstrap.wgsl",
    );
    let mut host = ClientHost::new(args.host).with_game_identity("minimal_game", "1");
    if let Some(tools) = tools {
        host = host.with_mcp_tools(tools);
    }
    let app = host.run(app, config, map_player_input)?;

    let state = app.world().resource::<GameState>()?;
    let simulated_entities = app.world().query::<&Position>().iter().count();
    tracing::info!(
        fixed_updates = state.fixed_updates(),
        frame_updates = state.frame_updates(),
        movement_quest_completed = state.movement_quest_completed(),
        stamina = state.stamina(),
        coins = state.coins(),
        simulated_entities,
        "minimal game client stopped"
    );
    Ok(())
}

#[cfg(test)]
mod tests {
    use clap::Parser;

    use super::GameArgs;

    #[test]
    fn smoke_frame_limit_is_optional_and_must_be_positive() {
        assert_eq!(
            GameArgs::try_parse_from(["client"])
                .unwrap()
                .host
                .smoke_frames,
            None
        );
        assert_eq!(
            GameArgs::try_parse_from(["client", "--smoke-frames", "3"])
                .unwrap()
                .host
                .smoke_frames,
            Some(3)
        );
        assert!(GameArgs::try_parse_from(["client", "--smoke-frames", "0"]).is_err());
        assert!(
            GameArgs::try_parse_from(["client", "--bridge", "127.0.0.1:47631"])
                .unwrap()
                .host
                .bridge
                .is_some()
        );
        assert!(GameArgs::try_parse_from(["client", "--mcp-stdio"]).is_err());
    }
}
