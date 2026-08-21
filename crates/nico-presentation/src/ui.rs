//! High-level UI layer boundary.
//!
//! Widget and layout APIs are intentionally deferred until the UI strategy is
//! chosen. This contract only establishes lifecycle and world access.

use std::{error::Error, fmt};

use nico_runtime::ecs::World;

/// Provider-independent UI failure.
#[derive(Debug, Eq, PartialEq)]
pub struct UiError(pub String);

impl fmt::Display for UiError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl Error for UiError {}

/// One presentation-side UI layer.
pub trait UiLayer: Send {
    /// Updates UI state using immutable authoritative state.
    fn update(&mut self, world: &World) -> Result<(), UiError>;
}
