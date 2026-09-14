//! Native client composition with default engine-owned bridge transport and opt-out.

use clap::Args;
use nico_input::InputState;
use nico_ops::{
    bridge::{BridgeClient, GameRegistration, GameRole},
    control_channel,
    mcp::ToolExtensions,
};
use nico_runtime::{App, events::Event};
use nico_winit::{
    NativeClientConfig, NativeClientResult, run_native_client, run_native_client_with_operations,
};
use std::{io, net::SocketAddr};

#[derive(Args, Clone, Copy, Debug, Default)]
pub struct ClientArgs {
    /// Exit after this many client-session frames, including skipped GPU presentations.
    #[arg(long, value_parser = clap::value_parser!(u64).range(1..))]
    pub smoke_frames: Option<u64>,
    /// Override the local bridge address (default: 127.0.0.1:47631).
    #[arg(long)]
    pub bridge: Option<SocketAddr>,

    /// Disable background bridge connection attempts.
    #[arg(long, conflicts_with = "bridge")]
    pub no_bridge: bool,
}

impl ClientArgs {
    /// Effective address; absence of a bridge never prevents game execution.
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
}

pub struct ClientHost {
    args: ClientArgs,
    identity: Option<(String, String)>,
    tools: ToolExtensions,
}

impl ClientHost {
    pub fn new(args: ClientArgs) -> Self {
        Self {
            args,
            identity: None,
            tools: ToolExtensions::default(),
        }
    }
    #[must_use]
    pub fn with_game_identity(
        mut self,
        game: impl Into<String>,
        api_version: impl Into<String>,
    ) -> Self {
        self.identity = Some((game.into(), api_version.into()));
        self
    }
    /// Game handlers remain game-owned; the engine owns transport and reconnects.
    #[must_use]
    pub fn with_mcp_tools(mut self, tools: ToolExtensions) -> Self {
        self.tools = tools;
        self
    }
    /// Runs Winit on this thread. Bridge I/O runs separately and survives reconnects.
    pub fn run<C: Event>(
        self,
        app: App,
        config: NativeClientConfig,
        map_input: impl FnMut(&InputState, &mut Vec<C>) + 'static,
    ) -> NativeClientResult<App> {
        let config = config.with_smoke_frames(self.args.smoke_frames);
        let Some(address) = self.args.bridge_address() else {
            return run_native_client(app, config, map_input);
        };
        let (game, version) = self
            .identity
            .ok_or_else(|| io::Error::other("bridge mode requires a game identity"))?;
        let (control, endpoint) = control_channel();
        control.snapshots().enable();
        let tools = crate::snapshot::register(self.tools, control.clone())?;
        let _bridge = BridgeClient::start(
            address,
            GameRegistration::new(game, GameRole::Client, version),
            control,
            crate::diagnostics::register(tools)?,
        )?;
        run_native_client_with_operations(app, config, map_input, endpoint)
    }
}

#[cfg(test)]
mod tests {
    use super::ClientArgs;
    use clap::Parser;

    #[derive(Parser)]
    struct TestArgs {
        #[command(flatten)]
        client: ClientArgs,
    }

    #[test]
    fn bridge_defaults_can_be_overridden_or_disabled() {
        let defaults = TestArgs::parse_from(["client"]).client;
        assert_eq!(
            defaults.bridge_address().unwrap().to_string(),
            nico_ops::bridge::DEFAULT_ADDRESS
        );
        assert_eq!(
            defaults.bridge_address(),
            ClientArgs::default().bridge_address()
        );
        let custom = TestArgs::parse_from(["client", "--bridge", "127.0.0.1:48000"]).client;
        assert_eq!(custom.bridge_address().unwrap().port(), 48000);
        assert!(
            TestArgs::parse_from(["client", "--no-bridge"])
                .client
                .bridge_address()
                .is_none()
        );
        assert!(
            TestArgs::try_parse_from(["client", "--no-bridge", "--bridge", "127.0.0.1:48000"])
                .is_err()
        );
    }
}
