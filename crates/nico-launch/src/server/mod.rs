//! Native headless host and engine-owned MCP lifecycle.
//!
//! Games supply an App and optional additional tools. This host owns control
//! channels, the service thread, disconnect handling, and the final join.

use std::{error::Error, io, net::SocketAddr, num::NonZeroU32, time::Duration};

use clap::Args;
use nico_ops::bridge::{BridgeClient, GameRegistration, GameRole};
use nico_ops::{control_channel, mcp};
use nico_runtime::App;

mod runner;
pub use runner::FixedRateServerRunner;

/// Native server policy shared by game executables.
#[derive(Args, Clone, Copy, Debug)]
pub struct ServerArgs {
    /// Authoritative simulation ticks per second.
    #[arg(long, default_value = "60")]
    pub tick_rate: NonZeroU32,

    /// Override the local bridge address (default: 127.0.0.1:47631).
    #[arg(long)]
    pub bridge: Option<SocketAddr>,

    /// Disable background bridge connection attempts.
    #[arg(long, conflicts_with = "bridge")]
    pub no_bridge: bool,
}

impl Default for ServerArgs {
    fn default() -> Self {
        Self {
            tick_rate: NonZeroU32::new(60).unwrap(),
            bridge: None,
            no_bridge: false,
        }
    }
}

impl ServerArgs {
    /// Explicit opt-out disables background bridge attempts.
    pub fn bridge_address(&self) -> Option<SocketAddr> {
        if self.no_bridge {
            None
        } else {
            Some(
                self.bridge
                    .unwrap_or_else(|| nico_ops::bridge::DEFAULT_ADDRESS.parse().unwrap()),
            )
        }
    }

    /// Fixed host step corresponding to the configured rate.
    pub fn tick_interval(&self) -> Duration {
        Duration::from_secs_f64(1.0 / f64::from(self.tick_rate.get()))
    }
}

/// Composes a game's App with engine-owned server and MCP services.
pub struct ServerHost {
    args: ServerArgs,
    tools: mcp::ToolExtensions,
    identity: Option<(String, String)>,
}

impl ServerHost {
    pub fn new(args: ServerArgs) -> Self {
        Self {
            args,
            tools: mcp::ToolExtensions::default(),
            identity: None,
        }
    }

    /// Adds game-owned operations; built-in lifecycle tools cannot be overridden.
    /// Extensions are uploaded when the host connects to the bridge.
    #[must_use]
    pub fn with_mcp_tools(mut self, tools: mcp::ToolExtensions) -> Self {
        self.tools = tools;
        self
    }

    /// Stable game identity and API revision used when registering with a bridge.
    #[must_use]
    pub fn with_game_identity(
        mut self,
        game: impl Into<String>,
        api_version: impl Into<String>,
    ) -> Self {
        self.identity = Some((game.into(), api_version.into()));
        self
    }

    /// Runs the App on this thread. MCP I/O and tool handlers run separately.
    /// After host shutdown, the bridge retains the last delivered status.
    pub fn run(self, app: &mut App) -> Result<(), Box<dyn Error + Send + Sync>> {
        let runner = FixedRateServerRunner::new(self.args.tick_interval());
        if self.args.bridge.is_some() && self.args.no_bridge {
            return Err(io::Error::other(
                "explicit bridge address conflicts with disabled bridge mode",
            )
            .into());
        }
        if let Some(address) = self.args.bridge_address() {
            let (game, version) = self
                .identity
                .ok_or_else(|| io::Error::other("bridge mode requires a game identity"))?;
            let (control, endpoint) = control_channel();
            let _bridge = BridgeClient::start(
                address,
                GameRegistration::new(game, GameRole::Server, version),
                control,
                crate::diagnostics::register(self.tools)?,
            )?;
            app.run_with(runner.with_operations(endpoint))?;
            return Ok(());
        }
        app.run_with(runner)?;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::ServerArgs;
    use clap::Parser;

    #[derive(Parser)]
    struct TestArgs {
        #[command(flatten)]
        server: ServerArgs,
    }

    #[test]
    fn server_arguments_reject_direct_mcp_and_zero_tick_rate() {
        let defaults = TestArgs::try_parse_from(["server"]).unwrap().server;
        assert_eq!(defaults.tick_rate.get(), 60);
        let args = TestArgs::try_parse_from(["server", "--tick-rate", "30"])
            .unwrap()
            .server;
        assert_eq!(args.tick_rate.get(), 30);
        assert!(TestArgs::try_parse_from(["server", "--mcp-stdio"]).is_err());
        assert!(TestArgs::try_parse_from(["server", "--tick-rate", "0"]).is_err());
        assert!(
            TestArgs::try_parse_from(["server", "--mcp-stdio", "--bridge", "127.0.0.1:47631"])
                .is_err()
        );
        assert!(
            TestArgs::try_parse_from(["server", "--bridge", "127.0.0.1:47631"])
                .unwrap()
                .server
                .bridge
                .is_some()
        );
    }
    #[test]
    fn bridge_defaults_allow_opt_out_and_custom_address() {
        let defaults = TestArgs::parse_from(["server"]).server;
        assert_eq!(
            defaults.bridge_address().unwrap().to_string(),
            nico_ops::bridge::DEFAULT_ADDRESS
        );
        assert_eq!(
            defaults.bridge_address(),
            ServerArgs::default().bridge_address()
        );
        let custom = TestArgs::parse_from(["server", "--bridge", "127.0.0.1:48000"]).server;
        assert_eq!(custom.bridge_address().unwrap().port(), 48000);
        assert!(
            TestArgs::parse_from(["server", "--no-bridge"])
                .server
                .bridge_address()
                .is_none()
        );
        assert!(
            TestArgs::try_parse_from(["server", "--no-bridge", "--bridge", "127.0.0.1:48000"])
                .is_err()
        );
    }
}
