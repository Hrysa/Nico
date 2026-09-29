//! Scene-rooted project content shared by client and server startup.
use crate::scene;
use nico_scene::{HostRole, SceneDefinition};
use std::{
    io,
    path::{Path, PathBuf},
};
pub const DEFAULT_PROJECT: &str = "games/arena-arpg";
/// Lightweight entry parsing. Gameplay content is prepared only after following the splash.
pub struct StartupScene {
    pub project: nico_scene::Project,
    pub scene: SceneDefinition,
    pub splash: Option<scene::Splash>,
}
impl StartupScene {
    pub fn load(root: &Path, selected: Option<&Path>) -> io::Result<Self> {
        let mut project = nico_scene::Project::open(root)?;
        if let Some(path) = selected {
            project.resolve_asset(path)?;
            project.manifest.default_scene = path.into();
        }
        let scene = project.load_scene()?;
        let splash = if scene
            .entities
            .iter()
            .any(|e| e.components.contains_key("arena.splash"))
        {
            scene::registry()?.prepare(&scene, HostRole::Client, |p| project.resolve_asset(p))?;
            if scene.entities.len() != 1
                || scene.entities[0].components.len() != 1
                || scene.entities[0].scope != nico_scene::ComponentScope::Client
            {
                return Err(error("splash scene requires one client splash entity"));
            }
            let splash: scene::Splash = scene::single(&scene, "arena.splash")?;
            if project.resolve_asset(&splash.next_scene)? == project.scene_path()? {
                return Err(error("splash cannot load itself"));
            }
            let target = SceneDefinition::load(&project.resolve_asset(&splash.next_scene)?)
                .map_err(error)?;
            if target
                .entities
                .iter()
                .any(|e| e.components.contains_key("arena.splash"))
            {
                return Err(error("splash target must be a gameplay scene"));
            }
            Some(splash)
        } else {
            None
        };
        Ok(Self {
            project,
            scene,
            splash,
        })
    }
    pub fn into_game(mut self, role: HostRole) -> io::Result<ProjectContent> {
        if let Some(splash) = self.splash {
            self.project.manifest.default_scene = splash.next_scene;
            self.scene = self.project.load_scene()?;
            if self
                .scene
                .entities
                .iter()
                .any(|e| e.components.contains_key("arena.splash"))
            {
                return Err(error("splash target must be a gameplay scene"));
            }
        }
        ProjectContent::from_scene(self.project, self.scene, role)
    }
}

pub struct SceneEntities(pub Vec<nico_ecs::Entity>);
pub struct ProjectContent {
    project: nico_scene::Project,
    pub scene: SceneDefinition,
    pub entities: nico_ecs::World,
    pub zone: crate::open_world::content::ZoneDefinition,
}
fn error(e: impl ToString) -> io::Error {
    io::Error::other(e.to_string())
}
impl ProjectContent {
    pub fn default_scene(root: &Path) -> io::Result<PathBuf> {
        nico_scene::Project::open(root)?.scene_path()
    }
    pub fn open(root: &Path) -> io::Result<Self> {
        Self::load(root, HostRole::Client)
    }
    pub fn load(root: &Path, role: HostRole) -> io::Result<Self> {
        Self::load_scene(root, None, role)
    }
    pub fn load_scene(root: &Path, selected: Option<&Path>, role: HostRole) -> io::Result<Self> {
        StartupScene::load(root, selected)?.into_game(role)
    }
    pub fn from_scene(
        project: nico_scene::Project,
        scene: SceneDefinition,
        role: HostRole,
    ) -> io::Result<Self> {
        let prepared = scene::registry()?.prepare(&scene, role, |p| project.resolve_asset(p))?;
        scene::validate(&scene)?;
        let world: scene::World = scene::single(&scene, "arena.world")?;
        let rules = scene::WorldRulesDefinition::load(&project.resolve_asset(&world.rules)?)
            .map_err(error)?;
        let zone = scene::world_definition(&scene, &rules)?;
        scene::validate_library(&project, &scene, &zone)?;
        let mut entities = nico_ecs::World::new();
        prepared.instantiate(&mut entities);
        Ok(Self {
            project,
            scene,
            entities,
            zone,
        })
    }
    /// Instantiate prepared components at startup; shutdown unloads only this scene's entities.
    pub fn attach(&self, builder: &mut nico_runtime::AppBuilder, role: HostRole) -> io::Result<()> {
        let mut prepared =
            Some(scene::registry()?.prepare(&self.scene, role, |p| self.project.resolve_asset(p))?);
        builder.add_system(
            nico_runtime::Stage::Startup,
            "scene::instantiate",
            move |ctx| {
                let ids = prepared
                    .take()
                    .expect("scene starts once")
                    .instantiate(ctx.world);
                ctx.world.insert_resource(SceneEntities(ids));
                Ok(())
            },
        );
        builder.add_system(nico_runtime::Stage::Shutdown, "scene::unload", |ctx| {
            if let Some(scene) = ctx.world.remove_resource::<SceneEntities>() {
                for id in scene.0 {
                    let _ = ctx.world.despawn(id);
                }
            }
            Ok(())
        });
        Ok(())
    }
    pub fn scene_path(&self) -> io::Result<PathBuf> {
        self.project.scene_path()
    }
    pub fn mode(&self) -> io::Result<scene::WorldMode> {
        Ok(scene::single::<scene::World>(&self.scene, "arena.world")?.mode)
    }
    pub fn source(&self, name: &str) -> io::Result<PathBuf> {
        let world: scene::World = scene::single(&self.scene, "arena.world")?;
        let environment: scene::Environment = scene::single(&self.scene, "arena.environment")?;
        self.project.resolve_asset(&match name {
            "logic" => world.rules,
            "logic_characters" => world.characters,
            "item" => world.item,
            "visual" => environment.library,
            "visual_characters" => environment.characters,
            _ => return Err(error(format!("unknown scene source {name}"))),
        })
    }
    pub fn asset(&self, relative: &str) -> io::Result<PathBuf> {
        self.project.resolve_asset(Path::new(relative))
    }
    pub fn root(&self) -> &Path {
        self.project.root()
    }
    /// Publish the selected scene without claiming a project content revision.
    #[cfg(feature = "tools")]
    pub fn register_tools(&self, tools: &mut nico_ops::mcp::ToolExtensions) -> io::Result<()> {
        use nico_ops::mcp::{CallToolResult, Tool, ToolAccess};
        use serde_json::json;
        let state = json!({"scene": self.project.manifest.default_scene,
            "content_revision": null, "definition_state": "parsed_and_validated"});
        tools.register(
            Tool::new(
                "scene_info",
                "Inspect the selected scene. No project revision is calculated during startup.",
                json!({"type":"object","properties":{},"additionalProperties":false})
                    .as_object()
                    .unwrap()
                    .clone(),
            ),
            move |args| {
                if !args.is_empty() {
                    return CallToolResult::structured_error(
                        json!({"error":"No arguments expected"}),
                    );
                }
                CallToolResult::structured(state.clone())
            },
        )?;
        tools.set_access("scene_info", ToolAccess::Inspect)
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn splash_rejects_self_references_chains_and_missing_targets() {
        let root =
            std::env::temp_dir().join(format!("nico-splash-validation-{}", std::process::id()));
        std::fs::create_dir_all(root.join("assets/scenes")).unwrap();
        std::fs::write(
            root.join("nico.project.toml"),
            "version = 1\nname = 'Fixture'\ndefault_scene = 'assets/scenes/splash.scene.toml'\n",
        )
        .unwrap();
        let source =
            Path::new(env!("CARGO_MANIFEST_DIR")).join("../assets/scenes/splash.scene.toml");
        let original = SceneDefinition::load(&source).unwrap();
        for target in [
            "assets/scenes/splash.scene.toml",
            "assets/scenes/other.scene.toml",
            "assets/scenes/missing.scene.toml",
        ] {
            let mut scene = original.clone();
            let mut splash: scene::Splash = scene::single(&scene, "arena.splash").unwrap();
            splash.next_scene = target.into();
            scene.entities[0]
                .set_component("arena.splash", &splash)
                .unwrap();
            scene
                .save_file(&root.join("assets/scenes/splash.scene.toml"))
                .unwrap();
            original
                .save_file(&root.join("assets/scenes/other.scene.toml"))
                .unwrap();
            assert!(StartupScene::load(&root, None).is_err(), "{target}");
        }
        std::fs::remove_dir_all(root).unwrap();
    }
    #[test]
    fn default_entry_is_splash_and_server_follows_its_gameplay_target() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("..");
        let entry = StartupScene::load(&root, None).unwrap();
        assert_eq!(
            entry.project.manifest.default_scene,
            Path::new("assets/scenes/splash.scene.toml")
        );
        assert_eq!(entry.splash.as_ref().unwrap().duration_seconds, 1.);
        let server = entry.into_game(HostRole::Server).unwrap();
        assert_eq!(server.zone.id, "meadow");
        assert_eq!(server.entities.query::<&scene::Splash>().iter().count(), 0);
        let direct = ProjectContent::load_scene(
            &root,
            Some(Path::new("assets/scenes/meadow.scene.toml")),
            HostRole::Server,
        )
        .unwrap();
        assert_eq!(server.scene_path().unwrap(), direct.scene_path().unwrap());
        assert!(
            StartupScene::load(&root, Some(Path::new("assets/scenes/arena.scene.toml")))
                .unwrap()
                .splash
                .is_none()
        );
    }
    #[test]
    fn composed_scene_supplies_world_camera_lights_and_typed_entities() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("..");
        let mut project = nico_scene::Project::open(&root).unwrap();
        project.manifest.default_scene = "assets/scenes/meadow.scene.toml".into();
        let scene = SceneDefinition::load(&project.scene_path().unwrap()).unwrap();
        let content = ProjectContent::from_scene(project, scene, HostRole::Client).unwrap();
        assert_eq!(content.zone.obstacles.len(), 13);
        assert_eq!(content.zone.monsters.len(), 3);
        assert_eq!(
            content
                .entities
                .query::<&nico_scene::components::Camera>()
                .iter()
                .count(),
            1
        );
        assert_eq!(
            content
                .entities
                .query::<&scene::BoxCollider>()
                .iter()
                .count(),
            13
        );
    }
    #[test]
    fn server_scene_omits_presentation_and_unloads_only_owned_entities() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("..");
        let content = ProjectContent::load(&root, HostRole::Server).unwrap();
        assert_eq!(
            content
                .entities
                .query::<&nico_scene::components::Camera>()
                .iter()
                .count(),
            0
        );
        assert_eq!(
            content.entities.query::<&scene::Scenery>().iter().count(),
            0
        );
        let mut builder = nico_runtime::AppBuilder::new();
        content.attach(&mut builder, HostRole::Server).unwrap();
        let mut app = builder.build().unwrap();
        let foreign = app.world_mut().spawn((123_u32,));
        app.start().unwrap();
        assert_eq!(
            app.world().query::<&scene::BoxCollider>().iter().count(),
            13
        );
        app.shutdown().unwrap();
        assert!(app.world().contains_entity(foreign));
        assert_eq!(app.world().entities().len(), 1);
    }
    #[test]
    fn scene_selection_changes_mode_and_path_and_missing_scene_fails() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("..");
        let meadow = ProjectContent::load(&root, HostRole::Server).unwrap();
        let arena = ProjectContent::load_scene(
            &root,
            Some(Path::new("assets/scenes/arena.scene.toml")),
            HostRole::Server,
        )
        .unwrap();
        assert_eq!(arena.mode().unwrap(), scene::WorldMode::Arena);
        assert_eq!(
            scene::arena_level(&arena.zone).unwrap(),
            crate::Level::default()
        );
        assert_ne!(meadow.scene_path().unwrap(), arena.scene_path().unwrap());
        assert!(
            ProjectContent::load_scene(
                &root,
                Some(Path::new("assets/scenes/missing.scene.toml")),
                HostRole::Server
            )
            .is_err()
        );
    }
    #[test]
    fn unknown_components_bad_references_and_missing_models_fail_before_startup() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("..");
        let mut project = nico_scene::Project::open(&root).unwrap();
        project.manifest.default_scene = "assets/scenes/meadow.scene.toml".into();
        let scene = project.load_scene().unwrap();
        for failure in 0..7 {
            let mut invalid = scene.clone();
            match failure {
                0 => {
                    invalid.entities[0].components.insert(
                        "arena.unknown".into(),
                        toml::Value::Table(Default::default()),
                    );
                }
                1 => {
                    invalid.entities.retain(|e| e.id != "player-spawn");
                }
                2 => {
                    let e = invalid
                        .entities
                        .iter_mut()
                        .find(|e| e.id == "decoration-0")
                        .unwrap();
                    let mut c = e
                        .component::<scene::Scenery>("arena.scenery")
                        .unwrap()
                        .unwrap();
                    c.model = "missing".into();
                    e.set_component("arena.scenery", &c).unwrap();
                }
                3 => {
                    let e = invalid
                        .entities
                        .iter_mut()
                        .find(|e| e.id == "world")
                        .unwrap();
                    let mut c = e.component::<scene::World>("arena.world").unwrap().unwrap();
                    c.rules = "../escape.toml".into();
                    e.set_component("arena.world", &c).unwrap();
                }
                4 => {
                    let e = invalid
                        .entities
                        .iter_mut()
                        .find(|e| e.id == "player-spawn")
                        .unwrap();
                    let mut t = e
                        .component::<nico_scene::components::Transform>("nico.transform")
                        .unwrap()
                        .unwrap();
                    t.position[1] = 2.;
                    e.set_component("nico.transform", &t).unwrap();
                }
                5 => {
                    let e = invalid
                        .entities
                        .iter_mut()
                        .find(|e| e.components.contains_key("nico.directional_light"))
                        .unwrap();
                    let mut light = e
                        .component::<nico_scene::components::DirectionalLight>(
                            "nico.directional_light",
                        )
                        .unwrap()
                        .unwrap();
                    light.direction = [f32::MAX; 3];
                    e.set_component("nico.directional_light", &light).unwrap();
                }
                _ => {
                    let quest: scene::Quest = scene::single(&invalid, "arena.quest").unwrap();
                    invalid
                        .entities
                        .iter_mut()
                        .find(|e| e.id == quest.warden)
                        .unwrap()
                        .scope = nico_scene::ComponentScope::Client;
                }
            }
            assert!(
                ProjectContent::from_scene(project.clone(), invalid, HostRole::Server).is_err()
            );
        }
    }
}
