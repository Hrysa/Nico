//! The authoring adapter edits the same composed scene loaded by both game hosts.
use crate::environment::Environment;
use arena_arpg_shared::project::ProjectContent;
use nico_authoring::Session;
use nico_presentation::{Camera3d, Scene3d, SceneLighting};
use nico_scene::{HostRole, Project, SceneDefinition};
use std::{fs, io};
pub const ADAPTER: &str = "arena-world-v1";
fn error(e: impl ToString) -> io::Error {
    io::Error::other(e.to_string())
}
pub fn open(project: &Project) -> io::Result<Box<dyn Session>> {
    Ok(Box::new(WorldEditor::open(project.clone())?))
}
struct WorldEditor {
    project: Project,
    original: Vec<u8>,
    revision: String,
    content: ProjectContent,
    environment: Environment,
}
fn environment(content: &ProjectContent) -> io::Result<Environment> {
    let mut environment = Environment::load(&content.source("visual")?).map_err(error)?;
    environment.ground_cache =
        Some(nico_assets::cache::ImportCache::new(content.root()).map_err(error)?);
    environment.apply_scene(&content.scene).map_err(error)?;
    environment.bind(&content.zone).map_err(error)?;
    Ok(environment)
}
impl WorldEditor {
    fn open(mut project: Project) -> io::Result<Self> {
        let entry = arena_arpg_shared::project::StartupScene::load(
            project.root(),
            Some(&project.manifest.default_scene),
        )?;
        if let Some(splash) = entry.splash {
            project.manifest.default_scene = splash.next_scene;
        }
        let original = fs::read(project.scene_path()?)?;
        let revision = nico_scene::content::revision(&project, &|| false)?;
        let content =
            ProjectContent::from_scene(project.clone(), project.load_scene()?, HostRole::Client)?;
        let environment = environment(&content)?;
        Ok(Self {
            project,
            original,
            revision,
            content,
            environment,
        })
    }
}
impl Session for WorldEditor {
    fn document(&self) -> SceneDefinition {
        self.content.scene.clone()
    }
    fn replace(&mut self, scene: &SceneDefinition) -> io::Result<()> {
        let content =
            ProjectContent::from_scene(self.project.clone(), scene.clone(), HostRole::Client)?;
        let environment = environment(&content)?;
        self.content = content;
        self.environment = environment;
        Ok(())
    }
    fn render(&mut self, camera: Camera3d) -> Scene3d {
        use nico_scene::components::{AmbientLight, DirectionalLight};
        let sun: DirectionalLight =
            arena_arpg_shared::scene::single(&self.content.scene, "nico.directional_light")
                .expect("validated scene light");
        let ambient: AmbientLight =
            arena_arpg_shared::scene::single(&self.content.scene, "nico.ambient_light")
                .expect("validated ambient light");
        let mut scene = Scene3d {
            camera,
            lighting: SceneLighting {
                direction: glam::Vec3::from_array(sun.direction).normalize().to_array(),
                radiance: sun.radiance,
                ambient: ambient.radiance,
            },
            ..Default::default()
        };
        self.environment.backdrop(camera, &mut scene);
        for i in 0..self.content.zone.obstacles.len() {
            self.environment.obstacle(i, None, &mut scene);
        }
        self.environment.decorate(None, &mut scene);
        scene
    }
    fn save(&mut self) -> io::Result<()> {
        let path = self.project.scene_path()?;
        if fs::read(&path)? != self.original {
            return Err(error("scene changed on disk; reload before saving"));
        }
        if nico_scene::content::revision(&self.project, &|| false)? != self.revision {
            return Err(error("project changed on disk; reload before saving"));
        }
        self.project.save_scene(&self.content.scene)?;
        self.original = fs::read(path)?;
        self.revision = nico_scene::content::revision(&self.project, &|| false)?;
        Ok(())
    }
    fn reload(&mut self) -> io::Result<()> {
        *self = Self::open(self.project.clone())?;
        Ok(())
    }
    fn refresh_assets(&mut self) -> io::Result<()> {
        if fs::read(self.project.scene_path()?)? != self.original {
            return Err(error("scene changed on disk; reload before refreshing"));
        }
        self.environment = environment(&self.content)?;
        Ok(())
    }
    fn inspect(&self) -> serde_json::Value {
        serde_json::json!({"adapter":ADAPTER,"scene":self.project.manifest.default_scene,"zone":self.content.zone,"environment":self.environment.inspection,"note":"Scene preview; monsters and quest rules are not simulated."})
    }
    fn transform_help(&self, _id: &str) -> &str {
        "Edit the entity's nico.transform component. Box colliders stay axis-aligned; scenery uses Y rotation and height_m."
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn adapter_edits_scene_components_and_rejects_invalid_replacements() {
        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("..");
        let mut editor = WorldEditor::open(Project::open(root).unwrap()).unwrap();
        let mut scene = editor.document();
        let entity = scene
            .entities
            .iter_mut()
            .find(|e| e.id == "decoration-0")
            .unwrap();
        let mut transform = entity
            .component::<nico_scene::components::Transform>("nico.transform")
            .unwrap()
            .unwrap();
        transform.position[0] += 0.5;
        entity.set_component("nico.transform", &transform).unwrap();
        editor.replace(&scene).unwrap();
        assert_eq!(editor.document(), scene);
        let mut invalid = scene.clone();
        invalid.entities.retain(|e| e.id != "camera");
        assert!(editor.replace(&invalid).is_err());
        assert_eq!(editor.document(), scene);
        editor.refresh_assets().unwrap();
        editor.reload().unwrap();
        assert_ne!(editor.document(), scene);
    }
}
