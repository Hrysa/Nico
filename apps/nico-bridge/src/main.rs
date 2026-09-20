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
    /// Optional loopback editor RPC endpoint; remote consumers must use a tunnel.
    #[arg(long, requires = "editor_token_file")]
    editor_listen: Option<SocketAddr>,
    /// Private file containing a 64-character hexadecimal editor endpoint token.
    #[arg(long, requires = "editor_listen")]
    editor_token_file: Option<std::path::PathBuf>,
}

fn main() -> Result<(), Box<dyn Error + Send + Sync>> {
    let args = Args::parse();
    let editor = args
        .editor_listen
        .zip(args.editor_token_file)
        .map(|(address, token_file)| nico_ops::bridge::EditorEndpoint {
            address,
            token_file,
        });
    nico_ops::bridge::serve_stdio_with_editor(args.listen, editor)
}
