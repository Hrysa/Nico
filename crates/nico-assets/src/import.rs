//! Runtime-free, explicitly selected CPU asset importers.
//!
//! Importers are trusted Rust code. Budgets and cancellation are cooperative;
//! they do not sandbox allocations, I/O, or execution in third-party code.
//!
//! An external crate can import a standard or custom type without runtime features:
//! ```
//! use nico_assets::{AssetId, import::*};
//! struct TextImporter;
//! impl AssetImporter for TextImporter {
//!     type Output = String;
//!     type Settings = ();
//!     fn descriptor(&self) -> ImporterDescriptor {
//!         ImporterDescriptor { id: "game.text", version: "1", extensions: &["txt"] }
//!     }
//!     fn import(&self, context: &mut ImportContext<'_>, _: &()) -> Result<String, ImportError> {
//!         context.claim_decoded(context.bytes().len())?;
//!         std::str::from_utf8(context.bytes()).map(str::to_owned).map_err(|error|
//!             ImportError::new(ImportErrorKind::Malformed, "utf8", &error.to_string()))
//!     }
//! }
//! let mut imports = ImportRegistry::new();
//! let text = imports.register(TextImporter)?;
//! let id = AssetId::from_u128(1);
//! imports.asset(id, "hello.txt", &text, (), ImportBudget::default())?;
//! assert_eq!(imports.import_bytes(id, b"hello", &|| false)?, "hello");
//! # Ok::<(), Box<dyn std::error::Error>>(())
//! ```

use std::{
    collections::BTreeMap,
    fmt,
    path::{Component, PathBuf},
    sync::Arc,
};

use crate::{AssetId, asset_error::AssetError};
#[cfg(any(feature = "runtime-loading", all(test, feature = "png-import")))]
use std::path::Path;
#[cfg(any(feature = "runtime-loading", all(test, feature = "png-import")))]
use std::{fs::File, io::Read};

/// Immutable provenance for a registered implementation. Versions are not cache keys.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ImporterDescriptor {
    pub id: &'static str,
    pub version: &'static str,
    pub extensions: &'static [&'static str],
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ImportErrorKind {
    Io,
    Malformed,
    Unsupported,
    InvalidSettings,
    LimitExceeded,
    Cancelled,
}

/// Structured importer failure with bounded UTF-8 diagnostic fields.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ImportError {
    kind: ImportErrorKind,
    code: String,
    message: String,
    location: Option<String>,
    truncated: bool,
}

fn bounded(value: &str, limit: usize) -> String {
    let mut end = value.len().min(limit);
    while !value.is_char_boundary(end) {
        end -= 1;
    }
    value[..end].to_owned()
}

impl ImportError {
    /// Codes are bounded to 128 bytes and messages/locations to 1024 bytes each.
    pub fn new(kind: ImportErrorKind, code: &str, message: &str) -> Self {
        Self {
            kind,
            code: bounded(code, 128),
            message: bounded(message, 1024),
            location: None,
            truncated: code.len() > 128 || message.len() > 1024,
        }
    }

    #[must_use]
    pub fn at(mut self, location: &str) -> Self {
        self.truncated |= location.len() > 1024;
        self.location = Some(bounded(location, 1024));
        self
    }

    #[must_use]
    pub const fn kind(&self) -> ImportErrorKind {
        self.kind
    }
    #[must_use]
    pub fn code(&self) -> &str {
        &self.code
    }
    #[must_use]
    pub fn message(&self) -> &str {
        &self.message
    }
    #[must_use]
    pub fn location(&self) -> Option<&str> {
        self.location.as_deref()
    }
    #[must_use]
    pub const fn is_truncated(&self) -> bool {
        self.truncated
    }
}

impl fmt::Display for ImportError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: {}", self.code, self.message)
    }
}
impl std::error::Error for ImportError {}

/// Per-source bounds, separate from format-specific settings and store capacity.
#[derive(Clone, Copy, Debug)]
pub struct ImportBudget {
    pub max_input_bytes: usize,
    pub max_decoded_bytes: usize,
}
impl Default for ImportBudget {
    fn default() -> Self {
        Self {
            max_input_bytes: 16 * 1024 * 1024,
            max_decoded_bytes: 64 * 1024 * 1024,
        }
    }
}
impl ImportBudget {
    fn validate(self) -> Result<(), ImportError> {
        if self.max_input_bytes == 0 || self.max_decoded_bytes == 0 {
            return Err(ImportError::new(
                ImportErrorKind::InvalidSettings,
                "zero_budget",
                "import budgets must be nonzero",
            ));
        }
        Ok(())
    }
}

/// Borrowed primary bytes and cooperative controls, also usable by offline tools.
/// External dependency reads are not supported in this first interface.
pub struct ImportContext<'a> {
    bytes: &'a [u8],
    budget: ImportBudget,
    claimed: usize,
    cancelled: &'a dyn Fn() -> bool,
}
impl<'a> ImportContext<'a> {
    pub fn new(
        bytes: &'a [u8],
        budget: ImportBudget,
        cancelled: &'a dyn Fn() -> bool,
    ) -> Result<Self, ImportError> {
        budget.validate()?;
        if bytes.len() > budget.max_input_bytes {
            return Err(limit_error());
        }
        let context = Self {
            bytes,
            budget,
            claimed: 0,
            cancelled,
        };
        context.check_cancelled()?;
        Ok(context)
    }
    #[must_use]
    pub const fn bytes(&self) -> &'a [u8] {
        self.bytes
    }
    #[must_use]
    pub const fn budget(&self) -> ImportBudget {
        self.budget
    }
    pub fn check_cancelled(&self) -> Result<(), ImportError> {
        if (self.cancelled)() {
            Err(ImportError::new(
                ImportErrorKind::Cancelled,
                "cancelled",
                "import cancelled",
            ))
        } else {
            Ok(())
        }
    }
    /// Claims decoded output bytes before allocation. Importers must account for
    /// their own allocations; this does not inspect or measure the returned value.
    pub fn claim_decoded(&mut self, bytes: usize) -> Result<(), ImportError> {
        self.check_cancelled()?;
        let claimed = self.claimed.checked_add(bytes).ok_or_else(limit_error)?;
        if claimed > self.budget.max_decoded_bytes {
            return Err(limit_error());
        }
        self.claimed = claimed;
        Ok(())
    }
}

fn limit_error() -> ImportError {
    ImportError::new(
        ImportErrorKind::LimitExceeded,
        "import_budget",
        "import budget exceeded",
    )
}

/// Implement in an engine, game, or external crate. Output types need no Nico trait.
pub trait AssetImporter: Send + Sync + 'static {
    type Output: Send + Sync + 'static;
    type Settings: Clone + Send + Sync + 'static;

    fn descriptor(&self) -> ImporterDescriptor;
    /// Called before catalog insertion, on the setup thread. Must not perform I/O.
    fn validate_settings(&self, _settings: &Self::Settings) -> Result<(), ImportError> {
        Ok(())
    }
    fn import(
        &self,
        context: &mut ImportContext<'_>,
        settings: &Self::Settings,
    ) -> Result<Self::Output, ImportError>;
}

/// Typed registration capability. A token can only configure its owning registry.
pub struct ImporterToken<I: AssetImporter> {
    owner: Arc<()>,
    importer: Arc<I>,
    descriptor: ImporterDescriptor,
}

trait ImportJob<T>: Send + Sync {
    fn run(&self, context: &mut ImportContext<'_>) -> Result<T, ImportError>;
}
struct Configured<I: AssetImporter> {
    importer: Arc<I>,
    settings: I::Settings,
}
impl<I: AssetImporter> ImportJob<I::Output> for Configured<I> {
    fn run(&self, context: &mut ImportContext<'_>) -> Result<I::Output, ImportError> {
        self.importer.import(context, &self.settings)
    }
}

/// Read-only catalog provenance. Importer settings remain typed, private configuration.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ImportSource {
    pub path: PathBuf,
    pub importer: ImporterDescriptor,
}

pub(crate) struct ImportEntry<T> {
    pub(crate) source: ImportSource,
    path: PathBuf,
    budget: ImportBudget,
    job: Arc<dyn ImportJob<T>>,
}
impl<T> Clone for ImportEntry<T> {
    fn clone(&self) -> Self {
        Self {
            source: self.source.clone(),
            path: self.path.clone(),
            budget: self.budget,
            job: self.job.clone(),
        }
    }
}
impl<T> ImportEntry<T> {
    fn failure(&self, error: ImportError) -> AssetError {
        AssetError::Import {
            importer: self.source.importer,
            error: Box::new(error),
        }
    }
    fn run(&self, bytes: &[u8], cancelled: &dyn Fn() -> bool) -> Result<T, AssetError> {
        let result = (|| {
            let mut context = ImportContext::new(bytes, self.budget, cancelled)?;
            let output = self.job.run(&mut context)?;
            context.check_cancelled()?;
            Ok(output)
        })();
        result.map_err(|error| self.failure(error))
    }
    #[cfg(feature = "runtime-loading")]
    pub(crate) fn load(&self, cancelled: &dyn Fn() -> bool) -> Result<T, AssetError> {
        let bytes =
            read_source(&self.path, self.budget, cancelled).map_err(|error| self.failure(error))?;
        self.run(&bytes, cancelled)
    }
}

/// Setup-time registry and typed catalog. Import selection is always explicit.
pub struct ImportRegistry<T: Send + Sync + 'static> {
    owner: Arc<()>,
    descriptors: BTreeMap<&'static str, ImporterDescriptor>,
    entries: BTreeMap<AssetId, ImportEntry<T>>,
}
impl<T: Send + Sync + 'static> Default for ImportRegistry<T> {
    fn default() -> Self {
        Self::new()
    }
}
impl<T: Send + Sync + 'static> ImportRegistry<T> {
    #[must_use]
    pub fn new() -> Self {
        Self {
            owner: Arc::new(()),
            descriptors: BTreeMap::new(),
            entries: BTreeMap::new(),
        }
    }
    pub fn register<I: AssetImporter<Output = T>>(
        &mut self,
        importer: I,
    ) -> Result<ImporterToken<I>, ImportError> {
        let descriptor = importer.descriptor();
        if descriptor.id.is_empty()
            || descriptor.id.len() > 128
            || descriptor.version.is_empty()
            || descriptor.version.len() > 128
            || descriptor.extensions.len() > 32
            || descriptor
                .extensions
                .iter()
                .any(|e| e.is_empty() || e.len() > 32)
        {
            return Err(ImportError::new(
                ImportErrorKind::InvalidSettings,
                "invalid_descriptor",
                "importer descriptor is empty or exceeds bounds",
            ));
        }
        if self.descriptors.contains_key(descriptor.id) {
            return Err(ImportError::new(
                ImportErrorKind::InvalidSettings,
                "duplicate_importer",
                "importer ID already registered",
            ));
        }
        self.descriptors.insert(descriptor.id, descriptor);
        Ok(ImporterToken {
            owner: self.owner.clone(),
            importer: Arc::new(importer),
            descriptor,
        })
    }

    pub fn asset<I: AssetImporter<Output = T>>(
        &mut self,
        id: AssetId,
        path: impl Into<PathBuf>,
        importer: &ImporterToken<I>,
        settings: I::Settings,
        budget: ImportBudget,
    ) -> Result<(), ImportError> {
        if !Arc::ptr_eq(&self.owner, &importer.owner) {
            return Err(ImportError::new(
                ImportErrorKind::InvalidSettings,
                "foreign_importer",
                "token belongs to another registry",
            ));
        }
        if self.entries.contains_key(&id) {
            return Err(ImportError::new(
                ImportErrorKind::InvalidSettings,
                "duplicate_asset",
                "asset ID already configured",
            ));
        }
        let path = path.into();
        if path.as_os_str().is_empty()
            || path
                .components()
                .any(|c| !matches!(c, Component::Normal(_)))
        {
            return Err(ImportError::new(
                ImportErrorKind::InvalidSettings,
                "invalid_source",
                "source must contain only relative normal path components",
            ));
        }
        budget.validate()?;
        importer.importer.validate_settings(&settings)?;
        self.entries.insert(
            id,
            ImportEntry {
                source: ImportSource {
                    path: path.clone(),
                    importer: importer.descriptor,
                },
                path,
                budget,
                job: Arc::new(Configured {
                    importer: importer.importer.clone(),
                    settings,
                }),
            },
        );
        Ok(())
    }
    pub fn importers(&self) -> impl Iterator<Item = &ImporterDescriptor> {
        self.descriptors.values()
    }
    #[must_use]
    pub fn source(&self, id: AssetId) -> Option<&ImportSource> {
        self.entries.get(&id).map(|e| &e.source)
    }
    /// Imports supplied bytes without runtime or filesystem access, using the same
    /// configured importer, validation, budget, and cancellation as a store worker.
    pub fn import_bytes(
        &self,
        id: AssetId,
        bytes: &[u8],
        cancelled: &dyn Fn() -> bool,
    ) -> Result<T, AssetError> {
        self.entries
            .get(&id)
            .ok_or(AssetError::UnknownAsset)?
            .run(bytes, cancelled)
    }
    #[cfg(feature = "runtime-loading")]
    pub(crate) fn into_entries(self, root: &Path) -> BTreeMap<AssetId, ImportEntry<T>> {
        self.entries
            .into_iter()
            .map(|(id, mut entry)| {
                entry.path = root.join(&entry.path);
                (id, entry)
            })
            .collect()
    }
}

/// Bounded primary-source reading, shared by all native importers.
#[cfg(any(feature = "runtime-loading", all(test, feature = "png-import")))]
pub(crate) fn read_source(
    path: &Path,
    budget: ImportBudget,
    cancelled: &dyn Fn() -> bool,
) -> Result<Vec<u8>, ImportError> {
    budget.validate()?;
    let io_error = |error: std::io::Error| {
        ImportError::new(ImportErrorKind::Io, "source_read", &error.to_string())
    };
    ImportContext::new(&[], budget, cancelled)?;
    let mut file = File::open(path).map_err(io_error)?;
    let mut bytes = Vec::new();
    let mut chunk = [0; 8192];
    loop {
        ImportContext::new(&[], budget, cancelled)?;
        // Read at most the remaining allowance plus one byte to detect overflow.
        let count = chunk.len().min(
            budget
                .max_input_bytes
                .saturating_sub(bytes.len())
                .saturating_add(1),
        );
        let read = file.read(&mut chunk[..count]).map_err(io_error)?;
        if read == 0 {
            return Ok(bytes);
        }
        if read > budget.max_input_bytes - bytes.len() {
            return Err(limit_error());
        }
        bytes.extend_from_slice(&chunk[..read]);
    }
}
