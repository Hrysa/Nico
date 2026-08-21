use crate::{App, RuntimeResult};

/// Host-owned execution policy for an application.
///
/// A native client event loop, paced server loop, deterministic test runner, or
/// embedding application can implement this without changing the runtime.
pub trait AppRunner {
    /// Drives an app through startup, updates, presentation if applicable, and
    /// orderly shutdown.
    fn run(self, app: &mut App) -> RuntimeResult<()>;
}
