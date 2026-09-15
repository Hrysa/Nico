//! Native executable startup shared by Nico clients and servers.
//!
//! Platform hosts that do not expose a command line, such as web or console
//! applications, install their own diagnostics output instead of using this
//! crate.

use std::error::Error;

#[cfg(any(feature = "client", feature = "server"))]
mod diagnostics;

#[cfg(feature = "server")]
pub mod server;

#[cfg(feature = "client")]
pub mod client;
#[cfg(feature = "client")]
mod snapshot;
#[cfg(feature = "client")]
mod window;

use clap::{Args, ValueEnum};
use tracing_subscriber::{EnvFilter, filter::LevelFilter, fmt::format::FmtSpan};

/// Command-line options shared by native client and server executables.
#[derive(Args, Clone, Copy, Debug, Default)]
pub struct CommonArgs {
    /// Maximum diagnostic verbosity. Overrides RUST_LOG when supplied.
    #[arg(long, value_enum)]
    pub log_level: Option<LogLevel>,
}

/// Diagnostic verbosity accepted by native Nico executables.
#[derive(Clone, Copy, Debug, Eq, PartialEq, ValueEnum)]
pub enum LogLevel {
    /// Disable diagnostics.
    Off,
    /// Emit errors only.
    Error,
    /// Emit warnings and errors.
    Warn,
    /// Emit normal lifecycle information.
    Info,
    /// Emit diagnostic details.
    Debug,
    /// Emit high-volume execution details.
    Trace,
}

impl From<LogLevel> for LevelFilter {
    fn from(level: LogLevel) -> Self {
        match level {
            LogLevel::Off => Self::OFF,
            LogLevel::Error => Self::ERROR,
            LogLevel::Warn => Self::WARN,
            LogLevel::Info => Self::INFO,
            LogLevel::Debug => Self::DEBUG,
            LogLevel::Trace => Self::TRACE,
        }
    }
}

/// Installs line-oriented diagnostics on stderr for a native executable.
///
/// An explicit command-line level takes precedence over `RUST_LOG`. When neither
/// is present, diagnostics default to `info`.
pub fn init_logging(log_level: Option<LogLevel>) -> Result<(), Box<dyn Error + Send + Sync>> {
    let filter = match log_level {
        Some(level) => EnvFilter::builder()
            .with_default_directive(LevelFilter::from(level).into())
            .parse("")?,
        None => EnvFilter::builder()
            .with_default_directive(LevelFilter::INFO.into())
            .from_env_lossy(),
    };

    use tracing_subscriber::prelude::*;
    let subscriber = tracing_subscriber::registry().with(filter).with(
        tracing_subscriber::fmt::layer()
            .with_writer(std::io::stderr)
            .with_span_events(FmtSpan::NEW | FmtSpan::CLOSE),
    );
    #[cfg(any(feature = "client", feature = "server"))]
    let subscriber = subscriber.with(diagnostics::capture_layer());
    subscriber.try_init()?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use clap::Parser;

    use super::{CommonArgs, LogLevel};

    #[derive(Debug, Parser)]
    struct TestArgs {
        #[command(flatten)]
        common: CommonArgs,
    }

    #[test]
    fn common_arguments_parse_log_level() {
        let args = TestArgs::try_parse_from(["test", "--log-level", "debug"])
            .expect("valid arguments should parse");

        assert_eq!(args.common.log_level, Some(LogLevel::Debug));
    }
}
