//! Editor-owned client launch. The headless runtime never sees a child process;
//! the editor reconciles this session at its update boundary.
use std::{
    fs, io,
    path::{Path, PathBuf},
    process::{Child, Command},
};

#[derive(Default)]
pub struct PlaySession {
    child: Option<Child>,
}

impl PlaySession {
    pub fn start(&mut self, game_root: &Path, target: &str) -> io::Result<()> {
        if self.poll() {
            return Ok(());
        }
        let mut command = Command::new("cargo");
        command
            .args(["run", "-p", target])
            .current_dir(working_directory(game_root));
        self.spawn(&mut command)
    }
    pub fn stop(&mut self) -> io::Result<()> {
        let Some(mut child) = self.child.take() else {
            return Ok(());
        };
        let result = terminate(&mut child);
        let _ = child.wait();
        result
    }
    pub fn poll(&mut self) -> bool {
        let Some(child) = &mut self.child else {
            return false;
        };
        match child.try_wait() {
            Ok(Some(_)) | Err(_) => {
                self.child = None;
                false
            }
            Ok(None) => true,
        }
    }
    fn spawn(&mut self, command: &mut Command) -> io::Result<()> {
        if self.poll() {
            return Ok(());
        }
        self.child = Some(command.spawn()?);
        Ok(())
    }
}

/// Game clients resolve shader and content defaults from the Cargo workspace root.
fn working_directory(game_root: &Path) -> PathBuf {
    game_root
        .ancestors()
        .find(|directory| {
            fs::read_to_string(directory.join("Cargo.toml"))
                .is_ok_and(|manifest| manifest.contains("[workspace]"))
        })
        .map_or_else(|| game_root.to_path_buf(), Path::to_path_buf)
}

#[cfg(windows)]
fn terminate(child: &mut Child) -> io::Result<()> {
    use std::process::Stdio;
    let status = Command::new("taskkill")
        .args(["/PID", &child.id().to_string(), "/T", "/F"])
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()?;
    if status.success() {
        Ok(())
    } else {
        child.kill()
    }
}

#[cfg(not(windows))]
fn terminate(child: &mut Child) -> io::Result<()> {
    child.kill()
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::process::Command;
    #[test]
    fn play_session_starts_and_stops_a_child_process() {
        let mut session = PlaySession::default();
        let mut command = if cfg!(windows) {
            let mut command = Command::new("cmd");
            command.args(["/C", "ping -n 30 127.0.0.1 > NUL"]);
            command
        } else {
            let mut command = Command::new("sleep");
            command.arg("30");
            command
        };
        session.spawn(&mut command).unwrap();
        assert!(session.poll());
        session.stop().unwrap();
        assert!(!session.poll());
        assert!(session.stop().is_ok());
    }
    #[test]
    fn working_directory_prefers_the_enclosing_workspace_root() {
        let root = tempfile::tempdir().unwrap();
        let workspace = root.path().join("outer");
        let game = workspace.join("games").join("demo");
        fs::create_dir_all(&game).unwrap();
        fs::write(workspace.join("Cargo.toml"), "[workspace]\nmembers = []\n").unwrap();
        fs::write(game.join("Cargo.toml"), "[package]\nname = 'demo'\n").unwrap();
        assert_eq!(working_directory(&game), workspace);
    }
    #[test]
    fn working_directory_falls_back_to_the_game_root() {
        let root = tempfile::tempdir().unwrap();
        let game = root.path().join("game");
        fs::create_dir_all(&game).unwrap();
        fs::write(game.join("Cargo.toml"), "[package]\nname = 'demo'\n").unwrap();
        assert_eq!(working_directory(&game), game);
    }
}
