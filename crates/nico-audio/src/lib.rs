//! Audio presentation boundary.

use std::{error::Error, fmt};

/// Provider-independent audio failure.
#[derive(Debug, Eq, PartialEq)]
pub struct AudioError(pub String);

impl fmt::Display for AudioError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl Error for AudioError {}

/// High-level audio output provider.
pub trait AudioOutput: Send {
    /// Acquires backend resources.
    fn start(&mut self) -> Result<(), AudioError>;

    /// Commits presentation-side audio changes for one frame.
    fn update(&mut self) -> Result<(), AudioError>;

    /// Releases backend resources.
    fn shutdown(&mut self) -> Result<(), AudioError>;
}

/// Silent audio provider used when no output device is desired.
#[derive(Default)]
pub struct NullAudio;

impl AudioOutput for NullAudio {
    fn start(&mut self) -> Result<(), AudioError> {
        Ok(())
    }

    fn update(&mut self) -> Result<(), AudioError> {
        Ok(())
    }

    fn shutdown(&mut self) -> Result<(), AudioError> {
        Ok(())
    }
}
