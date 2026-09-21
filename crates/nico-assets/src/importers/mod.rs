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
#[cfg_attr(feature = "import-cache", derive(serde::Serialize))]
#[derive(Clone, Copy, Debug)]
pub struct PngSettings {
    pub max_dimension: u32,
    /// Lossy GPU block compression; disable for exact pixels or sensitive data maps.
    pub compress: bool,
}
#[cfg(feature = "png-import")]
impl Default for PngSettings {
    fn default() -> Self {
        Self {
            max_dimension: 4096,
            compress: true,
        }
    }
}

/// Imports one static PNG to GPU-ready BC3 or RGBA8, straight alpha.
#[cfg(feature = "png-import")]
pub struct PngImporter;
#[cfg(feature = "png-import")]
impl AssetImporter for PngImporter {
    fn cache_settings(&self, s: &Self::Settings) -> Result<Option<Vec<u8>>, ImportError> {
        crate::cache::encode(&("texture-bc3-v2", s)).map(Some)
    }
    fn cache_encode(&self, value: &Self::Output) -> Result<Vec<u8>, ImportError> {
        let mut bytes = Vec::with_capacity(17 + value.encoded_bytes().len());
        bytes.extend_from_slice(&value.width().to_le_bytes());
        bytes.extend_from_slice(&value.height().to_le_bytes());
        bytes.extend_from_slice(&(value.encoded_bytes().len() as u64).to_le_bytes());
        bytes.push(match value.encoding() {
            crate::TextureEncoding::Rgba8 => 0,
            crate::TextureEncoding::Bc3 => 1,
        });
        bytes.extend_from_slice(value.encoded_bytes());
        Ok(bytes)
    }
    fn cache_decode(&self, bytes: &[u8], s: &Self::Settings) -> Result<Self::Output, ImportError> {
        let invalid = || {
            ImportError::new(
                ImportErrorKind::Malformed,
                "cache_texture",
                "invalid cached texture",
            )
        };
        let header = bytes.get(..17).ok_or_else(invalid)?;
        let w = u32::from_le_bytes(header[..4].try_into().unwrap());
        let h = u32::from_le_bytes(header[4..8].try_into().unwrap());
        let length = u64::from_le_bytes(header[8..16].try_into().unwrap());
        let encoding = match header[16] {
            0 => crate::TextureEncoding::Rgba8,
            1 if s.compress => crate::TextureEncoding::Bc3,
            _ => return Err(invalid()),
        };
        let pixels = &bytes[17..];
        let expected = u64::from(w).checked_mul(u64::from(h)).and_then(|n| {
            n.checked_mul(if encoding == crate::TextureEncoding::Rgba8 {
                4
            } else {
                1
            })
        });
        if w == 0 || h == 0 || expected != Some(length) || length != pixels.len() as u64 {
            return Err(invalid());
        }
        if w > s.max_dimension || h > s.max_dimension {
            return Err(failure(AssetError::LimitExceeded));
        }
        crate::Texture::from_encoded(w, h, encoding, pixels.to_vec()).ok_or_else(|| {
            ImportError::new(
                ImportErrorKind::Malformed,
                "cache_texture",
                "invalid cached texture",
            )
        })
    }

    type Output = crate::Texture;
    type Settings = PngSettings;
    fn descriptor(&self) -> ImporterDescriptor {
        ImporterDescriptor {
            id: "nico.png",
            version: "2",
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
        context.claim_decoded(texture.decoded_byte_len())?;
        if settings.compress {
            texture.compress_bc3(|| context.check_cancelled())
        } else {
            Ok(texture)
        }
    }
}

#[cfg(feature = "gltf-import")]
#[cfg_attr(feature = "import-cache", derive(serde::Serialize))]
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
    fn cache_settings(&self, s: &Self::Settings) -> Result<Option<Vec<u8>>, ImportError> {
        crate::cache::encode(&("mesh-v1", s)).map(Some)
    }
    fn cache_encode(&self, value: &Self::Output) -> Result<Vec<u8>, ImportError> {
        crate::cache::encode(&(value.vertices(), value.indices(), value.normals()))
    }
    fn cache_decode(&self, bytes: &[u8], s: &Self::Settings) -> Result<Self::Output, ImportError> {
        let (vertices, indices, normals): (Vec<crate::MeshVertex>, Vec<u32>, Vec<[f32; 3]>) =
            crate::cache::decode(bytes)?;
        if vertices.len() > s.max_vertices || indices.len() > s.max_indices {
            return Err(failure(AssetError::LimitExceeded));
        }
        crate::Mesh::triangles(vertices, indices)
            .and_then(|mesh| mesh.with_normals(normals))
            .ok_or_else(|| {
                ImportError::new(
                    ImportErrorKind::Malformed,
                    "cache_mesh",
                    "invalid cached mesh",
                )
            })
    }

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

#[cfg(all(test, feature = "png-import"))]
mod cache_tests {
    use super::*;
    #[test]
    fn png_compression_cache_and_exact_pixel_opt_out() {
        use crate::{TextureEncoding, import::ImportBudget};
        let rgba: Vec<u8> = (0..256 * 256)
            .flat_map(|i| [i as u8, (i / 256) as u8, 80, 255])
            .collect();
        let mut png = Vec::new();
        {
            let mut encoder = ::png::Encoder::new(&mut png, 256, 256);
            encoder.set_color(::png::ColorType::Rgba);
            encoder.set_depth(::png::BitDepth::Eight);
            encoder
                .write_header()
                .unwrap()
                .write_image_data(&rgba)
                .unwrap();
        }
        let start = std::time::Instant::now();
        let mut context = ImportContext::new(&png, ImportBudget::default(), &|| false).unwrap();
        let compressed = PngImporter
            .import(&mut context, &PngSettings::default())
            .unwrap();
        eprintln!(
            "256x256 PNG decode and BC3 cook: {:?} wall time",
            start.elapsed()
        );
        assert_eq!(compressed.encoding(), TextureEncoding::Bc3);
        assert_eq!(context.claimed_bytes(), rgba.len());
        let payload = PngImporter.cache_encode(&compressed).unwrap();
        assert_eq!(payload.len(), 17 + rgba.len() / 4);
        let restored = PngImporter
            .cache_decode(&payload, &PngSettings::default())
            .unwrap();
        assert_eq!(restored.encoded_bytes(), compressed.encoded_bytes());
        let exact = PngSettings {
            compress: false,
            ..Default::default()
        };
        assert_ne!(
            PngImporter.cache_settings(&exact).unwrap(),
            PngImporter.cache_settings(&PngSettings::default()).unwrap()
        );
        assert!(PngImporter.cache_decode(&payload, &exact).is_err());
        let raw = PngImporter
            .import(
                &mut ImportContext::new(&png, ImportBudget::default(), &|| false).unwrap(),
                &exact,
            )
            .unwrap();
        assert_eq!(raw.encoding(), TextureEncoding::Rgba8);
        assert_eq!(raw.pixels(), rgba);
        let mut unknown = payload.clone();
        unknown[16] = 255;
        assert!(
            PngImporter
                .cache_decode(&unknown, &PngSettings::default())
                .is_err()
        );
        let mut unaligned = payload.clone();
        unaligned[..4].copy_from_slice(&255u32.to_le_bytes());
        assert!(
            PngImporter
                .cache_decode(&unaligned, &PngSettings::default())
                .is_err()
        );
    }
    #[test]
    fn texture_codec_round_trips_and_rejects_bad_lengths() {
        let texture = crate::Texture::rgba8(2, 1, vec![1, 2, 3, 255, 4, 5, 6, 128]).unwrap();
        let old = PngImporter.cache_encode(&texture).unwrap();
        assert_eq!(
            PngImporter
                .cache_decode(&old, &PngSettings::default())
                .unwrap()
                .pixels(),
            texture.pixels()
        );
        for length in [0, 15, 16, old.len() - 1] {
            assert!(
                PngImporter
                    .cache_decode(&old[..length], &PngSettings::default())
                    .is_err()
            );
        }
        let mut extra = old.clone();
        extra.push(0);
        assert!(
            PngImporter
                .cache_decode(&extra, &PngSettings::default())
                .is_err()
        );
        let mut wrong = old.clone();
        wrong[8..16].copy_from_slice(&u64::MAX.to_le_bytes());
        assert!(
            PngImporter
                .cache_decode(&wrong, &PngSettings::default())
                .is_err()
        );
        assert!(
            PngImporter
                .cache_decode(
                    &old,
                    &PngSettings {
                        max_dimension: 1,
                        ..Default::default()
                    }
                )
                .is_err()
        );
    }
}
