use std::time::Duration;

use nico_runtime::{AppBuilder, RuntimeResult, Stage};

fn main() -> RuntimeResult<()> {
    let mut builder = AppBuilder::new();
    builder.add_system(Stage::Update, "server::update", |_context| Ok(()));
    let mut app = builder.build()?;
    app.run_for_frames(3, Duration::from_millis(16))?;
    println!("Nico server completed 3 headless frames.");
    Ok(())
}
