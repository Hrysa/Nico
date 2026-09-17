//! Stable asset identity shared by runtime and presentation.

use std::marker::PhantomData;

#[cfg(feature = "character")]
pub mod character;

mod texture;
pub use texture::Texture;
mod material;
pub use material::{MaterialTexture, PbrMaterial};
mod mesh;
pub use mesh::{Mesh, MeshVertex, SkinWeights};

mod asset_error;
pub use asset_error::{AssetError, AssetLimits};
pub mod import;
pub mod importers;

#[cfg(feature = "runtime-loading")]
pub mod loading;

/// Owning reference to an asset. Clone to share ownership; drop to release it.
///
/// Unlike a handle, a lease keeps a store entry alive. Final release is reconciled
/// at a runtime boundary; dropping a lease never mutates the world or blocks.
pub struct AssetLease<T> {
    handle: Handle<T>,
    ownership: std::sync::Arc<()>,
}

impl<T> AssetLease<T> {
    /// Returns identity without transferring or retaining ownership.
    #[must_use]
    pub const fn handle(&self) -> Handle<T> {
        self.handle
    }
}

impl<T> Clone for AssetLease<T> {
    fn clone(&self) -> Self {
        Self {
            handle: self.handle,
            ownership: self.ownership.clone(),
        }
    }
}

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

#[cfg(test)]
mod tests {
    use super::{AssetId, Handle};

    struct Texture;

    #[test]
    fn typed_handle_preserves_stable_identity() {
        let id = AssetId::from_u128(42);
        let handle = Handle::<Texture>::new(id);

        assert_eq!(handle.id(), id);
        assert_eq!(handle.id().to_u128(), 42);
    }
}

/// Procedural CPU mesh builders.
pub mod procedural;

/// Immutable generic model, skin, and animation data.
pub mod model;
