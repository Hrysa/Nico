//! Watched project sources. The worker owns filesystem/import work; consumers
//! adopt immutable snapshots at their update boundary. No runtime world is shared.
//! Discovery skips folders whose names start with `~`, including all their contents.
use crate::{
    Texture,
    cache::{FileStamp, ImportCache},
    import::ImportBudget,
    importers::{ModelGlbImporter, ModelGlbSettings, PngImporter, PngSettings},
    model_loading::{ImportedModel, cached_model_bundle},
};
use notify::{Event, EventKind, RecommendedWatcher, RecursiveMode, Watcher};
use std::{
    collections::{BTreeMap, BTreeSet},
    fs, io,
    path::{Path, PathBuf},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, AtomicU64, Ordering},
    },
    thread::{self, JoinHandle},
    time::{Duration, Instant},
};

const MAX_ASSETS: usize = 1024;
const MAX_VISITED: usize = 16384;
const RECONCILE: Duration = Duration::from_secs(2);
const DEBOUNCE: Duration = Duration::from_millis(150);
const MAX_RETAINED_BYTES: usize = 512 * 1024 * 1024;

#[derive(Clone)]
pub enum ImportedAsset {
    Texture(Arc<Texture>),
    Model(Arc<ImportedModel>),
}

impl ImportedAsset {
    // Conservative logical-content accounting, not allocator/GPU peak memory.
    fn retained_bytes(&self) -> usize {
        match self {
            Self::Texture(t) => t.decoded_byte_len(),
            Self::Model(bundle) => {
                let d = bundle.model.data();
                let mut bytes = d
                    .nodes
                    .iter()
                    .map(|n| std::mem::size_of_val(n) + n.name.len() + n.children.len() * 8 + 32)
                    .sum::<usize>();
                bytes += d
                    .meshes
                    .iter()
                    .map(|m| {
                        m.name.len()
                            + m.primitives
                                .iter()
                                .map(|p| {
                                    std::mem::size_of_val(p)
                                        + p.vertices.len()
                                            * std::mem::size_of::<crate::model::ModelVertex>()
                                        + p.indices.len() * 4
                                })
                                .sum::<usize>()
                    })
                    .sum::<usize>();
                bytes += d
                    .images
                    .iter()
                    .map(|i| i.bytes.len() + i.name.len() + std::mem::size_of_val(i))
                    .sum::<usize>();
                bytes += d
                    .skins
                    .iter()
                    .map(|s| {
                        s.name.len()
                            + s.joints.len() * 8
                            + s.inverse_bind.len() * 64
                            + std::mem::size_of_val(s)
                    })
                    .sum::<usize>();
                bytes += d
                    .clips
                    .iter()
                    .map(|c| {
                        c.name.len()
                            + c.tracks
                                .iter()
                                .map(|t| t.times.len() * 20 + std::mem::size_of_val(t))
                                .sum::<usize>()
                    })
                    .sum::<usize>();
                bytes += d.materials.len() * std::mem::size_of::<crate::model::Material>()
                    + d.textures.len() * std::mem::size_of::<crate::model::ModelTexture>();
                bytes += d
                    .scenes
                    .iter()
                    .map(|s| s.roots.len() * 8 + s.name.len() + std::mem::size_of_val(s))
                    .sum::<usize>();
                bytes += bundle
                    .textures
                    .iter()
                    .flatten()
                    .map(|t| t.decoded_byte_len())
                    .sum::<usize>();
                bytes
            }
        }
    }
}

#[derive(Clone)]
pub struct AssetEntry {
    /// Project-relative source identity. Rename is removal plus creation.
    pub path: PathBuf,
    /// Increments only after a successful import has been published.
    pub revision: u64,
    pub value: Option<ImportedAsset>,
    /// Unchanged source and primary cache object verified by metadata. Content
    /// may remain unloaded; this does not establish decoded or GPU readiness.
    pub cached: bool,
    /// A failure may coexist with a last-good value.
    pub error: Option<String>,
    pub missing: bool,
    stamp: Option<FileStamp>,
}

#[derive(Clone, Default)]
pub struct CatalogSnapshot {
    pub assets: BTreeMap<PathBuf, AssetEntry>,
    pub scans: u64,
    pub imports: u64,
    pub notifications: u64,
    pub error: Option<String>,
    pub retained_bytes: usize,
    pub importing: Option<PathBuf>,
}

/// Read-only handle for observing discovery and imports without owning the worker.
#[derive(Clone)]
pub struct CatalogReader(Arc<Mutex<Arc<CatalogSnapshot>>>);
impl CatalogReader {
    pub fn snapshot(&self) -> Arc<CatalogSnapshot> {
        self.0.lock().unwrap().clone()
    }
}

/// One joined worker, one coalesced wake flag, and one latest owned publication.
/// Imports are cooperative-cancellable. Dropping waits for the current decoder.
pub struct WatchedProject {
    root: PathBuf,
    snapshot: Arc<Mutex<Arc<CatalogSnapshot>>>,
    rescan: Arc<AtomicBool>,
    requested: Arc<Mutex<BTreeSet<PathBuf>>>,
    stop: Arc<AtomicBool>,
    worker: Option<JoinHandle<()>>,
}
impl WatchedProject {
    pub fn open(root: impl AsRef<Path>) -> io::Result<Self> {
        Self::open_roots(root, &[PathBuf::from(".")])
    }
    /// Watch only declared content directories, retaining project-relative keys.
    pub fn open_roots(root: impl AsRef<Path>, directories: &[PathBuf]) -> io::Result<Self> {
        Self::open_mode(root.as_ref(), directories, false)
    }
    /// Check unchanged cached sources using metadata and load their content only
    /// on request. New and changed sources are still imported automatically.
    pub fn open_roots_on_demand(
        root: impl AsRef<Path>,
        directories: &[PathBuf],
    ) -> io::Result<Self> {
        Self::open_mode(root.as_ref(), directories, true)
    }
    fn open_mode(root: &Path, directories: &[PathBuf], on_demand: bool) -> io::Result<Self> {
        let root = root.canonicalize()?;
        if !root.is_dir() {
            return Err(io::Error::other("project root must be a directory"));
        }
        if directories.is_empty() || directories.len() > 16 {
            return Err(io::Error::other("expected 1..16 asset roots"));
        }
        let mut roots = Vec::new();
        for directory in directories {
            let path = root.join(directory).canonicalize()?;
            if !path.starts_with(&root) || !path.is_dir() {
                return Err(io::Error::other(
                    "asset root must be a directory within project",
                ));
            }
            if !roots.contains(&path) {
                roots.push(path);
            }
        }
        let cache = ImportCache::new(&root).map_err(io::Error::other)?;
        let snapshot = Arc::new(Mutex::new(Arc::new(CatalogSnapshot::default())));
        let rescan = Arc::new(AtomicBool::new(true));
        let requested = Arc::new(Mutex::new(BTreeSet::new()));
        let stop = Arc::new(AtomicBool::new(false));
        let dirty = Arc::new(AtomicBool::new(false));
        let notifications = Arc::new(AtomicU64::new(0));
        let watch_error = Arc::new(Mutex::new(None::<String>));
        let mut watcher: RecommendedWatcher = notify::recommended_watcher({
            let dirty = dirty.clone();
            let notifications = notifications.clone();
            let watch_error = watch_error.clone();
            let root = root.clone();
            move |result: notify::Result<Event>| match result {
                Ok(event) if relevant(&root, &event) => {
                    notifications.fetch_add(1, Ordering::Relaxed);
                    dirty.store(true, Ordering::Release);
                }
                Err(error) => {
                    *watch_error.lock().unwrap() = Some(error.to_string());
                    dirty.store(true, Ordering::Release);
                }
                _ => {}
            }
        })
        .map_err(io::Error::other)?;
        for directory in &roots {
            watcher
                .watch(directory, RecursiveMode::Recursive)
                .map_err(io::Error::other)?;
        }
        let worker = thread::Builder::new()
            .name("nico-project-import".into())
            .spawn({
                let root = root.clone();
                let snapshot = snapshot.clone();
                let rescan = rescan.clone();
                let stop = stop.clone();
                let requested = requested.clone();
                move || {
                    let _watcher = watcher;
                    let mut catalog = CatalogSnapshot::default();
                    let mut last_scan = Instant::now();
                    let mut pending: Option<(Instant, Instant)> = None;
                    while !stop.load(Ordering::Acquire) {
                        let now = Instant::now();
                        if dirty.swap(false, Ordering::AcqRel) {
                            pending = Some((pending.map_or(now, |p| p.0), now));
                        }
                        let due = pending.is_some_and(|(first, last)| {
                            now.duration_since(last) >= DEBOUNCE
                                || now.duration_since(first) >= Duration::from_secs(1)
                        });
                        if rescan.swap(false, Ordering::AcqRel)
                            || due
                            || last_scan.elapsed() >= RECONCILE
                        {
                            let requests = requested.lock().unwrap().clone();
                            reconcile_requested(
                                &root,
                                &roots,
                                &cache,
                                &mut catalog,
                                &stop,
                                on_demand.then_some(&requests),
                                &mut |catalog| {
                                    *snapshot.lock().unwrap() = Arc::new(catalog.clone());
                                },
                            );
                            catalog.notifications = notifications.load(Ordering::Relaxed);
                            if let Some(error) = watch_error.lock().unwrap().take() {
                                catalog.error = Some(error);
                            }
                            *snapshot.lock().unwrap() = Arc::new(catalog.clone());
                            pending = None;
                            last_scan = Instant::now();
                        }
                        thread::park_timeout(Duration::from_millis(25));
                    }
                }
            })?;
        Ok(Self {
            root,
            snapshot,
            rescan,
            requested,
            stop,
            worker: Some(worker),
        })
    }
    pub fn reader(&self) -> CatalogReader {
        CatalogReader(self.snapshot.clone())
    }
    pub fn root(&self) -> &Path {
        &self.root
    }
    pub fn snapshot(&self) -> Arc<CatalogSnapshot> {
        self.snapshot.lock().unwrap().clone()
    }
    /// Request CPU content for a discovered source. Repeated requests coalesce;
    /// the worker publishes the result without blocking the caller.
    pub fn request(&self, path: &Path) {
        if !self.snapshot().assets.contains_key(path) {
            return;
        }
        if self.requested.lock().unwrap().insert(path.to_owned()) {
            self.refresh();
            if let Some(worker) = &self.worker {
                worker.thread().unpark();
            }
        }
    }
    /// Requests stat reconciliation, coalescing repeated requests without blocking.
    pub fn refresh(&self) {
        self.rescan.store(true, Ordering::Release);
    }
}
impl Drop for WatchedProject {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(worker) = self.worker.take() {
            worker.thread().unpark();
            let _ = worker.join();
        }
    }
}
fn ignored(path: &Path, is_directory: bool) -> bool {
    path.components().any(|c| {
        c.as_os_str().to_str().is_some_and(|s| {
            matches!(s, ".nico" | ".git" | "target" | "node_modules") || s.starts_with("._")
        })
    }) || (if is_directory {
        Some(path)
    } else {
        path.parent()
    })
    .is_some_and(|directories| {
        directories
            .components()
            .any(|c| c.as_os_str().as_encoded_bytes().starts_with(b"~"))
    })
}
fn supported(path: &Path) -> bool {
    path.extension()
        .and_then(|s| s.to_str())
        .is_some_and(|s| s.eq_ignore_ascii_case("png") || s.eq_ignore_ascii_case("glb"))
}
fn relevant(root: &Path, event: &Event) -> bool {
    !matches!(event.kind, EventKind::Access(_))
        && (event.need_rescan()
            || event.paths.is_empty()
            || event.paths.iter().any(|p| {
                p.strip_prefix(root)
                    .is_ok_and(|relative| !ignored(relative, p.is_dir()))
            }))
}
fn discover(
    root: &Path,
    roots: &[PathBuf],
    stop: &AtomicBool,
) -> io::Result<BTreeMap<PathBuf, Option<FileStamp>>> {
    let mut pending = roots.to_vec();
    let mut files = BTreeMap::new();
    let mut visited = 0;
    while let Some(directory) = pending.pop() {
        if ignored(directory.strip_prefix(root).unwrap(), true) {
            continue;
        }
        for entry in fs::read_dir(directory)? {
            if stop.load(Ordering::Acquire) {
                return Err(io::Error::other("import cancelled"));
            }
            let entry = entry?;
            visited += 1;
            if visited > MAX_VISITED {
                return Err(io::Error::other("project discovery exceeds 16384 entries"));
            }
            let path = entry.path();
            let relative = path.strip_prefix(root).unwrap();
            let kind = entry.file_type()?;
            if ignored(relative, kind.is_dir()) {
                continue;
            }
            // Do not traverse source links outside the selected project.
            if kind.is_dir() {
                pending.push(path);
            } else if kind.is_file() && supported(&path) {
                if files.len() >= MAX_ASSETS {
                    return Err(io::Error::other("project exceeds 1024 source assets"));
                }
                files.insert(relative.to_owned(), FileStamp::path(&path));
            }
        }
    }
    Ok(files)
}
#[cfg(test)]
fn reconcile(root: &Path, cache: &ImportCache, catalog: &mut CatalogSnapshot, stop: &AtomicBool) {
    reconcile_roots(
        root,
        &[root.to_path_buf()],
        cache,
        catalog,
        stop,
        &mut |_| {},
    );
}
#[cfg(test)]
fn reconcile_roots(
    root: &Path,
    roots: &[PathBuf],
    cache: &ImportCache,
    catalog: &mut CatalogSnapshot,
    stop: &AtomicBool,
    publish: &mut dyn FnMut(&CatalogSnapshot),
) {
    reconcile_requested(root, roots, cache, catalog, stop, None, publish);
}
#[allow(clippy::too_many_arguments)]
fn reconcile_requested(
    root: &Path,
    roots: &[PathBuf],
    cache: &ImportCache,
    catalog: &mut CatalogSnapshot,
    stop: &AtomicBool,
    requested: Option<&BTreeSet<PathBuf>>,
    publish: &mut dyn FnMut(&CatalogSnapshot),
) {
    catalog.scans += 1;
    catalog.error = None;
    let files = match discover(root, roots, stop) {
        Ok(files) => files,
        Err(error) => {
            catalog.error = Some(error.to_string());
            return;
        }
    };
    // Removed entries with last-good values remain available to current previews.
    for (path, entry) in &mut catalog.assets {
        if !files.contains_key(path) {
            entry.missing = true;
            entry.error = Some("source file is missing".into());
            entry.stamp = None;
            entry.cached = false;
        }
    }
    // Publish all source identities before decoding the first file.
    for path in files.keys() {
        if !catalog.assets.contains_key(path) {
            if catalog.assets.len() >= MAX_ASSETS {
                break;
            }
            catalog.assets.insert(
                path.clone(),
                AssetEntry {
                    path: path.clone(),
                    revision: 0,
                    value: None,
                    cached: false,
                    error: None,
                    missing: false,
                    stamp: None,
                },
            );
        }
    }
    publish(catalog);
    let probe = requested.and_then(|_| cache.probe().ok());
    for (path, stamp) in files {
        if stop.load(Ordering::Acquire) {
            break;
        }
        if catalog.assets.get(&path).is_some_and(|e| {
            !e.missing
                && stamp.is_some()
                && e.stamp == stamp
                && (e.value.is_some() || e.error.is_some())
        }) {
            continue;
        }
        if !catalog.assets.contains_key(&path) && catalog.assets.len() >= MAX_ASSETS {
            catalog.error = Some(
                "catalog including removed assets exceeds 1024 entries; reopen the project".into(),
            );
            break;
        }
        let full = root.join(&path);
        let cancelled = || stop.load(Ordering::Acquire);
        let budget = ImportBudget::default();
        if requested.is_some_and(|r| !r.contains(&path))
            && catalog.assets.get(&path).is_some_and(|e| e.value.is_none())
            && let Some(probe) = &probe
        {
            let current = if path.extension().unwrap().eq_ignore_ascii_case("png") {
                probe.current(&full, &PngImporter, &PngSettings::default(), budget)
            } else {
                probe.current(
                    &full,
                    &ModelGlbImporter,
                    &ModelGlbSettings::default(),
                    budget,
                )
            };
            if current.unwrap_or(false) && stamp == FileStamp::path(&full) {
                let entry = catalog.assets.get_mut(&path).unwrap();
                entry.stamp = stamp;
                entry.cached = true;
                entry.missing = false;
                entry.error = None;
                continue;
            }
        }
        catalog.importing = Some(path.clone());
        publish(catalog);
        let result = if path.extension().unwrap().eq_ignore_ascii_case("png") {
            cache
                .load(
                    &full,
                    &PngImporter,
                    &PngSettings::default(),
                    budget,
                    &cancelled,
                )
                .map(|v| ImportedAsset::Texture(Arc::new(v)))
        } else {
            cache
                .load(
                    &full,
                    &ModelGlbImporter,
                    &ModelGlbSettings::default(),
                    budget,
                    &cancelled,
                )
                .and_then(|v| cached_model_bundle(v, cache, &full, &cancelled))
                .map(|v| ImportedAsset::Model(Arc::new(v)))
        };
        catalog.imports += 1;
        // Never publish a result known to have raced a source replacement.
        if stamp != FileStamp::path(&full) {
            continue;
        }
        let old_bytes = catalog
            .assets
            .get(&path)
            .and_then(|e| e.value.as_ref())
            .map_or(0, ImportedAsset::retained_bytes);
        let new_bytes = result.as_ref().map_or(0, ImportedAsset::retained_bytes);
        let over_budget = catalog
            .retained_bytes
            .saturating_sub(old_bytes)
            .saturating_add(new_bytes)
            > MAX_RETAINED_BYTES;
        let entry = catalog
            .assets
            .entry(path.clone())
            .or_insert_with(|| AssetEntry {
                path,
                revision: 0,
                value: None,
                cached: false,
                error: None,
                missing: false,
                stamp: None,
            });
        entry.stamp = stamp;
        entry.missing = false;
        entry.cached = false;
        match result {
            Ok(_) if over_budget => {
                entry.error = Some("project retained content exceeds 512 MiB".into());
            }
            Ok(value) => {
                catalog.retained_bytes =
                    catalog.retained_bytes.saturating_sub(old_bytes) + new_bytes;
                entry.value = Some(value);
                entry.revision += 1;
                entry.error = None;
            }
            Err(error) => entry.error = Some(error.to_string()),
        }
        publish(catalog);
    }
    catalog.importing = None;
    publish(catalog);
}

#[cfg(test)]
mod tests {
    use super::*;
    static NEXT: AtomicU64 = AtomicU64::new(0);
    struct Project(PathBuf);
    impl Project {
        fn new() -> Self {
            let path = std::env::temp_dir().join(format!(
                "nico-watch-{}-{}",
                std::process::id(),
                NEXT.fetch_add(1, Ordering::Relaxed)
            ));
            fs::create_dir_all(&path).unwrap();
            Self(path)
        }
        fn png(&self, name: &str, width: u32) {
            let file = fs::File::create(self.0.join(name)).unwrap();
            let mut encoder = png::Encoder::new(file, width, 1);
            encoder.set_color(png::ColorType::Rgba);
            let mut writer = encoder.write_header().unwrap();
            writer
                .write_image_data(&vec![255; width as usize * 4])
                .unwrap();
        }
    }
    impl Drop for Project {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    #[test]
    fn discovery_and_individual_imports_are_published_before_batch_completion() {
        let project = Project::new();
        project.png("a.png", 1);
        project.png("b.png", 1);
        let cache = ImportCache::new(&project.0).unwrap();
        let mut state = CatalogSnapshot::default();
        let mut seen = Vec::new();
        reconcile_roots(
            &project.0,
            std::slice::from_ref(&project.0),
            &cache,
            &mut state,
            &AtomicBool::new(false),
            &mut |s| {
                seen.push((
                    s.assets.len(),
                    s.assets.values().filter(|a| a.value.is_some()).count(),
                    s.importing.clone(),
                ));
            },
        );
        assert_eq!(seen.first().unwrap(), &(2, 0, None));
        assert!(
            seen.iter()
                .any(|(sources, ready, _)| *sources == 2 && *ready == 1)
        );
        assert_eq!(seen.last().unwrap(), &(2, 2, None));
    }
    #[test]
    fn tilde_folders_are_skipped_but_tilde_files_are_imported() {
        let project = Project::new();
        for folder in ["~drafts/nested", "assets/~sources", "assets/visible"] {
            fs::create_dir_all(project.0.join(folder)).unwrap();
        }
        project.png("~drafts/nested/hidden.png", 1);
        project.png("assets/~sources/hidden.png", 1);
        project.png("assets/visible/~image.png", 1);
        let cache = ImportCache::new(&project.0).unwrap();
        let mut state = CatalogSnapshot::default();
        reconcile(&project.0, &cache, &mut state, &AtomicBool::new(false));
        assert_eq!(state.assets.len(), 1);
        assert!(
            state.assets[Path::new("assets/visible/~image.png")]
                .value
                .is_some()
        );
        for root in ["~drafts", "~drafts/nested", "assets/~sources"] {
            assert!(
                discover(&project.0, &[project.0.join(root)], &AtomicBool::new(false))
                    .unwrap()
                    .is_empty()
            );
        }
        let event =
            Event::new(EventKind::Any).add_path(project.0.join("assets/~sources/hidden.png"));
        assert!(!relevant(&project.0, &event));
        let event =
            Event::new(EventKind::Any).add_path(project.0.join("assets/visible/~image.png"));
        assert!(relevant(&project.0, &event));
    }
    #[test]
    fn declared_roots_exclude_assets_next_to_code() {
        let project = Project::new();
        fs::create_dir(project.0.join("assets")).unwrap();
        project.png("assets/content.png", 1);
        project.png("code.png", 1);
        let files = discover(
            &project.0,
            &[project.0.join("assets")],
            &AtomicBool::new(false),
        )
        .unwrap();
        assert_eq!(files.len(), 1);
        assert!(files.contains_key(Path::new("assets/content.png")));
    }
    #[test]
    fn warm_catalog_defers_content_until_requested_and_imports_changed_sources() {
        let project = Project::new();
        project.png("a.png", 1);
        project.png("b.png", 1);
        let cache = ImportCache::new(&project.0).unwrap();
        let stop = AtomicBool::new(false);
        reconcile(&project.0, &cache, &mut CatalogSnapshot::default(), &stop);
        let activity = crate::cache::observe_current_thread();
        let before = activity.stats();
        let mut state = CatalogSnapshot::default();
        let scan = |state: &mut CatalogSnapshot, requested: &BTreeSet<PathBuf>| {
            reconcile_requested(
                &project.0,
                std::slice::from_ref(&project.0),
                &cache,
                state,
                &stop,
                Some(requested),
                &mut |_| {},
            );
        };
        scan(&mut state, &BTreeSet::new());
        assert!(state.assets.values().all(|a| a.cached && a.value.is_none()));
        assert_eq!(state.imports, 0);
        assert_eq!(state.retained_bytes, 0);
        assert_eq!(activity.stats().hits, before.hits);
        assert_eq!(activity.stats().source_checks, before.source_checks);
        assert_eq!(activity.stats().object_checks, before.object_checks);
        let requested = BTreeSet::from([PathBuf::from("a.png")]);
        scan(&mut state, &requested);
        assert_eq!(state.imports, 1);
        assert!(state.assets[Path::new("a.png")].value.is_some());
        assert!(state.assets[Path::new("b.png")].value.is_none());
        scan(&mut state, &requested);
        assert_eq!(state.imports, 1);
        project.png("b.png", 2);
        scan(&mut state, &requested);
        assert_eq!(state.imports, 2);
        assert!(state.assets[Path::new("b.png")].value.is_some());
        // Another importer may have refreshed the disk cache already. Existing
        // last-good content must still adopt the changed revision.
        project.png("b.png", 3);
        cache
            .load(
                &project.0.join("b.png"),
                &PngImporter,
                &PngSettings::default(),
                ImportBudget::default(),
                &|| false,
            )
            .unwrap();
        scan(&mut state, &requested);
        assert_eq!(state.assets[Path::new("b.png")].revision, 2);
        let Some(ImportedAsset::Texture(texture)) = &state.assets[Path::new("b.png")].value else {
            panic!("expected loaded texture");
        };
        assert_eq!(texture.width(), 3);
        fs::write(project.0.join("b.png"), b"invalid").unwrap();
        scan(&mut state, &requested);
        assert!(state.assets[Path::new("b.png")].error.is_some());
        assert!(state.assets[Path::new("b.png")].value.is_some());
        fs::remove_file(project.0.join("a.png")).unwrap();
        scan(&mut state, &requested);
        assert!(state.assets[Path::new("a.png")].missing);
    }
    #[test]
    fn on_demand_worker_accepts_requests_and_joins_shutdown() {
        let project = Project::new();
        project.png("a.png", 1);
        let cache = ImportCache::new(&project.0).unwrap();
        reconcile(
            &project.0,
            &cache,
            &mut CatalogSnapshot::default(),
            &AtomicBool::new(false),
        );
        let watched =
            WatchedProject::open_roots_on_demand(&project.0, &[PathBuf::from(".")]).unwrap();
        wait(&watched, |s| {
            s.assets.get(Path::new("a.png")).is_some_and(|a| a.cached)
        });
        watched.request(Path::new("a.png"));
        watched.request(Path::new("a.png"));
        wait(&watched, |s| s.assets[Path::new("a.png")].value.is_some());
        assert_eq!(watched.snapshot().imports, 1);
        drop(watched);
    }
    #[test]
    fn stat_checks_preserve_identity_and_failed_replacements_keep_last_good() {
        let project = Project::new();
        project.png("a.png", 1);
        let cache = ImportCache::new(&project.0).unwrap();
        let mut state = CatalogSnapshot::default();
        let stop = AtomicBool::new(false);
        reconcile(&project.0, &cache, &mut state, &stop);
        assert_eq!(state.assets[Path::new("a.png")].revision, 1);
        reconcile(&project.0, &cache, &mut state, &stop);
        assert_eq!(state.imports, 1);
        fs::write(project.0.join("a.png"), b"broken").unwrap();
        reconcile(&project.0, &cache, &mut state, &stop);
        let entry = &state.assets[Path::new("a.png")];
        assert!(entry.error.is_some());
        assert!(entry.value.is_some());
        assert_eq!(entry.revision, 1);
        fs::remove_file(project.0.join("a.png")).unwrap();
        reconcile(&project.0, &cache, &mut state, &stop);
        assert!(state.assets[Path::new("a.png")].missing);
        project.png("a.png", 2);
        reconcile(&project.0, &cache, &mut state, &stop);
        let entry = &state.assets[Path::new("a.png")];
        assert!(!entry.missing);
        assert!(entry.error.is_none());
        assert_eq!(entry.revision, 2);
    }
    fn wait(project: &WatchedProject, predicate: impl Fn(&CatalogSnapshot) -> bool) {
        let deadline = Instant::now() + Duration::from_secs(8);
        loop {
            if predicate(&project.snapshot()) {
                return;
            }
            assert!(
                Instant::now() < deadline,
                "watcher did not publish expected state"
            );
            thread::sleep(Duration::from_millis(25));
        }
    }
    #[test]
    fn native_watch_discovers_atomic_save_rename_and_joins_shutdown() {
        let project = Project::new();
        let watched = WatchedProject::open(&project.0).unwrap();
        wait(&watched, |s| s.scans > 0);
        project.png("new.png", 1);
        wait(&watched, |s| {
            s.assets
                .get(Path::new("new.png"))
                .is_some_and(|a| a.revision == 1)
        });
        project.png("save.tmp", 2);
        fs::rename(project.0.join("save.tmp"), project.0.join("new.png")).unwrap();
        wait(&watched, |s| s.assets[Path::new("new.png")].revision == 2);
        fs::rename(project.0.join("new.png"), project.0.join("renamed.png")).unwrap();
        wait(&watched, |s| {
            s.assets[Path::new("new.png")].missing
                && s.assets.contains_key(Path::new("renamed.png"))
        });
        assert!(watched.snapshot().notifications > 0);
        drop(watched);
    }
}
