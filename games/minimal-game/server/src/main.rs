use std::time::Duration;

use minimal_game_server::FixedRateServerRunner;
use minimal_game_shared::MinimalGamePlugin;
use nico_runtime::{AppBuilder, RuntimeResult};

fn main() -> RuntimeResult<()> {
    let mut app = AppBuilder::new().add_plugin(MinimalGamePlugin).build()?;
    println!("Minimal game server running at 60 ticks per second. Press Ctrl+C to stop.");
    app.run_with(FixedRateServerRunner::new(Duration::from_nanos(16_666_667)))
}
