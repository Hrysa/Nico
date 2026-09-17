mod camera;
mod character;
mod controls;
mod view;
mod visuals;
mod world;
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

#[derive(Parser)]
#[command(about = "Persistent multiplayer action RPG with an optional arena combat test")]
struct Args {
    #[command(flatten)]
    common: CommonArgs,
    /// Run the standalone arena combat test instead of the multiplayer world.
    #[arg(long)]
    arena: bool,
    #[arg(long, default_value = "127.0.0.1:47640")]
    server: std::net::SocketAddr,
    #[arg(long, default_value = "hero")]
    character: String,
    #[command(flatten)]
    host: ClientArgs,
    /// Directory containing hero/grunt/brute .char.toml definitions.
    #[arg(long, default_value = arena_arpg_shared::characters::DEFAULT_LOGIC_ROOT)]
    logic_characters: PathBuf,
    /// Directory containing matching .char-vis.toml definitions and relative assets.
    #[arg(long, default_value = character::definition::DEFAULT_VISUAL_ROOT)]
    visual_characters: PathBuf,
    /// Client-only scenery definition; solid placements come from the server zone.
    #[arg(long, default_value = world::environment::DEFAULT_VISUAL_WORLD)]
    visual_world: PathBuf,
    /// Override the hero model; requires the selected animation directory.
    #[arg(long, requires = "character_animations")]
    character_model: Option<PathBuf>,
    #[arg(long, requires = "character_model")]
    character_animations: Option<PathBuf>,
    /// Use configured procedural hero visuals without importing the model or clips.
    #[arg(long, conflicts_with_all = ["character_model", "character_animations"])]
    procedural_hero: bool,
}
fn main() -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
    let args = Args::parse();
    init_logging(args.common.log_level)?;
    let logic = arena_arpg_shared::characters::CharacterCatalog::load(&args.logic_characters)?;
    let definitions = character::definition::load_visuals(&args.visual_characters, &logic)?;
    let overrides = args
        .character_model
        .as_deref()
        .zip(args.character_animations.as_deref());
    let mut character = std::array::from_fn(|_| None);
    for (index, definition) in definitions.iter().enumerate() {
        if definition.core.model.is_some() && !(index == 0 && args.procedural_hero) {
            character[index] = Some(character::CharacterAssets::load_definition(
                definition.clone(),
                &args.visual_characters,
                if index == 0 { overrides } else { None },
            )?);
        }
    }
    let builder = AppBuilder::new().with_fixed_step(FIXED_STEP);
    let (builder, tools) = if !args.arena {
        let client = world::network::WorldClient::new(args.server, args.character.clone(), logic)?;
        let environment = world::environment::Environment::load(&args.visual_world)?;
        world::register(builder, client, character, definitions, environment)?
    } else {
        let (builder, mut tools) = arena_arpg_shared::tools::register(
            builder
                .add_plugin(controls::ControlsPlugin)
                .add_plugin(ArenaPlugin::with_characters(logic.clone())),
        )?;
        (
            view::register_configured(builder, &mut tools, character, definitions, logic)?,
            tools,
        )
    };
    let config = NativeClientConfig::new(
        if !args.arena {
            "Nico | Meadow"
        } else {
            "Nico | Arena"
        },
        "assets/presentation/shaders/generated/wgpu/bootstrap.wgsl",
    )
    .with_mesh_shaders(
        "assets/presentation/shaders/generated/wgpu/meshes.wgsl",
        "assets/presentation/shaders/generated/wgpu/quads.wgsl",
    )
    .with_skin_shader("assets/presentation/shaders/generated/wgpu/skinned_meshes.wgsl")
    .with_pointer_capture();
    let host = ClientHost::new(args.host)
        .with_game_identity("arena_arpg", "1")
        .with_mcp_tools(tools);
    if !args.arena {
        host.run(builder.build()?, config, world::map_input)?;
    } else {
        host.run(builder.build()?, config, controls::map_input)?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Measures CPU startup preparation only; never opens a window or connects a host.
    #[test]
    #[ignore = "manual startup elapsed-time measurement using local game assets"]
    fn startup_asset_preparation_measurement() {
        use std::time::Instant;
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../..");
        let args = Args::parse_from(["arena"]);
        let start = Instant::now();
        let logic = arena_arpg_shared::characters::CharacterCatalog::load(
            &root.join(args.logic_characters),
        )
        .unwrap();
        let visual_root = root.join(args.visual_characters);
        let definitions = character::definition::load_visuals(&visual_root, &logic).unwrap();
        let mut characters = Vec::new();
        for definition in definitions {
            if definition.core.model.is_some() {
                let name = definition.core.character.clone();
                let phase = Instant::now();
                characters.push(
                    character::CharacterAssets::load_definition(definition, &visual_root, None)
                        .unwrap(),
                );
                eprintln!("startup measurement: {name} {:?}", phase.elapsed());
            }
        }
        let phase = Instant::now();
        let mut environment =
            world::environment::Environment::load(&root.join(args.visual_world)).unwrap();
        eprintln!(
            "startup measurement: environment assets {:?}",
            phase.elapsed()
        );
        let zone = arena_arpg_shared::open_world::content::ZoneDefinition::load(
            &root.join("games/arena-arpg/assets/logic/worlds/meadow.world.toml"),
        )
        .unwrap();
        let phase = Instant::now();
        environment.bind(&zone).unwrap();
        eprintln!(
            "startup measurement: scenery preparation {:?}; total {:?}; cache {:?}",
            phase.elapsed(),
            start.elapsed(),
            nico_assets::cache::stats()
        );
        assert_eq!(characters.len(), 3);
    }

    #[test]
    fn default_hero_uses_game_presentation_assets() {
        let args = Args::try_parse_from(["arena"]).unwrap();
        assert!(!args.arena);
        assert!(Args::try_parse_from(["game", "--arena"]).unwrap().arena);
        assert_eq!(
            args.logic_characters,
            PathBuf::from(arena_arpg_shared::characters::DEFAULT_LOGIC_ROOT)
        );
        assert_eq!(
            args.visual_characters,
            PathBuf::from(character::definition::DEFAULT_VISUAL_ROOT)
        );
        assert!(
            args.character_model.is_none()
                && args.character_animations.is_none()
                && !args.procedural_hero
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
            args.character_model.zip(args.character_animations),
            Some((PathBuf::from("hero.glb"), PathBuf::from("clips")))
        );
    }

    #[test]
    fn procedural_hero_skips_assets_and_rejects_overrides() {
        let args = Args::try_parse_from(["arena", "--procedural-hero"]).unwrap();
        assert!(args.procedural_hero);
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
