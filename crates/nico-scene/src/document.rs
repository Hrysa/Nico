use serde::{Deserialize, Serialize};
use std::{
    fs, io,
    path::{Component, Path, PathBuf},
};

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Object {
    pub id: u64,
    pub asset: PathBuf,
    pub name: String,
    pub position: [f32; 3],
    pub rotation: [f32; 3],
    pub scale: f32,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Document {
    pub version: u32,
    pub objects: Vec<Object>,
}
impl Default for Document {
    fn default() -> Self {
        Self {
            version: 1,
            objects: Vec::new(),
        }
    }
}
pub fn relative(path: &Path) -> bool {
    !path.as_os_str().is_empty() && path.components().all(|c| matches!(c, Component::Normal(_)))
}
impl Document {
    pub fn validate(&self) -> io::Result<()> {
        if self.version != 1 || self.objects.len() > 128 {
            return Err(io::Error::other(
                "unsupported scene version or more than 128 objects",
            ));
        }
        let mut ids = std::collections::BTreeSet::new();
        for o in &self.objects {
            if o.id == 0
                || !ids.insert(o.id)
                || !relative(&o.asset)
                || o.name.len() > 256
                || !o
                    .position
                    .iter()
                    .chain(&o.rotation)
                    .all(|v| v.is_finite() && v.abs() <= 10000.)
                || !o.scale.is_finite()
                || !(0.001..=1000.).contains(&o.scale)
            {
                return Err(io::Error::other("invalid scene object"));
            }
        }
        Ok(())
    }
    pub fn load(root: &Path) -> io::Result<Self> {
        let path = root.join("scene.nico.json");
        match Self::load_file(&path) {
            Ok(v) => Ok(v),
            Err(e) if e.kind() == io::ErrorKind::NotFound => Ok(Self::default()),
            Err(e) => Err(e),
        }
    }
    /// Read a bounded scene file. Missing declared scenes are errors.
    pub fn load_file(path: &Path) -> io::Result<Self> {
        use std::io::Read;
        let mut bytes = Vec::new();
        fs::File::open(path)?
            .take(1024 * 1024 + 1)
            .read_to_end(&mut bytes)?;
        if bytes.len() > 1024 * 1024 {
            return Err(io::Error::other("scene exceeds 1 MiB"));
        }
        let doc: Self = serde_json::from_slice(&bytes)?;
        doc.validate()?;
        Ok(doc)
    }
    pub fn save(&self, root: &Path) -> io::Result<()> {
        self.save_file(&root.join("scene.nico.json"))
    }
    /// Atomically replace the scene in an existing directory after validation.
    pub fn save_file(&self, path: &Path) -> io::Result<()> {
        use std::io::Write;
        self.validate()?;
        let parent = path
            .parent()
            .ok_or_else(|| io::Error::other("scene path has no parent"))?;
        let temporary = parent.join(format!(".scene-{}.tmp", std::process::id()));
        let mut file = fs::OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&temporary)?;
        let result = (|| {
            file.write_all(&serde_json::to_vec_pretty(self)?)?;
            file.sync_all()?;
            drop(file);
            fs::rename(&temporary, path)
        })();
        if result.is_err() {
            let _ = fs::remove_file(&temporary);
        }
        result
    }
}
