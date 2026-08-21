//! Authoritative physics boundary.
//!
//! Physics is a runtime capability rather than presentation so dedicated
//! servers can execute the same collision and movement rules as clients.

use std::{error::Error, fmt, time::Duration};

use nico_runtime::ecs::World;

/// Provider-independent physics failure.
#[derive(Debug, Eq, PartialEq)]
pub struct PhysicsError(pub String);

impl fmt::Display for PhysicsError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl Error for PhysicsError {}

/// Authoritative physics simulation provider.
pub trait PhysicsService: Send {
    /// Advances physics state stored in the runtime world.
    fn step(&mut self, world: &mut World, delta: Duration) -> Result<(), PhysicsError>;
}

/// Physics provider that performs no simulation.
#[derive(Default)]
pub struct NullPhysics;

impl PhysicsService for NullPhysics {
    fn step(&mut self, _world: &mut World, _delta: Duration) -> Result<(), PhysicsError> {
        Ok(())
    }
}
