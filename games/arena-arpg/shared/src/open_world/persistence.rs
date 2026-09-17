//! Versioned local character storage. One server holds the directory lock.
//! Save writes and syncs a temporary file before replacing the committed record.
use super::CharacterRecord;
use std::{
    fs::{self, File, OpenOptions},
    io::{self, Read, Write},
    path::{Path, PathBuf},
};
pub struct CharacterStore {
    root: PathBuf,
    _lock: File,
}
impl CharacterStore {
    pub fn open(root: &Path) -> io::Result<Self> {
        fs::create_dir_all(root)?;
        let lock = OpenOptions::new()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(root.join(".lock"))?;
        lock.try_lock()
            .map_err(|e| io::Error::other(format!("character directory already in use: {e}")))?;
        Ok(Self {
            root: root.into(),
            _lock: lock,
        })
    }
    fn path(&self, name: &str) -> io::Result<PathBuf> {
        CharacterRecord::new(name.into(), 1)
            .validate()
            .map_err(io::Error::other)?;
        Ok(self.root.join(format!("character-{name}.toml")))
    }
    pub fn load(&self, name: &str) -> io::Result<Option<CharacterRecord>> {
        let path = self.path(name)?;
        let file = match File::open(path) {
            Ok(f) => f,
            Err(e) if e.kind() == io::ErrorKind::NotFound => return Ok(None),
            Err(e) => return Err(e),
        };
        let mut text = String::new();
        file.take(65537).read_to_string(&mut text)?;
        if text.len() > 65536 {
            return Err(io::Error::other("character record exceeds limit"));
        }
        let record: CharacterRecord = toml::from_str(&text).map_err(io::Error::other)?;
        record.validate().map_err(io::Error::other)?;
        if record.name != name {
            return Err(io::Error::other("character record identity mismatch"));
        }
        Ok(Some(record))
    }
    pub fn save(&self, record: &CharacterRecord) -> io::Result<()> {
        record.validate().map_err(io::Error::other)?;
        let path = self.path(&record.name)?;
        let temporary = path.with_extension("pending");
        let text = toml::to_string(record).map_err(io::Error::other)?;
        let mut file = File::create(&temporary)?;
        file.write_all(text.as_bytes())?;
        file.sync_all()?;
        drop(file);
        fs::rename(temporary, path)
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn saves_survive_reopen_and_reject_duplicate_writer_or_bad_records() {
        let root = std::env::temp_dir().join(format!(
            "nico-world-save-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        ));
        let store = CharacterStore::open(&root).unwrap();
        assert!(CharacterStore::open(&root).is_err());
        assert!(store.load("hero").unwrap().is_none());
        assert!(store.load("../escape").is_err());
        let mut record = CharacterRecord::new("hero".into(), 80);
        record.experience = 30;
        record.inventory.push(super::super::ITEM_SWORD.into());
        record.equipped = Some(super::super::ITEM_SWORD.into());
        store.save(&record).unwrap();
        record.experience = 40;
        store.save(&record).unwrap();
        drop(store);
        let store = CharacterStore::open(&root).unwrap();
        assert_eq!(store.load("hero").unwrap(), Some(record));
        fs::write(root.join("character-hero.toml"), "invalid").unwrap();
        assert!(store.load("hero").is_err());
        drop(store);
        fs::remove_dir_all(root).unwrap();
    }
}
