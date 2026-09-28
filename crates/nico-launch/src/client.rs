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

#[derive(Args, Clone, Debug, Default)]
pub struct ClientArgs {
    #[command(flatten)]
    pub debug: crate::DebugArgs,
    /// Open without requesting foreground focus.
    #[arg(long)]
    pub background: bool,
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
        self.debug.address(self.bridge, self.no_bridge)
    }
}

/// A prepared scene and its tools, published together at a host frame boundary.
pub struct ClientScene {
    pub scene: nico_winit::NativeScene,
    pub tools: ToolExtensions,
    pub content_revision: Option<String>,
}

pub struct ClientHost {
    args: ClientArgs,
    identity: Option<(String, String)>,
    content_revision: Option<String>,
    tools: ToolExtensions,
}

impl ClientHost {
    pub fn new(args: ClientArgs) -> Self {
        Self {
            args,
            identity: None,
            content_revision: None,
            tools: ToolExtensions::default(),
        }
    }
    /// Supply a revision of the content actually loaded; never a path or planned revision.
    #[must_use]
    pub fn with_content_revision(mut self, revision: impl Into<String>) -> Self {
        self.content_revision = Some(revision.into());
        self
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
    /// Keep the native window open while a caller polls prepared scene data.
    /// Scene changes reconnect the bridge with the new tool catalog and content revision.
    pub fn run_scenes(
        self,
        scene: nico_winit::NativeScene,
        config: NativeClientConfig,
        mut poll: impl FnMut(&App) -> NativeClientResult<Option<ClientScene>> + 'static,
    ) -> NativeClientResult<App> {
        let config = config.with_smoke_frames(self.args.smoke_frames);
        let config = if self.args.background {
            config.with_initial_focus(false)
        } else {
            config
        };
        let Some(address) = self.args.bridge_address() else {
            return nico_winit::run_native_scenes(scene, config, None, move |app| {
                Ok(poll(app)?.map(|next| next.scene))
            });
        };
        let (game, version) = self
            .identity
            .ok_or_else(|| io::Error::other("bridge mode requires a game identity"))?;
        let (control, endpoint) = control_channel();
        control.snapshots().enable();
        let access = self.args.debug.access();
        let connect = move |tools, revision| -> NativeClientResult<BridgeClient> {
            let tools = crate::snapshot::register(tools, control.clone())?;
            let tools = crate::rendering::register(tools, control.clone())?;
            let tools = crate::window::register(tools, control.clone())?;
            Ok(BridgeClient::start(
                address,
                GameRegistration {
                    access: access.clone(),
                    content_revision: revision,
                    ..GameRegistration::new(game.clone(), GameRole::Client, version.clone())
                },
                control.clone(),
                crate::diagnostics::register(tools)?,
            )?)
        };
        let mut bridge = Some(connect(self.tools, self.content_revision)?);
        nico_winit::run_native_scenes(scene, config, Some(endpoint), move |app| {
            let Some(next) = poll(app)? else {
                return Ok(None);
            };
            drop(bridge.take());
            bridge = Some(connect(next.tools, next.content_revision)?);
            Ok(Some(next.scene))
        })
    }

    /// Runs Winit on this thread. Bridge I/O runs separately and survives reconnects.
    pub fn run<C: Event>(
        self,
        app: App,
        config: NativeClientConfig,
        map_input: impl FnMut(&InputState, &mut Vec<C>) + 'static,
    ) -> NativeClientResult<App> {
        let config = config.with_smoke_frames(self.args.smoke_frames);
        let config = if self.args.background {
            config.with_initial_focus(false)
        } else {
            config
        };
        let Some(address) = self.args.bridge_address() else {
            return run_native_client(app, config, map_input);
        };
        let (game, version) = self
            .identity
            .ok_or_else(|| io::Error::other("bridge mode requires a game identity"))?;
        let (control, endpoint) = control_channel();
        control.snapshots().enable();
        let tools = crate::snapshot::register(self.tools, control.clone())?;
        let tools = crate::rendering::register(tools, control.clone())?;
        let tools = crate::window::register(tools, control.clone())?;
        let _bridge = BridgeClient::start(
            address,
            GameRegistration {
                access: self.args.debug.access(),
                content_revision: self.content_revision,
                ..GameRegistration::new(game, GameRole::Client, version)
            },
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
        assert!(!defaults.background);
        assert!(
            TestArgs::parse_from(["client", "--background"])
                .client
                .background
        );
        assert_eq!(
            defaults.bridge_address().map(|address| address.to_string()),
            cfg!(debug_assertions).then(|| nico_ops::bridge::DEFAULT_ADDRESS.to_owned())
        );
        assert_eq!(
            defaults.bridge_address(),
            ClientArgs::default().bridge_address()
        );
        let custom =
            TestArgs::parse_from(["client", "--enable-debug", "--bridge", "127.0.0.1:48000"])
                .client;
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
