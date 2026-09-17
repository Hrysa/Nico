//! Coordinate helpers shared by presentation extraction and input adapters.
use glam::{DQuat, DVec3, Mat3, Vec3};
use nico_presentation::Quaternion;

/// Projects an on-screen world anchor into logical UI pixels using the 3D camera.
/// Rejects invalid views and points outside the clip volume (including behind the
/// camera). The caller applies widget offsets/size; this performs no occlusion test.
pub fn project_world_to_ui(
    camera: nico_presentation::Camera3d,
    viewport: [f32; 2],
    point: [f32; 3],
) -> Option<[f32; 2]> {
    if viewport.iter().any(|v| !v.is_finite() || *v <= 0.) {
        return None;
    }
    let clip = camera.view_projection(viewport[0] / viewport[1])? * Vec3::from(point).extend(1.);
    if !clip.is_finite()
        || clip.w <= 0.
        || clip.z < 0.
        || clip.z > clip.w
        || clip.x.abs() > clip.w
        || clip.y.abs() > clip.w
    {
        return None;
    }
    let screen = [
        (clip.x / clip.w + 1.) * viewport[0] * 0.5,
        (1. - clip.y / clip.w) * viewport[1] * 0.5,
    ];
    screen.iter().all(|v| v.is_finite()).then_some(screen)
}

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
    fn world_anchors_use_logical_pixels_and_clip_behind_camera() {
        let mut camera = nico_presentation::Camera3d {
            position: [0., 0., 3.],
            orientation: Quaternion::IDENTITY,
            ..Default::default()
        };
        assert_eq!(
            project_world_to_ui(camera, [800., 600.], [0.; 3]),
            Some([400., 300.])
        );
        let above = project_world_to_ui(camera, [800., 600.], [0., 1., 0.]).unwrap();
        assert!(above[1] < 300.);
        camera.position[0] = 1.;
        assert!(project_world_to_ui(camera, [800., 600.], [0.; 3]).unwrap()[0] < 400.);
        for point in [
            [0., 0., 4.],
            [0., 0., 3.],
            [1000., 0., 0.],
            [f32::NAN, 0., 0.],
            [0., 0., -200.],
        ] {
            assert!(project_world_to_ui(camera, [800., 600.], point).is_none());
        }
        assert!(project_world_to_ui(camera, [0., 600.], [0.; 3]).is_none());
    }
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
