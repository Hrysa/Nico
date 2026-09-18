//! Arena's authoring adapter uses exactly the client's scenery and zone validation.
use crate::environment::{Decoration, Definition, Environment};
use arena_arpg_shared::open_world::content::ZoneDefinition;
use nico_authoring::Session;
use nico_presentation::{Camera3d, Scene3d};
use nico_scene::{Document, Object, Project};
use std::{
    collections::BTreeMap,
    fs, io,
    path::{Path, PathBuf},
};

pub const ADAPTER: &str = "arena-world-v1";
fn error(e: impl ToString) -> io::Error {
    io::Error::other(e.to_string())
}
pub fn open(project: &Project) -> io::Result<Box<dyn Session>> {
    Ok(Box::new(WorldEditor::open(project.clone())?))
}
struct WorldEditor {
    project: Project,
    logic_path: PathBuf,
    visual_path: PathBuf,
    original_logic: Vec<u8>,
    original_visual: Vec<u8>,
    base_zone: ZoneDefinition,
    base_visual: Definition,
    zone: ZoneDefinition,
    environment: Environment,
    document: Document,
    assets: BTreeMap<String, PathBuf>,
}
impl WorldEditor {
    fn open(project: Project) -> io::Result<Self> {
        let declaration = project
            .manifest
            .editor
            .as_ref()
            .ok_or_else(|| error("missing adapter"))?;
        if declaration.sources.len() != 2 {
            return Err(error("Arena requires logic and visual sources"));
        }
        let source = |name: &str| {
            project.resolve_asset(
                declaration
                    .sources
                    .get(name)
                    .ok_or_else(|| error(format!("missing {name} source")))?,
            )
        };
        let logic_path = source("logic")?;
        let visual_path = source("visual")?;
        let original_logic = fs::read(&logic_path)?;
        let original_visual = fs::read(&visual_path)?;
        let zone = ZoneDefinition::load(&logic_path).map_err(error)?;
        let mut environment = Environment::load(&visual_path).map_err(error)?;
        if zone.id != environment.definition.zone {
            return Err(error("logic and visual zone IDs differ"));
        }
        environment.bind(&zone).map_err(error)?;
        let mut assets = BTreeMap::new();
        for (key, path) in &environment.definition.models {
            let full = visual_path.parent().unwrap().join(path).canonicalize()?;
            let relative = full
                .strip_prefix(project.root())
                .map_err(error)?
                .to_path_buf();
            project.resolve_asset(&relative)?;
            assets.insert(key.clone(), relative);
        }
        let mut document = Document::default();
        for (i, obstacle) in zone.obstacles.iter().enumerate() {
            let binding = environment
                .definition
                .obstacles
                .get(&obstacle.id)
                .ok_or_else(|| {
                    error(format!("obstacle '{}' has no visual binding", obstacle.id))
                })?;
            document.objects.push(Object {
                id: i as u64 + 1,
                name: format!("Obstacle: {}", obstacle.id),
                asset: assets[&binding.model].clone(),
                position: obstacle.center.map(|v| v as f32),
                rotation: [0.; 3],
                scale: 1.,
            });
        }
        for (i, d) in environment.definition.decorations.iter().enumerate() {
            document.objects.push(Object {
                id: 1000 + i as u64,
                name: format!("Decoration: {} {}", d.model, i + 1),
                asset: assets[&d.model].clone(),
                position: d.position,
                rotation: [0., d.yaw_radians.to_degrees(), 0.],
                scale: d.height_m,
            });
        }
        document.validate()?;
        Ok(Self {
            project,
            logic_path,
            visual_path,
            original_logic,
            original_visual,
            base_zone: zone.clone(),
            base_visual: environment.definition.clone(),
            zone,
            environment,
            document,
            assets,
        })
    }
}
impl Session for WorldEditor {
    fn document(&self) -> Document {
        self.document.clone()
    }
    fn replace(&mut self, document: &Document) -> io::Result<()> {
        self.project.validate_scene(document)?;
        let mut zone = self.base_zone.clone();
        let mut visual = self.base_visual.clone();
        visual.decorations.clear();
        for (i, obstacle) in zone.obstacles.iter_mut().enumerate() {
            let object = document
                .objects
                .iter()
                .find(|o| o.id == i as u64 + 1)
                .ok_or_else(|| error("obstacles cannot be removed by the transform editor"))?;
            let binding = visual.obstacles.get_mut(&obstacle.id).unwrap();
            if object.rotation != [0.; 3] || object.asset != self.assets[&binding.model] {
                return Err(error(
                    "obstacles use axis-aligned collision boxes; rotation/model changes are unsupported",
                ));
            }
            if object.position != obstacle.center.map(|v| v as f32) {
                obstacle.center = object.position.map(f64::from);
            }
            obstacle.size = obstacle.size.map(|v| v * f64::from(object.scale));
            if let Some(height) = &mut binding.height_m {
                *height *= object.scale;
                if !(0.05..=20.).contains(height) {
                    return Err(error("obstacle visual height must be 0.05..20 metres"));
                }
            }
        }
        zone.validate().map_err(error)?;
        for object in &document.objects {
            if object.id <= zone.obstacles.len() as u64 {
                continue;
            }
            if object.id < 1000
                || object.rotation[0] != 0.
                || object.rotation[2] != 0.
                || !(0.05..=20.).contains(&object.scale)
                || object.position.iter().any(|v| v.abs() > 128.)
            {
                return Err(error(
                    "decorations accept position within ±128m, Y rotation, and height 0.05..20m",
                ));
            }
            let model = self
                .assets
                .iter()
                .find(|(_, p)| **p == object.asset)
                .map(|(key, _)| key.clone())
                .ok_or_else(|| {
                    error("add a model registered in the visual world's models table")
                })?;
            let autumn = self
                .base_visual
                .decorations
                .get((object.id - 1000) as usize)
                .is_some_and(|d| d.autumn);
            visual.decorations.push(Decoration {
                model,
                position: object.position,
                height_m: object.scale,
                yaw_radians: object.rotation[1].to_radians(),
                autumn,
            });
        }
        let old = std::mem::replace(&mut self.environment.definition, visual);
        if let Err(e) = self.environment.bind(&zone) {
            self.environment.definition = old;
            return Err(error(e));
        }
        self.zone = zone;
        self.document = document.clone();
        Ok(())
    }
    fn render(&mut self, camera: Camera3d) -> Scene3d {
        let mut scene = Scene3d {
            camera,
            ..Default::default()
        };
        scene.camera.far = 5000.;
        self.environment.backdrop(scene.camera, &mut scene);
        // Submit solids before decorative foliage, matching the game client's priority.
        for i in 0..self.zone.obstacles.len() {
            self.environment.obstacle(i, None, &mut scene);
        }
        self.environment.decorate(None, &mut scene);
        scene
    }
    fn save(&mut self) -> io::Result<()> {
        if fs::read(&self.logic_path)? != self.original_logic
            || fs::read(&self.visual_path)? != self.original_visual
        {
            return Err(error(
                "world sources changed on disk; save refused to overwrite external edits",
            ));
        }
        let logic = toml::to_string_pretty(&self.zone)
            .map_err(error)?
            .into_bytes();
        let visual = toml::to_string_pretty(&self.environment.definition)
            .map_err(error)?
            .into_bytes();
        // Each file is replaced atomically; roll back logic if the second replacement fails.
        let saved_zone: ZoneDefinition =
            toml::from_str(std::str::from_utf8(&self.original_logic).map_err(error)?)
                .map_err(error)?;
        let logic_changed = toml::to_string_pretty(&saved_zone)
            .map_err(error)?
            .as_bytes()
            != logic;
        if logic_changed {
            write(&self.logic_path, &logic)?;
        }
        if let Err(e) = write(&self.visual_path, &visual) {
            if logic_changed && let Err(rollback) = write(&self.logic_path, &self.original_logic) {
                return Err(error(format!(
                    "visual save failed: {e}; logic rollback failed: {rollback}"
                )));
            }
            return Err(e);
        }
        if logic_changed {
            self.original_logic = logic;
        }
        self.original_visual = visual;
        Ok(())
    }
    fn reload(&mut self) -> io::Result<()> {
        *self = Self::open(self.project.clone())?;
        Ok(())
    }
    fn refresh_assets(&mut self) -> io::Result<()> {
        if fs::read(&self.visual_path)? != self.original_visual {
            return Err(error(
                "visual definition changed on disk; reload it before refreshing models",
            ));
        }
        let mut environment = Environment::load(&self.visual_path).map_err(error)?;
        environment.definition = self.environment.definition.clone();
        environment.bind(&self.zone).map_err(error)?;
        self.environment = environment;
        Ok(())
    }
    fn inspect(&self) -> serde_json::Value {
        serde_json::json!({"adapter":ADAPTER,"logic_source":self.logic_path,"visual_source":self.visual_path,"environment":self.environment.inspection,"zone":self.zone,"note":"Authored world preview; monsters and quest data are not simulated. Decoration scale is height in metres. Obstacle scale multiplies the original collision box."})
    }
    fn transform_help(&self, id: u64) -> &str {
        if id < 1000 {
            "Obstacle: position is collider center; scale multiplies its original size. Rotation and removal are unsupported. Game zone validation applies."
        } else {
            "Decoration: position is its ground anchor; only Y rotation is supported. Scale is canopy height in metres (0.05–20)."
        }
    }
}
fn write(path: &Path, bytes: &[u8]) -> io::Result<()> {
    use io::Write;
    let temporary = path.with_extension(format!("editor-{}.tmp", std::process::id()));
    let mut file = fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(&temporary)?;
    let result = (|| {
        file.write_all(bytes)?;
        file.sync_all()?;
        drop(file);
        fs::rename(&temporary, path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(temporary);
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    fn fixture() -> tempfile::TempDir {
        let root = tempfile::tempdir().unwrap();
        let source = Path::new(env!("CARGO_MANIFEST_DIR")).join("..");
        let visual = Path::new("assets/presentation/worlds/meadow.world-vis.toml");
        let logic = Path::new("assets/logic/worlds/meadow.world.toml");
        let definition: Definition =
            toml::from_str(&fs::read_to_string(source.join(visual)).unwrap()).unwrap();
        let mut paths = vec![
            PathBuf::from("nico.project.toml"),
            visual.into(),
            logic.into(),
        ];
        paths.extend(
            definition
                .models
                .values()
                .map(|p| visual.parent().unwrap().join(p)),
        );
        for path in paths {
            fs::create_dir_all(root.path().join(&path).parent().unwrap()).unwrap();
            fs::copy(source.join(&path), root.path().join(&path)).unwrap();
        }
        root
    }
    #[test]
    fn real_world_edits_render_save_reload_and_preserve_logic_and_conflicts() {
        let root = fixture();
        let mut editor = WorldEditor::open(Project::open(root.path()).unwrap()).unwrap();
        assert_eq!(editor.zone.obstacles.len(), 13);
        assert_eq!(editor.document.objects.len(), 99);
        let original_logic = editor.original_logic.clone();
        let mut edited = editor.document();
        let decoration = edited.objects.iter_mut().find(|o| o.id == 1000).unwrap();
        decoration.position[0] += 0.25;
        decoration.scale *= 1.05;
        let mut invalid = edited.clone();
        invalid
            .objects
            .iter_mut()
            .find(|o| o.id == 1000)
            .unwrap()
            .rotation[0] = 30.;
        let before = editor.document();
        assert!(editor.replace(&invalid).is_err());
        assert_eq!(editor.document(), before);
        editor.replace(&edited).unwrap();
        let camera = Camera3d::looking_at([25., 35., 40.], [0.; 3], [0., 1., 0.]).unwrap();
        let scene = editor.render(camera);
        assert!(scene.meshes.len() > 30 && scene.meshes.len() <= 256);
        assert_eq!(editor.inspect()["environment"]["loaded_models"], 13);
        editor.save().unwrap();
        assert_eq!(fs::read(&editor.logic_path).unwrap(), original_logic);
        let saved_visual: Definition =
            toml::from_str(&fs::read_to_string(&editor.visual_path).unwrap()).unwrap();
        assert_eq!(
            saved_visual.decorations[0].position,
            edited
                .objects
                .iter()
                .find(|o| o.id == 1000)
                .unwrap()
                .position
        );
        assert_eq!(
            saved_visual.decorations[0].autumn,
            editor.base_visual.decorations[0].autumn
        );
        editor.reload().unwrap();
        assert_eq!(editor.document(), edited);
        // A failed visual replacement must restore the already-written logic file.
        let mut obstacle_edit = editor.document();
        obstacle_edit.objects[0].position[0] += 0.25;
        editor.replace(&obstacle_edit).unwrap();
        let blocked_temp = editor
            .visual_path
            .with_extension(format!("editor-{}.tmp", std::process::id()));
        fs::write(&blocked_temp, b"occupied").unwrap();
        assert!(editor.save().is_err());
        assert_eq!(fs::read(&editor.logic_path).unwrap(), original_logic);
        fs::remove_file(blocked_temp).unwrap();
        editor.save().unwrap();
        let game_zone = ZoneDefinition::load(&editor.logic_path).unwrap();
        assert_eq!(
            game_zone.obstacles[0].center[0],
            f64::from(obstacle_edit.objects[0].position[0])
        );
        let mut game_environment = Environment::load(&editor.visual_path).unwrap();
        game_environment.bind(&game_zone).unwrap();
        // External edits remain untouched even if our in-memory document is valid.
        let external = format!(
            "{}\n# external edit\n",
            fs::read_to_string(&editor.visual_path).unwrap()
        );
        fs::write(&editor.visual_path, &external).unwrap();
        assert!(editor.save().is_err());
        assert_eq!(fs::read_to_string(&editor.visual_path).unwrap(), external);
    }
}
