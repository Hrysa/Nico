//! Immutable, validated static instance batches. Providers own placement; renderers
//! consume snapshots without callbacks into providers or authoritative state.
use glam::{Mat3, Mat4, Vec3};
use nico_assets::{Mesh, PbrMaterial, model::AlphaMode};
use std::{fmt, sync::Arc};

pub const MAX_BATCH_INSTANCES: usize = 500_000;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct InstanceBounds {
    min: Vec3,
    max: Vec3,
}
impl InstanceBounds {
    pub fn new(min: [f32; 3], max: [f32; 3]) -> Option<Self> {
        let (min, max) = (Vec3::from(min), Vec3::from(max));
        (min.is_finite() && max.is_finite() && min.cmple(max).all()).then_some(Self { min, max })
    }
    pub fn min(self) -> [f32; 3] {
        self.min.to_array()
    }
    pub fn max(self) -> [f32; 3] {
        self.max.to_array()
    }
    pub fn center(self) -> Vec3 {
        self.min * 0.5 + self.max * 0.5
    }
    pub fn half_extent(self) -> Vec3 {
        self.max * 0.5 - self.min * 0.5
    }
    pub fn transformed(self, transform: Mat4) -> Option<Self> {
        let center = transform.transform_point3(self.center());
        let m = Mat3::from_mat4(transform);
        let extent =
            Mat3::from_cols(m.x_axis.abs(), m.y_axis.abs(), m.z_axis.abs()) * self.half_extent();
        Self::new((center - extent).to_array(), (center + extent).to_array())
    }
    fn union(self, other: Self) -> Self {
        Self {
            min: self.min.min(other.min),
            max: self.max.max(other.max),
        }
    }
    /// Conservative plane support test, with zero-to-one depth. Extract planes
    /// before evaluating bounds to avoid cancellation of projected z/w at far
    /// distance. The visibility shader shares this arithmetic and error margin.
    pub fn intersects_clip(self, clip: Mat4) -> bool {
        let w = clip.row(3);
        let magnitude = self.min.abs().max(self.max.abs());
        for plane in [
            w + clip.row(0),
            w - clip.row(0),
            w + clip.row(1),
            w - clip.row(1),
            clip.row(2),
            w - clip.row(2),
        ] {
            let normal = plane.truncate();
            let support = Vec3::select(normal.cmpge(Vec3::ZERO), self.max, self.min);
            let margin = (normal.abs().dot(magnitude) + plane.w.abs()).max(1.) * 1e-6;
            if normal.dot(support) + plane.w < -margin {
                return false;
            }
        }
        true
    }
    pub fn within_distance(self, position: Vec3, distance: f32) -> bool {
        position.distance(position.clamp(self.min, self.max)) <= distance
    }
    /// Conservative clip/draw-distance culling predicate shared by the CPU paths
    /// and the validity-check reference in `nico-render`. GPU visibility repeats
    /// this arithmetic in `instance_visibility.slang`.
    pub fn visible_in_view(self, clip: Mat4, position: Vec3, distance: f32) -> bool {
        self.intersects_clip(clip) && self.within_distance(position, distance)
    }
    /// True only when the entire bound lies inside all clip planes and the
    /// draw-distance sphere. A false result still requires ordinary culling.
    pub fn fully_visible(self, clip: Mat4, position: Vec3, distance: f32) -> bool {
        if !clip.is_finite() || !position.is_finite() || !distance.is_finite() || distance < 0. {
            return false;
        }
        let farthest = (self.min - position).abs().max((self.max - position).abs());
        if farthest.length() >= distance - distance.max(1.) * 1e-6 {
            return false;
        }
        let w = clip.row(3);
        let magnitude = self.min.abs().max(self.max.abs());
        for plane in [
            w + clip.row(0),
            w - clip.row(0),
            w + clip.row(1),
            w - clip.row(1),
            clip.row(2),
            w - clip.row(2),
        ] {
            let normal = plane.truncate();
            let support = Vec3::select(normal.cmpge(Vec3::ZERO), self.min, self.max);
            let margin = (normal.abs().dot(magnitude) + plane.w.abs()).max(1.) * 1e-6;
            let separation = normal.dot(support) + plane.w;
            if !separation.is_finite() || !margin.is_finite() || separation <= margin {
                return false;
            }
        }
        true
    }
}

/// Stable provider identity and seed survive visibility compaction. Color is a
/// linear material multiplier. Transform and inverse-transpose are validated once.
#[derive(Clone, Debug)]
pub struct InstanceRecord {
    id: u64,
    seed: u32,
    foliage_response: crate::foliage::FoliageResponse,
    transform: Mat4,
    normal: Mat3,
    tint: [f32; 4],
    mirrored: bool,
}
impl InstanceRecord {
    pub fn new(id: u64, seed: u32, transform: Mat4, tint: [f32; 4]) -> Option<Self> {
        if !transform.is_finite()
            || transform.row(3) != glam::Vec4::W
            || tint.iter().any(|v| !v.is_finite() || *v < 0.)
            || tint[3] > 1.
        {
            return None;
        }
        let linear = Mat3::from_mat4(transform);
        let determinant = linear.determinant();
        if !determinant.is_finite() || determinant == 0. {
            return None;
        }
        let normal = linear.inverse().transpose();
        if !normal.is_finite() {
            return None;
        }
        Some(Self {
            id,
            seed,
            foliage_response: crate::foliage::FoliageResponse::default(),
            transform,
            normal,
            tint,
            mirrored: determinant < 0.,
        })
    }
    pub fn id(&self) -> u64 {
        self.id
    }
    pub fn seed(&self) -> u32 {
        self.seed
    }
    pub fn with_foliage_response(mut self, response: crate::foliage::FoliageResponse) -> Self {
        self.foliage_response = response;
        self
    }
    pub fn foliage_response(&self) -> crate::foliage::FoliageResponse {
        self.foliage_response
    }
    pub fn transform(&self) -> Mat4 {
        self.transform
    }
    pub fn normal_transform(&self) -> Mat3 {
        self.normal
    }
    pub fn tint(&self) -> [f32; 4] {
        self.tint
    }
    pub fn mirrored(&self) -> bool {
        self.mirrored
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum InstanceError {
    UnsupportedMaterial,
    SkinnedMesh,
    TooManyInstances,
    DuplicateIdentity,
    MixedWinding,
    InvalidBounds,
    InvalidDistance,
    InvalidChunkSize,
    TooManyBatches,
}
impl fmt::Display for InstanceError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "invalid instance batch: {self:?}")
    }
}
impl std::error::Error for InstanceError {}

/// One compatible type/chunk. Share the batch itself via Arc between snapshots;
/// replacing records creates a new identity while old snapshots remain valid.
/// Blended and skinned instances retain their existing ordinary draw paths.
#[derive(Clone, Debug)]
pub struct InstanceBatch {
    mesh: Arc<Mesh>,
    material: Arc<PbrMaterial>,
    records: Vec<InstanceRecord>,
    record_bounds: Vec<InstanceBounds>,
    prototype_bounds: InstanceBounds,
    bounds: Option<InstanceBounds>,
    max_draw_distance: f32,
    mirrored: bool,
    foliage: Option<crate::foliage::FoliageProfile>,
}
impl InstanceBatch {
    /// Partition a validated batch in source order without regenerating placements
    /// or duplicating prototype assets. Record values and foliage configuration are
    /// preserved; each result receives bounds for its own records. At most 512
    /// groups may be produced. An already-fitting batch retains its allocation.
    pub fn split(self, max_records: usize) -> Result<Vec<Self>, InstanceError> {
        if max_records == 0 || max_records > MAX_BATCH_INSTANCES {
            return Err(InstanceError::InvalidChunkSize);
        }
        if self.records.len() <= max_records {
            return Ok(vec![self]);
        }
        let count = self.records.len().div_ceil(max_records);
        if count > 512 {
            return Err(InstanceError::TooManyBatches);
        }
        let mut source = self.records.into_iter();
        let mut source_bounds = self.record_bounds.into_iter();
        let mut batches = Vec::with_capacity(count);
        while source.len() != 0 {
            let records: Vec<_> = source.by_ref().take(max_records).collect();
            let record_bounds: Vec<_> = source_bounds.by_ref().take(records.len()).collect();
            let mut bounds: Option<InstanceBounds> = None;
            for &bound in &record_bounds {
                bounds = Some(bounds.map_or(bound, |old| old.union(bound)));
            }
            batches.push(Self {
                mesh: self.mesh.clone(),
                material: self.material.clone(),
                records,
                record_bounds,
                prototype_bounds: self.prototype_bounds,
                bounds,
                max_draw_distance: self.max_draw_distance,
                mirrored: self.mirrored,
                foliage: self.foliage,
            });
        }
        Ok(batches)
    }
    pub fn new(
        mesh: Arc<Mesh>,
        material: Arc<PbrMaterial>,
        records: Vec<InstanceRecord>,
        max_draw_distance: f32,
    ) -> Result<Self, InstanceError> {
        if mesh.joint_count() != 0 {
            return Err(InstanceError::SkinnedMesh);
        }
        if !material.is_valid() || material.alpha == AlphaMode::Blend {
            return Err(InstanceError::UnsupportedMaterial);
        }
        if records.len() > MAX_BATCH_INSTANCES {
            return Err(InstanceError::TooManyInstances);
        }
        if !max_draw_distance.is_finite() || max_draw_distance <= 0. {
            return Err(InstanceError::InvalidDistance);
        }
        let mirrored = records.first().is_some_and(InstanceRecord::mirrored);
        let (mut min, mut max) = (Vec3::splat(f32::INFINITY), Vec3::splat(f32::NEG_INFINITY));
        for vertex in mesh.vertices() {
            min = min.min(Vec3::from(vertex.position));
            max = max.max(Vec3::from(vertex.position));
        }
        let prototype_bounds = InstanceBounds::new(min.to_array(), max.to_array())
            .ok_or(InstanceError::InvalidBounds)?;
        let mut bounds: Option<InstanceBounds> = None;
        let mut record_bounds = Vec::with_capacity(records.len());
        // Sorting a compact ID array avoids one tree allocation per instance.
        let mut ids: Vec<_> = records.iter().map(InstanceRecord::id).collect();
        ids.sort_unstable();
        if ids.windows(2).any(|pair| pair[0] == pair[1]) {
            return Err(InstanceError::DuplicateIdentity);
        }
        for record in &records {
            if record.mirrored() != mirrored {
                return Err(InstanceError::MixedWinding);
            }
            let bound = prototype_bounds
                .transformed(record.transform())
                .ok_or(InstanceError::InvalidBounds)?;
            bounds = Some(bounds.map_or(bound, |previous| previous.union(bound)));
            record_bounds.push(bound);
        }
        Ok(Self {
            mesh,
            material,
            records,
            record_bounds,
            prototype_bounds,
            bounds,
            max_draw_distance,
            mirrored,
            foliage: None,
        })
    }
    /// Attach a shape extension before sharing the batch. This consumes the
    /// allocation without copying or regenerating placement records. Influence
    /// snapshots remain separate and never change this immutable identity.
    pub fn with_foliage(
        mut self,
        profile: crate::foliage::FoliageProfile,
    ) -> Result<Self, InstanceError> {
        let mut combined: Option<InstanceBounds> = None;
        for (record, cached) in self.records.iter().zip(&mut self.record_bounds) {
            let bounds = self
                .prototype_bounds
                .transformed(record.transform())
                .and_then(|bounds| profile.expanded_bounds(bounds))
                .ok_or(InstanceError::InvalidBounds)?;
            // Validate the shader's height gradient once rather than accepting
            // a profile/transform pair that can overflow at render time.
            let gradient = record.normal_transform() * Vec3::Y / profile.height();
            if !gradient.is_finite() || gradient.try_normalize().is_none() {
                return Err(InstanceError::InvalidBounds);
            }
            combined = Some(combined.map_or(bounds, |old| old.union(bounds)));
            *cached = bounds;
        }
        self.bounds = combined;
        self.foliage = Some(profile);
        Ok(self)
    }
    pub fn foliage(&self) -> Option<crate::foliage::FoliageProfile> {
        self.foliage
    }

    /// Conservative world-space bounds for a record, including the full allowed
    /// deformation even when the current influence snapshot is empty.
    pub fn record_bounds(&self, index: usize) -> Option<InstanceBounds> {
        self.record_bounds.get(index).copied()
    }
    pub fn mesh(&self) -> &Arc<Mesh> {
        &self.mesh
    }
    pub fn material(&self) -> &Arc<PbrMaterial> {
        &self.material
    }
    pub fn records(&self) -> &[InstanceRecord] {
        &self.records
    }
    /// CPU batch payload including cached bounds and unused allocation capacity. Excludes
    /// shared mesh/material assets, allocator metadata and Arc control blocks.
    pub fn decoded_bytes(&self) -> usize {
        std::mem::size_of::<Self>()
            + self.records.capacity() * std::mem::size_of::<InstanceRecord>()
            + self.record_bounds.capacity() * std::mem::size_of::<InstanceBounds>()
    }
    pub fn prototype_bounds(&self) -> InstanceBounds {
        self.prototype_bounds
    }
    pub fn bounds(&self) -> Option<InstanceBounds> {
        self.bounds
    }
    pub fn max_draw_distance(&self) -> f32 {
        self.max_draw_distance
    }
    pub fn mirrored(&self) -> bool {
        self.mirrored
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn full_visibility_requires_every_corner_inside_clip_and_distance() {
        use super::*;
        let inside = InstanceBounds::new([-0.25, -0.25, 0.25], [0.25, 0.25, 0.75]).unwrap();
        assert!(inside.fully_visible(Mat4::IDENTITY, Vec3::ZERO, 2.));
        for (min, max) in [
            ([-2., -0.25, 0.25], [0.25, 0.25, 0.75]),
            ([-0.25, -0.25, 0.25], [2., 0.25, 0.75]),
            ([-0.25, -2., 0.25], [0.25, 0.25, 0.75]),
            ([-0.25, -0.25, 0.25], [0.25, 2., 0.75]),
            ([-0.25, -0.25, -0.25], [0.25, 0.25, 0.75]),
            ([-0.25, -0.25, 0.25], [0.25, 0.25, 2.]),
        ] {
            let crossing = InstanceBounds::new(min, max).unwrap();
            assert!(crossing.intersects_clip(Mat4::IDENTITY));
            assert!(!crossing.fully_visible(Mat4::IDENTITY, Vec3::ZERO, 10.));
        }
        assert!(inside.within_distance(Vec3::ZERO, 0.5));
        assert!(!inside.fully_visible(Mat4::IDENTITY, Vec3::ZERO, 0.5));
        let touching = InstanceBounds::new([-1., -0.25, 0.25], [0.25, 0.25, 0.75]).unwrap();
        assert!(!touching.fully_visible(Mat4::IDENTITY, Vec3::ZERO, 10.));
        assert!(!inside.fully_visible(Mat4::ZERO, Vec3::ZERO, 10.));
        assert!(!inside.fully_visible(Mat4::IDENTITY * f32::MAX, Vec3::ZERO, 10.));
        assert!(!inside.fully_visible(Mat4::IDENTITY, Vec3::ZERO, f32::NAN));
    }

    #[test]
    fn plane_support_culls_far_bounds_without_projected_depth_cancellation() {
        use super::InstanceBounds;
        let camera = crate::Camera3d::looking_at([25., 35., 40.], [0.; 3], [0., 1., 0.]).unwrap();
        let clip = camera.view_projection(840. / 764.).unwrap();
        for (min, max) in [
            (
                [-18.552738, -0.4, -51.624855],
                [-17.518452, 1.0117071, -50.5307],
            ),
            (
                [-59.165154, -0.4, -25.84423],
                [-58.178246, 0.7038448, -24.8498],
            ),
        ] {
            // Evaluate the supplied f32 matrix in f64 as an independent check:
            // these bounds really are outside, despite GPU projected z/w ties.
            let plane = clip.row(3).as_dvec4() - clip.row(2).as_dvec4();
            let support = glam::DVec3::select(
                plane.truncate().cmpge(glam::DVec3::ZERO),
                glam::Vec3::from(max).as_dvec3(),
                glam::Vec3::from(min).as_dvec3(),
            );
            assert!(plane.truncate().dot(support) + plane.w < -1e-6);
            assert!(!InstanceBounds::new(min, max).unwrap().intersects_clip(clip));
        }
        assert!(
            InstanceBounds::new([0., 0., 1.0000005], [0., 0., 1.0000005])
                .unwrap()
                .intersects_clip(glam::Mat4::IDENTITY)
        );
        assert!(
            !InstanceBounds::new([0., 0., 1.0001], [0., 0., 1.0001])
                .unwrap()
                .intersects_clip(glam::Mat4::IDENTITY)
        );
    }
    use super::*;
    fn mesh() -> Arc<Mesh> {
        Arc::new(
            Mesh::triangles(
                vec![
                    nico_assets::MeshVertex {
                        position: [0., 0., 0.],
                        uv: [0.; 2],
                    },
                    nico_assets::MeshVertex {
                        position: [1., 0., 0.],
                        uv: [0.; 2],
                    },
                    nico_assets::MeshVertex {
                        position: [0., 1., 0.],
                        uv: [0.; 2],
                    },
                ],
                vec![0, 1, 2],
            )
            .unwrap(),
        )
    }
    #[test]
    fn splitting_preserves_records_shared_assets_and_deformed_bounds() {
        let records: Vec<_> = (0..5)
            .map(|id| {
                InstanceRecord::new(
                    id,
                    id as u32 + 9,
                    Mat4::from_scale_rotation_translation(
                        Vec3::new(-2., 3., 4.),
                        glam::Quat::from_rotation_y(0.4),
                        Vec3::new(id as f32 * 10., 0., 0.),
                    ),
                    [0.2, 0.4, 0.6, 1.],
                )
                .unwrap()
                .with_foliage_response(
                    crate::foliage::FoliageResponse::from_seed(id as u32 + 9, 0.8, 1.).unwrap(),
                )
            })
            .collect();
        let batch = InstanceBatch::new(
            mesh(),
            Arc::new(PbrMaterial::default()),
            records.clone(),
            50.,
        )
        .unwrap()
        .with_foliage(crate::foliage::FoliageProfile::new(0., 1., 0.4).unwrap())
        .unwrap();
        let original_bounds = batch.bounds().unwrap();
        let original_record_bounds = batch.record_bounds.clone();
        let original_mesh = batch.mesh().clone();
        let original_material = batch.material().clone();
        let chunks = batch.split(2).unwrap();
        assert_eq!(
            chunks
                .iter()
                .flat_map(|b| b.record_bounds.iter().copied())
                .collect::<Vec<_>>(),
            original_record_bounds
        );
        assert_eq!(
            chunks.iter().map(|b| b.records().len()).collect::<Vec<_>>(),
            [2, 2, 1]
        );
        for (a, b) in chunks.iter().flat_map(|b| b.records()).zip(&records) {
            assert_eq!(a.id(), b.id());
            assert_eq!(a.seed(), b.seed());
            assert_eq!(a.foliage_response(), b.foliage_response());
            assert_eq!(a.transform(), b.transform());
            assert_eq!(a.normal_transform(), b.normal_transform());
            assert_eq!(a.tint(), b.tint());
        }
        let mut union: Option<InstanceBounds> = None;
        for chunk in &chunks {
            assert!(Arc::ptr_eq(chunk.mesh(), &original_mesh));
            assert!(Arc::ptr_eq(chunk.material(), &original_material));
            assert!(chunk.mirrored());
            assert_eq!(chunk.max_draw_distance(), 50.);
            assert_eq!(chunk.foliage().unwrap().max_bend(), 0.4);
            let bound = chunk.bounds().unwrap();
            union = Some(union.map_or(bound, |old| old.union(bound)));
            for i in 0..chunk.records().len() {
                let record = chunk.record_bounds(i).unwrap();
                assert_eq!(
                    Some(record),
                    chunk
                        .prototype_bounds()
                        .transformed(chunk.records()[i].transform())
                        .and_then(|bounds| chunk.foliage().unwrap().expanded_bounds(bounds))
                );
                assert!(
                    Vec3::from(record.min())
                        .cmpge(Vec3::from(bound.min()))
                        .all()
                );
                assert!(
                    Vec3::from(record.max())
                        .cmple(Vec3::from(bound.max()))
                        .all()
                );
            }
        }
        assert_eq!(union, Some(original_bounds));
    }
    #[test]
    fn split_limits_empty_and_exact_fit_are_explicit() {
        let make = |count| {
            InstanceBatch::new(
                mesh(),
                Arc::new(PbrMaterial::default()),
                (0..count)
                    .map(|id| InstanceRecord::new(id, 0, Mat4::IDENTITY, [1.; 4]).unwrap())
                    .collect(),
                10.,
            )
            .unwrap()
        };
        assert_eq!(
            make(1).split(0).unwrap_err(),
            InstanceError::InvalidChunkSize
        );
        assert_eq!(
            make(513).split(1).unwrap_err(),
            InstanceError::TooManyBatches
        );
        assert_eq!(make(512).split(1).unwrap().len(), 512);
        let batch = make(2);
        let pointer = batch.records().as_ptr();
        let split = batch.split(2).unwrap();
        assert_eq!(split[0].records().as_ptr(), pointer);
        let empty = make(0).split(1).unwrap();
        assert_eq!(empty.len(), 1);
        assert!(empty[0].bounds().is_none());
    }
    #[test]
    fn decoded_byte_accounting_includes_spare_record_capacity() {
        let mut records = Vec::with_capacity(100);
        records.push(InstanceRecord::new(0, 0, Mat4::IDENTITY, [1.; 4]).unwrap());
        let capacity = records.capacity();
        let batch =
            InstanceBatch::new(mesh(), Arc::new(PbrMaterial::default()), records, 100.).unwrap();
        assert_eq!(batch.records().len(), 1);
        assert_eq!(
            batch.decoded_bytes(),
            std::mem::size_of::<InstanceBatch>()
                + capacity * std::mem::size_of::<InstanceRecord>()
                + batch.record_bounds.capacity() * std::mem::size_of::<InstanceBounds>()
        );
    }
    #[test]
    fn affine_normals_and_bounds_preserve_nonuniform_scale_and_reflection() {
        let transform = Mat4::from_scale_rotation_translation(
            Vec3::new(-2., 3., 4.),
            glam::Quat::from_rotation_y(0.7),
            Vec3::new(5., 1., 2.),
        );
        let record = InstanceRecord::new(12, 19, transform, [1.; 4]).unwrap();
        assert!(record.mirrored());
        let tangent = transform.transform_vector3(Vec3::X);
        let normal = record.normal_transform() * Vec3::Z;
        assert!(tangent.dot(normal).abs() < 1e-5);
        let batch =
            InstanceBatch::new(mesh(), Arc::new(PbrMaterial::default()), vec![record], 100.)
                .unwrap();
        let bounds = batch.bounds().unwrap();
        for vertex in batch.mesh().vertices() {
            let p = transform.transform_point3(Vec3::from(vertex.position));
            assert!(p.cmpge(Vec3::from(bounds.min()) - Vec3::splat(1e-5)).all());
            assert!(p.cmple(Vec3::from(bounds.max()) + Vec3::splat(1e-5)).all());
        }
    }
    #[test]
    fn foliage_bounds_expand_in_world_units_without_copying_placements() {
        let records = vec![
            InstanceRecord::new(4, 5, Mat4::from_scale(Vec3::new(2., 3., 4.)), [1.; 4]).unwrap(),
        ];
        let batch =
            InstanceBatch::new(mesh(), Arc::new(PbrMaterial::default()), records, 100.).unwrap();
        let pointer = batch.records().as_ptr();
        let original = batch.record_bounds(0).unwrap();
        let batch = batch
            .with_foliage(crate::foliage::FoliageProfile::new(0., 1., 0.5).unwrap())
            .unwrap();
        assert_eq!(pointer, batch.records().as_ptr());
        let expanded = batch.record_bounds(0).unwrap();
        assert_eq!(
            Vec3::from(expanded.min()),
            Vec3::from(original.min()) - Vec3::splat(0.5)
        );
        assert_eq!(
            Vec3::from(expanded.max()),
            Vec3::from(original.max()) + Vec3::splat(0.5)
        );
        assert_eq!(batch.bounds(), Some(expanded));
        assert!(batch.record_bounds(1).is_none());
        let replaced = batch
            .with_foliage(crate::foliage::FoliageProfile::new(0., 1., 0.25).unwrap())
            .unwrap();
        assert_eq!(
            Vec3::from(replaced.bounds().unwrap().min()),
            Vec3::from(original.min()) - Vec3::splat(0.25)
        );
    }
    #[test]
    fn invalid_records_batches_and_unsupported_materials_are_rejected() {
        assert!(InstanceRecord::new(0, 0, Mat4::ZERO, [1.; 4]).is_none());
        assert!(InstanceRecord::new(0, 0, Mat4::IDENTITY, [f32::NAN; 4]).is_none());
        let record = InstanceRecord::new(0, 0, Mat4::IDENTITY, [1.; 4]).unwrap();
        assert_eq!(
            InstanceBatch::new(
                mesh(),
                Arc::new(PbrMaterial::default()),
                vec![record.clone(), record.clone()],
                1.
            )
            .unwrap_err(),
            InstanceError::DuplicateIdentity
        );
        let reflected =
            InstanceRecord::new(1, 0, Mat4::from_scale(Vec3::new(-1., 1., 1.)), [1.; 4]).unwrap();
        assert_eq!(
            InstanceBatch::new(
                mesh(),
                Arc::new(PbrMaterial::default()),
                vec![record, reflected],
                1.
            )
            .unwrap_err(),
            InstanceError::MixedWinding
        );
        assert_eq!(
            InstanceBatch::new(
                mesh(),
                Arc::new(PbrMaterial {
                    alpha: AlphaMode::Blend,
                    ..Default::default()
                }),
                vec![],
                1.
            )
            .unwrap_err(),
            InstanceError::UnsupportedMaterial
        );
        assert!(
            InstanceBatch::new(mesh(), Arc::new(PbrMaterial::default()), vec![], 1.)
                .unwrap()
                .bounds()
                .is_none()
        );
    }
    #[test]
    fn visibility_is_conservative_at_clip_and_distance_boundaries() {
        let bounds = InstanceBounds::new([-1., -1., 0.], [1., 1., 1.]).unwrap();
        assert!(bounds.intersects_clip(Mat4::IDENTITY));
        assert!(!bounds.intersects_clip(Mat4::from_translation(Vec3::new(3., 0., 0.))));
        assert!(bounds.within_distance(Vec3::new(2., 0., 0.), 1.));
        assert!(!bounds.within_distance(Vec3::new(2.1, 0., 0.), 1.));
        assert!(bounds.visible_in_view(Mat4::IDENTITY, Vec3::new(2., 0., 0.), 1.));
        assert!(!bounds.visible_in_view(Mat4::IDENTITY, Vec3::new(2.1, 0., 0.), 1.));
        assert!(!bounds.visible_in_view(
            Mat4::from_translation(Vec3::new(3., 0., 0.)),
            Vec3::ZERO,
            1.
        ));
    }
}
