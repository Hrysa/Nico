//! Local MCP bridge for independently launched games.
//!
//! The bridge never launches or owns game processes. Native hosts default to a loopback
//! connection, upload schemas, and keep running when that connection disappears.
//! Tool schemas are cached for the bridge's lifetime, not persisted to disk.

mod client;
mod server;
mod wire;

pub use client::{BridgeClient, GameRegistration};
pub use server::serve_stdio;
pub use wire::GameRole;

use std::{io, net::SocketAddr};

/// Default local game-registration endpoint. MCP itself uses the bridge's stdio.
pub const DEFAULT_ADDRESS: &str = "127.0.0.1:47631";

fn check_address(address: SocketAddr) -> io::Result<()> {
    if !address.ip().is_loopback() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "bridge address must be loopback",
        ));
    }
    Ok(())
}
