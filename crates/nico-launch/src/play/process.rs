//! Ownership is the direct Child handle, never a discovered PID or process name.
use std::{
    io,
    process::{Child, Command, ExitStatus},
    thread,
    time::{Duration, Instant},
};

pub(super) struct OwnedProcess {
    child: Child,
    exit: Option<ExitStatus>,
    build_tree: bool,
}

impl OwnedProcess {
    pub fn spawn(command: &mut Command) -> io::Result<Self> {
        Ok(Self {
            child: command.spawn()?,
            exit: None,
            build_tree: false,
        })
    }
    /// Cargo owns compiler children. Isolate its process group before spawning so
    /// cancellation cannot leave a compiler writing into a later session's build.
    pub fn spawn_build(command: &mut Command) -> io::Result<Self> {
        #[cfg(unix)]
        {
            use std::os::unix::process::CommandExt;
            command.process_group(0);
        }
        let mut child = Self::spawn(command)?;
        child.build_tree = true;
        Ok(child)
    }
    fn terminate(&mut self) -> io::Result<()> {
        if self.build_tree {
            #[cfg(unix)]
            {
                let status = Command::new("/bin/kill")
                    .args(["-KILL", "--", &format!("-{}", self.id())])
                    .status()?;
                if status.success() {
                    return Ok(());
                }
            }
            #[cfg(windows)]
            {
                let status = Command::new("taskkill")
                    .args(["/PID", &self.id().to_string(), "/T", "/F"])
                    .status()?;
                if status.success() {
                    return Ok(());
                }
            }
        }
        self.child.kill()
    }
    pub fn id(&self) -> u32 {
        self.child.id()
    }
    pub fn poll(&mut self) -> io::Result<Option<ExitStatus>> {
        if self.exit.is_none() {
            self.exit = self.child.try_wait()?;
        }
        Ok(self.exit)
    }
    /// Wait for requested graceful shutdown, then terminate/reap this exact child.
    /// The returned boolean records whether force was necessary.
    pub fn stop(&mut self, grace: Duration) -> io::Result<(ExitStatus, bool)> {
        let deadline = Instant::now() + grace;
        loop {
            if let Some(exit) = self.poll()? {
                return Ok((exit, false));
            }
            if Instant::now() >= deadline {
                break;
            }
            thread::sleep(Duration::from_millis(20));
        }
        // A natural exit can race kill. Always try to reap before reporting failure.
        let killed = self.terminate();
        match self.child.wait() {
            Ok(exit) => {
                self.exit = Some(exit);
                Ok((exit, killed.is_ok()))
            }
            Err(error) => Err(error),
        }
    }
}

impl Drop for OwnedProcess {
    fn drop(&mut self) {
        if self.exit.is_none() {
            let _ = self.stop(Duration::ZERO);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn sleeping_child(build_tree: bool) -> OwnedProcess {
        let mut command = Command::new(std::env::current_exe().unwrap());
        command
            .args([
                "--exact",
                "play::process::tests::child_fixture",
                "--nocapture",
            ])
            .env("NICO_PLAY_CHILD_FIXTURE", "1")
            .stdout(std::process::Stdio::null());
        if build_tree {
            OwnedProcess::spawn_build(&mut command).unwrap()
        } else {
            OwnedProcess::spawn(&mut command).unwrap()
        }
    }
    #[test]
    fn child_fixture() {
        match std::env::var("NICO_PLAY_CHILD_FIXTURE").as_deref() {
            Ok("parent") => {
                let mut child = Command::new(std::env::current_exe().unwrap())
                    .args(["--exact", "play::process::tests::child_fixture"])
                    .env("NICO_PLAY_CHILD_FIXTURE", "grandchild")
                    .stdout(std::process::Stdio::null())
                    .spawn()
                    .unwrap();
                child.wait().unwrap();
            }
            Ok("grandchild") => {
                let directory = std::path::PathBuf::from(
                    std::env::var_os("NICO_PLAY_FIXTURE_DIRECTORY").unwrap(),
                );
                std::fs::write(directory.join("ready"), b"ready").unwrap();
                thread::sleep(Duration::from_secs(2));
                std::fs::write(directory.join("survived"), b"survived").unwrap();
            }
            Ok(_) => thread::sleep(Duration::from_secs(60)),
            Err(_) => (),
        }
    }
    #[test]
    fn build_cancellation_terminates_its_compiler_descendants() {
        let directory = tempfile::tempdir().unwrap();
        let mut command = Command::new(std::env::current_exe().unwrap());
        command
            .args(["--exact", "play::process::tests::child_fixture"])
            .env("NICO_PLAY_CHILD_FIXTURE", "parent")
            .env("NICO_PLAY_FIXTURE_DIRECTORY", directory.path())
            .stdout(std::process::Stdio::null());
        let mut build = OwnedProcess::spawn_build(&mut command).unwrap();
        let mut unrelated = sleeping_child(false);
        let deadline = Instant::now() + Duration::from_secs(5);
        while !directory.path().join("ready").exists() && Instant::now() < deadline {
            thread::sleep(Duration::from_millis(10));
        }
        assert!(directory.path().join("ready").exists());
        assert!(build.stop(Duration::ZERO).unwrap().1);
        thread::sleep(Duration::from_millis(2100));
        assert!(!directory.path().join("survived").exists());
        assert!(unrelated.poll().unwrap().is_none());
        unrelated.stop(Duration::ZERO).unwrap();
    }
    #[test]
    fn stopping_one_owned_child_preserves_other_children_and_reaps_once() {
        let mut owned = sleeping_child(true);
        let mut unrelated = sleeping_child(false);
        assert_ne!(owned.id(), unrelated.id());
        let (_, forced) = owned.stop(Duration::ZERO).unwrap();
        assert!(forced);
        assert!(owned.poll().unwrap().is_some());
        assert!(!owned.stop(Duration::ZERO).unwrap().1);
        assert!(unrelated.poll().unwrap().is_none());
        unrelated.stop(Duration::ZERO).unwrap();
    }
    #[test]
    fn spawn_failure_creates_no_owned_process() {
        assert!(OwnedProcess::spawn(&mut Command::new("nico-missing-play-executable")).is_err());
    }
}
