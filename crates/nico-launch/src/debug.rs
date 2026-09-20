//! Native debug startup policy shared by client and server hosts.

use std::{net::SocketAddr, path::PathBuf};

use clap::Args;

#[derive(Args, Clone, Debug, Default)]
pub struct DebugArgs {
    /// Explicitly enable debug operations in a release executable.
    #[arg(long, conflicts_with = "no_bridge")]
    pub enable_debug: bool,
    /// Private host-local JSON credentials and grants; reread to enforce revocation.
    #[arg(long, requires = "enable_debug")]
    pub debug_access_file: Option<PathBuf>,
}

impl DebugArgs {
    pub(crate) fn address(
        &self,
        address: Option<SocketAddr>,
        disabled: bool,
    ) -> Option<SocketAddr> {
        self.address_for_build(address, disabled, cfg!(debug_assertions))
    }

    fn address_for_build(
        &self,
        address: Option<SocketAddr>,
        disabled: bool,
        development: bool,
    ) -> Option<SocketAddr> {
        if disabled || (!development && !self.enable_debug) {
            None
        } else {
            Some(address.unwrap_or_else(|| nico_ops::bridge::DEFAULT_ADDRESS.parse().unwrap()))
        }
    }

    pub(crate) fn access(&self) -> nico_ops::bridge::DebugAccess {
        let access = nico_ops::bridge::DebugAccess::default();
        match &self.debug_access_file {
            Some(path) => access.with_policy_file(path.clone()),
            None => access,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn release_requires_explicit_enable_even_with_a_custom_bridge_address() {
        let address = "127.0.0.1:48000".parse().unwrap();
        let mut args = DebugArgs::default();
        assert_eq!(args.address_for_build(Some(address), false, false), None);
        assert_eq!(
            args.address_for_build(Some(address), false, true),
            Some(address)
        );
        args.enable_debug = true;
        assert_eq!(
            args.address_for_build(Some(address), false, false),
            Some(address)
        );
        assert_eq!(args.address_for_build(Some(address), true, false), None);
    }
}
