mod camera;
mod controls;
mod view;
mod visuals;
use arena_arpg_shared::{ArenaPlugin, FIXED_STEP};
use clap::Parser;
use nico_launch::{
    CommonArgs,
    client::{ClientArgs, ClientHost},
    init_logging,
};
use nico_runtime::AppBuilder;
use nico_winit::NativeClientConfig;
#[derive(Parser)]
#[command(about = "Third-person arena ARPG combat prototype")]
struct Args {
    #[command(flatten)]
    common: CommonArgs,
    #[command(flatten)]
    host: ClientArgs,
}
fn main() -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let args = Args::parse();
    init_logging(args.common.log_level)?;
    let (builder, mut tools) = arena_arpg_shared::tools::register(
        AppBuilder::new()
            .with_fixed_step(FIXED_STEP)
            .add_plugin(controls::ControlsPlugin)
            .add_plugin(ArenaPlugin),
    )?;
    let builder = view::register(builder, &mut tools)?;
    let config = NativeClientConfig::new(
        "Nico | Arena",
        "assets/presentation/shaders/generated/wgpu/bootstrap.wgsl",
    )
    .with_mesh_shaders(
        "assets/presentation/shaders/generated/wgpu/meshes.wgsl",
        "assets/presentation/shaders/generated/wgpu/quads.wgsl",
    )
    .with_pointer_capture();
    ClientHost::new(args.host)
        .with_game_identity("arena_arpg", "1")
        .with_mcp_tools(tools)
        .run(builder.build()?, config, controls::map_input)?;
    Ok(())
}
