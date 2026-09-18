//! Game-provided authoring, called only at the editor runtime's update boundary.
//! Implementations own game formats; the host owns UI, history, commands and transport.
use nico_presentation::{Camera3d, Scene3d};
use nico_scene::{Document, Project};
use std::{collections::BTreeMap, io};

pub trait Session: Send + Sync {
    fn document(&self) -> Document;
    /// Validate and prepare the entire edit before replacing live state.
    fn replace(&mut self, document: &Document) -> io::Result<()>;
    fn render(&mut self, camera: Camera3d) -> Scene3d;
    fn save(&mut self) -> io::Result<()>;
    fn reload(&mut self) -> io::Result<()>;
    /// Reimport presentation assets without discarding authored edits.
    fn refresh_assets(&mut self) -> io::Result<()>;
    fn inspect(&self) -> serde_json::Value;
    fn transform_help(&self, id: u64) -> &str;
    fn initial_camera(&self) -> [f32; 3] {
        [0.5, 0.6, 60.]
    }
}
pub type Factory = fn(&Project) -> io::Result<Box<dyn Session>>;
#[derive(Default)]
pub struct Registry {
    factories: BTreeMap<String, Factory>,
}
impl Registry {
    pub fn register(&mut self, name: &str, factory: Factory) -> io::Result<()> {
        if self.factories.contains_key(name) {
            return Err(io::Error::other("duplicate authoring adapter"));
        }
        self.factories.insert(name.to_owned(), factory);
        Ok(())
    }
    pub fn open(&self, project: &Project) -> io::Result<Option<Box<dyn Session>>> {
        let Some(editor) = &project.manifest.editor else {
            return Ok(None);
        };
        let factory = self.factories.get(&editor.adapter).ok_or_else(|| {
            io::Error::other(format!(
                "authoring adapter '{}' is not registered in this editor build",
                editor.adapter
            ))
        })?;
        factory(project).map(Some)
    }
}
