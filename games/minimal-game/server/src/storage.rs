//! Optional game progress persistence. Play-session directory ownership stays in launch.
use minimal_game_shared::GameState;
use std::{
    fs,
    io::{self, Read, Write},
    path::Path,
};

pub fn load(directory: &Path, state: &mut GameState) -> io::Result<()> {
    fs::create_dir_all(directory)?;
    let file = match fs::File::open(directory.join("progress.json")) {
        Ok(file) => file,
        Err(error) if error.kind() == io::ErrorKind::NotFound => return Ok(()),
        Err(error) => return Err(error),
    };
    let mut bytes = Vec::new();
    file.take(16_385).read_to_end(&mut bytes)?;
    if bytes.len() > 16_384 {
        return Err(io::Error::other("saved progress exceeds 16 KiB"));
    }
    let progress = serde_json::from_slice(&bytes).map_err(io::Error::other)?;
    state.restore_progress(progress).map_err(io::Error::other)
}
pub fn save(directory: &Path, state: &GameState) -> io::Result<()> {
    let temporary = tempfile_path(directory);
    let mut file = fs::File::create_new(&temporary)?;
    let result = (|| {
        file.write_all(&serde_json::to_vec(&state.saved_progress())?)?;
        file.sync_all()?;
        drop(file);
        fs::rename(&temporary, directory.join("progress.json"))
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result
}
fn tempfile_path(directory: &Path) -> std::path::PathBuf {
    directory.join(format!("progress-{}.tmp", std::process::id()))
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn storage_is_explicit_isolated_and_invalid_progress_preserves_state() {
        let a = tempfile::tempdir().unwrap();
        let b = tempfile::tempdir().unwrap();
        let state = GameState::default();
        save(a.path(), &state).unwrap();
        let mut restored = GameState::default();
        load(a.path(), &mut restored).unwrap();
        assert_eq!(restored.saved_progress(), state.saved_progress());
        assert!(!b.path().join("progress.json").exists());
        fs::write(a.path().join("progress.json"), b"{\"version\":999}").unwrap();
        assert!(load(a.path(), &mut restored).is_err());
        assert_eq!(restored.saved_progress(), state.saved_progress());
    }
}
