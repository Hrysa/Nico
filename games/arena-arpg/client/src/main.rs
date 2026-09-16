mod camera;
mod character;
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
use std::path::PathBuf;

const HERO_ROOT: &str = "games/arena-arpg/assets/presentation/characters/hero";
#[derive(Parser)]
#[command(about = "Third-person arena ARPG combat prototype")]
struct Args {
    #[command(flatten)]
    common: CommonArgs,
    #[command(flatten)]
    host: ClientArgs,
    /// Override the game hero model; requires the RPG animation directory.
    #[arg(long, requires = "character_animations")]
    character_model: Option<PathBuf>,
    #[arg(long, requires = "character_model")]
    character_animations: Option<PathBuf>,
    /// Use procedural hero visuals without loading character assets.
    #[arg(long, conflicts_with_all = ["character_model", "character_animations"])]
    procedural_hero: bool,
}
impl Args {
    fn character_paths(&self) -> Option<(PathBuf, PathBuf)> {
        if self.procedural_hero {
            return None;
        }
        let root = PathBuf::from(HERO_ROOT);
        Some((
            self.character_model
                .clone()
                .unwrap_or_else(|| root.join("model.glb")),
            self.character_animations
                .clone()
                .unwrap_or_else(|| root.join("animations")),
        ))
    }
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
    let character = args
        .character_paths()
        .map(|(model, animations)| character::CharacterAssets::load(&model, &animations))
        .transpose()?;
    let builder = view::register(builder, &mut tools, character)?;
    let config = NativeClientConfig::new(
        "Nico | Arena",
        "assets/presentation/shaders/generated/wgpu/bootstrap.wgsl",
    )
    .with_mesh_shaders(
        "assets/presentation/shaders/generated/wgpu/meshes.wgsl",
        "assets/presentation/shaders/generated/wgpu/quads.wgsl",
    )
    .with_skin_shader("assets/presentation/shaders/generated/wgpu/skinned_meshes.wgsl")
    .with_pointer_capture();
    ClientHost::new(args.host)
        .with_game_identity("arena_arpg", "1")
        .with_mcp_tools(tools)
        .run(builder.build()?, config, controls::map_input)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn default_hero_uses_game_presentation_assets() {
        let args = Args::try_parse_from(["arena"]).unwrap();
        assert_eq!(
            args.character_paths(),
            Some((
                PathBuf::from(HERO_ROOT).join("model.glb"),
                PathBuf::from(HERO_ROOT).join("animations"),
            ))
        );
    }

    #[test]
    fn custom_hero_requires_both_paths_and_preserves_them() {
        assert!(Args::try_parse_from(["arena", "--character-model", "hero.glb"]).is_err());
        assert!(Args::try_parse_from(["arena", "--character-animations", "clips"]).is_err());
        let args = Args::try_parse_from([
            "arena",
            "--character-model",
            "hero.glb",
            "--character-animations",
            "clips",
        ])
        .unwrap();
        assert_eq!(
            args.character_paths(),
            Some((PathBuf::from("hero.glb"), PathBuf::from("clips")))
        );
    }

    #[test]
    fn procedural_hero_skips_assets_and_rejects_overrides() {
        let args = Args::try_parse_from(["arena", "--procedural-hero"]).unwrap();
        assert!(args.character_paths().is_none());
        assert!(
            Args::try_parse_from([
                "arena",
                "--procedural-hero",
                "--character-model",
                "hero.glb",
                "--character-animations",
                "clips",
            ])
            .is_err()
        );
    }
}
