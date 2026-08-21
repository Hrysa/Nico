use std::{error::Error, time::Duration};

use nico_presentation::Presentation;
use nico_render::RenderFrame;
use nico_runtime::AppBuilder;

fn main() -> Result<(), Box<dyn Error>> {
    let mut app = AppBuilder::new().build()?;
    let mut presentation = Presentation::null();

    presentation.start()?;
    app.start()?;
    app.tick(Duration::from_millis(16))?;
    presentation.present(
        app.world(),
        RenderFrame {
            frame_number: 0,
            interpolation: 0.0,
        },
    )?;
    app.shutdown()?;
    presentation.shutdown()?;

    println!("Nico player completed 1 frame with null presentation.");
    Ok(())
}
