//! Rebuildable generated ground pixels; server geometry remains authoritative.
use arena_arpg_shared::open_world::content::ZoneDefinition;
use nico_assets::{
    Texture,
    import::{AssetImporter, ImportContext, ImportError, ImportErrorKind, ImporterDescriptor},
    importers::{PngImporter, PngSettings},
};

pub(super) struct GroundImporter;
fn invalid(message: impl ToString) -> ImportError {
    ImportError::new(
        ImportErrorKind::Malformed,
        "ground_texture",
        &message.to_string(),
    )
}
impl AssetImporter for GroundImporter {
    type Output = Texture;
    type Settings = ZoneDefinition;
    fn descriptor(&self) -> ImporterDescriptor {
        // Bump when the generator or its output schema changes.
        ImporterDescriptor {
            id: "arena-ground",
            version: "2",
            extensions: &["toml"],
        }
    }
    fn validate_settings(&self, zone: &ZoneDefinition) -> Result<(), ImportError> {
        zone.validate().map_err(invalid)
    }
    fn cache_settings(&self, zone: &ZoneDefinition) -> Result<Option<Vec<u8>>, ImportError> {
        // Only these values influence pixels, including unsaved obstacle edits.
        nico_assets::cache::encode(&(
            zone.half_extent_m,
            zone.obstacles
                .iter()
                .map(|o| (o.center, o.size))
                .collect::<Vec<_>>(),
        ))
        .map(Some)
    }
    fn cache_encode(&self, texture: &Texture) -> Result<Vec<u8>, ImportError> {
        PngImporter.cache_encode(texture)
    }
    fn cache_decode(&self, bytes: &[u8], _: &ZoneDefinition) -> Result<Texture, ImportError> {
        let texture = PngImporter.cache_decode(
            bytes,
            &PngSettings {
                max_dimension: 1024,
                ..Default::default()
            },
        )?;
        if texture.width() != 1024 || texture.height() != 1024 {
            return Err(invalid("expected 1024 x 1024 pixels"));
        }
        Ok(texture)
    }
    fn import(
        &self,
        context: &mut ImportContext<'_>,
        zone: &ZoneDefinition,
    ) -> Result<Texture, ImportError> {
        context.claim_decoded(1024 * 1024 * 4)?;
        let texture = super::landscape::ground_texture(zone);
        context.check_cancelled()?;
        Ok(texture)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use nico_assets::{cache::ImportCache, import::ImportBudget};
    #[test]
    fn generated_ground_reuses_pixels_and_invalidates_extent_and_obstacles() {
        let root = tempfile::tempdir().unwrap();
        let source = root.path().join("world.toml");
        std::fs::write(&source, "visual source").unwrap();
        let cache = ImportCache::new(root.path()).unwrap();
        let mut zone = ZoneDefinition::default();
        let load = |zone: &ZoneDefinition| {
            cache
                .load(
                    &source,
                    &GroundImporter,
                    zone,
                    ImportBudget::default(),
                    &|| false,
                )
                .unwrap()
        };
        let first = load(&zone);
        assert_eq!(
            first.pixels(),
            super::super::landscape::ground_texture(&zone).pixels()
        );
        assert_eq!(load(&zone).pixels(), first.pixels());
        zone.half_extent_m -= 1.;
        assert_ne!(load(&zone).pixels(), first.pixels());
        let key = GroundImporter.cache_settings(&zone).unwrap();
        zone.obstacles
            .push(arena_arpg_shared::open_world::content::Obstacle {
                id: "rock".into(),
                center: [0., 1., 0.],
                size: [2., 2., 2.],
                color: [1.; 4],
            });
        assert_ne!(GroundImporter.cache_settings(&zone).unwrap(), key);
        assert!(
            cache
                .load(
                    &source,
                    &GroundImporter,
                    &zone,
                    ImportBudget::default(),
                    &|| true
                )
                .is_err()
        );
    }
}
