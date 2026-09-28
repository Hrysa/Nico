//! Compact, rebuildable placement data. Camera and visual time never enter this recipe.
use arena_arpg_shared::open_world::content::ZoneDefinition;
use glam::{Mat4, Quat, Vec3};
use nico_assets::definition::DefinitionValidation;
use nico_assets::{
    Mesh, MeshVertex, PbrMaterial,
    import::{AssetImporter, ImportContext, ImportError, ImportErrorKind, ImporterDescriptor},
};
use nico_presentation::{InstanceBatch, InstanceRecord};
use std::sync::Arc;

const CHUNKS: usize = 64;
const MAX_PER_CHUNK: usize = 5400;
const RECORD_BYTES: usize = 28;
pub(super) struct GrassImporter;
#[derive(Clone, Debug, PartialEq)]
struct Placement {
    id: u32,
    seed: u32,
    x: f32,
    z: f32,
    height: f32,
    angle: f32,
    color: u32,
}
#[derive(Clone, Debug, PartialEq)]
pub(super) struct GrassPlacements {
    chunks: Arc<[Vec<Placement>]>,
}
fn invalid(message: &str) -> ImportError {
    ImportError::new(ImportErrorKind::Malformed, "grass_placement", message)
}
impl GrassPlacements {
    pub(super) fn generate(
        zone: &ZoneDefinition,
        mut check: impl FnMut() -> Result<(), ImportError>,
    ) -> Result<Self, ImportError> {
        let h = zone.half_extent_m as f32;
        let mut chunks = Vec::with_capacity(CHUNKS);
        let mut seed = 27491;
        for chunk in 0..CHUNKS {
            check()?;
            let minx = -h + (chunk % 8) as f32 * h / 4.;
            let minz = -h + (chunk / 8) as f32 * h / 4.;
            let mut records = Vec::new();
            for candidate in 0..1800 {
                let x = minx + super::landscape::random(&mut seed) * h / 4.;
                let z = minz + super::landscape::random(&mut seed) * h / 4.;
                if (x - super::landscape::path_x(z)).abs() < 3.3
                    || zone.obstacles.iter().any(|o| {
                        (x - o.center[0] as f32).abs() < o.size[0] as f32 * 0.5 + 0.3
                            && (z - o.center[2] as f32).abs() < o.size[2] as f32 * 0.5 + 0.3
                    })
                {
                    continue;
                }
                let height = 0.18 + super::landscape::random(&mut seed) * 0.48;
                let color = if x > 7. && z > -16. {
                    3 + (super::landscape::random(&mut seed) * 2.) as u32
                } else {
                    (super::landscape::random(&mut seed) * 3.) as u32
                };
                for blade in 0..3 {
                    let angle =
                        super::landscape::random(&mut seed) * std::f32::consts::TAU + blade as f32;
                    records.push(Placement {
                        id: ((chunk * 1800 + candidate) * 3 + blade) as u32,
                        seed,
                        x,
                        z,
                        height,
                        angle,
                        color,
                    });
                }
            }
            chunks.push(records);
        }
        Ok(Self {
            chunks: chunks.into(),
        })
    }
    #[cfg(test)]
    pub(super) fn batches(
        &self,
    ) -> Result<Vec<Arc<InstanceBatch>>, nico_presentation::InstanceError> {
        let provider = GrassProvider::new(0, self.clone());
        self.chunks
            .iter()
            .map(|chunk| provider.prototype.batch(chunk, || false))
            .collect()
    }
}
struct GrassPrototype {
    mesh: Arc<Mesh>,
    material: Arc<PbrMaterial>,
    colors: [[f32; 4]; 6],
}
mod shrubs;
/// Cached placements remain shared; loading expands only the requested chunk.
pub(super) struct GrassProvider {
    id: u32,
    placements: GrassPlacements,
    prototype: GrassPrototype,
    shrubs: shrubs::ShrubPrototype,
}
impl GrassProvider {
    pub(super) fn catalog(
        &self,
    ) -> Vec<(
        nico_presentation_control::instances::ChunkKey,
        nico_presentation::InstanceBounds,
    )> {
        self.placements
            .chunks
            .iter()
            .enumerate()
            .filter_map(|(index, chunk)| {
                if chunk.is_empty() {
                    return None;
                }
                let mut min = Vec3::splat(f32::INFINITY);
                let mut max = Vec3::splat(f32::NEG_INFINITY);
                for p in chunk {
                    // Sum of horizontal blade extents bounds every yaw; include full bend.
                    let radius = 0.075 + 0.42 * p.height + 0.4;
                    min = min.min(Vec3::new(p.x - radius, -0.4, p.z - radius));
                    let top = (p.height + 0.4).max(p.height + 0.15 + 0.25);
                    max = max.max(Vec3::new(p.x + radius, top, p.z + radius));
                }
                Some((
                    nico_presentation_control::instances::ChunkKey {
                        provider: self.id,
                        chunk: index as u64,
                    },
                    nico_presentation::InstanceBounds::new(min.to_array(), max.to_array())
                        .expect("validated placement bounds"),
                ))
            })
            .collect()
    }
    pub(super) fn new(id: u32, placements: GrassPlacements) -> Self {
        Self {
            id,
            placements,
            prototype: GrassPrototype::new(),
            shrubs: shrubs::ShrubPrototype::new(),
        }
    }
}
impl nico_presentation_control::instances::workers::InstanceProvider for GrassProvider {
    fn load(
        &self,
        request: &nico_presentation_control::instances::ChunkRequest,
        limits: nico_presentation_control::instances::workers::ResultLimits,
    ) -> nico_presentation_control::instances::workers::ChunkResult {
        use nico_presentation_control::instances::ChunkError;
        if request.cancelled() {
            return Err(ChunkError::Stale);
        }
        if request.key().provider != self.id {
            return Err(ChunkError::ProviderFailed);
        }
        let index = usize::try_from(request.key().chunk).map_err(|_| ChunkError::ProviderFailed)?;
        let chunk = self
            .placements
            .chunks
            .get(index)
            .ok_or(ChunkError::ProviderFailed)?;
        let shrub_count = shrubs::ShrubPrototype::count(chunk);
        let batch_count = 1 + usize::from(shrub_count > 0);
        let bytes = (chunk.len() + shrub_count)
            * (std::mem::size_of::<InstanceRecord>()
                + std::mem::size_of::<nico_presentation::InstanceBounds>())
            + batch_count
                * (std::mem::size_of::<InstanceBatch>()
                    + std::mem::size_of::<Arc<InstanceBatch>>());
        if bytes > limits.decoded_bytes {
            return Err(ChunkError::Capacity);
        }
        let batch = self
            .prototype
            .batch(chunk, || request.cancelled())
            .map_err(|_| ChunkError::ProviderFailed)?;
        let mut batches = Vec::with_capacity(batch_count);
        batches.push(batch);
        if shrub_count > 0 {
            batches.push(
                self.shrubs
                    .batch(chunk, || request.cancelled())
                    .map_err(|_| ChunkError::ProviderFailed)?,
            );
        }
        if request.cancelled() {
            return Err(ChunkError::Stale);
        }
        Ok(batches)
    }
}
impl GrassPrototype {
    fn new() -> Self {
        let positions = [
            [-0.075, 0., 0.],
            [0.075, 0., 0.],
            [-0.04125, 0.55, -0.105],
            [0.04125, 0.55, -0.105],
            [0., 1., -0.42],
        ];
        let mesh = Arc::new(
            Mesh::triangles(
                positions
                    .into_iter()
                    .map(|position| MeshVertex {
                        position,
                        uv: [0.5; 2],
                    })
                    .collect(),
                vec![0, 1, 2, 1, 3, 2, 2, 3, 4],
            )
            .expect("valid blade prototype"),
        );
        let material = Arc::new(PbrMaterial {
            double_sided: true,
            ..Default::default()
        });
        let colors = [
            [107u8, 164, 25],
            [139, 186, 37],
            [161, 192, 42],
            [184, 170, 40],
            [207, 173, 42],
            [96, 145, 29],
        ]
        .map(|rgb| {
            let rgb = rgb.map(|v| {
                let v = v as f32 / 255.;
                if v <= 0.04045 {
                    v / 12.92
                } else {
                    ((v + 0.055) / 1.055).powf(2.4)
                }
            });
            [rgb[0], rgb[1], rgb[2], 1.]
        });
        Self {
            mesh,
            material,
            colors,
        }
    }
    fn batch(
        &self,
        chunk: &[Placement],
        mut cancelled: impl FnMut() -> bool,
    ) -> Result<Arc<InstanceBatch>, nico_presentation::InstanceError> {
        let mut records = Vec::with_capacity(chunk.len());
        records.extend(chunk.iter().take_while(|_| !cancelled()).map(|p| {
            InstanceRecord::new(
                p.id as u64,
                p.seed,
                Mat4::from_scale_rotation_translation(
                    Vec3::new(1., p.height, p.height),
                    Quat::from_rotation_y(-p.angle),
                    Vec3::new(p.x, 0., p.z),
                ),
                self.colors[p.color as usize],
            )
            .expect("validated grass placement")
            .with_foliage_response(
                nico_presentation::foliage::FoliageResponse::from_seed(p.seed, 1., 0.65)
                    .expect("constant grass response"),
            )
        }));
        InstanceBatch::new(self.mesh.clone(), self.material.clone(), records, 512.)
            .and_then(|batch| {
                batch.with_foliage(
                    nico_presentation::foliage::FoliageProfile::new(0., 1., 0.4)
                        .expect("constant grass foliage profile"),
                )
            })
            .map(Arc::new)
    }
}
impl AssetImporter for GrassImporter {
    type Output = GrassPlacements;
    type Settings = ZoneDefinition;
    fn descriptor(&self) -> ImporterDescriptor {
        ImporterDescriptor {
            id: "arena-grass",
            version: "1",
            extensions: &["toml"],
        }
    }
    fn validate_settings(&self, zone: &ZoneDefinition) -> Result<(), ImportError> {
        zone.validate().map_err(|_| invalid("invalid grass zone"))
    }
    fn cache_settings(&self, zone: &ZoneDefinition) -> Result<Option<Vec<u8>>, ImportError> {
        super::ground_import::GroundImporter.cache_settings(zone)
    }
    fn cache_encode(&self, data: &GrassPlacements) -> Result<Vec<u8>, ImportError> {
        let mut bytes = Vec::new();
        for chunk in data.chunks.iter() {
            bytes.extend_from_slice(&(chunk.len() as u32).to_le_bytes());
            for p in chunk {
                for word in [
                    p.id,
                    p.seed,
                    p.x.to_bits(),
                    p.z.to_bits(),
                    p.height.to_bits(),
                    p.angle.to_bits(),
                    p.color,
                ] {
                    bytes.extend_from_slice(&word.to_le_bytes());
                }
            }
        }
        Ok(bytes)
    }
    fn cache_decode(
        &self,
        bytes: &[u8],
        zone: &ZoneDefinition,
    ) -> Result<GrassPlacements, ImportError> {
        let mut chunks = Vec::with_capacity(CHUNKS);
        let mut rest = bytes;
        for chunk in 0..CHUNKS {
            if rest.len() < 4 {
                return Err(invalid("truncated grass chunk"));
            }
            let count = u32::from_le_bytes(rest[..4].try_into().unwrap()) as usize;
            rest = &rest[4..];
            if count > MAX_PER_CHUNK || rest.len() < count * RECORD_BYTES {
                return Err(invalid("grass chunk exceeds budget or is truncated"));
            }
            let mut records = Vec::with_capacity(count);
            let mut previous = None;
            for bytes in rest[..count * RECORD_BYTES].chunks_exact(RECORD_BYTES) {
                let words: [u32; 7] = std::array::from_fn(|i| {
                    u32::from_le_bytes(bytes[i * 4..i * 4 + 4].try_into().unwrap())
                });
                let p = Placement {
                    id: words[0],
                    seed: words[1],
                    x: f32::from_bits(words[2]),
                    z: f32::from_bits(words[3]),
                    height: f32::from_bits(words[4]),
                    angle: f32::from_bits(words[5]),
                    color: words[6],
                };
                let h = zone.half_extent_m as f32;
                let minx = -h + (chunk % 8) as f32 * h / 4.;
                let minz = -h + (chunk / 8) as f32 * h / 4.;
                if !(chunk * MAX_PER_CHUNK..(chunk + 1) * MAX_PER_CHUNK).contains(&(p.id as usize))
                    || previous.is_some_and(|id| id >= p.id)
                    || !p.x.is_finite()
                    || !p.z.is_finite()
                    || !(minx..=minx + h / 4.).contains(&p.x)
                    || !(minz..=minz + h / 4.).contains(&p.z)
                    || !(0.18..=0.66).contains(&p.height)
                    || !(0.0..=std::f32::consts::TAU + 2.).contains(&p.angle)
                    || p.color > 4
                {
                    return Err(invalid("invalid cached grass placement"));
                }
                previous = Some(p.id);
                records.push(p);
            }
            rest = &rest[count * RECORD_BYTES..];
            chunks.push(records);
        }
        if !rest.is_empty() {
            return Err(invalid("trailing grass data"));
        }
        Ok(GrassPlacements {
            chunks: chunks.into(),
        })
    }
    fn import(
        &self,
        context: &mut ImportContext<'_>,
        zone: &ZoneDefinition,
    ) -> Result<GrassPlacements, ImportError> {
        context.claim_decoded(CHUNKS * MAX_PER_CHUNK * RECORD_BYTES)?;
        GrassPlacements::generate(zone, || context.check_cancelled())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use nico_assets::{cache::ImportCache, import::ImportBudget};
    use std::sync::atomic::{AtomicUsize, Ordering};
    struct CountingImporter(AtomicUsize);
    impl AssetImporter for CountingImporter {
        type Output = GrassPlacements;
        type Settings = ZoneDefinition;
        fn descriptor(&self) -> ImporterDescriptor {
            GrassImporter.descriptor()
        }
        fn validate_settings(&self, s: &ZoneDefinition) -> Result<(), ImportError> {
            GrassImporter.validate_settings(s)
        }
        fn cache_settings(&self, s: &ZoneDefinition) -> Result<Option<Vec<u8>>, ImportError> {
            GrassImporter.cache_settings(s)
        }
        fn cache_encode(&self, o: &GrassPlacements) -> Result<Vec<u8>, ImportError> {
            GrassImporter.cache_encode(o)
        }
        fn cache_decode(
            &self,
            b: &[u8],
            s: &ZoneDefinition,
        ) -> Result<GrassPlacements, ImportError> {
            GrassImporter.cache_decode(b, s)
        }
        fn import(
            &self,
            c: &mut ImportContext<'_>,
            s: &ZoneDefinition,
        ) -> Result<GrassPlacements, ImportError> {
            self.0.fetch_add(1, Ordering::Relaxed);
            GrassImporter.import(c, s)
        }
    }
    fn zone() -> ZoneDefinition {
        arena_arpg_shared::project::ProjectContent::open(
            &std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".."),
        )
        .unwrap()
        .zone
    }
    #[test]
    fn cached_provider_reentry_preserves_records_and_shares_prototype() {
        use nico_presentation_control::instances::{
            ChunkError, ChunkKey, InstanceChunks,
            workers::{ChunkWorkers, InstanceProvider, ResultLimits},
        };
        let zone = zone();
        let generated = GrassPlacements::generate(&zone, || Ok(())).unwrap();
        let bytes = GrassImporter.cache_encode(&generated).unwrap();
        let cached = GrassImporter.cache_decode(&bytes, &zone).unwrap();
        let provider = Arc::new(GrassProvider::new(7, cached.clone()));
        assert!(Arc::ptr_eq(&provider.placements.chunks, &cached.chunks));
        let mut chunks = InstanceChunks::new(2, 2, 20000).unwrap();
        let key = ChunkKey {
            provider: 7,
            chunk: 0,
        };
        let request = chunks.request(key).unwrap();
        assert!(matches!(
            provider.load(&request, ResultLimits { decoded_bytes: 1 }),
            Err(ChunkError::Capacity)
        ));
        let mut workers = ChunkWorkers::new(1).unwrap();
        let mut previous: Option<Arc<InstanceBatch>> = None;
        let mut previous_shrub: Option<Arc<InstanceBatch>> = None;
        for _ in 0..2 {
            let request = chunks.request(key).unwrap();
            workers.submit(request, provider.clone()).unwrap();
            let deadline = std::time::Instant::now() + std::time::Duration::from_secs(5);
            loop {
                if let Some((ticket, result)) = workers.poll().pop() {
                    chunks.complete(&ticket, result).unwrap();
                    break;
                }
                assert!(std::time::Instant::now() < deadline);
                std::thread::yield_now();
            }
            let resident = chunks.resident(key).unwrap();
            assert_eq!(resident.len(), 2);
            let shrub = &resident[1];
            assert_eq!(
                shrub.records().len(),
                shrubs::ShrubPrototype::count(&generated.chunks[0])
            );
            assert!(!Arc::ptr_eq(resident[0].mesh(), shrub.mesh()));
            assert!(!Arc::ptr_eq(resident[0].material(), shrub.material()));
            assert_ne!(resident[0].foliage(), shrub.foliage());
            if let Some(old) = &previous_shrub {
                assert!(!Arc::ptr_eq(old, shrub));
                assert!(Arc::ptr_eq(old.mesh(), shrub.mesh()));
                for (a, b) in old.records().iter().zip(shrub.records()) {
                    assert_eq!(a.id(), b.id());
                    assert_eq!(a.transform(), b.transform());
                    assert_eq!(a.foliage_response(), b.foliage_response());
                }
            }
            previous_shrub = Some(shrub.clone());
            let batch = &resident[0];
            let catalog = provider.catalog();
            let declared = catalog
                .iter()
                .find(|(candidate, _)| *candidate == key)
                .unwrap()
                .1;
            let actual = batch.bounds().unwrap();
            assert!(
                Vec3::from(declared.min())
                    .cmple(Vec3::from(actual.min()))
                    .all()
            );
            assert!(
                Vec3::from(declared.max())
                    .cmpge(Vec3::from(actual.max()))
                    .all()
            );
            assert_eq!(batch.records().len(), generated.chunks[0].len());
            for (record, placement) in batch.records().iter().zip(&generated.chunks[0]) {
                assert_eq!(record.id(), placement.id as u64);
                assert_eq!(record.seed(), placement.seed);
                assert_eq!(
                    record.foliage_response(),
                    nico_presentation::foliage::FoliageResponse::from_seed(
                        placement.seed,
                        1.,
                        0.65
                    )
                    .unwrap()
                );
            }
            if let Some(old) = &previous {
                assert!(!Arc::ptr_eq(old, batch));
                assert!(Arc::ptr_eq(old.mesh(), batch.mesh()));
                assert!(Arc::ptr_eq(old.material(), batch.material()));
                for (a, b) in old.records().iter().zip(batch.records()) {
                    assert_eq!(a.transform(), b.transform());
                    assert_eq!(a.normal_transform(), b.normal_transform());
                    assert_eq!(a.tint(), b.tint());
                    assert_eq!(a.foliage_response(), b.foliage_response());
                }
            }
            previous = Some(batch.clone());
            chunks.unload(key);
        }
        let cancelled = chunks.request(key).unwrap();
        chunks.unload(key);
        assert!(matches!(
            provider.load(
                &cancelled,
                ResultLimits {
                    decoded_bytes: usize::MAX
                }
            ),
            Err(ChunkError::Stale)
        ));
        assert!(workers.shutdown());
    }
    #[test]
    fn warm_cache_skips_generator_and_relevant_edits_invalidate_recipe() {
        let root = tempfile::tempdir().unwrap();
        let source = root.path().join("world.toml");
        std::fs::write(&source, "placement source").unwrap();
        let cache = ImportCache::new(root.path()).unwrap();
        let importer = CountingImporter(AtomicUsize::new(0));
        let mut zone = ZoneDefinition::default();
        zone.obstacles
            .push(arena_arpg_shared::open_world::content::Obstacle {
                id: "rock".into(),
                center: [20., 1., 20.],
                size: [2.; 3],
                color: [1.; 4],
            });
        let load = |zone: &ZoneDefinition| {
            cache
                .load(&source, &importer, zone, ImportBudget::default(), &|| false)
                .unwrap()
        };
        let first = load(&zone);
        assert_eq!(first, load(&zone));
        assert_eq!(importer.0.load(Ordering::Relaxed), 1);
        let recipe = GrassImporter.cache_settings(&zone).unwrap();
        zone.obstacles[0].color = [0.2; 4];
        zone.obstacles[0].id.push_str("renamed");
        assert_eq!(recipe, GrassImporter.cache_settings(&zone).unwrap());
        assert_eq!(first, load(&zone));
        assert_eq!(importer.0.load(Ordering::Relaxed), 1);
        zone.half_extent_m -= 1.;
        assert_ne!(first, load(&zone));
        assert_eq!(importer.0.load(Ordering::Relaxed), 2);
        zone.obstacles[0].center[0] += 5.;
        load(&zone);
        assert_eq!(importer.0.load(Ordering::Relaxed), 3);
        zone.obstacles[0].size[0] += 1.;
        load(&zone);
        assert_eq!(importer.0.load(Ordering::Relaxed), 4);
        assert!(
            cache
                .load(&source, &importer, &zone, ImportBudget::default(), &|| true)
                .is_err()
        );
        assert_eq!(importer.0.load(Ordering::Relaxed), 4);
    }
    #[test]
    fn decoding_rejects_corruption_before_unbounded_allocation() {
        let zone = zone();
        let data = GrassPlacements::generate(&zone, || Ok(())).unwrap();
        let bytes = GrassImporter.cache_encode(&data).unwrap();
        assert_eq!(data, GrassImporter.cache_decode(&bytes, &zone).unwrap());
        for length in [0, 3, bytes.len() - 1] {
            assert!(GrassImporter.cache_decode(&bytes[..length], &zone).is_err());
        }
        let mut bad = bytes.clone();
        bad[..4].copy_from_slice(&u32::MAX.to_le_bytes());
        assert!(GrassImporter.cache_decode(&bad, &zone).is_err());
        let mut bad = bytes.clone();
        bad[12..16].copy_from_slice(&f32::NAN.to_le_bytes());
        assert!(GrassImporter.cache_decode(&bad, &zone).is_err());
        let mut bad = bytes.clone();
        bad[4 + RECORD_BYTES..8 + RECORD_BYTES].copy_from_slice(&bytes[4..8]);
        assert!(GrassImporter.cache_decode(&bad, &zone).is_err());
        let mut checks = 0;
        assert!(
            GrassPlacements::generate(&zone, || {
                checks += 1;
                if checks == 3 {
                    Err(invalid("cancelled"))
                } else {
                    Ok(())
                }
            })
            .is_err()
        );
        assert_eq!(checks, 3);
    }
    #[test]
    #[ignore = "manual cold/warm placement wall-time measurement"]
    fn cached_grass_preparation_measurement() {
        let root = tempfile::tempdir().unwrap();
        let source = root.path().join("world.toml");
        std::fs::write(&source, "placement source").unwrap();
        let cache = ImportCache::new(root.path()).unwrap();
        let zone = zone();
        for label in ["cold", "warm"] {
            let start = std::time::Instant::now();
            let data = cache
                .load(
                    &source,
                    &GrassImporter,
                    &zone,
                    ImportBudget::default(),
                    &|| false,
                )
                .unwrap();
            let loaded = start.elapsed();
            let batches = data.batches().unwrap();
            // Diagnostic serialization below is not part of warm preparation.
            let prepared = start.elapsed();
            eprintln!(
                "{label} grass: chunks={}, blades={}, placement_bytes={}, load_wall_ms={:.3}, total_prepare_wall_ms={:.3}",
                batches.len(),
                batches.iter().map(|b| b.records().len()).sum::<usize>(),
                GrassImporter.cache_encode(&data).unwrap().len(),
                loaded.as_secs_f64() * 1000.,
                prepared.as_secs_f64() * 1000.
            );
            assert!(
                batches
                    .windows(2)
                    .all(|pair| Arc::ptr_eq(pair[0].mesh(), pair[1].mesh()))
            );
        }
    }
}
