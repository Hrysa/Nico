mod controls;

use std::error::Error;

use clap::Parser;
use controls::map_player_input;
use minimal_game_shared::{GameState, MinimalGamePlugin, Position};
use nico_launch::{CommonArgs, init_logging};
use nico_runtime::AppBuilder;
use nico_winit::{NativeClientConfig, run_native_client};

#[derive(Debug, Parser)]
#[command(
    name = "minimal-game-client",
    about = "Runs the minimal Nico game client"
)]
struct ClientArgs {
    #[command(flatten)]
    common: CommonArgs,

    /// Exits after presenting this many frames; intended for smoke validation.
    #[arg(long, value_parser = clap::value_parser!(u64).range(1..))]
    smoke_frames: Option<u64>,
}

fn main() -> Result<(), Box<dyn Error + Send + Sync>> {
    let args = ClientArgs::parse();
    init_logging(args.common.log_level)?;

    tracing::info!(
        smoke_frames = args.smoke_frames,
        "minimal game client starting"
    );
    let app = AppBuilder::new().add_plugin(MinimalGamePlugin).build()?;
    let config = NativeClientConfig::new("Nico minimal game").with_smoke_frames(args.smoke_frames);
    let app = run_native_client(app, config, map_player_input)?;

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

    use super::ClientArgs;

    #[test]
    fn smoke_frame_limit_is_optional_and_must_be_positive() {
        assert_eq!(
            ClientArgs::try_parse_from(["client"]).unwrap().smoke_frames,
            None
        );
        assert_eq!(
            ClientArgs::try_parse_from(["client", "--smoke-frames", "3"])
                .unwrap()
                .smoke_frames,
            Some(3)
        );
        assert!(ClientArgs::try_parse_from(["client", "--smoke-frames", "0"]).is_err());
    }
}
