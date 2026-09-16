use std::fmt;

/// Structured loading failures. Failed entries remain inspectable until released.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum AssetError {
    Import {
        importer: crate::import::ImporterDescriptor,
        error: Box<crate::import::ImportError>,
    },
    UnknownAsset,
    Capacity,
    Closed,
    NotFailed,
    Io(String),
    InvalidPng(String),
    InvalidMesh(String),
    UnsupportedMesh,
    UnsupportedPng,
    LimitExceeded,
    Cancelled,
    WorkerUnavailable,
}

impl fmt::Display for AssetError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{self:?}")
    }
}
impl std::error::Error for AssetError {}

/// Compatibility limits for built-in store installation. Explicit registries use
/// `StoreLimits`, `ImportBudget`, and the importer's format-specific settings.
#[derive(Clone, Copy, Debug)]
pub struct AssetLimits {
    pub max_assets: usize,
    pub max_file_bytes: usize,
    pub max_decoded_bytes: usize,
    pub max_dimension: u32,
    pub max_vertices: usize,
    pub max_indices: usize,
}

impl Default for AssetLimits {
    fn default() -> Self {
        Self {
            max_assets: 64,
            max_file_bytes: 16 * 1024 * 1024,
            max_decoded_bytes: 64 * 1024 * 1024,
            max_dimension: 4096,
            max_vertices: 250_000,
            max_indices: 750_000,
        }
    }
}
