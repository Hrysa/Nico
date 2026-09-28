use crate::{SceneDefinition, relative};
use nico_assets::definition::DefinitionValidation;
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
pub struct AuthoringDefinition {
    pub adapter: String,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ProjectManifest {
    pub version: u32,
    pub name: String,
    pub default_scene: PathBuf,
    #[serde(default)]
    pub targets: Targets,
    #[serde(default)]
    pub authoring: Option<AuthoringDefinition>,
}
#[derive(Clone, Debug)]
pub struct Project {
    root: PathBuf,
    pub manifest: ProjectManifest,
    declared: bool,
}
impl Project {
    /// Project asset paths are relative to the game folder and stay under `assets`.
    /// A directory without a manifest can still hold a loose scene.
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
                default_scene: "scene.nico.toml".into(),
                targets: Targets::default(),
                authoring: None,
            }
        };
        if manifest.version != 1
            || manifest.name.is_empty()
            || manifest.name.len() > 256
            || !relative(&manifest.default_scene)
        {
            return Err(io::Error::other(
                "invalid project version, name, or default scene",
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
        if project.declared && !project.contained(Path::new("assets"))?.is_dir() {
            return Err(io::Error::other("asset root must be a directory"));
        }
        if let Some(authoring) = &project.manifest.authoring
            && (authoring.adapter.is_empty() || authoring.adapter.len() > 128)
        {
            return Err(io::Error::other("invalid authoring adapter declaration"));
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
    pub fn load_scene(&self) -> io::Result<SceneDefinition> {
        let scene = SceneDefinition::load(&self.scene_path()?)
            .map_err(|e| io::Error::other(e.to_string()))?;
        self.validate_scene(&scene)?;
        Ok(scene)
    }
    pub fn save_scene(&self, scene: &SceneDefinition) -> io::Result<()> {
        self.validate_scene(scene)?;
        scene.save_file(&self.scene_path()?)
    }
    /// Structural validation. Component registries validate typed values and resolve asset references before instantiation.
    pub fn validate_scene(&self, scene: &SceneDefinition) -> io::Result<()> {
        scene
            .validate()
            .map_err(|e| io::Error::other(e.to_string()))
    }
    pub fn allows_asset(&self, path: &Path) -> bool {
        relative(path) && path.starts_with("assets")
    }
    pub fn resolve_asset(&self, path: &Path) -> io::Result<PathBuf> {
        if !self.allows_asset(path) {
            return Err(io::Error::other("asset is outside the assets folder"));
        }
        self.contained(path)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn declared_scenes_load_even_with_authoring_and_missing_scenes_fail() {
        let root = tempfile::tempdir().unwrap();
        fs::create_dir(root.path().join("assets")).unwrap();
        fs::write(root.path().join("nico.project.toml"),"version=1\nname='Test'\ndefault_scene='assets/main.scene.toml'\n[authoring]\nadapter='game'\n").unwrap();
        let project = Project::open(root.path()).unwrap();
        assert!(project.load_scene().is_err());
        project.save_scene(&SceneDefinition::default()).unwrap();
        assert_eq!(project.load_scene().unwrap(), SceneDefinition::default());
        assert!(project.resolve_asset(Path::new("../outside")).is_err());
        assert!(!project.allows_asset(Path::new("assets/../outside")));
        assert!(!project.allows_asset(Path::new("other/model")));
    }
}
