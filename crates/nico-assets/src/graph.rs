//! One loading request for a set of root definitions and their recursive dependencies.
//! Games supply definition readers. Workers only produce owned CPU assets.
use crate::{
    Texture,
    batch::{Batch, Error},
    import::ImportBudget,
    importers::{ModelGlbImporter, ModelGlbSettings, PngImporter, PngSettings},
    model_loading::{ImportedModel, cached_model_bundle},
    progress::ImportProgress,
};
use std::{
    collections::{BTreeMap, BTreeSet},
    io,
    path::{Path, PathBuf},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
};

enum Asset {
    Model(ImportedModel),
    Texture(Arc<Texture>),
}

/// Assets are keyed by canonical source paths. Repeated references share one loaded value.
#[derive(Default)]
pub struct LoadedAssets {
    values: BTreeMap<PathBuf, Asset>,
}
impl LoadedAssets {
    pub fn model(&self, path: &Path) -> Result<&ImportedModel, Error> {
        match self.values.get(&path.canonicalize()?) {
            Some(Asset::Model(model)) => Ok(model),
            _ => Err(format!(
                "model was not included in the load request: {}",
                path.display()
            )
            .into()),
        }
    }
    pub fn texture(&self, path: &Path) -> Result<Arc<Texture>, Error> {
        match self.values.get(&path.canonicalize()?) {
            Some(Asset::Texture(texture)) => Ok(texture.clone()),
            _ => Err(format!(
                "texture was not included in the load request: {}",
                path.display()
            )
            .into()),
        }
    }
    pub fn len(&self) -> usize {
        self.values.len()
    }
    pub fn is_empty(&self) -> bool {
        self.values.is_empty()
    }
}

/// Follow definition references, deduplicate sources, then load with one fixed progress total.
/// GLB textures belong to their model's completion. Definition files do not add progress units.
/// This startup call waits. Runtime callers should use `start` and poll the returned request.
pub fn load(
    roots: Vec<PathBuf>,
    dependencies: impl Fn(&Path) -> Result<Vec<PathBuf>, Error>,
) -> Result<LoadedAssets, Error> {
    load_with_cancel(roots, dependencies, Arc::new(AtomicBool::new(false)))
}

/// Start one recursive load request. Poll its batch while the scene keeps running.
/// Cancelling or dropping the request also cancels its child workers.
pub fn start(
    roots: Vec<PathBuf>,
    dependencies: impl Fn(&Path) -> Result<Vec<PathBuf>, Error> + Send + Sync + 'static,
) -> io::Result<Batch<LoadedAssets>> {
    Batch::start(vec![roots], move |roots, cancelled| {
        load_with_cancel(roots, &dependencies, cancelled.clone())
    })
}

fn load_with_cancel(
    roots: Vec<PathBuf>,
    dependencies: impl Fn(&Path) -> Result<Vec<PathBuf>, Error>,
    cancelled: Arc<AtomicBool>,
) -> Result<LoadedAssets, Error> {
    let paths = discover(roots, &dependencies, &cancelled)?;

    let progress = ImportProgress::new("project assets", paths.len());
    let batch = Batch::start(paths, move |path, local_cancelled| {
        let cancelled =
            || cancelled.load(Ordering::Acquire) || local_cancelled.load(Ordering::Acquire);
        let asset = if extension(&path) == "glb" {
            let cache = crate::cache::source_cache(&path)?;
            let model = cache.load(
                &path,
                &ModelGlbImporter,
                &ModelGlbSettings {
                    allow_material_fallback: true,
                    ..Default::default()
                },
                ImportBudget {
                    max_input_bytes: 64 * 1024 * 1024,
                    max_decoded_bytes: 128 * 1024 * 1024,
                },
                &cancelled,
            )?;
            Asset::Model(cached_model_bundle(model, &cache, &path, &cancelled)?)
        } else {
            Asset::Texture(Arc::new(crate::cache::load_file(
                &path,
                &PngImporter,
                &PngSettings::default(),
                ImportBudget {
                    max_input_bytes: 64 * 1024 * 1024,
                    max_decoded_bytes: 256 * 1024 * 1024,
                },
                &cancelled,
            )?))
        };
        Ok((path, asset))
    })?;
    let values = batch.wait(|| progress.complete_one())?;
    // Share embedded images across models after workers finish. No model can observe partial data.
    let mut images = BTreeMap::<Vec<u8>, Arc<Texture>>::new();
    let mut loaded = LoadedAssets::default();
    for (path, mut asset) in values {
        if let Asset::Model(bundle) = &mut asset {
            for (index, texture) in bundle.textures.iter_mut().enumerate() {
                if let Some(value) = texture {
                    let image = bundle.model.data().textures[index].image;
                    let bytes = &bundle.model.data().images[image].bytes;
                    *value = images
                        .entry(bytes.clone())
                        .or_insert_with(|| value.clone())
                        .clone();
                }
            }
        }
        loaded.values.insert(path, asset);
    }
    progress.finish();
    Ok(loaded)
}

fn extension(path: &Path) -> String {
    path.extension()
        .and_then(|v| v.to_str())
        .unwrap_or("")
        .to_ascii_lowercase()
}

fn discover(
    roots: Vec<PathBuf>,
    dependencies: &impl Fn(&Path) -> Result<Vec<PathBuf>, Error>,
    cancelled: &AtomicBool,
) -> Result<Vec<PathBuf>, Error> {
    if roots.len() > 1024 {
        return Err(io::Error::other("too many asset roots").into());
    }
    let mut pending: Vec<_> = roots
        .into_iter()
        .rev()
        .map(|path| (path, false, 0usize))
        .collect();
    let mut visiting = BTreeSet::new();
    let mut visited = BTreeSet::new();
    let mut assets = BTreeSet::new();
    let mut source_bytes = 0u64;
    while let Some((path, leaving, depth)) = pending.pop() {
        if cancelled.load(Ordering::Acquire) {
            return Err(io::Error::other("asset loading cancelled").into());
        }
        let path = path.canonicalize()?;
        if leaving {
            visiting.remove(&path);
            visited.insert(path);
            continue;
        }
        if visiting.contains(&path) {
            return Err(format!("asset dependency cycle: {}", path.display()).into());
        }
        if visited.contains(&path) {
            continue;
        }
        if depth > 64 || visited.len() + visiting.len() >= 1024 {
            return Err(io::Error::other("asset dependency graph exceeds limits").into());
        }
        let metadata = path.metadata()?;
        if !metadata.is_file() {
            return Err(format!("asset dependency must be a file: {}", path.display()).into());
        }
        if matches!(extension(&path).as_str(), "glb" | "png") {
            source_bytes = source_bytes
                .checked_add(metadata.len())
                .filter(|n| *n <= 512 * 1024 * 1024)
                .ok_or_else(|| io::Error::other("project sources exceed 512 MiB"))?;
            assets.insert(path.clone());
            visited.insert(path);
        } else {
            if metadata.len() > 1024 * 1024 {
                return Err(io::Error::other("asset definition exceeds 1 MiB").into());
            }
            let children = dependencies(&path)?;
            if children.len() > 1024 {
                return Err(io::Error::other("too many asset dependencies").into());
            }
            visiting.insert(path.clone());
            pending.push((path.clone(), true, depth));
            for child in children.into_iter().rev() {
                pending.push((
                    if child.is_absolute() {
                        child
                    } else {
                        path.parent().unwrap().join(child)
                    },
                    false,
                    depth + 1,
                ));
            }
        }
    }
    Ok(assets.into_iter().collect())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::{fs, sync::atomic::AtomicU64};
    static NEXT: AtomicU64 = AtomicU64::new(0);
    struct Fixture(PathBuf);
    impl Fixture {
        fn new() -> Self {
            let root = std::env::temp_dir().join(format!(
                "nico-graph-{}-{}",
                std::process::id(),
                NEXT.fetch_add(1, Ordering::Relaxed)
            ));
            fs::create_dir_all(&root).unwrap();
            for name in ["root.def", "child.def", "other.def"] {
                fs::write(root.join(name), "definition").unwrap();
            }
            fs::write(
                root.join("cube.glb"),
                include_bytes!("../tests/fixtures/meshes/cube.glb"),
            )
            .unwrap();
            fs::write(
                root.join("sample.png"),
                include_bytes!("../tests/fixtures/textures/sample.png"),
            )
            .unwrap();
            Self(root)
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    fn dependencies(path: &Path) -> Result<Vec<PathBuf>, Error> {
        Ok(match path.file_name().unwrap().to_str().unwrap() {
            "root.def" => vec!["child.def".into(), "other.def".into()],
            "child.def" => vec!["cube.glb".into(), "sample.png".into()],
            "other.def" => vec!["cube.glb".into()],
            _ => return Err("unknown definition".into()),
        })
    }
    #[test]
    fn recursive_references_load_once_with_one_fixed_total() {
        let fixture = Fixture::new();
        let (send, receive) = std::sync::mpsc::channel();
        let _observer = crate::progress::observe_progress(move |update| {
            send.send(update).unwrap();
        });
        let assets = load(vec![fixture.0.join("root.def")], dependencies).unwrap();
        assert_eq!(assets.len(), 2);
        assert_eq!(
            assets
                .texture(&fixture.0.join("sample.png"))
                .unwrap()
                .width(),
            2
        );
        assert!(
            !assets
                .model(&fixture.0.join("cube.glb"))
                .unwrap()
                .model
                .data()
                .meshes
                .is_empty()
        );
        let updates: Vec<_> = receive.try_iter().collect();
        assert_eq!(
            updates.iter().map(|p| p.completed).collect::<Vec<_>>(),
            vec![0, 1, 2, 2]
        );
        assert!(
            updates
                .iter()
                .all(|p| p.label == "project assets" && p.total == 2)
        );
    }
    #[test]
    fn cycles_missing_sources_and_directories_fail_during_discovery() {
        let fixture = Fixture::new();
        let result = load(vec![fixture.0.join("root.def")], |_| {
            Ok(vec!["root.def".into()])
        });
        assert!(result.err().unwrap().to_string().contains("cycle"));
        assert!(load(vec![fixture.0.join("missing.glb")], dependencies).is_err());
        assert!(load(vec![fixture.0.clone()], dependencies).is_err());
    }
    #[test]
    fn asynchronous_request_returns_the_whole_graph_and_propagates_failures() {
        let fixture = Fixture::new();
        let mut results = start(vec![fixture.0.join("root.def")], dependencies)
            .unwrap()
            .wait(|| {})
            .unwrap();
        assert_eq!(results.pop().unwrap().len(), 2);
        fs::write(fixture.0.join("cube.glb"), "invalid GLB").unwrap();
        assert!(
            start(vec![fixture.0.join("root.def")], dependencies)
                .unwrap()
                .wait(|| {})
                .is_err()
        );
    }
    #[test]
    fn cancelled_discovery_does_not_read_dependencies() {
        let fixture = Fixture::new();
        assert!(
            load_with_cancel(
                vec![fixture.0.join("root.def")],
                |_| panic!("must not read"),
                Arc::new(AtomicBool::new(true))
            )
            .is_err()
        );
    }
}
