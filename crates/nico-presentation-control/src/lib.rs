//! Stateful controllers that produce immutable presentation snapshots.
//!
//! Games supply targets, tuning, input bindings, and scene geometry. Controllers
//! depend on presentation contracts without requiring the runtime lifecycle or
//! native providers. Renderers consume snapshots from `nico-presentation`.

pub mod camera;

pub mod text;

pub mod coordinates;

pub mod model;

#[cfg(feature = "scene")]
pub mod scene;
