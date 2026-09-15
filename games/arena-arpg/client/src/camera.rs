use nico_presentation::Camera3d;
use nico_presentation_control::camera::{OrbitCamera, OrbitSettings};

/// Arena-specific tuning and target selection; engine owns controller mechanics.
#[derive(Clone, Debug)]
pub struct Camera {
    pub rig: OrbitCamera,
}
impl Default for Camera {
    fn default() -> Self {
        Self {
            rig: OrbitCamera::new(
                OrbitSettings {
                    sensitivity: [-0.003, 0.003],
                    pitch_limits: [0.15, 1.1],
                    distance: 7.5,
                    minimum_distance: 0.15,
                    radius: 0.2,
                    collision_margin: 0.02,
                    restoration_rate: 10.0,
                    maximum_delta: 0.1,
                    vertical_fov_radians: 1.0,
                    near: 0.05,
                    far: 80.0,
                },
                0.0,
                0.48,
            )
            .expect("valid arena camera settings"),
        }
    }
}
impl Camera {
    pub fn orbit(&mut self, delta: [f32; 2]) {
        self.rig.orbit(delta);
    }
    pub fn view(&mut self, hero: [f32; 2], delta: f32) -> Camera3d {
        self.rig.view([hero[0], 1.2, hero[1]], delta, |sweep| {
            arena_arpg_shared::geometry::WALLS
                .into_iter()
                .filter_map(|wall| {
                    let [min, max] = wall.bounds();
                    sweep.cast_aabb(min, max)
                })
                .reduce(f32::min)
        })
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn arena_camera_uses_configured_angles_and_hero_offset() {
        let mut camera = Camera::default();
        camera.orbit([10000.0, -10000.0]);
        assert_eq!(camera.rig.pitch(), 0.15);
        assert!(camera.rig.yaw().abs() <= std::f32::consts::PI);
        let view = camera.view([2.0, 3.0], 0.016);
        let forward = view
            .orientation
            .mul_vec3([0.0, 0.0, -1.0].into())
            .to_array();
        for i in 0..3 {
            assert!(
                (view.position[i] + forward[i] * camera.rig.distance() - [2.0, 1.2, 3.0][i]).abs()
                    < 1e-5
            );
        }
        assert!(view.has_valid_pose());
    }
}
