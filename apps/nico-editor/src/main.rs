//! Integrated Nico authoring application; runtime and host stay engine-owned.
mod assets_tree;
mod core;
mod document;
mod loading;
mod operations;
mod play;
mod ui;
use clap::Parser;
use nico_launch::{
    CommonArgs,
    client::{ClientArgs, ClientHost},
};
use std::{
    path::PathBuf,
    sync::{Arc, Mutex},
};

#[derive(Parser)]
struct Args {
    #[command(flatten)]
    common: CommonArgs,
    #[command(flatten)]
    host: ClientArgs,
    /// Game project with nico.project.toml, or a loose content directory.
    #[arg(long, default_value = ".")]
    project: PathBuf,
}
fn main() -> nico_winit::NativeClientResult<()> {
    let args = Args::parse();
    nico_launch::init_logging(args.common.log_level)?;
    let queue = Arc::new(Mutex::new(operations::Queue::new(32)));
    let published = Arc::new(Mutex::new(nico_ops::publication::Publication::default()));
    let tools = operations::register(queue.clone(), published.clone())?;
    let mut adapters = nico_authoring::Registry::default();
    adapters.register(
        arena_arpg_presentation::authoring::ADAPTER,
        arena_arpg_presentation::authoring::open,
    )?;
    let title = format!("Nico Editor — {}", args.project.display());
    let editor = loading::LoadingEditor::new(args.project, queue, published, adapters);
    ClientHost::new(args.host)
        .with_game_identity("nico-editor", "1")
        .with_mcp_tools(tools)
        .run_editor(editor, title)
}
