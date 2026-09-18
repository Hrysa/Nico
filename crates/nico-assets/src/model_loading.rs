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
        let value = Arc::new(PngImporter.import(
            &mut ImportContext::new(&image.bytes, budget, cancelled)?,
            &PngSettings::default(),
        )?);
        remaining = remaining.saturating_sub(value.pixels().len());
        decoded.insert(texture.image, value.clone());
        textures.push(Some(value));
    }
    Ok(ImportedModel {
        model: Arc::new(model),
        textures,
    })
}
