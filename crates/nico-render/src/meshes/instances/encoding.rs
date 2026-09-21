use nico_presentation::{InstanceBatch, InstanceRecord};

/// Private GPU serialization; logical affine records and placement caches are unchanged.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(in crate::meshes) enum Encoding {
    Full,
    Compact,
}

impl Encoding {
    pub fn bytes(self) -> u64 {
        match self {
            Self::Full => 112,
            Self::Compact => 80,
        }
    }

    pub fn for_batch(batch: &InstanceBatch) -> Self {
        if batch.records().iter().all(compact_normal_is_safe) {
            Self::Compact
        } else {
            Self::Full
        }
    }

    pub fn pack(self, bytes: &mut Vec<u8>, record: &InstanceRecord) {
        for row in 0..3 {
            for value in record.transform().row(row).to_array() {
                bytes.extend_from_slice(&value.to_le_bytes());
            }
        }
        match self {
            Self::Full => {
                for row in 0..3 {
                    for value in record
                        .normal_transform()
                        .row(row)
                        .extend(record.foliage_response().parameters()[row])
                        .to_array()
                    {
                        bytes.extend_from_slice(&value.to_le_bytes());
                    }
                }
            }
            Self::Compact => {
                for value in record.foliage_response().parameters().into_iter().chain([
                    glam::Mat3::from_mat4(record.transform())
                        .determinant()
                        .recip(),
                ]) {
                    bytes.extend_from_slice(&value.to_le_bytes());
                }
            }
        }
        for value in record.tint() {
            bytes.extend_from_slice(&value.to_le_bytes());
        }
    }
}

fn compact_normal_is_safe(record: &InstanceRecord) -> bool {
    let linear = glam::Mat3::from_mat4(record.transform());
    let values = linear.to_cols_array();
    // Avoid denormal inputs/products and very large products, independently of
    // backend denormal handling. Translation has no effect on normal conditioning.
    if values
        .iter()
        .any(|v| *v != 0. && !(1e-12..=1e6).contains(&v.abs()))
    {
        return false;
    }
    let inverse = linear.determinant().recip().abs();
    if !(1e-18..=1e18).contains(&inverse) {
        return false;
    }
    let maximum = values.into_iter().map(f32::abs).fold(0., f32::max);
    let normal_maximum = record
        .normal_transform()
        .to_cols_array()
        .into_iter()
        .map(f32::abs)
        .fold(0., f32::max);
    if maximum * normal_maximum > 64. {
        return false;
    }
    // Cancellation must not leave a denormal cofactor that a GPU could flush.
    [
        linear.row(1).cross(linear.row(2)),
        linear.row(2).cross(linear.row(0)),
        linear.row(0).cross(linear.row(1)),
    ]
    .into_iter()
    .flat_map(|row| row.to_array())
    .all(|v| v == 0. || v.is_normal())
}

#[cfg(test)]
mod tests {
    use super::*;
    use glam::{Mat4, Quat, Vec3};
    #[test]
    fn compact_selection_preserves_full_fallback_for_badly_conditioned_affine_data() {
        for scale in [Vec3::ONE, Vec3::new(0.5, 2., -1.)] {
            let record = InstanceRecord::new(
                0,
                0,
                Mat4::from_scale_rotation_translation(scale, Quat::from_rotation_y(0.7), Vec3::ONE),
                [1.; 4],
            )
            .unwrap();
            assert!(compact_normal_is_safe(&record));
        }
        for scale in [
            Vec3::new(1e-20, 1e-20, 100.),
            Vec3::new(1., 1., 1e-5),
            Vec3::splat(1e8),
        ] {
            let record = InstanceRecord::new(0, 0, Mat4::from_scale(scale), [1.; 4]).unwrap();
            assert!(!compact_normal_is_safe(&record));
        }
    }
}
