//! Arena presentation shared by the game client and its authoring adapter.
#[cfg(feature = "authoring")]
pub mod authoring;
pub mod environment;
mod landscape;
fn load_model(
    path: &std::path::Path,
) -> Result<std::sync::Arc<nico_assets::model::Model>, Box<dyn std::error::Error + Send + Sync>> {
    use nico_assets::{
        import::ImportBudget,
        importers::{ModelGlbImporter, ModelGlbSettings},
    };
    Ok(std::sync::Arc::new(nico_assets::cache::load_file(
        path,
        &ModelGlbImporter,
        &ModelGlbSettings {
            allow_material_fallback: true,
            ..Default::default()
        },
        ImportBudget {
            max_input_bytes: 64 * 1024 * 1024,
            max_decoded_bytes: 128 * 1024 * 1024,
        },
        &|| false,
    )?))
}
