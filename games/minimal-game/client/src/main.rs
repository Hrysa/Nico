use std::{error::Error, time::Duration};

use minimal_game_shared::{GameState, MinimalGamePlugin};
use nico_presentation::{Presentation, RenderFrame};
use nico_runtime::AppBuilder;

fn main() -> Result<(), Box<dyn Error>> {
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
    println!(
        "Minimal game client: fixed_updates={}, frame_updates={}.",
        state.fixed_updates(),
        state.frame_updates()
    );
    Ok(())
}
