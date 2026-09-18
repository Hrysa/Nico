use crate::{Document, relative};
use serde::{Deserialize, Serialize};
use std::{
    fs,
    io::{self, Read},
    path::{Path, PathBuf},
};

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Targets {
    pub client: Option<String>,
    pub server: Option<String>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct EditorDefinition {
    pub adapter: String,
    pub sources: std::collections::BTreeMap<String, PathBuf>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ProjectManifest {
    pub version: u32,
    pub name: String,
    pub asset_roots: Vec<PathBuf>,
    pub default_scene: PathBuf,
    #[serde(default)]
    pub targets: Targets,
    #[serde(default)]
    pub editor: Option<EditorDefinition>,
}
#[derive(Clone, Debug)]
pub struct Project {
    root: PathBuf,
    pub manifest: ProjectManifest,
    declared: bool,
}
impl Project {
    /// Manifest projects use game-root-relative assets. A directory without a
    /// manifest retains the initial editor's loose-content behavior.
    pub fn open(root: impl AsRef<Path>) -> io::Result<Self> {
        let root = root.as_ref().canonicalize()?;
        if !root.is_dir() {
            return Err(io::Error::other("project root must be a directory"));
        }
        let mut text = String::new();
        let declared = match fs::File::open(root.join("nico.project.toml")) {
            Ok(file) => {
                file.take(65537).read_to_string(&mut text)?;
                true
            }
            Err(e) if e.kind() == io::ErrorKind::NotFound => false,
            Err(e) => return Err(e),
        };
        if text.len() > 65536 {
            return Err(io::Error::other("project manifest exceeds 64 KiB"));
        }
        let manifest: ProjectManifest = if declared {
            toml::from_str(&text).map_err(io::Error::other)?
        } else {
            ProjectManifest {
                version: 1,
                name: root
                    .file_name()
                    .unwrap_or_default()
                    .to_string_lossy()
                    .into_owned(),
                asset_roots: vec![PathBuf::from(".")],
                default_scene: "scene.nico.json".into(),
                targets: Targets::default(),
                editor: None,
            }
        };
        if manifest.version != 1
            || manifest.name.is_empty()
            || manifest.name.len() > 256
            || manifest.asset_roots.is_empty()
            || manifest.asset_roots.len() > 16
            || !relative(&manifest.default_scene)
            || manifest
                .asset_roots
                .iter()
                .any(|p| !relative(p) && (declared || p != Path::new(".")))
        {
            return Err(io::Error::other(
                "invalid project version, name, asset roots, or default scene",
            ));
        }
        for target in [&manifest.targets.client, &manifest.targets.server]
            .into_iter()
            .flatten()
        {
            if target.is_empty()
                || target.len() > 128
                || !target
                    .bytes()
                    .all(|b| b.is_ascii_alphanumeric() || matches!(b, b'_' | b'-'))
            {
                return Err(io::Error::other("game targets must be Cargo package names"));
            }
        }
        let project = Self {
            root,
            manifest,
            declared,
        };
        for directory in &project.manifest.asset_roots {
            let path = project.contained(directory)?;
            if !path.is_dir() {
                return Err(io::Error::other("asset root must be a directory"));
            }
        }
        if let Some(editor) = &project.manifest.editor {
            if editor.adapter.is_empty()
                || editor.adapter.len() > 128
                || editor.sources.is_empty()
                || editor.sources.len() > 16
            {
                return Err(io::Error::other("invalid editor adapter declaration"));
            }
            for path in editor.sources.values() {
                project.resolve_asset(path)?;
            }
        }
        // Validate even absent scenes against their real parent directory.
        project.scene_path()?;
        Ok(project)
    }
    pub fn root(&self) -> &Path {
        &self.root
    }
    pub fn is_declared(&self) -> bool {
        self.declared
    }
    fn contained(&self, relative: &Path) -> io::Result<PathBuf> {
        let path = self.root.join(relative).canonicalize()?;
        if !path.starts_with(&self.root) {
            return Err(io::Error::other(
                "project path escapes through a filesystem link",
            ));
        }
        Ok(path)
    }
    pub fn scene_path(&self) -> io::Result<PathBuf> {
        let path = self.root.join(&self.manifest.default_scene);
        let parent = path.parent().unwrap().canonicalize()?;
        if !parent.starts_with(&self.root) {
            return Err(io::Error::other("scene parent escapes project"));
        }
        if path.exists() {
            self.contained(&self.manifest.default_scene)
        } else {
            Ok(path)
        }
    }
    pub fn load_scene(&self) -> io::Result<Document> {
        if self.manifest.editor.is_some() {
            return Err(io::Error::other(
                "this project requires its registered authoring adapter",
            ));
        }
        let scene = if self.declared {
            Document::load_file(&self.scene_path()?)
        } else {
            Document::load(&self.root)
        }?;
        self.validate_scene(&scene)?;
        Ok(scene)
    }
    pub fn save_scene(&self, scene: &Document) -> io::Result<()> {
        if self.manifest.editor.is_some() {
            return Err(io::Error::other(
                "this project requires its registered authoring adapter",
            ));
        }
        self.validate_scene(scene)?;
        scene.save_file(&self.scene_path()?)
    }
    /// Validate reference membership independently of source availability, so an
    /// editor can still save and repair scenes with missing source content.
    pub fn validate_scene(&self, scene: &Document) -> io::Result<()> {
        scene.validate()?;
        for object in &scene.objects {
            if !self.allows_asset(&object.asset) {
                return Err(io::Error::other(format!(
                    "asset is outside declared roots: {}",
                    object.asset.display()
                )));
            }
        }
        Ok(())
    }
    pub fn allows_asset(&self, path: &Path) -> bool {
        relative(path)
            && self
                .manifest
                .asset_roots
                .iter()
                .any(|r| r == Path::new(".") || path.starts_with(r))
    }
    pub fn resolve_asset(&self, path: &Path) -> io::Result<PathBuf> {
        if !self.allows_asset(path) {
            return Err(io::Error::other("asset is outside declared roots"));
        }
        self.contained(path)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn setup() -> tempfile::TempDir {
        let root = tempfile::tempdir().unwrap();
        fs::create_dir(root.path().join("assets")).unwrap();
        fs::create_dir(root.path().join("scenes")).unwrap();
        fs::write(root.path().join("nico.project.toml"), "version = 1\nname = 'Test'\nasset_roots = ['assets']\ndefault_scene = 'scenes/main.json'\n[targets]\nclient = 'test-client'\n").unwrap();
        root
    }
    #[test]
    fn manifest_selects_scene_and_roots_and_missing_declared_scene_fails() {
        let root = setup();
        let project = Project::open(root.path()).unwrap();
        assert!(project.load_scene().is_err());
        assert!(project.allows_asset(Path::new("assets/model.glb")));
        assert!(!project.allows_asset(Path::new("client/model.glb")));
        assert!(!project.allows_asset(Path::new("assets/../outside.glb")));
        project.save_scene(&Document::default()).unwrap();
        assert_eq!(project.load_scene().unwrap(), Document::default());
        assert_eq!(
            project.manifest.targets.client.as_deref(),
            Some("test-client")
        );
    }
    #[test]
    fn adapter_projects_reject_generic_scene_io_and_escaping_sources() {
        let root = setup();
        let manifest = root.path().join("nico.project.toml");
        let original = fs::read_to_string(&manifest).unwrap();
        fs::write(root.path().join("assets/world.toml"), "game_data = true").unwrap();
        let adapter = format!(
            "{original}\n[editor]\nadapter = 'game-world'\n[editor.sources]\nworld = 'assets/world.toml'\n"
        );
        fs::write(&manifest, &adapter).unwrap();
        let project = Project::open(root.path()).unwrap();
        assert!(project.load_scene().is_err());
        assert!(project.save_scene(&Document::default()).is_err());
        fs::write(
            manifest,
            adapter.replace("assets/world.toml", "../world.toml"),
        )
        .unwrap();
        assert!(Project::open(root.path()).is_err());
    }
    #[test]
    fn loading_and_saving_reject_references_outside_declared_roots() {
        let root = setup();
        let project = Project::open(root.path()).unwrap();
        let scene = Document {
            version: 1,
            objects: vec![crate::Object {
                id: 1,
                asset: "client/private.glb".into(),
                name: "Invalid".into(),
                position: [0.; 3],
                rotation: [0.; 3],
                scale: 1.,
            }],
        };
        assert!(project.save_scene(&scene).is_err());
        scene.save_file(&project.scene_path().unwrap()).unwrap();
        assert!(project.load_scene().is_err());
    }
    #[test]
    fn unsupported_manifest_versions_and_escaping_scene_paths_fail() {
        let root = setup();
        let path = root.path().join("nico.project.toml");
        let original = fs::read_to_string(&path).unwrap();
        for text in [
            original.replace("version = 1", "version = 2"),
            original.replace("scenes/main.json", "../outside.json"),
            original.replace("test-client", "cargo run --evil"),
        ] {
            fs::write(&path, text).unwrap();
            assert!(Project::open(root.path()).is_err());
        }
    }
    #[test]
    fn scene_instantiation_validates_before_modifying_world() {
        let mut world = nico_ecs::World::new();
        let object = crate::Object {
            id: 1,
            asset: "assets/cube.glb".into(),
            name: "Cube".into(),
            position: [1., 2., 3.],
            rotation: [0.; 3],
            scale: 2.,
        };
        let mut doc = Document {
            version: 1,
            objects: vec![object.clone()],
        };
        let entities = crate::instantiate(&doc, &mut world).unwrap();
        assert_eq!(
            *world.entities().get::<&crate::Object>(entities[0]).unwrap(),
            object
        );
        doc.objects.push(object);
        assert!(crate::instantiate(&doc, &mut world).is_err());
        assert_eq!(world.entities().len(), 1);
    }
}
