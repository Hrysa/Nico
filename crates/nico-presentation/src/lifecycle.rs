//! World-facing presentation boundary for the minimal client.
//!
//! The native host currently coordinates `nico-render` over `nico-rhi`
//! alongside this immutable world-facing lifecycle. Rendering resources do not
//! enter the authoritative runtime boundary.

use std::{error::Error, fmt};

use nico_runtime::ecs::World;

use crate::{Scene2d, Scene3d};

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
    scene: Scene2d,
    scene3d: Scene3d,
}

impl Presentation {
    /// Creates the no-device presentation boundary.
    #[must_use]
    pub fn null() -> Self {
        Self {
            started: false,
            presented_frames: 0,
            scene: Scene2d::default(),
            scene3d: Scene3d::default(),
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
    pub fn present(&mut self, world: &World, _frame: RenderFrame) -> Result<(), PresentationError> {
        if !self.started {
            return Err(PresentationError::NotRunning);
        }
        self.presented_frames = self.presented_frames.saturating_add(1);
        self.scene = world.resource::<Scene2d>().cloned().unwrap_or_default();
        self.scene3d = world.resource::<Scene3d>().cloned().unwrap_or_default();
        Ok(())
    }

    /// Stops accepting frames.
    pub fn shutdown(&mut self) -> Result<(), PresentationError> {
        if !self.started {
            return Err(PresentationError::NotRunning);
        }
        self.started = false;
        self.scene = Scene2d::default();
        self.scene3d = Scene3d::default();
        Ok(())
    }

    /// Returns the number of frames accepted by this instance.
    #[must_use]
    pub const fn presented_frames(&self) -> u64 {
        self.presented_frames
    }

    /// Immutable owned draw snapshot from the last accepted runtime frame.
    #[must_use]
    pub fn scene(&self) -> &Scene2d {
        &self.scene
    }
    #[must_use]
    pub fn scene3d(&self) -> &Scene3d {
        &self.scene3d
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
