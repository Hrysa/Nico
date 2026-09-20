//! Arena's saved-project path contract, shared by its native hosts.
use std::{
    io,
    path::{Path, PathBuf},
};
pub struct ProjectContent {
    project: nico_scene::Project,
    pub revision: String,
}
impl ProjectContent {
    pub fn open(root: &Path) -> io::Result<Self> {
        let project = nico_scene::Project::open(root)?;
        if project
            .manifest
            .editor
            .as_ref()
            .map(|editor| editor.adapter.as_str())
            != Some("arena-world-v1")
        {
            return Err(io::Error::other(
                "Arena requires the arena-world-v1 project adapter",
            ));
        }
        let revision = nico_scene::content::revision(&project, &|| false)?;
        Ok(Self { project, revision })
    }
    pub fn source(&self, name: &str) -> io::Result<PathBuf> {
        let relative = self
            .project
            .manifest
            .editor
            .as_ref()
            .unwrap()
            .sources
            .get(name)
            .ok_or_else(|| io::Error::other(format!("Arena project has no {name} source")))?;
        self.project.resolve_asset(relative)
    }
    pub fn asset(&self, relative: &str) -> io::Result<PathBuf> {
        let relative = Path::new(relative);
        if !nico_scene::relative(relative)
            || !self
                .project
                .manifest
                .play
                .content_roots
                .iter()
                .chain(&self.project.manifest.asset_roots)
                .any(|root| relative.starts_with(root))
        {
            return Err(io::Error::other(
                "Arena play asset is outside declared content roots",
            ));
        }
        let path = self.project.root().join(relative).canonicalize()?;
        if !path.starts_with(self.project.root()) {
            return Err(io::Error::other("Arena play asset escapes its project"));
        }
        Ok(path)
    }
    pub fn verify(&self) -> io::Result<()> {
        if nico_scene::content::revision(&self.project, &|| false)? != self.revision {
            return Err(io::Error::other("Arena content changed during startup"));
        }
        Ok(())
    }
}
