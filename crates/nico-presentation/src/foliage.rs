//! Render-only world influences. Placement and gameplay state remain provider-owned.
use crate::InstanceBounds;
use glam::{Mat3, Mat4, Vec3};

pub const MAX_WORLD_INFLUENCES: usize = 256;
pub const MAX_CHUNK_INFLUENCES: usize = 16;

/// Immutable per-instance response. Wind varies at one cycle per visual second;
/// radial interactions use only response strength. Neither changes placement.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FoliageResponse {
    phase: f32,
    strength: f32,
    wind_variation: f32,
}
impl Default for FoliageResponse {
    fn default() -> Self {
        Self {
            phase: 0.,
            strength: 1.,
            wind_variation: 0.,
        }
    }
}
impl FoliageResponse {
    /// Hash all seed bits into a reproducible phase. Strength and variation are
    /// bounded so the profile's maximum displacement remains conservative.
    pub fn from_seed(seed: u32, strength: f32, wind_variation: f32) -> Option<Self> {
        if !(0. ..=1.).contains(&strength) || !(0. ..=1.).contains(&wind_variation) {
            return None;
        }
        let mut hash = seed ^ (seed >> 16);
        hash = hash.wrapping_mul(0x7feb_352d);
        hash ^= hash >> 15;
        hash = hash.wrapping_mul(0x846c_a68b);
        hash ^= hash >> 16;
        Some(Self {
            phase: (hash >> 8) as f32 / 16_777_216.,
            strength,
            wind_variation,
        })
    }
    /// Packed into the unused normal-matrix W components of the instance ABI.
    pub fn parameters(self) -> [f32; 3] {
        [self.phase, self.strength, self.wind_variation]
    }
    fn wind_weight(self, time: f64) -> f32 {
        let angle = (time.rem_euclid(1.) as f32 + self.phase) * std::f32::consts::TAU;
        1. - self.wind_variation * 0.5 * (1. - angle.cos())
    }
}

/// Version-one foliage shape: local Y measures height above an anchored root.
/// The bend is a world-space shear perpendicular to the height gradient, keeping
/// its Jacobian invertible even for nonuniform scale, reflection and shear.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct FoliageProfile {
    root_y: f32,
    height: f32,
    max_bend: f32,
}

impl FoliageProfile {
    pub fn root_y(self) -> f32 {
        self.root_y
    }
    pub fn height(self) -> f32 {
        self.height
    }
    pub fn max_bend(self) -> f32 {
        self.max_bend
    }
    pub fn new(root_y: f32, height: f32, max_bend: f32) -> Option<Self> {
        (root_y.is_finite()
            && height.is_finite()
            && height > 0.
            && height.recip().is_finite()
            && max_bend.is_finite()
            && max_bend >= 0.)
            .then_some(Self {
                root_y,
                height,
                max_bend,
            })
    }

    /// Expand already transformed bounds in world units, rejecting overflow.
    pub fn expanded_bounds(self, bounds: InstanceBounds) -> Option<InstanceBounds> {
        InstanceBounds::new(
            (Vec3::from(bounds.min()) - Vec3::splat(self.max_bend)).to_array(),
            (Vec3::from(bounds.max()) + Vec3::splat(self.max_bend)).to_array(),
        )
    }

    /// CPU reference for position and inverse-transpose normal deformation.
    /// Fields are sampled at the instance root, so every vertex shares one bend.
    /// Inputs use the same affine contract as an instance record.
    pub fn deform(
        self,
        transform: Mat4,
        local_position: Vec3,
        local_normal: Vec3,
        fields: &ChunkInfluences,
    ) -> Option<(Vec3, Vec3)> {
        self.deform_with_response(
            transform,
            local_position,
            local_normal,
            fields,
            FoliageResponse::default(),
        )
    }

    pub fn deform_with_response(
        self,
        transform: Mat4,
        local_position: Vec3,
        local_normal: Vec3,
        fields: &ChunkInfluences,
        response: FoliageResponse,
    ) -> Option<(Vec3, Vec3)> {
        if !transform.is_finite()
            || transform.row(3) != glam::Vec4::W
            || !local_position.is_finite()
            || !local_normal.is_finite()
        {
            return None;
        }
        let normal_matrix = Mat3::from_mat4(transform).inverse().transpose();
        if !normal_matrix.is_finite() {
            return None;
        }
        let gradient = normal_matrix * Vec3::Y / self.height;
        let axis = gradient.try_normalize()?;
        let root = transform.transform_point3(Vec3::Y * self.root_y);
        let raw_bend = fields.displacement_with_response(root, 1., self.max_bend, response)?;
        let bend = (raw_bend - axis * raw_bend.dot(axis)).clamp_length_max(self.max_bend);
        let relative_height = (local_position.y - self.root_y) / self.height;
        let weight = relative_height.clamp(0., 1.);
        // At the tip use the interior derivative: the prototype surface ends
        // here, so lighting must follow its bent tangent rather than the
        // constant extension above the configured height.
        let derivative = if relative_height > 0. && relative_height <= 1. {
            2. * weight
        } else {
            0.
        };
        let position = transform.transform_point3(local_position) + bend * weight * weight;
        let normal = normal_matrix * local_normal;
        let normal = (normal - gradient * (derivative * bend.dot(normal))).try_normalize()?;
        position.is_finite().then_some((position, normal))
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum InfluenceKind {
    DirectionalWind,
    RadialBend,
}

/// Finite world-space field, active on [start, end). Strength fades to zero at
/// expiry, so an expired radial interaction recovers without persistent state.
#[derive(Clone, Copy, Debug)]
pub struct WorldInfluence {
    id: u64,
    kind: InfluenceKind,
    position: Vec3,
    direction: Vec3,
    radius: f32,
    strength: f32,
    start: f64,
    end: f64,
}

impl WorldInfluence {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        id: u64,
        kind: InfluenceKind,
        position: [f32; 3],
        direction: [f32; 3],
        radius: f32,
        strength: f32,
        start: f64,
        end: f64,
    ) -> Option<Self> {
        let position = Vec3::from(position);
        let direction = Vec3::from(direction);
        if !position.is_finite()
            || !direction.is_finite()
            || !radius.is_finite()
            || radius <= 0.
            || !strength.is_finite()
            || !(0. ..=1.).contains(&strength)
            || !start.is_finite()
            || !end.is_finite()
            || !((end - start).is_finite() && end > start)
        {
            return None;
        }
        let direction = direction.try_normalize()?;
        Some(Self {
            id,
            kind,
            position,
            direction,
            radius,
            strength,
            start,
            end,
        })
    }

    pub fn id(self) -> u64 {
        self.id
    }

    pub fn kind(self) -> InfluenceKind {
        self.kind
    }
    pub fn position(self) -> [f32; 3] {
        self.position.to_array()
    }
    pub fn direction(self) -> [f32; 3] {
        self.direction.to_array()
    }
    pub fn radius(self) -> f32 {
        self.radius
    }

    pub fn strength_at(self, time: f64) -> f32 {
        if !time.is_finite() || !self.active(time) {
            return 0.;
        }
        self.strength * ((self.end - time) / (self.end - self.start)) as f32
    }

    fn active(self, time: f64) -> bool {
        time >= self.start && time < self.end
    }

    fn sample(self, root: Vec3, time: f64) -> Vec3 {
        if !self.active(time) {
            return Vec3::ZERO;
        }
        let delta = root - self.position;
        let distance = delta.length();
        if distance >= self.radius {
            return Vec3::ZERO;
        }
        let radial = 1. - distance / self.radius;
        let direction = match self.kind {
            InfluenceKind::DirectionalWind => self.direction,
            InfluenceKind::RadialBend => delta.try_normalize().unwrap_or(self.direction),
        };
        direction * (self.strength_at(time) * radial * radial)
    }
}

/// Owned bounded snapshot, sorted by stable ID to make overflow selection
/// independent of provider completion order. Time can be paused or sought.
#[derive(Clone, Debug)]
pub struct InfluenceSnapshot {
    time: f64,
    fields: Vec<WorldInfluence>,
}
impl InfluenceSnapshot {
    /// Re-evaluate the same bounded fields at an owner-selected visual time.
    pub fn at_time(&self, time: f64) -> Option<Self> {
        Self::new(time, self.fields.clone())
    }
    pub fn new(time: f64, mut fields: Vec<WorldInfluence>) -> Option<Self> {
        if !time.is_finite() || fields.len() > MAX_WORLD_INFLUENCES {
            return None;
        }
        fields.sort_unstable_by_key(|field| field.id);
        if fields.windows(2).any(|pair| pair[0].id == pair[1].id) {
            return None;
        }
        Some(Self { time, fields })
    }

    pub fn time(&self) -> f64 {
        self.time
    }
    pub fn fields(&self) -> &[WorldInfluence] {
        &self.fields
    }

    /// Select against conservative chunk bounds. Lowest stable IDs win ties;
    /// excess intersecting fields are counted explicitly instead of silently lost.
    pub fn for_chunk(&self, bounds: InstanceBounds) -> ChunkInfluences {
        let mut selected = Vec::with_capacity(MAX_CHUNK_INFLUENCES);
        let mut overflow = 0;
        for field in &self.fields {
            if field.active(self.time) && bounds.within_distance(field.position, field.radius) {
                if selected.len() < MAX_CHUNK_INFLUENCES {
                    selected.push(*field);
                } else {
                    overflow += 1;
                }
            }
        }
        ChunkInfluences {
            time: self.time,
            fields: selected,
            overflow,
        }
    }
}

#[derive(Clone, Debug)]
pub struct ChunkInfluences {
    time: f64,
    fields: Vec<WorldInfluence>,
    overflow: usize,
}
impl ChunkInfluences {
    pub fn time(&self) -> f64 {
        self.time
    }
    pub fn fields(&self) -> &[WorldInfluence] {
        &self.fields
    }
    pub fn overflow(&self) -> usize {
        self.overflow
    }

    /// CPU reference for the foliage extension: add fields, clamp the combined
    /// response, then weight root-to-tip quadratically. Maximum bend is in world
    /// units, independent of prototype scale. Invalid inputs are rejected.
    pub fn displacement(&self, root: Vec3, weight: f32, max_bend: f32) -> Option<Vec3> {
        self.displacement_with_response(root, weight, max_bend, FoliageResponse::default())
    }

    pub fn displacement_with_response(
        &self,
        root: Vec3,
        weight: f32,
        max_bend: f32,
        response: FoliageResponse,
    ) -> Option<Vec3> {
        if !root.is_finite()
            || !(0. ..=1.).contains(&weight)
            || !max_bend.is_finite()
            || max_bend < 0.
        {
            return None;
        }
        let sum: Vec3 = self
            .fields
            .iter()
            .map(|field| {
                let wind = if field.kind == InfluenceKind::DirectionalWind {
                    response.wind_weight(self.time)
                } else {
                    1.
                };
                field.sample(root, self.time) * wind
            })
            .sum();
        Some(sum.clamp_length_max(1.) * (weight * weight * max_bend * response.strength))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn field(id: u64) -> WorldInfluence {
        WorldInfluence::new(
            id,
            InfluenceKind::RadialBend,
            [0.; 3],
            [1., 0., 0.],
            2.,
            1.,
            0.,
            10.,
        )
        .unwrap()
    }
    fn bounds() -> InstanceBounds {
        InstanceBounds::new([-1.; 3], [1.; 3]).unwrap()
    }

    #[test]
    fn seeded_response_replays_wind_without_modulating_radial_recovery() {
        let response = FoliageResponse::from_seed(17, 0.7, 1.).unwrap();
        assert_eq!(response, FoliageResponse::from_seed(17, 0.7, 1.).unwrap());
        assert_ne!(
            response.parameters()[0],
            FoliageResponse::from_seed(18, 0.7, 1.)
                .unwrap()
                .parameters()[0]
        );
        assert_eq!(
            response.wind_weight(0.25),
            response.wind_weight(1_000_000_000.25)
        );
        assert_ne!(response.wind_weight(0.), response.wind_weight(0.25));
        assert!(FoliageResponse::from_seed(0, f32::NAN, 0.).is_none());
        assert!(FoliageResponse::from_seed(0, 1., 1.01).is_none());
        let fields = InfluenceSnapshot::new(0., vec![field(0)])
            .unwrap()
            .for_chunk(bounds());
        let radial = fields
            .displacement_with_response(Vec3::ZERO, 1., 2., response)
            .unwrap();
        assert!((radial - Vec3::X * 1.4).length() < 1e-6);
        let still = FoliageResponse::from_seed(17, 0., 1.).unwrap();
        assert_eq!(
            fields.displacement_with_response(Vec3::ZERO, 1., 2., still),
            Some(Vec3::ZERO)
        );
        let wind = WorldInfluence::new(
            1,
            InfluenceKind::DirectionalWind,
            [0.; 3],
            [1., 0., 0.],
            2.,
            1.,
            0.,
            10.,
        )
        .unwrap();
        let sample = |time| {
            InfluenceSnapshot::new(time, vec![wind])
                .unwrap()
                .for_chunk(bounds())
                .displacement_with_response(Vec3::ZERO, 1., 2., response)
                .unwrap()
        };
        assert_ne!(sample(0.25), sample(0.5));
        for time in [0., 0.25, 0.5, 1., 9., 10.] {
            assert!(sample(time).length() <= 1.4);
        }
        assert_eq!(sample(10.), Vec3::ZERO);
    }

    #[test]
    fn deformed_normals_match_surface_derivatives_under_affine_transform() {
        let profile = FoliageProfile::new(0., 1., 0.8).unwrap();
        let fields = InfluenceSnapshot::new(0., vec![field(0)])
            .unwrap()
            .for_chunk(bounds());
        for scale in [1., -1.] {
            let transform = Mat4::from_cols(
                glam::Vec4::new(scale * 2., 0., 0., 0.),
                glam::Vec4::new(0.2, 1.5, 0.1, 0.),
                glam::Vec4::new(0., 0., 0.5, 0.),
                glam::Vec4::W,
            );
            let p = Vec3::new(0., 0.4, 0.);
            let (position, normal) = profile.deform(transform, p, Vec3::X, &fields).unwrap();
            let sample = |p| profile.deform(transform, p, Vec3::X, &fields).unwrap().0;
            let dy = sample(p + Vec3::Y * 0.001) - sample(p - Vec3::Y * 0.001);
            let dz = sample(p + Vec3::Z * 0.001) - sample(p - Vec3::Z * 0.001);
            assert!(normal.dot(dy.normalize()).abs() < 0.001);
            assert!(normal.dot(dz.normalize()).abs() < 0.001);
            assert!((position - transform.transform_point3(p)).length() <= 0.8);
            assert_eq!(sample(Vec3::ZERO), Vec3::ZERO);
            let expanded = profile
                .expanded_bounds(bounds().transformed(transform).unwrap())
                .unwrap();
            for y in [0., 0.25, 0.5, 1.] {
                let point = sample(Vec3::Y * y);
                assert!(point.cmpge(Vec3::from(expanded.min())).all());
                assert!(point.cmple(Vec3::from(expanded.max())).all());
            }
        }
        assert!(
            profile
                .deform(Mat4::ZERO, Vec3::ZERO, Vec3::X, &fields)
                .is_none()
        );
        assert!(FoliageProfile::new(0., 0., 1.).is_none());
    }

    #[test]
    fn tip_normal_uses_the_bent_surface_interior_tangent() {
        let profile = FoliageProfile::new(0., 1., 0.8).unwrap();
        let fields = InfluenceSnapshot::new(0., vec![field(0)])
            .unwrap()
            .for_chunk(bounds());
        let (tip, normal) = profile
            .deform(Mat4::IDENTITY, Vec3::Y, Vec3::X, &fields)
            .unwrap();
        let below = profile
            .deform(Mat4::IDENTITY, Vec3::Y * 0.9999, Vec3::X, &fields)
            .unwrap()
            .0;
        assert!(normal.dot((tip - below).normalize()).abs() < 0.001);
        assert!(normal.dot(Vec3::X) < 0.9);
    }

    #[test]
    fn selection_is_bounded_stable_and_rejects_duplicate_identity() {
        let a = InfluenceSnapshot::new(0., (0..20).rev().map(field).collect())
            .unwrap()
            .for_chunk(bounds());
        assert_eq!(a.overflow(), 4);
        assert_eq!(
            a.fields().iter().map(|f| f.id()).collect::<Vec<_>>(),
            (0..16).collect::<Vec<_>>()
        );
        assert!(InfluenceSnapshot::new(0., vec![field(1), field(1)]).is_none());
        assert!(InfluenceSnapshot::new(0., (0..257).map(field).collect()).is_none());
    }

    #[test]
    fn roots_radius_expiry_pause_and_maximum_bend_are_conservative() {
        let snapshot = InfluenceSnapshot::new(0., (0..20).map(field).collect()).unwrap();
        let fields = snapshot.for_chunk(bounds());
        assert_eq!(fields.displacement(Vec3::ZERO, 0., 3.), Some(Vec3::ZERO));
        assert_eq!(fields.displacement(Vec3::X * 2., 1., 3.), Some(Vec3::ZERO));
        assert_eq!(fields.displacement(Vec3::ZERO, 1., 3.), Some(Vec3::X * 3.));
        assert_eq!(
            fields.displacement(Vec3::X, 0.5, 3.),
            snapshot.for_chunk(bounds()).displacement(Vec3::X, 0.5, 3.)
        );
        let expired = InfluenceSnapshot::new(10., vec![field(0)])
            .unwrap()
            .for_chunk(bounds());
        assert!(expired.fields().is_empty());
        let recovering = InfluenceSnapshot::new(9., vec![field(0)])
            .unwrap()
            .for_chunk(bounds());
        assert!((recovering.displacement(Vec3::ZERO, 1., 1.).unwrap().x - 0.1).abs() < 1e-6);
        assert!(fields.displacement(Vec3::ZERO, f32::NAN, 1.).is_none());
    }
}
