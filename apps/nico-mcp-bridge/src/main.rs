use clap::Parser;
use std::{error::Error, net::SocketAddr};

#[derive(Parser)]
#[command(
    about = "MCP bridge for independently launched Nico games; never launches game processes"
)]
struct Args {
    /// Loopback address accepting game registrations. MCP uses stdin/stdout.
    #[arg(long, default_value = nico_ops::bridge::DEFAULT_ADDRESS)]
    listen: SocketAddr,
    /// Run the shared game listener without a stdio frontend.
    #[arg(long)]
    daemon: bool,
    /// Local daemon discovery and lock directory. Defaults to a per-address temporary directory.
    #[arg(long)]
    state_dir: Option<std::path::PathBuf>,
    /// Exit the daemon after this many seconds without frontends or games.
    #[arg(long, default_value_t = 30, value_parser = clap::value_parser!(u64).range(1..))]
    idle_seconds: u64,
}

fn main() -> Result<(), Box<dyn Error + Send + Sync>> {
    let args = Args::parse();
    let mut config = nico_ops::bridge::DaemonConfig::new(args.listen);
    if let Some(path) = args.state_dir {
        config.state_dir = path;
    }
    config.idle_timeout = std::time::Duration::from_secs(args.idle_seconds);
    if args.daemon {
        nico_ops::bridge::serve_daemon(config)
    } else {
        nico_ops::bridge::serve_frontend(config)
    }
}
