//! Asset identity and loading contracts shared by runtime and presentation.

use std::{
    any::TypeId,
    error::Error,
    fmt,
    marker::PhantomData,
    path::{Path, PathBuf},
};

/// Stable, type-independent identity of an asset.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct AssetId(u128);

impl AssetId {
    /// Creates an ID from its serialized representation.
    #[must_use]
    pub const fn from_u128(value: u128) -> Self {
        Self(value)
    }

    /// Returns the serialized representation.
    #[must_use]
    pub const fn to_u128(self) -> u128 {
        self.0
    }
}

/// Typed reference to an asset owned by an asset provider.
#[derive(Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct Handle<T> {
    id: AssetId,
    marker: PhantomData<fn() -> T>,
}

impl<T> Handle<T> {
    /// Creates a typed handle from an asset ID.
    #[must_use]
    pub const fn new(id: AssetId) -> Self {
        Self {
            id,
            marker: PhantomData,
        }
    }

    /// Returns the type-independent identity.
    #[must_use]
    pub const fn id(&self) -> AssetId {
        self.id
    }
}

impl<T> Clone for Handle<T> {
    fn clone(&self) -> Self {
        *self
    }
}

impl<T> Copy for Handle<T> {}

/// Logical path to a source or generated asset.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub struct AssetPath(PathBuf);

impl AssetPath {
    /// Creates a logical asset path.
    #[must_use]
    pub fn new(path: impl Into<PathBuf>) -> Self {
        Self(path.into())
    }

    /// Returns the underlying path.
    #[must_use]
    pub fn as_path(&self) -> &Path {
        &self.0
    }
}

/// Observable loading state of an asset.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AssetState {
    /// The asset is known but has not started loading.
    Pending,
    /// The provider is loading or importing the asset.
    Loading,
    /// The runtime representation is ready.
    Ready,
    /// Loading failed.
    Failed,
}

/// Provider-independent asset failure.
#[derive(Debug, Eq, PartialEq)]
pub struct AssetError(pub String);

impl fmt::Display for AssetError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl Error for AssetError {}

/// Backend-neutral asset loading service.
pub trait AssetService: Send {
    /// Requests an asset of the supplied runtime type.
    fn request(&mut self, path: &AssetPath, asset_type: TypeId) -> Result<AssetId, AssetError>;

    /// Returns the current state of an asset.
    fn state(&self, id: AssetId) -> Option<AssetState>;
}
