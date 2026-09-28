//! Scene-rooted project content shared by client and server startup.
use crate::scene;
use nico_scene::{HostRole, SceneDefinition};
use std::{
    io,
    path::{Path, PathBuf},
};
pub const DEFAULT_PROJECT: &str = "games/arena-arpg";
pub struct SceneEntities(pub Vec<nico_ecs::Entity>);
pub struct ProjectContent {
    project: nico_scene::Project,
    pub revision: String,
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
        let mut project = nico_scene::Project::open(root)?;
        if let Some(path) = selected {
            project.resolve_asset(path)?;
            project.manifest.default_scene = path.into();
        }
        let scene = project.load_scene()?;
        Self::from_scene(project, scene, role)
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
        let revision = nico_scene::content::revision(&project, &|| false)?;
        Ok(Self {
            project,
            revision,
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
    pub fn verify(&self) -> io::Result<()> {
        if nico_scene::content::revision(&self.project, &|| false)? != self.revision {
            return Err(error("scene content changed during startup"));
        }
        Ok(())
    }
}
#[cfg(test)]
mod tests {
    use super::*;
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
    fn scene_selection_changes_mode_and_revision_and_missing_scene_fails() {
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
        assert_ne!(meadow.revision, arena.revision);
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
        let project = nico_scene::Project::open(&root).unwrap();
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
