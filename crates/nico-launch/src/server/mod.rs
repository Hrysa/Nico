//! Native headless host and engine-owned MCP lifecycle.
//!
//! Games supply an App and optional additional tools. This host owns control
//! channels, the service thread, disconnect handling, and the final join.

use std::{error::Error, io, num::NonZeroU32, thread, time::Duration};

use clap::Args;
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

    /// Serve MCP tools on stdin/stdout until the client disconnects.
    #[arg(long)]
    pub mcp_stdio: bool,
}

impl Default for ServerArgs {
    fn default() -> Self {
        Self {
            tick_rate: NonZeroU32::new(60).unwrap(),
            mcp_stdio: false,
        }
    }
}

impl ServerArgs {
    /// Fixed host step corresponding to the configured rate.
    pub fn tick_interval(&self) -> Duration {
        Duration::from_secs_f64(1.0 / f64::from(self.tick_rate.get()))
    }
}

/// Composes a game's App with engine-owned server and MCP services.
pub struct ServerHost {
    args: ServerArgs,
    tools: mcp::ToolExtensions,
}

impl ServerHost {
    pub fn new(args: ServerArgs) -> Self {
        Self {
            args,
            tools: mcp::ToolExtensions::default(),
        }
    }

    /// Adds game-owned operations; built-in lifecycle tools cannot be overridden.
    /// Extensions are used only when `--mcp-stdio` is enabled.
    #[must_use]
    pub fn with_mcp_tools(mut self, tools: mcp::ToolExtensions) -> Self {
        self.tools = tools;
        self
    }

    /// Runs the App on this thread. MCP I/O and tool handlers run separately.
    /// After host shutdown, final status stays available until MCP disconnects.
    pub fn run(self, app: &mut App) -> Result<(), Box<dyn Error + Send + Sync>> {
        let runner = FixedRateServerRunner::new(self.args.tick_interval());
        if !self.args.mcp_stdio {
            app.run_with(runner)?;
            return Ok(());
        }
        let (control, endpoint) = control_channel();
        let service = thread::Builder::new()
            .name("nico-mcp".into())
            .spawn(move || mcp::serve_stdio_with_tools(control, self.tools))?;
        let host_result = app.run_with(runner.with_operations(endpoint));
        let service_result = service
            .join()
            .map_err(|_| io::Error::other("MCP service thread panicked"))?;
        host_result?;
        service_result?;
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
    fn server_arguments_own_mcp_opt_in_and_nonzero_tick_rate() {
        let defaults = TestArgs::try_parse_from(["server"]).unwrap().server;
        assert_eq!(defaults.tick_rate.get(), 60);
        assert!(!defaults.mcp_stdio);
        let args = TestArgs::try_parse_from(["server", "--tick-rate", "30", "--mcp-stdio"])
            .unwrap()
            .server;
        assert_eq!(args.tick_rate.get(), 30);
        assert!(args.mcp_stdio);
        assert!(TestArgs::try_parse_from(["server", "--tick-rate", "0"]).is_err());
    }
}
