//! Immutable drawing contracts. The optional runtime lifecycle extracts owned
//! snapshots; renderers can use the contracts without depending on runtime.

mod scene;
pub use scene::{Camera2d, Camera3d, MeshInstance, Quad, Scene2d, Scene3d};

#[cfg(feature = "runtime")]
mod lifecycle;
#[cfg(feature = "runtime")]
pub use lifecycle::{Presentation, PresentationError, RenderFrame};
