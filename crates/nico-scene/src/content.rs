//! Bounded snapshots of saved project files.
//! Revisions describe file bytes and paths, not a promise that every asset imports.
use crate::Project;
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeSet,
    fs,
    io::{self, Read, Write},
    path::{Path, PathBuf},
};

const MAX_FILES: usize = 32_768;
const MAX_BYTES: u64 = 512 * 1024 * 1024;
const MAX_DEPTH: usize = 64;

/// Hash the manifest, default scene, and asset folder. Call off the UI/runtime thread.
pub fn revision(project: &Project, cancelled: &impl Fn() -> bool) -> io::Result<String> {
    digest(project, None, cancelled)
}

/// Create a new, exclusively owned snapshot directory. Never overwrite a project.
/// The revision hashes the exact bytes written; failure removes only this new tree.
/// Callers own the returned directory's lifetime.
pub fn snapshot(
    project: &Project,
    destination: &Path,
    cancelled: &impl Fn() -> bool,
) -> io::Result<(Project, String)> {
    let parent = destination
        .parent()
        .ok_or_else(|| io::Error::other("missing snapshot parent"))?
        .canonicalize()?;
    if parent.starts_with(project.root()) {
        return Err(io::Error::other(
            "snapshot must be outside the source project",
        ));
    }
    fs::create_dir(destination)?;
    let result = (|| {
        let revision = digest(project, Some(destination), cancelled)?;
        Ok((Project::open(destination)?, revision))
    })();
    if result.is_err() {
        let _ = fs::remove_dir_all(destination);
    }
    result
}

fn check(cancelled: &impl Fn() -> bool) -> io::Result<()> {
    if cancelled() {
        Err(io::Error::other("content preparation cancelled"))
    } else {
        Ok(())
    }
}

fn collect(
    root: &Path,
    relative: &Path,
    files: &mut BTreeSet<PathBuf>,
    depth: usize,
    entries: &mut usize,
    cancelled: &impl Fn() -> bool,
) -> io::Result<()> {
    check(cancelled)?;
    *entries += 1;
    if *entries > MAX_FILES * 2 {
        return Err(io::Error::other("content exceeds 65536 directory entries"));
    }
    if depth > MAX_DEPTH {
        return Err(io::Error::other("content directory depth exceeds 64"));
    }
    let path = root.join(relative);
    let metadata = fs::symlink_metadata(&path)?;
    if metadata.is_dir() {
        for entry in fs::read_dir(path)? {
            let entry = entry?;
            collect(
                root,
                &relative.join(entry.file_name()),
                files,
                depth + 1,
                entries,
                cancelled,
            )?;
        }
    } else if metadata.is_file() {
        if relative.to_str().is_none() {
            return Err(io::Error::other("content paths must be UTF-8"));
        }
        files.insert(relative.to_path_buf());
        if files.len() > MAX_FILES {
            return Err(io::Error::other("content exceeds 32768 files"));
        }
    } else {
        return Err(io::Error::other(
            "content cannot contain symlinks or special files",
        ));
    }
    Ok(())
}

fn digest(
    project: &Project,
    destination: Option<&Path>,
    cancelled: &impl Fn() -> bool,
) -> io::Result<String> {
    if !project.is_declared() {
        return Err(io::Error::other(
            "content snapshots require a declared project",
        ));
    }
    let mut files = BTreeSet::new();
    let mut entries = 0;
    for path in std::iter::once(Path::new("nico.project.toml"))
        .chain(std::iter::once(project.manifest.default_scene.as_path()))
        .chain(std::iter::once(Path::new("assets")))
    {
        collect(project.root(), path, &mut files, 0, &mut entries, cancelled)?;
    }
    let mut hash = Sha256::new();
    hash.update(b"nico-content-v2\0");
    let selected = project.manifest.default_scene.to_string_lossy();
    hash.update((selected.len() as u64).to_le_bytes());
    hash.update(selected.as_bytes());
    let mut total = 0_u64;
    for relative in files {
        check(cancelled)?;
        let path = project.root().join(&relative);
        // Recheck after traversal; content is trusted local input, not a sandbox.
        if !fs::symlink_metadata(&path)?.is_file() {
            return Err(io::Error::other("content changed type during preparation"));
        }
        let mut source = fs::File::open(path)?;
        let length = source.metadata()?.len();
        total = total
            .checked_add(length)
            .filter(|n| *n <= MAX_BYTES)
            .ok_or_else(|| io::Error::other("content exceeds 512 MiB"))?;
        let name = relative
            .iter()
            .map(|p| p.to_str().unwrap())
            .collect::<Vec<_>>()
            .join("/");
        hash.update((name.len() as u64).to_le_bytes());
        hash.update(name.as_bytes());
        hash.update(length.to_le_bytes());
        let mut output = destination
            .map(|root| -> io::Result<fs::File> {
                let target = root.join(&relative);
                fs::create_dir_all(target.parent().unwrap())?;
                fs::File::create_new(target)
            })
            .transpose()?;
        let mut remaining = length;
        let mut buffer = [0; 64 * 1024];
        while remaining > 0 {
            check(cancelled)?;
            let limit = remaining.min(buffer.len() as u64) as usize;
            source.read_exact(&mut buffer[..limit])?;
            hash.update(&buffer[..limit]);
            if let Some(output) = &mut output {
                output.write_all(&buffer[..limit])?;
            }
            remaining -= limit as u64;
        }
        if source.read(&mut [0])? != 0 {
            return Err(io::Error::other("content grew during preparation"));
        }
    }
    // Preserve the asset folder when it is empty.
    if let Some(root) = destination {
        fs::create_dir_all(root.join("assets"))?;
    }
    Ok(format!("sha256:{:x}", hash.finalize()))
}

#[cfg(test)]
mod tests {
    use super::*;
    fn project() -> tempfile::TempDir {
        let root = tempfile::tempdir().unwrap();
        fs::create_dir(root.path().join("assets")).unwrap();
        fs::write(
            root.path().join("nico.project.toml"),
            "version=1\nname='Test'\ndefault_scene='scene.json'\n",
        )
        .unwrap();
        fs::write(
            root.path().join("scene.json"),
            "{\"version\":1,\"objects\":[]}",
        )
        .unwrap();
        fs::write(root.path().join("assets/model.bin"), b"content").unwrap();
        root
    }
    #[test]
    fn snapshot_revision_matches_copied_bytes_and_ignores_later_source_edits() {
        let root = project();
        let project = Project::open(root.path()).unwrap();
        let output = tempfile::tempdir().unwrap();
        let (copy, expected) = snapshot(&project, &output.path().join("copy"), &|| false).unwrap();
        assert_eq!(revision(&project, &|| false).unwrap(), expected);
        assert_eq!(revision(&copy, &|| false).unwrap(), expected);
        fs::write(root.path().join("assets/model.bin"), b"changed").unwrap();
        assert_ne!(revision(&project, &|| false).unwrap(), expected);
        assert_eq!(revision(&copy, &|| false).unwrap(), expected);
        assert!(snapshot(&project, copy.root(), &|| false).is_err());
        assert!(copy.root().exists());
    }
    #[test]
    fn paths_are_part_of_identity_and_cancellation_removes_only_new_output() {
        let root = project();
        let project = Project::open(root.path()).unwrap();
        let before = revision(&project, &|| false).unwrap();
        fs::rename(
            root.path().join("assets/model.bin"),
            root.path().join("assets/other.bin"),
        )
        .unwrap();
        assert_ne!(revision(&project, &|| false).unwrap(), before);
        let output = tempfile::tempdir().unwrap();
        let destination = output.path().join("cancelled");
        assert!(snapshot(&project, &destination, &|| true).is_err());
        assert!(!destination.exists());
        assert!(snapshot(&project, &root.path().join("nested"), &|| false).is_err());
    }
    #[test]
    fn snapshots_ignore_files_outside_the_asset_folder_and_scene() {
        let root = project();
        fs::create_dir(root.path().join("other")).unwrap();
        fs::write(root.path().join("other/tuning.toml"), "value=1").unwrap();
        let project = Project::open(root.path()).unwrap();
        let output = tempfile::tempdir().unwrap();
        let (copy, before) = snapshot(&project, &output.path().join("copy"), &|| false).unwrap();
        assert!(!copy.root().join("other").exists());
        assert_eq!(revision(&copy, &|| false).unwrap(), before);
        fs::write(root.path().join("other/tuning.toml"), "value=2").unwrap();
        assert_eq!(revision(&project, &|| false).unwrap(), before);
    }

    #[test]
    fn oversized_sources_fail_without_allocating_their_size() {
        let root = project();
        fs::File::create(root.path().join("assets/large"))
            .unwrap()
            .set_len(MAX_BYTES + 1)
            .unwrap();
        assert!(revision(&Project::open(root.path()).unwrap(), &|| false).is_err());
    }
    #[cfg(unix)]
    #[test]
    fn linked_sources_are_rejected() {
        let root = project();
        std::os::unix::fs::symlink("model.bin", root.path().join("assets/link")).unwrap();
        assert!(revision(&Project::open(root.path()).unwrap(), &|| false).is_err());
    }
}
