//! Window capabilities and events, independent of a native window library.
//!
//! Event-loop ownership is intentionally left open until a backend is chosen.

use std::{error::Error, fmt};

/// Identity of a window owned by a window provider.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub struct WindowId(pub u64);

/// Requested properties for a new window.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct WindowConfig {
    /// Initial title.
    pub title: String,
    /// Initial logical width.
    pub width: u32,
    /// Initial logical height.
    pub height: u32,
}

impl Default for WindowConfig {
    fn default() -> Self {
        Self {
            title: "Nico".to_owned(),
            width: 1280,
            height: 720,
        }
    }
}

/// Backend-neutral window lifecycle event.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WindowEvent {
    /// The user or host requested that the window close.
    CloseRequested(WindowId),
    /// The drawable area changed size.
    Resized {
        /// Affected window.
        window: WindowId,
        /// New logical width.
        width: u32,
        /// New logical height.
        height: u32,
    },
    /// Keyboard focus changed.
    Focused(WindowId, bool),
    /// The host requested a redraw.
    RedrawRequested(WindowId),
}

/// Provider-independent window failure.
#[derive(Debug, Eq, PartialEq)]
pub struct WindowError(pub String);

impl fmt::Display for WindowError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl Error for WindowError {}

/// Capabilities exposed by a concrete window provider.
pub trait WindowService: Send {
    /// Creates a window.
    fn create(&mut self, config: &WindowConfig) -> Result<WindowId, WindowError>;

    /// Requests destruction of a window.
    fn destroy(&mut self, window: WindowId) -> Result<(), WindowError>;

    /// Changes a window title.
    fn set_title(&mut self, window: WindowId, title: &str) -> Result<(), WindowError>;

    /// Requests a redraw from the host event loop.
    fn request_redraw(&mut self, window: WindowId) -> Result<(), WindowError>;
}
