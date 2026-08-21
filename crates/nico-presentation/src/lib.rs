//! Optional client-side presentation coordinator.
//!
//! The runtime never depends on this crate. A game client may combine it
//! with the runtime, while a dedicated server omits it entirely.

pub mod audio;
pub mod input;
pub mod render;
pub mod ui;
pub mod window;

pub use render::RenderFrame;

use std::{error::Error, fmt};

use nico_runtime::World;

use crate::{
    audio::{AudioOutput, NullAudio},
    render::{NullRenderer, Renderer},
};

/// Failure produced while coordinating presentation services.
#[derive(Debug, Eq, PartialEq)]
pub struct PresentationError(pub String);

impl fmt::Display for PresentationError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl Error for PresentationError {}

/// Coordinates optional renderer and audio providers for a client frame.
pub struct Presentation {
    renderer: Box<dyn Renderer>,
    audio: Box<dyn AudioOutput>,
    started: bool,
}

impl Presentation {
    /// Creates a presentation layer from selected providers.
    #[must_use]
    pub fn new(renderer: impl Renderer + 'static, audio: impl AudioOutput + 'static) -> Self {
        Self {
            renderer: Box::new(renderer),
            audio: Box::new(audio),
            started: false,
        }
    }

    /// Creates the no-device presentation used by the first skeleton.
    #[must_use]
    pub fn null() -> Self {
        Self::new(NullRenderer::default(), NullAudio)
    }

    /// Starts presentation providers.
    pub fn start(&mut self) -> Result<(), PresentationError> {
        if self.started {
            return Err(PresentationError(
                "presentation is already running".to_owned(),
            ));
        }
        self.renderer
            .start()
            .map_err(|error| PresentationError(error.to_string()))?;
        if let Err(error) = self.audio.start() {
            let _ = self.renderer.shutdown();
            return Err(PresentationError(error.to_string()));
        }
        self.started = true;
        Ok(())
    }

    /// Presents one frame with immutable access to authoritative state.
    ///
    /// Null providers do not query the world. Concrete presentation systems may
    /// query it directly or maintain selective caches when profiling justifies
    /// that optimization.
    pub fn present(&mut self, _world: &World, frame: RenderFrame) -> Result<(), PresentationError> {
        if !self.started {
            return Err(PresentationError("presentation is not running".to_owned()));
        }
        self.renderer
            .render(frame)
            .map_err(|error| PresentationError(error.to_string()))?;
        self.audio
            .update()
            .map_err(|error| PresentationError(error.to_string()))
    }

    /// Stops presentation providers in reverse startup order.
    pub fn shutdown(&mut self) -> Result<(), PresentationError> {
        if !self.started {
            return Err(PresentationError("presentation is not running".to_owned()));
        }
        let audio = self
            .audio
            .shutdown()
            .map_err(|error| PresentationError(error.to_string()));
        let renderer = self
            .renderer
            .shutdown()
            .map_err(|error| PresentationError(error.to_string()));
        self.started = false;
        audio.and(renderer)
    }
}

#[cfg(test)]
mod tests {
    use nico_runtime::World;

    use super::{Presentation, RenderFrame};

    #[test]
    fn null_presentation_completes_a_frame() -> Result<(), super::PresentationError> {
        let mut presentation = Presentation::null();
        presentation.start()?;
        presentation.present(
            &World::new(),
            RenderFrame {
                frame_number: 0,
                interpolation: 0.0,
            },
        )?;
        presentation.shutdown()
    }
}
