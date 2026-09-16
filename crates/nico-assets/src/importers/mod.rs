//! Optional built-in importers using the same public interface as userland.

#[cfg(feature = "gltf-import")]
mod mesh;
#[cfg(feature = "png-import")]
pub(crate) mod png;

#[cfg(any(feature = "png-import", feature = "gltf-import"))]
use crate::{
    AssetError, AssetLimits,
    import::{AssetImporter, ImportContext, ImportError, ImportErrorKind, ImporterDescriptor},
};

#[cfg(any(feature = "png-import", feature = "gltf-import"))]
fn failure(error: AssetError) -> ImportError {
    let (kind, code) = match error {
        AssetError::InvalidPng(_) => (ImportErrorKind::Malformed, "invalid_png"),
        AssetError::InvalidMesh(_) => (ImportErrorKind::Malformed, "invalid_mesh"),
        AssetError::UnsupportedPng => (ImportErrorKind::Unsupported, "unsupported_png"),
        AssetError::UnsupportedMesh => (ImportErrorKind::Unsupported, "unsupported_mesh"),
        AssetError::LimitExceeded => (ImportErrorKind::LimitExceeded, "import_budget"),
        _ => (ImportErrorKind::Malformed, "decode_failed"),
    };
    ImportError::new(kind, code, &error.to_string())
}

#[cfg(feature = "png-import")]
#[derive(Clone, Copy, Debug)]
pub struct PngSettings {
    pub max_dimension: u32,
}
#[cfg(feature = "png-import")]
impl Default for PngSettings {
    fn default() -> Self {
        Self {
            max_dimension: 4096,
        }
    }
}

/// Decodes one static PNG to RGBA8/sRGB, straight alpha. No runtime dependency.
#[cfg(feature = "png-import")]
pub struct PngImporter;
#[cfg(feature = "png-import")]
impl AssetImporter for PngImporter {
    type Output = crate::Texture;
    type Settings = PngSettings;
    fn descriptor(&self) -> ImporterDescriptor {
        ImporterDescriptor {
            id: "nico.png",
            version: "1",
            extensions: &["png"],
        }
    }
    fn validate_settings(&self, settings: &PngSettings) -> Result<(), ImportError> {
        if settings.max_dimension == 0 {
            return Err(ImportError::new(
                ImportErrorKind::InvalidSettings,
                "png_dimensions",
                "maximum dimension must be nonzero",
            ));
        }
        Ok(())
    }
    fn import(
        &self,
        context: &mut ImportContext<'_>,
        settings: &PngSettings,
    ) -> Result<crate::Texture, ImportError> {
        self.validate_settings(settings)?;
        context.check_cancelled()?;
        let texture = png::decode_bytes(
            context.bytes(),
            AssetLimits {
                max_file_bytes: context.budget().max_input_bytes,
                max_decoded_bytes: context.budget().max_decoded_bytes,
                max_dimension: settings.max_dimension,
                ..AssetLimits::default()
            },
        )
        .map_err(failure)?;
        // Decoder checks the same output bound before allocation and separately
        // bounds its working buffer. These are not a combined peak-memory limit.
        context.claim_decoded(texture.pixels().len())?;
        Ok(texture)
    }
}

#[cfg(feature = "gltf-import")]
#[derive(Clone, Copy, Debug)]
pub struct StaticGlbSettings {
    pub max_vertices: usize,
    pub max_indices: usize,
}
#[cfg(feature = "gltf-import")]
impl Default for StaticGlbSettings {
    fn default() -> Self {
        Self {
            max_vertices: 250_000,
            max_indices: 750_000,
        }
    }
}

/// Restricted static GLB importer. Scenes, skins, animation, and materials remain unsupported.
#[cfg(feature = "gltf-import")]
pub struct StaticGlbImporter;
#[cfg(feature = "gltf-import")]
impl AssetImporter for StaticGlbImporter {
    type Output = crate::Mesh;
    type Settings = StaticGlbSettings;
    fn descriptor(&self) -> ImporterDescriptor {
        ImporterDescriptor {
            id: "nico.static_glb",
            version: "1",
            extensions: &["glb"],
        }
    }
    fn validate_settings(&self, settings: &StaticGlbSettings) -> Result<(), ImportError> {
        if settings.max_vertices == 0 || settings.max_indices == 0 {
            return Err(ImportError::new(
                ImportErrorKind::InvalidSettings,
                "mesh_counts",
                "mesh limits must be nonzero",
            ));
        }
        Ok(())
    }
    fn import(
        &self,
        context: &mut ImportContext<'_>,
        settings: &StaticGlbSettings,
    ) -> Result<crate::Mesh, ImportError> {
        self.validate_settings(settings)?;
        context.check_cancelled()?;
        let mesh = mesh::decode_bytes(
            context.bytes(),
            AssetLimits {
                max_file_bytes: context.budget().max_input_bytes,
                max_decoded_bytes: context.budget().max_decoded_bytes,
                max_vertices: settings.max_vertices,
                max_indices: settings.max_indices,
                ..AssetLimits::default()
            },
        )
        .map_err(failure)?;
        context.claim_decoded(mesh.vertices().len() * 20 + mesh.indices().len() * 4)?;
        Ok(mesh)
    }
}

#[cfg(feature = "gltf-import")]
mod model;
#[cfg(feature = "gltf-import")]
pub use model::{ModelGlbImporter, ModelGlbSettings};
