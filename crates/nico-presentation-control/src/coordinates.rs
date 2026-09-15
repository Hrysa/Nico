//! Coordinate helpers shared by presentation extraction and input adapters.
use glam::{DQuat, DVec3, Mat3, Vec3};
use nico_presentation::Quaternion;

/// Scale in local axes, rotate by a unit quaternion, then translate into world space.
pub fn transform_point(
    position: [f32; 3],
    orientation: Quaternion,
    scale: [f32; 3],
    point: [f32; 3],
) -> [f32; 3] {
    (Vec3::from(position) + orientation * (Vec3::from(scale) * Vec3::from(point))).to_array()
}
/// Rotate XZ coordinates about world +Y. Heading is a control input; quaternion
/// rotation performs the transformation. Magnitude/axis mapping are caller policy.
pub fn rotate_on_floor(heading: f64, vector: [f64; 2]) -> [f64; 2] {
    let rotated = DQuat::from_rotation_y(heading) * DVec3::new(vector[0], 0.0, vector[1]);
    [rotated.x, rotated.z]
}
/// Align local +Z toward the camera's backward direction while preserving the
/// supplied up axis. This cylindrical billboard ignores camera roll. Parallel
/// directions (including a vertical camera over a Y-up plane) return None so the
/// caller can retain its previous orientation or choose a stable fallback.
pub fn cylindrical_billboard(camera: Quaternion, up: [f32; 3]) -> Option<Quaternion> {
    let up = Vec3::from(up);
    if !camera.is_finite()
        || !camera.is_normalized()
        || !up.is_finite()
        || up.length_squared() < 1e-8
    {
        return None;
    }
    let up = up.normalize();
    let back = camera * Vec3::Z;
    let back = back - up * back.dot(up);
    if back.length_squared() < 1e-8 {
        return None;
    }
    let back = back.normalize();
    Some(Quaternion::from_mat3(&Mat3::from_cols(up.cross(back), up, back)).normalize())
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn local_scale_rotation_translation_and_floor_direction_preserve_conventions() {
        let point = transform_point(
            [1.0, 2.0, 3.0],
            Quaternion::from_rotation_y(std::f32::consts::FRAC_PI_2),
            [2.0, 3.0, 4.0],
            [1.0, 1.0, 1.0],
        );
        assert!((Vec3::from(point) - Vec3::new(5.0, 5.0, 1.0)).length() < 1e-5);
        assert_eq!(rotate_on_floor(0.0, [-1.0, 0.0]), [-1.0, 0.0]);
        let direction = rotate_on_floor(std::f64::consts::FRAC_PI_2, [0.0, 1.0]);
        assert!((direction[0] - 1.0).abs() < 1e-12 && direction[1].abs() < 1e-12);
    }
    #[test]
    fn cylindrical_billboards_ignore_roll_and_make_pole_fallback_explicit() {
        let camera = Quaternion::from_rotation_y(0.7)
            * Quaternion::from_rotation_x(0.4)
            * Quaternion::from_rotation_z(1.0);
        let billboard = cylindrical_billboard(camera, [0.0, 1.0, 0.0]).unwrap();
        assert!((billboard * Vec3::Y - Vec3::Y).length() < 1e-5);
        assert!(billboard.dot(Quaternion::from_rotation_y(0.7)).abs() > 0.9999);
        assert!(
            cylindrical_billboard(
                Quaternion::from_rotation_x(std::f32::consts::FRAC_PI_2),
                [0.0, 1.0, 0.0]
            )
            .is_none()
        );
    }
}
