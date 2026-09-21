//! Immutable drawing contracts. The optional runtime lifecycle extracts owned
//! snapshots; renderers can use the contracts without depending on runtime.

/// Quaternion rotation in XYZW order. Published orientations must be finite and normalized.
pub use glam::Quat as Quaternion;

pub mod foliage;
mod instances;
pub use instances::{
    InstanceBatch, InstanceBounds, InstanceError, InstanceRecord, MAX_BATCH_INSTANCES,
};

mod scene;
pub use scene::{Camera2d, Camera3d, MeshInstance, Quad, Scene2d, Scene3d, SceneLighting, UiScene};

#[cfg(feature = "runtime")]
mod lifecycle;
#[cfg(feature = "runtime")]
pub use lifecycle::{Presentation, PresentationError, RenderFrame};
