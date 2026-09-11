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
}

fn main() -> Result<(), Box<dyn Error + Send + Sync>> {
    nico_ops::bridge::serve_stdio(Args::parse().listen)
}
