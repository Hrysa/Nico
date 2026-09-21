//! A second foliage consumer sharing meadow chunk ownership and cached placement.
use super::*;

pub(super) struct ShrubPrototype {
    mesh: Arc<Mesh>,
    material: Arc<PbrMaterial>,
}

impl ShrubPrototype {
    pub fn new() -> Self {
        let mut vertices = Vec::new();
        let mut indices = Vec::new();
        // Three broad upright leaves, each anchored at the same root.
        for leaf in 0..3 {
            let rotation = Quat::from_rotation_y(leaf as f32 * std::f32::consts::TAU / 3.);
            let base = vertices.len() as u32;
            for position in [
                [0., 0., 0.],
                [-0.22, 0.55, 0.08],
                [0., 1., 0.16],
                [0.22, 0.55, 0.08],
            ] {
                vertices.push(MeshVertex {
                    position: (rotation * Vec3::from(position)).to_array(),
                    uv: [0.5; 2],
                });
            }
            indices.extend([base, base + 1, base + 2, base, base + 2, base + 3]);
        }
        Self {
            mesh: Arc::new(Mesh::triangles(vertices, indices).expect("valid shrub prototype")),
            material: Arc::new(PbrMaterial {
                double_sided: true,
                ..Default::default()
            }),
        }
    }
    pub fn count(chunk: &[Placement]) -> usize {
        chunk.iter().filter(|p| Self::selected(p)).count()
    }
    fn selected(p: &Placement) -> bool {
        // One root per 47 accepted candidate identities, never all three blades.
        p.id.is_multiple_of(3 * 47)
    }
    pub fn batch(
        &self,
        chunk: &[Placement],
        mut cancelled: impl FnMut() -> bool,
    ) -> Result<Arc<InstanceBatch>, nico_presentation::InstanceError> {
        let mut records = Vec::with_capacity(Self::count(chunk));
        for p in chunk
            .iter()
            .filter(|p| Self::selected(p))
            .take_while(|_| !cancelled())
        {
            let height = p.height + 0.15;
            records.push(
                InstanceRecord::new(
                    (1_u64 << 32) | p.id as u64,
                    p.seed,
                    Mat4::from_scale_rotation_translation(
                        Vec3::splat(height),
                        Quat::from_rotation_y(-p.angle),
                        Vec3::new(p.x, 0., p.z),
                    ),
                    [0.16, 0.38, 0.055, 1.],
                )
                .expect("validated shrub placement")
                .with_foliage_response(
                    nico_presentation::foliage::FoliageResponse::from_seed(p.seed, 0.55, 0.35)
                        .unwrap(),
                ),
            );
        }
        InstanceBatch::new(self.mesh.clone(), self.material.clone(), records, 512.)?
            .with_foliage(nico_presentation::foliage::FoliageProfile::new(0., 1., 0.25).unwrap())
            .map(Arc::new)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn shrub_chunks_share_geometry_and_fit_streaming_bounds_under_full_bend() {
        let placements = GrassPlacements::generate(&ZoneDefinition::default(), || Ok(())).unwrap();
        let provider = GrassProvider::new(0, placements.clone());
        let catalog = provider.catalog();
        let mut total = 0;
        for (index, chunk) in placements.chunks.iter().enumerate() {
            let batch = provider.shrubs.batch(chunk, || false).unwrap();
            if batch.records().is_empty() {
                continue;
            }
            total += batch.records().len();
            assert!(Arc::ptr_eq(batch.mesh(), &provider.shrubs.mesh));
            let declared = catalog
                .iter()
                .find(|(key, _)| key.chunk == index as u64)
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
            for record in batch.records() {
                assert!(record.id() >= 1_u64 << 32);
                let root = record.transform().transform_point3(Vec3::ZERO);
                let fields = nico_presentation::foliage::InfluenceSnapshot::new(
                    0.,
                    vec![
                        nico_presentation::foliage::WorldInfluence::new(
                            0,
                            nico_presentation::foliage::InfluenceKind::DirectionalWind,
                            root.to_array(),
                            [1., 0., 0.],
                            2.,
                            1.,
                            0.,
                            10.,
                        )
                        .unwrap(),
                    ],
                )
                .unwrap()
                .for_chunk(actual);
                let profile = batch.foliage().unwrap();
                let deform = |position| {
                    profile
                        .deform_with_response(
                            record.transform(),
                            position,
                            Vec3::Z,
                            &fields,
                            record.foliage_response(),
                        )
                        .unwrap()
                        .0
                };
                assert_eq!(deform(Vec3::ZERO), root);
                assert!(
                    (deform(Vec3::Y) - record.transform().transform_point3(Vec3::Y)).length() > 0.
                );
            }
        }
        assert!(total > 0 && total < 3_000);
    }
}
