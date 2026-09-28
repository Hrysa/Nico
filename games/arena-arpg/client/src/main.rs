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
    /// Saved Arena project; loads its world and character assets as one revision.
    #[arg(long, conflicts_with_all = ["arena", "logic_characters", "visual_characters", "visual_world", "character_model", "character_animations"])]
    project: Option<PathBuf>,
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
    let mut args = Args::parse();
    init_logging(args.common.log_level)?;
    let content = args
        .project
        .as_deref()
        .map(arena_arpg_shared::project::ProjectContent::open)
        .transpose()?;
    if let Some(content) = &content {
        args.logic_characters = content.asset("assets/logic/characters")?;
        args.visual_characters = content.asset("assets/presentation/characters")?;
        args.visual_world = arena_arpg_shared::project::ProjectContent::default_scene(
            args.project.as_deref().unwrap(),
        )?;
    }
    if content.is_none()
        && args.visual_world == std::path::Path::new(world::environment::DEFAULT_VISUAL_WORLD)
    {
        args.visual_world = arena_arpg_shared::project::ProjectContent::default_scene(
            std::path::Path::new("games/arena-arpg"),
        )?;
    }
    let logic = arena_arpg_shared::characters::CharacterCatalog::load(&args.logic_characters)?;
    let definitions = character::definition::load_visuals(&args.visual_characters, &logic)?;
    let overrides = args
        .character_model
        .as_deref()
        .zip(args.character_animations.as_deref());
    let loaded = load_project_assets(&args, &definitions)?;
    let mut character = std::array::from_fn(|_| None);
    for (index, definition) in definitions.iter().enumerate() {
        if definition.core.model.is_some() && !(index == 0 && args.procedural_hero) {
            character[index] = Some(character::CharacterAssets::from_loaded(
                definition.clone(),
                &args.visual_characters,
                if index == 0 { overrides } else { None },
                &loaded,
            )?);
        }
    }
    let builder = AppBuilder::new().with_fixed_step(FIXED_STEP);
    let (builder, tools) = if !args.arena {
        let client = world::network::WorldClient::new(args.server, args.character.clone(), logic)?;
        let environment =
            world::environment::Environment::from_loaded(&args.visual_world, &loaded)?;
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
    .with_instance_shader("assets/presentation/shaders/generated/wgpu/instanced_meshes.wgsl")
    .with_gpu_instance_shaders(
        "assets/presentation/shaders/generated/wgpu/instanced_storage.wgsl",
        "assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl",
    )
    .with_skin_shader("assets/presentation/shaders/generated/wgpu/skinned_meshes.wgsl")
    .with_foliage_shaders(
        "assets/presentation/shaders/generated/wgpu/foliage_meshes.wgsl",
        "assets/presentation/shaders/generated/wgpu/foliage_storage.wgsl",
        "assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl",
    )
    .with_pointer_capture();
    let mut host = ClientHost::new(args.host)
        .with_game_identity("arena_arpg", "1")
        .with_mcp_tools(tools);
    if let Some(content) = &content {
        content.verify()?;
        host = host.with_content_revision(content.revision.clone());
    }
    if !args.arena {
        host.run(builder.build()?, config, world::map_input)?;
    } else {
        host.run(builder.build()?, config, controls::map_input)?;
    }
    Ok(())
}

fn load_project_assets(
    args: &Args,
    definitions: &[character::definition::CharacterVisualDefinition; 3],
) -> Result<nico_assets::graph::LoadedAssets, Box<dyn std::error::Error + Send + Sync>> {
    use std::path::Path;
    let root = args
        .project
        .as_deref()
        .unwrap_or(Path::new("games/arena-arpg"));
    nico_assets::graph::load(vec![root.join("nico.project.toml")], |path| {
        let name = path
            .file_name()
            .and_then(|name| name.to_str())
            .ok_or("invalid asset filename")?;
        if name == "nico.project.toml" {
            // The project selects the scene; Arena's catalog selects its character definitions.
            let scene =
                arena_arpg_shared::project::ProjectContent::default_scene(path.parent().unwrap())?;
            let scene = if args.visual_world == Path::new(world::environment::DEFAULT_VISUAL_WORLD)
            {
                scene
            } else {
                args.visual_world.canonicalize()?
            };
            let mut sources = vec![scene];
            for name in arena_arpg_shared::characters::NAMES {
                sources.push(
                    args.visual_characters
                        .join(format!("{name}.char-vis.toml"))
                        .canonicalize()?,
                );
            }
            return Ok(sources);
        }

        if name.ends_with(".char-vis.toml") {
            let definition = character::definition::CharacterVisualDefinition::parse(
                &arena_arpg_shared::characters::read_definition(path)?,
            )?;
            let hero = definition.core.character == definitions[0].core.character;
            if hero && args.procedural_hero {
                return Ok(Vec::new());
            }
            let overrides = if hero {
                args.character_model
                    .as_deref()
                    .zip(args.character_animations.as_deref())
            } else {
                None
            };
            return character::CharacterAssets::dependencies(
                &definition,
                path.parent().unwrap(),
                overrides,
            );
        }
        if name.ends_with(".world-vis.toml") {
            return if args.arena {
                Ok(Vec::new())
            } else {
                world::environment::Environment::dependencies(path)
            };
        }
        Err(format!("unsupported asset definition: {}", path.display()).into())
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn project_load_supplies_all_character_and_world_assets_with_one_progress_total() {
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("..")
            .canonicalize()
            .unwrap();
        let mut args = Args::parse_from(["arena"]);
        args.project = Some(root.clone());
        args.logic_characters = root.join("assets/logic/characters");
        args.visual_characters = root.join("assets/presentation/characters");
        args.visual_world = root.join("assets/presentation/worlds/meadow.world-vis.toml");
        let logic =
            arena_arpg_shared::characters::CharacterCatalog::load(&args.logic_characters).unwrap();
        let definitions =
            character::definition::load_visuals(&args.visual_characters, &logic).unwrap();
        let (send, receive) = std::sync::mpsc::channel();
        let _observer = nico_assets::progress::observe_progress(move |update| {
            send.send(update).unwrap();
        });
        let assets = load_project_assets(&args, &definitions).unwrap();
        assert!(!assets.is_empty());
        for definition in definitions {
            if definition.core.model.is_some() {
                character::CharacterAssets::from_loaded(
                    definition,
                    &args.visual_characters,
                    None,
                    &assets,
                )
                .unwrap();
            }
        }
        world::environment::Environment::from_loaded(&args.visual_world, &assets).unwrap();
        let updates: Vec<_> = receive.try_iter().collect();
        assert!(
            updates
                .iter()
                .all(|update| update.label == "project assets" && update.total == assets.len())
        );
        assert_eq!(updates.iter().filter(|update| update.finished).count(), 1);
        assert_eq!(updates.last().unwrap().completed, assets.len());
    }

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
