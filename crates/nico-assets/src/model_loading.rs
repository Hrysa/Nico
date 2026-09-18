//! Shared bounded decoding of model presentation content.
use crate::{
    Texture,
    import::{AssetImporter, ImportBudget, ImportContext},
    importers::{PngImporter, PngSettings},
    model::Model,
};
use std::{collections::BTreeMap, sync::Arc};
pub struct ImportedModel {
    pub model: Arc<Model>,
    pub textures: Vec<Option<Arc<Texture>>>,
}

pub fn model_bundle(
    model: Model,
    cancelled: &dyn Fn() -> bool,
) -> Result<ImportedModel, crate::import::ImportError> {
    bundle_with(model, |_, bytes, budget| {
        PngImporter.import(
            &mut ImportContext::new(bytes, budget, cancelled)?,
            &PngSettings::default(),
        )
    })
}

/// Use the watched project's cache even in release builds. Embedded image bytes
/// participate in the key, so replacing a GLB cannot reuse its old pixels.
#[cfg(feature = "watch")]
pub(crate) fn cached_model_bundle(
    model: Model,
    cache: &crate::cache::ImportCache,
    source: &std::path::Path,
    cancelled: &dyn Fn() -> bool,
) -> Result<ImportedModel, crate::import::ImportError> {
    bundle_with(model, |image, bytes, budget| {
        cache.import(
            source,
            &format!("image/{image}"),
            bytes,
            &PngImporter,
            &PngSettings::default(),
            budget,
            cancelled,
        )
    })
}

fn bundle_with(
    model: Model,
    mut decode: impl FnMut(usize, &[u8], ImportBudget) -> Result<Texture, crate::import::ImportError>,
) -> Result<ImportedModel, crate::import::ImportError> {
    let mut textures = Vec::new();
    let mut remaining = 256 * 1024 * 1024;
    let mut decoded = BTreeMap::new();
    for (index, texture) in model.data().textures.iter().enumerate() {
        if !model
            .data()
            .materials
            .iter()
            .any(|m| m.texture_indices().contains(&Some(index)))
        {
            textures.push(None);
            continue;
        }
        if let Some(value) = decoded.get(&texture.image) {
            textures.push(Some(Arc::clone(value)));
            continue;
        }
        let image = &model.data().images[texture.image];
        let budget = ImportBudget {
            max_input_bytes: 64 * 1024 * 1024,
            max_decoded_bytes: remaining,
        };
        let value = Arc::new(decode(texture.image, &image.bytes, budget)?);
        remaining = remaining.saturating_sub(value.pixels().len());
        decoded.insert(texture.image, value.clone());
        textures.push(Some(value));
    }
    Ok(ImportedModel {
        model: Arc::new(model),
        textures,
    })
}

#[cfg(all(test, feature = "watch"))]
mod tests {
    use super::*;
    use crate::{cache::ImportCache, model::*};

    fn model(pixel: [u8; 4]) -> Model {
        let mut bytes = Vec::new();
        {
            let mut encoder = png::Encoder::new(&mut bytes, 1, 1);
            encoder.set_color(png::ColorType::Rgba);
            encoder.set_depth(png::BitDepth::Eight);
            encoder
                .write_header()
                .unwrap()
                .write_image_data(&pixel)
                .unwrap();
        }
        let texture = ModelTexture {
            image: 0,
            wrap_s: WrapMode::Repeat,
            wrap_t: WrapMode::Repeat,
            min_filter: None,
            mag_filter: None,
        };
        Model::new(ModelData {
            images: vec![ModelImage {
                name: String::new(),
                encoding: ImageEncoding::Png,
                bytes,
            }],
            textures: vec![texture.clone(), texture.clone(), texture],
            materials: vec![Material {
                name: String::new(),
                base_color: [1.; 4],
                metallic: 0.,
                roughness: 1.,
                base_color_texture: Some(0),
                metallic_roughness_texture: None,
                normal_texture: None,
                normal_scale: 1.,
                occlusion_texture: None,
                occlusion_strength: 1.,
                emissive_texture: Some(1),
                emissive: [0.; 3],
                alpha: AlphaMode::Opaque,
                alpha_cutoff: 0.5,
                double_sided: false,
            }],
            ..Default::default()
        })
        .unwrap()
    }

    #[test]
    fn cached_embedded_images_reopen_and_invalidate_without_losing_aliases() {
        let root = std::env::temp_dir().join(format!("nico-bundle-cache-{}", std::process::id()));
        std::fs::create_dir_all(&root).unwrap();
        struct Cleanup(std::path::PathBuf);
        impl Drop for Cleanup {
            fn drop(&mut self) {
                let _ = std::fs::remove_dir_all(&self.0);
            }
        }
        let _cleanup = Cleanup(root.clone());
        let source = root.join("model.glb");
        std::fs::write(&source, b"owning source").unwrap();
        let cache = ImportCache::new(&root).unwrap();
        let activity = crate::cache::observe_current_thread();
        let red = [255, 0, 0, 255];
        let first = cached_model_bundle(model(red), &cache, &source, &|| false).unwrap();
        let before = activity.stats();
        let second = cached_model_bundle(model(red), &cache, &source, &|| false).unwrap();
        let after = activity.stats();
        assert_eq!(after.hits - before.hits, 1);
        assert_eq!(after.imports, before.imports);
        assert_eq!(
            first.textures[0].as_ref().unwrap().pixels(),
            second.textures[0].as_ref().unwrap().pixels()
        );
        assert!(Arc::ptr_eq(
            second.textures[0].as_ref().unwrap(),
            second.textures[1].as_ref().unwrap()
        ));
        assert!(second.textures[2].is_none());
        let blue = [0, 0, 255, 255];
        let changed = cached_model_bundle(model(blue), &cache, &source, &|| false).unwrap();
        assert_eq!(changed.textures[0].as_ref().unwrap().pixels(), &blue);
        assert_eq!(activity.stats().imports - after.imports, 1);
        assert!(cached_model_bundle(model(blue), &cache, &source, &|| true).is_err());
        let mut broken = model(red).data().clone();
        broken.images[0].bytes = vec![1, 2, 3];
        assert!(
            cached_model_bundle(Model::new(broken).unwrap(), &cache, &source, &|| false).is_err()
        );
    }
}
