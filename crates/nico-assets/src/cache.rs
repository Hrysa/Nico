//! Rebuildable development import cache. Sources remain authoritative.
//! Objects are binary CPU data, keyed by SHA-256; the index is atomically published
//! under an OS file lock. Cache hits still validate/reconstruct engine values.
use crate::import::{AssetImporter, ImportBudget, ImportContext, ImportError, ImportErrorKind};
use bincode::Options;
use serde::{Deserialize, Serialize, de::DeserializeOwned};
use sha2::{Digest, Sha256};
use std::{
    collections::BTreeMap,
    fs::{self, File},
    io::{Read, Write},
    path::{Component, Path, PathBuf},
    sync::atomic::{AtomicU64, Ordering},
    time::{Duration, Instant},
};

const FORMAT: u32 = 1;
const INDEX_LIMIT: usize = 8 * 1024 * 1024;
static TEMP: AtomicU64 = AtomicU64::new(0);
static HITS: AtomicU64 = AtomicU64::new(0);
static IMPORTS: AtomicU64 = AtomicU64::new(0);
static REBUILDS: AtomicU64 = AtomicU64::new(0);
static SOURCE_CHECKS: AtomicU64 = AtomicU64::new(0);
static OBJECT_CHECKS: AtomicU64 = AtomicU64::new(0);
#[derive(Debug, Default, Clone, Copy, Serialize)]
pub struct CacheStats {
    pub hits: u64,
    pub imports: u64,
    pub rebuilds: u64,
    pub source_checks: u64,
    pub object_checks: u64,
}
impl CacheStats {
    pub(crate) fn since(self, earlier: Self) -> Self {
        Self {
            hits: self.hits.saturating_sub(earlier.hits),
            imports: self.imports.saturating_sub(earlier.imports),
            rebuilds: self.rebuilds.saturating_sub(earlier.rebuilds),
            source_checks: self.source_checks.saturating_sub(earlier.source_checks),
            object_checks: self.object_checks.saturating_sub(earlier.object_checks),
        }
    }
}
/// Process-wide operational counters, not timings or profiling data.
pub fn stats() -> CacheStats {
    CacheStats {
        hits: HITS.load(Ordering::Relaxed),
        imports: IMPORTS.load(Ordering::Relaxed),
        rebuilds: REBUILDS.load(Ordering::Relaxed),
        source_checks: SOURCE_CHECKS.load(Ordering::Relaxed),
        object_checks: OBJECT_CHECKS.load(Ordering::Relaxed),
    }
}
// A startup progress scope observes its loading thread, not unrelated workers.
#[derive(Default)]
pub(crate) struct CacheActivity {
    hits: AtomicU64,
    imports: AtomicU64,
    rebuilds: AtomicU64,
    source_checks: AtomicU64,
    object_checks: AtomicU64,
}
impl CacheActivity {
    pub(crate) fn stats(&self) -> CacheStats {
        CacheStats {
            hits: self.hits.load(Ordering::Relaxed),
            imports: self.imports.load(Ordering::Relaxed),
            rebuilds: self.rebuilds.load(Ordering::Relaxed),
            source_checks: self.source_checks.load(Ordering::Relaxed),
            object_checks: self.object_checks.load(Ordering::Relaxed),
        }
    }
}
thread_local! {
    static ACTIVITY: std::sync::Arc<CacheActivity> = std::sync::Arc::default();
}
pub(crate) fn observe_current_thread() -> std::sync::Arc<CacheActivity> {
    ACTIVITY.with(Clone::clone)
}
pub(crate) fn record_activity(stats: CacheStats) {
    ACTIVITY.with(|a| {
        a.hits.fetch_add(stats.hits, Ordering::Relaxed);
        a.imports.fetch_add(stats.imports, Ordering::Relaxed);
        a.rebuilds.fetch_add(stats.rebuilds, Ordering::Relaxed);
        a.source_checks
            .fetch_add(stats.source_checks, Ordering::Relaxed);
        a.object_checks
            .fetch_add(stats.object_checks, Ordering::Relaxed);
    });
}
#[derive(Serialize, Deserialize)]
struct Index {
    format: u32,
    entries: BTreeMap<String, Entry>,
}
impl Default for Index {
    fn default() -> Self {
        Self {
            format: FORMAT,
            entries: BTreeMap::new(),
        }
    }
}
#[derive(Clone, Serialize, Deserialize)]
struct Entry {
    source: String,
    source_hash: String,
    recipe: String,
    object: String,
    decoded_bytes: usize,
    #[serde(default)]
    source_stat: Option<FileStamp>,
    #[serde(default)]
    object_stat: Option<FileStamp>,
    #[serde(default)]
    source_len: u64,
}
/// Metadata fast path. Unix change time and identity detect timestamp-preserving
/// edits and file replacement; missing timestamps always require a full check.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct FileStamp {
    len: u64,
    modified: (u64, u32),
    created: Option<(u64, u32)>,
    identity: Option<(u64, u64, i64, i64)>,
}
impl FileStamp {
    fn metadata(meta: &fs::Metadata) -> Option<Self> {
        fn time(t: std::io::Result<std::time::SystemTime>) -> Option<(u64, u32)> {
            let d = t.ok()?.duration_since(std::time::UNIX_EPOCH).ok()?;
            Some((d.as_secs(), d.subsec_nanos()))
        }
        if !meta.is_file() {
            return None;
        }
        #[cfg(unix)]
        let identity = {
            use std::os::unix::fs::MetadataExt;
            Some((meta.dev(), meta.ino(), meta.ctime(), meta.ctime_nsec()))
        };
        #[cfg(not(unix))]
        let identity = None;
        Some(Self {
            len: meta.len(),
            modified: time(meta.modified())?,
            created: time(meta.created()),
            identity,
        })
    }
    /// Missing or unsupported metadata returns `None` and must not be treated
    /// as proof that a source is unchanged.
    pub fn path(path: &Path) -> Option<Self> {
        Self::metadata(&fs::metadata(path).ok()?)
    }
}
enum Input<'a> {
    File,
    Bytes(&'a [u8]),
}
impl Input<'_> {
    fn read(
        &self,
        path: &Path,
        budget: ImportBudget,
        cancelled: &dyn Fn() -> bool,
    ) -> Result<std::borrow::Cow<'_, [u8]>, ImportError> {
        match self {
            Self::File => {
                crate::import::read_source(path, budget, cancelled).map(std::borrow::Cow::Owned)
            }
            Self::Bytes(b) => Ok(std::borrow::Cow::Borrowed(b)),
        }
    }
    fn stamp(&self, path: &Path) -> Option<FileStamp> {
        let now = FileStamp::path(path);
        match self {
            Self::File => now,
            Self::Bytes(_) => None,
        }
    }
}
#[derive(Clone, Debug)]
pub struct ImportCache {
    root: PathBuf,
}
/// One index snapshot for a metadata-only catalog scan. This is a freshness hint,
/// not a decoded value: normal loading still validates and repairs cache objects.
#[cfg(feature = "watch")]
pub(crate) struct CacheProbe<'a> {
    cache: &'a ImportCache,
    index: Index,
}
#[cfg(feature = "watch")]
impl CacheProbe<'_> {
    pub(crate) fn current<I: AssetImporter>(
        &self,
        path: &Path,
        importer: &I,
        settings: &I::Settings,
        budget: ImportBudget,
    ) -> Result<bool, ImportError> {
        importer.validate_settings(settings)?;
        let Some(recipe) = recipe(importer, settings, budget)? else {
            return Ok(false);
        };
        let source = self.cache.source(path)?;
        let key = serde_json::to_string(&(&source, "", importer.descriptor().id, &recipe))
            .map_err(|e| error("cache_key", e))?;
        let Some(entry) = self.index.entries.get(&key) else {
            return Ok(false);
        };
        let stamp = FileStamp::path(path);
        if stamp.is_none()
            || stamp != entry.source_stat
            || entry.source_len > budget.max_input_bytes as u64
            || entry.decoded_bytes > budget.max_decoded_bytes
            || entry.recipe != recipe
            || !hex(&entry.object)
        {
            return Ok(false);
        }
        let object = self
            .cache
            .directory()
            .join("objects")
            .join(&entry.object[..2])
            .join(&entry.object[2..]);
        Ok(entry.object_stat.is_some() && FileStamp::path(&object) == entry.object_stat)
    }
}
fn recipe<I: AssetImporter>(
    importer: &I,
    settings: &I::Settings,
    budget: ImportBudget,
) -> Result<Option<String>, ImportError> {
    let Some(settings_key) = importer.cache_settings(settings)? else {
        return Ok(None);
    };
    let descriptor = importer.descriptor();
    Ok(Some(digest(&encode(&(
        FORMAT,
        descriptor.id,
        descriptor.version,
        settings_key,
        std::env::consts::ARCH,
        budget.max_input_bytes,
        budget.max_decoded_bytes,
    ))?)))
}
fn error(code: &str, message: impl std::fmt::Display) -> ImportError {
    ImportError::new(ImportErrorKind::Io, code, &message.to_string())
}
fn check(cancelled: &dyn Fn() -> bool) -> Result<(), ImportError> {
    if cancelled() {
        Err(ImportError::new(
            ImportErrorKind::Cancelled,
            "cancelled",
            "import cancelled",
        ))
    } else {
        Ok(())
    }
}
fn digest(bytes: &[u8]) -> String {
    format!("{:x}", Sha256::digest(bytes))
}
fn source_digest(bytes: &[u8]) -> String {
    SOURCE_CHECKS.fetch_add(1, Ordering::Relaxed);
    ACTIVITY.with(|a| a.source_checks.fetch_add(1, Ordering::Relaxed));
    digest(bytes)
}
fn object_digest(bytes: &[u8]) -> String {
    OBJECT_CHECKS.fetch_add(1, Ordering::Relaxed);
    ACTIVITY.with(|a| a.object_checks.fetch_add(1, Ordering::Relaxed));
    digest(bytes)
}
fn hex(s: &str) -> bool {
    s.len() == 64
        && s.bytes()
            .all(|b| b.is_ascii_hexdigit() && !b.is_ascii_uppercase())
}
/// Fixed-endian, bounded binary encoding. Codec schema changes must bump the
/// importer's cache version even when its source-format parser is unchanged.
pub fn encode<T: Serialize>(value: &T) -> Result<Vec<u8>, ImportError> {
    bincode::DefaultOptions::new()
        .with_fixint_encoding()
        .serialize(value)
        .map_err(|e| error("cache_encode", e))
}
pub fn decode<T: DeserializeOwned>(bytes: &[u8]) -> Result<T, ImportError> {
    bincode::DefaultOptions::new()
        .with_fixint_encoding()
        .with_limit(bytes.len() as u64)
        .reject_trailing_bytes()
        .deserialize(bytes)
        .map_err(|e| error("cache_decode", e))
}
fn read_bounded(path: &Path, limit: usize) -> std::io::Result<Vec<u8>> {
    let file = File::open(path)?;
    let mut bytes = Vec::new();
    file.take(limit.saturating_add(1) as u64)
        .read_to_end(&mut bytes)?;
    if bytes.len() > limit {
        return Err(std::io::Error::new(
            std::io::ErrorKind::InvalidData,
            "cache file exceeds limit",
        ));
    }
    Ok(bytes)
}
fn publish(path: &Path, bytes: &[u8]) -> Result<(), ImportError> {
    let parent = path.parent().unwrap();
    fs::create_dir_all(parent).map_err(|e| error("cache_directory", e))?;
    let (temporary, mut file) = loop {
        let temporary = parent.join(format!(
            ".tmp-{}-{}",
            std::process::id(),
            TEMP.fetch_add(1, Ordering::Relaxed)
        ));
        match File::create_new(&temporary) {
            Ok(file) => break (temporary, file),
            Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(e) => return Err(error("cache_publish", e)),
        }
    };
    let result = (|| {
        file.write_all(bytes)?;
        file.sync_all()?;
        drop(file);
        fs::rename(&temporary, path)
    })();
    if result.is_err() {
        let _ = fs::remove_file(&temporary);
    }
    result.map_err(|e| error("cache_publish", e))
}
impl ImportCache {
    #[cfg(feature = "watch")]
    pub(crate) fn probe(&self) -> Result<CacheProbe<'_>, ImportError> {
        Ok(CacheProbe {
            cache: self,
            index: self.index()?,
        })
    }
    /// Explicit project-cache loading, including in release editor builds.
    pub fn load<I: AssetImporter>(
        &self,
        path: &Path,
        importer: &I,
        settings: &I::Settings,
        budget: ImportBudget,
        cancelled: &dyn Fn() -> bool,
    ) -> Result<I::Output, ImportError> {
        self.import_input(path, "", Input::File, importer, settings, budget, cancelled)
    }
    /// Select an asset root explicitly. `.nico` is always a child of this root.
    pub fn new(root: impl AsRef<Path>) -> Result<Self, ImportError> {
        let root = root
            .as_ref()
            .canonicalize()
            .map_err(|e| error("cache_root", e))?;
        Ok(Self { root })
    }
    fn directory(&self) -> PathBuf {
        self.root.join(".nico")
    }
    fn lock(&self, name: &str, cancelled: &dyn Fn() -> bool) -> Result<File, ImportError> {
        let path = self.directory().join(name);
        fs::create_dir_all(path.parent().unwrap()).map_err(|e| error("cache_directory", e))?;
        let file = File::options()
            .read(true)
            .write(true)
            .create(true)
            .truncate(false)
            .open(path)
            .map_err(|e| error("cache_lock", e))?;
        let start = Instant::now();
        loop {
            check(cancelled)?;
            match file.try_lock() {
                Ok(()) => return Ok(file),
                Err(std::fs::TryLockError::WouldBlock) => {}
                Err(e) => return Err(error("cache_lock", e)),
            }
            if start.elapsed() > Duration::from_secs(60) {
                return Err(error(
                    "cache_lock_timeout",
                    "another importer holds the cache lock",
                ));
            }
            std::thread::sleep(Duration::from_millis(10));
        }
    }
    fn index(&self) -> Result<Index, ImportError> {
        match read_bounded(&self.directory().join("index.json"), INDEX_LIMIT) {
            Ok(bytes) => Ok(serde_json::from_slice::<Index>(&bytes)
                .ok()
                .filter(|i| i.format == FORMAT)
                .unwrap_or_default()),
            Err(e)
                if matches!(
                    e.kind(),
                    std::io::ErrorKind::NotFound | std::io::ErrorKind::InvalidData
                ) =>
            {
                Ok(Index::default())
            }
            Err(e) => Err(error("cache_index", e)),
        }
    }
    fn save(&self, index: &Index) -> Result<(), ImportError> {
        let bytes = serde_json::to_vec_pretty(index).map_err(|e| error("cache_index", e))?;
        if bytes.len() > INDEX_LIMIT {
            return Err(error("cache_index", "index size limit exceeded"));
        }
        publish(&self.directory().join("index.json"), &bytes)
    }
    fn source(&self, path: &Path) -> Result<String, ImportError> {
        let full = path.canonicalize().map_err(|e| error("source_path", e))?;
        let relative = full
            .strip_prefix(&self.root)
            .map_err(|e| error("source_root", e))?;
        if relative
            .components()
            .any(|c| !matches!(c, Component::Normal(_)))
            || relative.starts_with(".nico")
        {
            return Err(error(
                "source_path",
                "source must be inside the asset root and outside .nico",
            ));
        }
        relative
            .to_str()
            .map(|s| s.replace('\\', "/"))
            .ok_or_else(|| error("source_path", "non-UTF-8 source path"))
    }
    /// Import a file or an embedded subresource. `bytes` are the exact primary
    /// input; embedded GLBs already contain their buffer/image dependencies.
    /// Userland codecs must include any additional dependency digests in settings.
    #[allow(clippy::too_many_arguments)]
    pub fn import<I: AssetImporter>(
        &self,
        path: &Path,
        subresource: &str,
        bytes: &[u8],
        importer: &I,
        settings: &I::Settings,
        budget: ImportBudget,
        cancelled: &dyn Fn() -> bool,
    ) -> Result<I::Output, ImportError> {
        self.import_input(
            path,
            subresource,
            Input::Bytes(bytes),
            importer,
            settings,
            budget,
            cancelled,
        )
    }
    #[allow(clippy::too_many_arguments)]
    fn import_input<I: AssetImporter>(
        &self,
        path: &Path,
        subresource: &str,
        input: Input<'_>,
        importer: &I,
        settings: &I::Settings,
        budget: ImportBudget,
        cancelled: &dyn Fn() -> bool,
    ) -> Result<I::Output, ImportError> {
        check(cancelled)?;
        importer.validate_settings(settings)?;
        let Some(recipe) = recipe(importer, settings, budget)? else {
            let bytes = input.read(path, budget, cancelled)?;
            let output = importer.import(
                &mut ImportContext::new(&bytes, budget, cancelled)?,
                settings,
            )?;
            check(cancelled)?;
            return Ok(output);
        };
        let source = self.source(path)?;
        let descriptor = importer.descriptor();
        let key = serde_json::to_string(&(&source, subresource, descriptor.id, &recipe))
            .map_err(|e| error("cache_key", e))?;
        let _asset_lock =
            self.lock(&format!("locks/{}.lock", digest(key.as_bytes())), cancelled)?;
        let source_stat = input.stamp(path);
        let source_len = match &input {
            Input::File => fs::metadata(path)
                .map_err(|e| error("source_metadata", e))?
                .len(),
            Input::Bytes(b) => b.len() as u64,
        };
        if source_len > budget.max_input_bytes as u64 {
            return Err(ImportError::new(
                ImportErrorKind::LimitExceeded,
                "import_budget",
                "source exceeds input budget",
            ));
        }
        let entry = {
            let _lock = self.lock("index.lock", cancelled)?;
            let mut index = self.index()?;
            let before = index.entries.len();
            index.entries.retain(|_, e| {
                let p = Path::new(&e.source);
                p.components().all(|c| matches!(c, Component::Normal(_)))
                    && !p.starts_with(".nico")
                    && self.root.join(p).is_file()
            });
            if before != index.entries.len() {
                self.save(&index)?;
            }
            index.entries.get(&key).cloned()
        };
        let quick = entry.as_ref().is_some_and(|e| {
            source_stat.is_some() && e.source_stat == source_stat && e.source_len == source_len
        });
        let mut bytes = None;
        let mut source_hash = None;
        if !quick {
            let loaded = input.read(path, budget, cancelled)?;
            source_hash = Some(source_digest(&loaded));
            bytes = Some(loaded);
        }
        let limit = budget
            .max_decoded_bytes
            .saturating_mul(2)
            .saturating_add(1024 * 1024);
        if let Some(mut entry) = entry
            && entry.recipe == recipe
            && (quick || source_hash.as_ref() == Some(&entry.source_hash))
            && entry.decoded_bytes <= budget.max_decoded_bytes
            && hex(&entry.object)
        {
            let object = self
                .directory()
                .join("objects")
                .join(&entry.object[..2])
                .join(&entry.object[2..]);
            let before = FileStamp::path(&object);
            if let Ok(payload) = read_bounded(&object, limit) {
                let after = FileStamp::path(&object);
                let stable = before.is_some() && before == after;
                let object_quick = stable && entry.object_stat == before;
                if object_quick || object_digest(&payload) == entry.object {
                    let mut context = ImportContext::new(&[], budget, cancelled)?;
                    context.claim_decoded(entry.decoded_bytes)?;
                    if let Ok(output) = importer.cache_decode(&payload, settings) {
                        check(cancelled)?;
                        let verified = source_stat
                            .clone()
                            .filter(|s| Some(s) == input.stamp(path).as_ref());
                        if entry.source_stat != verified
                            || entry.object_stat != after
                            || entry.source_len != source_len
                        {
                            entry.source_stat = verified.clone();
                            entry.object_stat = after.filter(|_| stable);
                            entry.source_len = source_len;
                            let _lock = self.lock("index.lock", cancelled)?;
                            let mut index = self.index()?;
                            index.entries.insert(key, entry);
                            self.save(&index)?;
                        }
                        HITS.fetch_add(1, Ordering::Relaxed);
                        ACTIVITY.with(|a| a.hits.fetch_add(1, Ordering::Relaxed));
                        return Ok(output);
                    }
                }
            }
            REBUILDS.fetch_add(1, Ordering::Relaxed);
            ACTIVITY.with(|a| a.rebuilds.fetch_add(1, Ordering::Relaxed));
        }
        {
            let _lock = self.lock("index.lock", cancelled)?;
            let mut index = self.index()?;
            index.entries.remove(&key);
            self.save(&index)?;
        }
        let bytes = match bytes {
            Some(b) => b,
            None => input.read(path, budget, cancelled)?,
        };
        let source_hash = source_hash.unwrap_or_else(|| source_digest(&bytes));
        let mut context = ImportContext::new(&bytes, budget, cancelled)?;
        let output = importer.import(&mut context, settings)?;
        check(cancelled)?;
        let payload = importer.cache_encode(&output)?;
        if payload.len() > limit {
            return Err(error("cache_object", "encoded object exceeds limit"));
        }
        let object = digest(&payload);
        let object_path = self
            .directory()
            .join("objects")
            .join(&object[..2])
            .join(&object[2..]);
        // Different sources can produce the same object. Preserve its metadata
        // when it is already correct so publishing one source does not invalidate
        // the other sources' metadata-only freshness checks.
        {
            let _lock = self.lock(&format!("locks/object-{object}.lock"), cancelled)?;
            if !read_bounded(&object_path, limit).is_ok_and(|existing| existing == payload) {
                publish(&object_path, &payload)?;
            }
        }
        check(cancelled)?;
        let verified = source_stat.filter(|s| Some(s) == input.stamp(path).as_ref());
        let entry = Entry {
            source,
            source_hash,
            recipe,
            object,
            decoded_bytes: context.claimed_bytes(),
            source_stat: verified.clone(),
            object_stat: FileStamp::path(&object_path),
            source_len,
        };
        let _lock = self.lock("index.lock", cancelled)?;
        let mut index = self.index()?;
        index.entries.insert(key, entry);
        self.save(&index)?;
        IMPORTS.fetch_add(1, Ordering::Relaxed);
        ACTIVITY.with(|a| a.imports.fetch_add(1, Ordering::Relaxed));
        Ok(output)
    }
}
pub(crate) fn source_cache(path: &Path) -> Result<ImportCache, ImportError> {
    let full = path.canonicalize().map_err(|e| error("source_path", e))?;
    let parent = full
        .parent()
        .ok_or_else(|| error("source_path", "missing source parent"))?;
    let root = parent
        .ancestors()
        .find(|p| p.join("nico.project.toml").is_file())
        .or_else(|| {
            parent
                .ancestors()
                .find(|p| p.file_name().is_some_and(|name| name == "assets"))
                .and_then(Path::parent)
        })
        .unwrap_or(parent);
    ImportCache::new(root)
}
/// Reuse imported file data across launches in debug and release builds.
/// Store `.nico` beside the nearest project manifest, or above the nearest `assets` folder.
/// Standalone sources use their containing folder. Sources remain authoritative.
pub fn load_file<I: AssetImporter>(
    path: &Path,
    importer: &I,
    settings: &I::Settings,
    budget: ImportBudget,
    cancelled: &dyn Fn() -> bool,
) -> Result<I::Output, ImportError> {
    check(cancelled)?;
    source_cache(path)?.load(path, importer, settings, budget, cancelled)
}
/// Cache embedded bytes by owner, subresource, settings, and content.
/// Without an owner file, import directly without creating a cache.
pub fn load_bytes<I: AssetImporter>(
    path: &Path,
    subresource: &str,
    bytes: &[u8],
    importer: &I,
    settings: &I::Settings,
    budget: ImportBudget,
    cancelled: &dyn Fn() -> bool,
) -> Result<I::Output, ImportError> {
    check(cancelled)?;
    if path.is_file() {
        return source_cache(path)?.import(
            path,
            subresource,
            bytes,
            importer,
            settings,
            budget,
            cancelled,
        );
    }
    importer.validate_settings(settings)?;
    let output = importer.import(&mut ImportContext::new(bytes, budget, cancelled)?, settings)?;
    check(cancelled)?;
    IMPORTS.fetch_add(1, Ordering::Relaxed);
    ACTIVITY.with(|a| a.imports.fetch_add(1, Ordering::Relaxed));
    Ok(output)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::import::ImporterDescriptor;
    use std::sync::{Arc, atomic::AtomicUsize};

    struct Fixture(PathBuf);
    impl Fixture {
        fn new() -> Self {
            let path = std::env::temp_dir().join(format!(
                "nico-cache-{}-{}",
                std::process::id(),
                TEMP.fetch_add(1, Ordering::Relaxed)
            ));
            fs::create_dir_all(&path).unwrap();
            Self(path)
        }
        fn source(&self) -> PathBuf {
            self.0.join("source.bin")
        }
        fn cache(&self) -> ImportCache {
            ImportCache::new(&self.0).unwrap()
        }
    }
    impl Drop for Fixture {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }
    struct Counting {
        calls: Arc<AtomicUsize>,
        version: &'static str,
    }
    impl Default for Counting {
        fn default() -> Self {
            Self {
                calls: Arc::new(AtomicUsize::new(0)),
                version: "1",
            }
        }
    }
    impl AssetImporter for Counting {
        type Output = Vec<u8>;
        type Settings = u8;
        fn descriptor(&self) -> ImporterDescriptor {
            ImporterDescriptor {
                id: "test.bytes",
                version: self.version,
                extensions: &["bin"],
            }
        }
        fn cache_settings(&self, s: &u8) -> Result<Option<Vec<u8>>, ImportError> {
            Ok(Some(vec![*s]))
        }
        fn cache_encode(&self, v: &Vec<u8>) -> Result<Vec<u8>, ImportError> {
            encode(v)
        }
        fn cache_decode(&self, b: &[u8], _: &u8) -> Result<Vec<u8>, ImportError> {
            decode(b)
        }
        fn import(&self, c: &mut ImportContext<'_>, s: &u8) -> Result<Vec<u8>, ImportError> {
            self.calls.fetch_add(1, Ordering::SeqCst);
            if c.bytes() == b"bad" {
                return Err(error("bad_source", "invalid test source"));
            }
            c.claim_decoded(c.bytes().len())?;
            Ok(c.bytes().iter().map(|v| v.wrapping_add(*s)).collect())
        }
    }
    fn budget() -> ImportBudget {
        ImportBudget {
            max_input_bytes: 1024,
            max_decoded_bytes: 1024,
        }
    }
    fn run(f: &Fixture, i: &Counting, s: u8) -> Result<Vec<u8>, ImportError> {
        let bytes = fs::read(f.source()).unwrap();
        f.cache()
            .import(&f.source(), "", &bytes, i, &s, budget(), &|| false)
    }
    fn file_run(f: &Fixture, i: &Counting) -> Result<Vec<u8>, ImportError> {
        f.cache()
            .import_input(&f.source(), "", Input::File, i, &0, budget(), &|| false)
    }
    fn object_path(f: &Fixture) -> PathBuf {
        let index = f.cache().index().unwrap();
        let hash = &index.entries.values().next().unwrap().object;
        f.cache()
            .directory()
            .join("objects")
            .join(&hash[..2])
            .join(&hash[2..])
    }
    #[test]
    fn unchanged_files_skip_source_and_object_content_checks() {
        let f = Fixture::new();
        let i = Counting::default();
        fs::write(f.source(), b"abc").unwrap();
        file_run(&f, &i).unwrap();
        let observer = observe_current_thread();
        let before = observer.stats();
        assert_eq!(file_run(&f, &i).unwrap(), b"abc");
        assert_eq!(observer.stats().source_checks, before.source_checks);
        assert_eq!(observer.stats().object_checks, before.object_checks);
        assert_eq!(i.calls.load(Ordering::SeqCst), 1);
    }
    #[test]
    fn native_file_loading_reuses_cache_and_reimports_changed_sources() {
        let f = Fixture::new();
        let importer = Counting::default();
        fs::write(f.source(), b"abc").unwrap();
        load_file(&f.source(), &importer, &0, budget(), &|| false).unwrap();
        load_file(&f.source(), &importer, &0, budget(), &|| false).unwrap();
        assert_eq!(importer.calls.load(Ordering::SeqCst), 1);
        fs::write(f.source(), b"def").unwrap();
        for _ in 0..2 {
            assert_eq!(
                load_file(&f.source(), &importer, &0, budget(), &|| false).unwrap(),
                b"def"
            );
        }
        assert_eq!(importer.calls.load(Ordering::SeqCst), 2);
        assert_eq!(
            load_file(&f.source(), &importer, &1, budget(), &|| false).unwrap(),
            b"efg"
        );
        assert_eq!(importer.calls.load(Ordering::SeqCst), 3);
        fs::write(f.source(), b"bad").unwrap();
        assert!(load_file(&f.source(), &importer, &0, budget(), &|| false).is_err());
    }

    #[test]
    fn native_imports_preserve_bounds_cancellation_and_memory_only_sources() {
        let f = Fixture::new();
        let importer = Counting::default();
        fs::write(f.source(), b"abc").unwrap();
        let missing = f.0.join("missing.glb");
        assert_eq!(
            load_bytes(
                &missing,
                "image/0",
                b"def",
                &importer,
                &0,
                budget(),
                &|| false
            )
            .unwrap(),
            b"def"
        );
        assert!(load_file(&missing, &importer, &0, budget(), &|| false).is_err());
        assert!(load_file(&f.source(), &importer, &0, budget(), &|| true).is_err());
        assert!(load_bytes(&missing, "", b"def", &importer, &0, budget(), &|| true).is_err());
        assert!(!f.0.join(".nico").exists());
        let small = ImportBudget {
            max_input_bytes: 2,
            ..budget()
        };
        assert!(load_file(&f.source(), &importer, &0, small, &|| false).is_err());
        assert!(load_bytes(&missing, "", b"def", &importer, &0, small, &|| false).is_err());
    }

    #[test]
    fn embedded_imports_reuse_cache_and_track_content_and_subresources() {
        let f = Fixture::new();
        fs::write(f.source(), b"owner").unwrap();
        let importer = Counting::default();
        for _ in 0..2 {
            assert_eq!(
                load_bytes(
                    &f.source(),
                    "image/0",
                    b"abc",
                    &importer,
                    &0,
                    budget(),
                    &|| false
                )
                .unwrap(),
                b"abc"
            );
        }
        assert_eq!(importer.calls.load(Ordering::SeqCst), 1);
        for (name, bytes) in [("image/0", b"def"), ("image/1", b"def")] {
            assert_eq!(
                load_bytes(&f.source(), name, bytes, &importer, &0, budget(), &|| false).unwrap(),
                bytes
            );
        }
        assert_eq!(importer.calls.load(Ordering::SeqCst), 3);
    }

    #[test]
    fn native_cache_uses_project_then_assets_then_source_folder() {
        let f = Fixture::new();
        let folder = f.0.join("assets/models");
        fs::create_dir_all(&folder).unwrap();
        let source = folder.join("model.bin");
        fs::write(&source, b"abc").unwrap();
        assert_eq!(
            source_cache(&source).unwrap().root,
            f.0.canonicalize().unwrap()
        );
        fs::write(folder.join("nico.project.toml"), "").unwrap();
        assert_eq!(
            source_cache(&source).unwrap().root,
            folder.canonicalize().unwrap()
        );
        fs::write(f.source(), b"abc").unwrap();
        assert_eq!(
            source_cache(&f.source()).unwrap().root,
            f.0.canonicalize().unwrap()
        );
    }

    #[cfg(feature = "watch")]
    #[test]
    fn metadata_probe_checks_recipe_source_and_object_without_loading_content() {
        let f = Fixture::new();
        let i = Counting::default();
        fs::write(f.source(), b"abc").unwrap();
        file_run(&f, &i).unwrap();
        let cache = f.cache();
        let probe = cache.probe().unwrap();
        let activity = observe_current_thread();
        let before = activity.stats();
        assert!(probe.current(&f.source(), &i, &0, budget()).unwrap());
        assert!(!probe.current(&f.source(), &i, &1, budget()).unwrap());
        assert!(
            !probe
                .current(
                    &f.source(),
                    &i,
                    &0,
                    ImportBudget {
                        max_decoded_bytes: 1,
                        ..budget()
                    }
                )
                .unwrap()
        );
        assert_eq!(activity.stats().hits, before.hits);
        assert_eq!(activity.stats().source_checks, before.source_checks);
        assert_eq!(activity.stats().object_checks, before.object_checks);
        fs::write(object_path(&f), b"broken cache").unwrap();
        assert!(!probe.current(&f.source(), &i, &0, budget()).unwrap());
        assert_eq!(file_run(&f, &i).unwrap(), b"abc");
        let probe = cache.probe().unwrap();
        assert!(probe.current(&f.source(), &i, &0, budget()).unwrap());
        fs::write(f.source(), b"changed").unwrap();
        assert!(!probe.current(&f.source(), &i, &0, budget()).unwrap());
        fs::remove_file(f.source()).unwrap();
        assert!(probe.current(&f.source(), &i, &0, budget()).is_err());
    }
    #[test]
    fn metadata_changes_hash_once_then_refresh_without_reimport() {
        let f = Fixture::new();
        let i = Counting::default();
        fs::write(f.source(), b"abc").unwrap();
        file_run(&f, &i).unwrap();
        let observer = observe_current_thread();
        let before = observer.stats();
        let future = std::time::SystemTime::now() + Duration::from_secs(5);
        File::options()
            .write(true)
            .open(f.source())
            .unwrap()
            .set_modified(future)
            .unwrap();
        File::options()
            .write(true)
            .open(object_path(&f))
            .unwrap()
            .set_modified(future)
            .unwrap();
        file_run(&f, &i).unwrap();
        assert_eq!(observer.stats().source_checks, before.source_checks + 1);
        assert_eq!(observer.stats().object_checks, before.object_checks + 1);
        file_run(&f, &i).unwrap();
        assert_eq!(observer.stats().source_checks, before.source_checks + 1);
        assert_eq!(observer.stats().object_checks, before.object_checks + 1);
        assert_eq!(i.calls.load(Ordering::SeqCst), 1);
    }
    #[cfg(unix)]
    #[test]
    fn quick_stat_detects_preserved_mtime_edits_and_replacement() {
        let f = Fixture::new();
        let i = Counting::default();
        fs::write(f.source(), b"abc").unwrap();
        file_run(&f, &i).unwrap();
        let original = fs::metadata(f.source()).unwrap().modified().unwrap();
        fs::write(f.source(), b"xyz").unwrap();
        File::options()
            .write(true)
            .open(f.source())
            .unwrap()
            .set_modified(original)
            .unwrap();
        assert_eq!(file_run(&f, &i).unwrap(), b"xyz");
        let replacement = f.0.join("replacement");
        fs::write(&replacement, b"new").unwrap();
        File::options()
            .write(true)
            .open(&replacement)
            .unwrap()
            .set_modified(original)
            .unwrap();
        fs::rename(replacement, f.source()).unwrap();
        assert_eq!(file_run(&f, &i).unwrap(), b"new");
        assert_eq!(i.calls.load(Ordering::SeqCst), 3);
    }
    #[test]
    fn changed_object_is_checked_and_rebuilt_then_returns_to_quick_path() {
        let f = Fixture::new();
        let i = Counting::default();
        fs::write(f.source(), b"abc").unwrap();
        file_run(&f, &i).unwrap();
        let object = object_path(&f);
        let mut content = fs::read(&object).unwrap();
        content[8] ^= 1;
        fs::write(&object, content).unwrap();
        assert_eq!(file_run(&f, &i).unwrap(), b"abc");
        assert_eq!(i.calls.load(Ordering::SeqCst), 2);
        let before = observe_current_thread().stats();
        file_run(&f, &i).unwrap();
        let after = observe_current_thread().stats();
        assert_eq!(
            (before.source_checks, before.object_checks),
            (after.source_checks, after.object_checks)
        );
    }
    #[test]
    fn legacy_entries_are_verified_once_without_discarding_objects() {
        let f = Fixture::new();
        let i = Counting::default();
        fs::write(f.source(), b"abc").unwrap();
        file_run(&f, &i).unwrap();
        let path = f.cache().directory().join("index.json");
        let mut json: serde_json::Value =
            serde_json::from_slice(&fs::read(&path).unwrap()).unwrap();
        for entry in json["entries"].as_object_mut().unwrap().values_mut() {
            for key in ["source_stat", "object_stat", "source_len"] {
                entry.as_object_mut().unwrap().remove(key);
            }
        }
        fs::write(path, serde_json::to_vec(&json).unwrap()).unwrap();
        let before = observe_current_thread().stats();
        file_run(&f, &i).unwrap();
        let checked = observe_current_thread().stats();
        assert_eq!(checked.source_checks, before.source_checks + 1);
        assert_eq!(checked.object_checks, before.object_checks + 1);
        file_run(&f, &i).unwrap();
        let warm = observe_current_thread().stats();
        assert_eq!(warm.source_checks, checked.source_checks);
        assert_eq!(warm.object_checks, checked.object_checks);
        assert_eq!(i.calls.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn warm_load_skips_parser_and_content_settings_and_version_invalidate() {
        let f = Fixture::new();
        let mut i = Counting::default();
        fs::write(f.source(), b"abc").unwrap();
        assert_eq!(run(&f, &i, 0).unwrap(), b"abc");
        assert_eq!(run(&f, &i, 0).unwrap(), b"abc");
        assert_eq!(i.calls.load(Ordering::SeqCst), 1);
        let modified = fs::metadata(f.source()).unwrap().modified().unwrap();
        fs::write(f.source(), b"xyz").unwrap();
        File::options()
            .write(true)
            .open(f.source())
            .unwrap()
            .set_modified(modified)
            .unwrap();
        assert_eq!(run(&f, &i, 0).unwrap(), b"xyz");
        assert_eq!(run(&f, &i, 1).unwrap(), b"yz{");
        i.version = "2";
        run(&f, &i, 1).unwrap();
        assert_eq!(i.calls.load(Ordering::SeqCst), 4);
    }
    #[test]
    fn alternating_budgets_retain_both_recipe_variants() {
        let f = Fixture::new();
        let i = Counting::default();
        fs::write(f.source(), b"abc").unwrap();
        for _ in 0..2 {
            for max_decoded_bytes in [512, 1024] {
                f.cache()
                    .import(
                        &f.source(),
                        "image/0",
                        b"abc",
                        &i,
                        &0,
                        ImportBudget {
                            max_decoded_bytes,
                            ..budget()
                        },
                        &|| false,
                    )
                    .unwrap();
            }
        }
        assert_eq!(i.calls.load(Ordering::SeqCst), 2);
        assert_eq!(f.cache().index().unwrap().entries.len(), 2);
    }

    #[test]
    fn missing_and_corrupt_objects_and_index_rebuild_from_source() {
        let f = Fixture::new();
        let i = Counting::default();
        fs::write(f.source(), b"abc").unwrap();
        run(&f, &i, 0).unwrap();
        let index = f.cache().index().unwrap();
        let e = index.entries.values().next().unwrap();
        let object = f
            .cache()
            .directory()
            .join("objects")
            .join(&e.object[..2])
            .join(&e.object[2..]);
        fs::remove_file(&object).unwrap();
        run(&f, &i, 0).unwrap();
        fs::write(&object, b"corrupt").unwrap();
        run(&f, &i, 0).unwrap();
        fs::write(f.cache().directory().join("index.json"), b"{").unwrap();
        run(&f, &i, 0).unwrap();
        assert_eq!(i.calls.load(Ordering::SeqCst), 4);
        assert_eq!(run(&f, &i, 0).unwrap(), b"abc");
        assert_eq!(i.calls.load(Ordering::SeqCst), 4);
    }
    #[test]
    fn failed_reimport_invalidates_previous_row_and_cancellation_does_not_publish() {
        let f = Fixture::new();
        let i = Counting::default();
        fs::write(f.source(), b"abc").unwrap();
        run(&f, &i, 0).unwrap();
        fs::write(f.source(), b"bad").unwrap();
        assert!(run(&f, &i, 0).is_err());
        assert!(f.cache().index().unwrap().entries.is_empty());
        assert!(
            f.cache()
                .import(&f.source(), "", b"abc", &i, &0, budget(), &|| true)
                .is_err()
        );
        assert!(f.cache().index().unwrap().entries.is_empty());
    }
    #[test]
    fn stricter_budget_cannot_reuse_larger_cached_output() {
        let f = Fixture::new();
        let i = Counting::default();
        fs::write(f.source(), b"abc").unwrap();
        run(&f, &i, 0).unwrap();
        let small = ImportBudget {
            max_decoded_bytes: 2,
            ..budget()
        };
        assert!(
            f.cache()
                .import(&f.source(), "", b"abc", &i, &0, small, &|| false)
                .is_err()
        );
    }
    #[test]
    fn concurrent_imports_publish_one_parse_and_prune_deleted_sources() {
        let f = Fixture::new();
        let i = Arc::new(Counting::default());
        fs::write(f.source(), b"abc").unwrap();
        std::thread::scope(|scope| {
            for _ in 0..4 {
                scope.spawn(|| {
                    assert_eq!(run(&f, &i, 0).unwrap(), b"abc");
                });
            }
        });
        assert_eq!(i.calls.load(Ordering::SeqCst), 1);
        fs::remove_file(f.source()).unwrap();
        let other = f.0.join("other.bin");
        fs::write(&other, b"abc").unwrap();
        f.cache()
            .import(&other, "", b"abc", i.as_ref(), &0, budget(), &|| false)
            .unwrap();
        let index = f.cache().index().unwrap();
        assert_eq!(index.entries.len(), 1);
        assert_eq!(index.entries.values().next().unwrap().source, "other.bin");
    }
    #[test]
    fn progress_observer_excludes_other_loading_threads() {
        let f = Fixture::new();
        let i = Counting::default();
        fs::write(f.source(), b"abc").unwrap();
        let observer = observe_current_thread();
        let before = observer.stats();
        std::thread::scope(|scope| {
            scope.spawn(|| {
                let local = observe_current_thread();
                run(&f, &i, 0).unwrap();
                run(&f, &i, 0).unwrap();
                assert_eq!(local.stats().imports, 1);
                assert_eq!(local.stats().hits, 1);
            });
        });
        assert_eq!(observer.stats().imports, before.imports);
        assert_eq!(observer.stats().hits, before.hits);
    }

    #[test]
    fn concurrent_distinct_sources_merge_index_entries() {
        let f = Fixture::new();
        let i = Counting::default();
        let paths: Vec<_> = (0..8)
            .map(|n| {
                let path = f.0.join(format!("{n}.bin"));
                fs::write(&path, b"abc").unwrap();
                path
            })
            .collect();
        std::thread::scope(|scope| {
            for path in &paths {
                let cache = f.cache();
                let importer = &i;
                scope.spawn(move || {
                    cache
                        .import(path, "", b"abc", importer, &0, budget(), &|| false)
                        .unwrap();
                });
            }
        });
        assert_eq!(f.cache().index().unwrap().entries.len(), 8);
        assert_eq!(i.calls.load(Ordering::SeqCst), 8);
    }

    #[cfg(feature = "png-import")]
    #[test]
    fn compressed_png_warm_load_skips_cooking_and_content_checks() {
        use crate::importers::{PngImporter, PngSettings};
        let f = Fixture::new();
        let mut bytes = Vec::new();
        {
            let mut encoder = png::Encoder::new(&mut bytes, 8, 8);
            encoder.set_color(png::ColorType::Rgba);
            encoder.set_depth(png::BitDepth::Eight);
            encoder
                .write_header()
                .unwrap()
                .write_image_data(&[255, 0, 0, 128].repeat(64))
                .unwrap();
        }
        fs::write(f.source(), &bytes).unwrap();
        let load = || {
            f.cache()
                .load(
                    &f.source(),
                    &PngImporter,
                    &PngSettings::default(),
                    budget(),
                    &|| false,
                )
                .unwrap()
        };
        let first = load();
        assert_eq!(first.encoding(), crate::TextureEncoding::Bc3);
        let observer = observe_current_thread();
        let before = observer.stats();
        let second = load();
        let delta = observer.stats().since(before);
        assert_eq!(delta.imports, 0);
        assert_eq!(delta.source_checks, 0);
        assert_eq!(delta.object_checks, 0);
        assert_eq!(delta.hits, 1);
        assert_eq!(second.encoded_bytes(), first.encoded_bytes());
        assert_eq!(second.encoded_bytes().len(), 64);
    }

    #[cfg(feature = "png-import")]
    #[test]
    fn png_object_round_trip_preserves_dimensions_and_pixels() {
        use crate::importers::{PngImporter, PngSettings};
        let mut bytes = Vec::new();
        {
            let mut encoder = png::Encoder::new(&mut bytes, 2, 1);
            encoder.set_color(png::ColorType::Rgba);
            encoder.set_depth(png::BitDepth::Eight);
            encoder
                .write_header()
                .unwrap()
                .write_image_data(&[255, 0, 0, 255, 0, 255, 0, 128])
                .unwrap();
        }
        let f = Fixture::new();
        fs::write(f.source(), &bytes).unwrap();
        let first = f
            .cache()
            .import(
                &f.source(),
                "",
                &bytes,
                &PngImporter,
                &PngSettings::default(),
                budget(),
                &|| false,
            )
            .unwrap();
        let second = f
            .cache()
            .import(
                &f.source(),
                "",
                &bytes,
                &PngImporter,
                &PngSettings::default(),
                budget(),
                &|| false,
            )
            .unwrap();
        assert_eq!((second.width(), second.height()), (2, 1));
        assert_eq!(first.pixels(), second.pixels());
    }
}

/// Binary-compatible with a bincode Vec<u8>, but avoids per-byte serde dispatch.
/// Model images can contain multi-megabyte compressed buffers even on cache hits.
pub(crate) mod byte_buffer {
    use serde::{
        Deserializer, Serializer,
        de::{Error, Visitor},
    };
    pub fn serialize<S: Serializer>(bytes: &[u8], serializer: S) -> Result<S::Ok, S::Error> {
        serializer.serialize_bytes(bytes)
    }
    pub fn deserialize<'de, D: Deserializer<'de>>(deserializer: D) -> Result<Vec<u8>, D::Error> {
        struct Bytes;
        impl<'de> Visitor<'de> for Bytes {
            type Value = Vec<u8>;
            fn expecting(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
                f.write_str("an encoded image byte buffer")
            }
            fn visit_seq<A: serde::de::SeqAccess<'de>>(
                self,
                mut seq: A,
            ) -> Result<Self::Value, A::Error> {
                let mut bytes = Vec::with_capacity(seq.size_hint().unwrap_or(0).min(4096));
                while let Some(byte) = seq.next_element()? {
                    bytes.push(byte);
                }
                Ok(bytes)
            }
            fn visit_bytes<E: Error>(self, value: &[u8]) -> Result<Self::Value, E> {
                Ok(value.to_vec())
            }
            fn visit_byte_buf<E: Error>(self, value: Vec<u8>) -> Result<Self::Value, E> {
                Ok(value)
            }
        }
        deserializer.deserialize_byte_buf(Bytes)
    }
}
