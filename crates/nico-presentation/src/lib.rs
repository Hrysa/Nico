//! Concrete no-device presentation boundary for the minimal client.
//!
//! Input, window, renderer, audio, and UI contracts are intentionally absent
//! until concrete consumers establish their ownership and lifecycle.

use std::{error::Error, fmt};

use nico_runtime::ecs::World;

/// Presentation values associated with a frame.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RenderFrame {
    /// Zero-based presentation frame number.
    pub frame_number: u64,
    /// Fixed-step remainder used for visual interpolation.
    pub interpolation: f64,
}

/// Invalid lifecycle operation on the no-device presentation boundary.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PresentationError {
    /// Presentation was started more than once.
    AlreadyRunning,
    /// A frame or shutdown was requested before startup or after shutdown.
    NotRunning,
}

impl fmt::Display for PresentationError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::AlreadyRunning => formatter.write_str("presentation is already running"),
            Self::NotRunning => formatter.write_str("presentation is not running"),
        }
    }
}

impl Error for PresentationError {}

/// No-device presentation used by the bounded client smoke application.
#[derive(Default)]
pub struct Presentation {
    started: bool,
    presented_frames: u64,
}

impl Presentation {
    /// Creates the no-device presentation boundary.
    #[must_use]
    pub const fn null() -> Self {
        Self {
            started: false,
            presented_frames: 0,
        }
    }

    /// Starts accepting frames.
    pub fn start(&mut self) -> Result<(), PresentationError> {
        if self.started {
            return Err(PresentationError::AlreadyRunning);
        }
        self.started = true;
        Ok(())
    }

    /// Accepts one frame with immutable access to authoritative state.
    pub fn present(
        &mut self,
        _world: &World,
        _frame: RenderFrame,
    ) -> Result<(), PresentationError> {
        if !self.started {
            return Err(PresentationError::NotRunning);
        }
        self.presented_frames = self.presented_frames.saturating_add(1);
        Ok(())
    }

    /// Stops accepting frames.
    pub fn shutdown(&mut self) -> Result<(), PresentationError> {
        if !self.started {
            return Err(PresentationError::NotRunning);
        }
        self.started = false;
        Ok(())
    }

    /// Returns the number of frames accepted by this instance.
    #[must_use]
    pub const fn presented_frames(&self) -> u64 {
        self.presented_frames
    }
}

#[cfg(test)]
mod tests {
    use nico_runtime::ecs::World;

    use super::{Presentation, PresentationError, RenderFrame};

    #[test]
    fn null_presentation_enforces_lifecycle_and_counts_frames() {
        let mut presentation = Presentation::null();
        let frame = RenderFrame {
            frame_number: 0,
            interpolation: 0.0,
        };

        assert_eq!(
            presentation.present(&World::new(), frame),
            Err(PresentationError::NotRunning)
        );
        presentation.start().unwrap();
        assert_eq!(presentation.start(), Err(PresentationError::AlreadyRunning));
        presentation.present(&World::new(), frame).unwrap();
        presentation.shutdown().unwrap();

        assert_eq!(presentation.presented_frames(), 1);
    }
}
