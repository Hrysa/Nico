use std::{error::Error, time::Duration};

use clap::Parser;
use minimal_game_server::FixedRateServerRunner;
use minimal_game_shared::MinimalGamePlugin;
use nico_launch::{CommonArgs, init_logging};
use nico_runtime::AppBuilder;

#[derive(Debug, Parser)]
#[command(
    name = "minimal-game-server",
    about = "Runs the minimal Nico dedicated server"
)]
struct ServerArgs {
    #[command(flatten)]
    common: CommonArgs,

    /// Authoritative simulation ticks per second.
    #[arg(
        long,
        default_value_t = 60,
        value_parser = clap::value_parser!(u32).range(1..)
    )]
    tick_rate: u32,
}

fn main() -> Result<(), Box<dyn Error + Send + Sync>> {
    let args = ServerArgs::parse();
    init_logging(args.common.log_level)?;

    let tick_interval = Duration::from_secs_f64(1.0 / f64::from(args.tick_rate));
    tracing::info!(tick_rate = args.tick_rate, "minimal game server starting");

    let mut app = AppBuilder::new()
        .with_fixed_step(tick_interval)
        .add_plugin(MinimalGamePlugin)
        .build()?;
    app.run_with(FixedRateServerRunner::new(tick_interval))?;
    tracing::info!("minimal game server stopped");
    Ok(())
}

#[cfg(test)]
mod tests {
    use clap::Parser;

    use super::ServerArgs;

    #[test]
    fn tick_rate_is_configurable_and_must_be_non_zero() {
        let args = ServerArgs::try_parse_from(["server", "--tick-rate", "30"])
            .expect("a positive tick rate should parse");
        assert_eq!(args.tick_rate, 30);

        assert!(ServerArgs::try_parse_from(["server", "--tick-rate", "0"]).is_err());
    }
}
