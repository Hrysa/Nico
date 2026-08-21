//! High-level rendering boundary.
//!
//! GPU resources and command APIs belong to a future concrete backend and do
//! not appear in this contract.

use std::{error::Error, fmt};

/// Presentation values associated with a render submission.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RenderFrame {
    /// Zero-based presentation frame number.
    pub frame_number: u64,
    /// Fixed-step remainder used for visual interpolation.
    pub interpolation: f64,
}

/// Provider-independent renderer failure.
#[derive(Debug, Eq, PartialEq)]
pub struct RenderError(pub String);

impl fmt::Display for RenderError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl Error for RenderError {}

/// High-level rendering provider.
pub trait Renderer: Send {
    /// Acquires backend resources.
    fn start(&mut self) -> Result<(), RenderError>;

    /// Presents one previously extracted frame.
    fn render(&mut self, frame: RenderFrame) -> Result<(), RenderError>;

    /// Releases backend resources.
    fn shutdown(&mut self) -> Result<(), RenderError>;
}

/// Renderer used by servers, tests, and the initial architecture skeleton.
#[derive(Default)]
pub struct NullRenderer {
    rendered_frames: u64,
}

impl NullRenderer {
    /// Returns the number of accepted render submissions.
    #[must_use]
    pub const fn rendered_frames(&self) -> u64 {
        self.rendered_frames
    }
}

impl Renderer for NullRenderer {
    fn start(&mut self) -> Result<(), RenderError> {
        Ok(())
    }

    fn render(&mut self, _frame: RenderFrame) -> Result<(), RenderError> {
        self.rendered_frames = self.rendered_frames.saturating_add(1);
        Ok(())
    }

    fn shutdown(&mut self) -> Result<(), RenderError> {
        Ok(())
    }
}
