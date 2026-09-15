//! Client-side camera controllers producing immutable presentation cameras.
//! Callers own targets, tuning, input bindings, and scene geometry. No runtime,
//! window provider, or game rules are required.
use nico_presentation::{Camera3d, Quaternion};

/// Game-authored orbit and collision tuning. Angles are radians, distances are
/// world units, and restoration rate is inverse seconds.
#[derive(Clone, Copy, Debug)]
pub struct OrbitSettings {
    pub sensitivity: [f32; 2],
    pub pitch_limits: [f32; 2],
    pub distance: f32,
    pub minimum_distance: f32,
    pub radius: f32,
    pub collision_margin: f32,
    pub restoration_rate: f32,
    pub maximum_delta: f32,
    pub vertical_fov_radians: f32,
    pub near: f32,
    pub far: f32,
}

/// Camera collision query, shared with headless spatial consumers.
pub use nico_spatial::SphereSweep as BoomSweep;

/// Follows the supplied pivot immediately. Obstructions shorten the boom
/// immediately; only restoration is smoothed, avoiding motion through walls.
#[derive(Clone, Debug)]
pub struct OrbitCamera {
    settings: OrbitSettings,
    yaw: f32,
    pitch: f32,
    distance: f32,
}
impl OrbitCamera {
    pub fn new(settings: OrbitSettings, yaw: f32, pitch: f32) -> Result<Self, &'static str> {
        let s = settings;
        if !s
            .sensitivity
            .into_iter()
            .chain(s.pitch_limits)
            .chain([
                s.distance,
                s.minimum_distance,
                s.radius,
                s.collision_margin,
                s.restoration_rate,
                s.maximum_delta,
                s.vertical_fov_radians,
                s.near,
                s.far,
                yaw,
                pitch,
            ])
            .all(f32::is_finite)
            || s.pitch_limits[0] > s.pitch_limits[1]
            || s.minimum_distance < 0.001
            || s.distance < s.minimum_distance
            || s.radius < 0.0
            || s.collision_margin < 0.0
            || s.restoration_rate < 0.0
            || s.maximum_delta <= 0.0
            || s.near <= 0.0
            || s.far <= s.near
            || s.vertical_fov_radians <= 0.0
            || s.vertical_fov_radians >= std::f32::consts::PI
        {
            return Err("invalid_camera_settings");
        }
        let mut camera = Self {
            settings,
            yaw,
            pitch,
            distance: s.distance,
        };
        camera.set_angles(yaw, pitch);
        Ok(camera)
    }
    pub fn yaw(&self) -> f32 {
        self.yaw
    }
    pub fn pitch(&self) -> f32 {
        self.pitch
    }
    pub fn distance(&self) -> f32 {
        self.distance
    }
    /// Nonfinite input is ignored. Finite angles are wrapped/clamped to the tuning.
    pub fn set_angles(&mut self, yaw: f32, pitch: f32) {
        if yaw.is_finite() && pitch.is_finite() {
            self.yaw = (yaw + std::f32::consts::PI).rem_euclid(std::f32::consts::TAU)
                - std::f32::consts::PI;
            self.pitch = pitch.clamp(self.settings.pitch_limits[0], self.settings.pitch_limits[1]);
        }
    }
    pub fn orbit(&mut self, delta: [f32; 2]) {
        self.set_angles(
            self.yaw + delta[0] * self.settings.sensitivity[0],
            self.pitch + delta[1] * self.settings.sensitivity[1],
        );
    }
    /// Pivot must be finite. The callback receives the full desired boom every
    /// frame, allowing restoration after an obstruction disappears. Invalid hit
    /// distances are ignored. Minimum distance can overlap geometry near the pivot.
    pub fn view(
        &mut self,
        pivot: [f32; 3],
        delta: f32,
        query: impl FnOnce(BoomSweep) -> Option<f32>,
    ) -> Camera3d {
        let s = self.settings;
        // Yaw/pitch are orbit-control coordinates, not the published pose.
        // Camera local +Z points back along the boom; local -Z looks at the pivot.
        let orientation = (Quaternion::from_rotation_y(self.yaw + std::f32::consts::PI)
            * Quaternion::from_rotation_x(-self.pitch))
        .normalize();
        let direction = orientation.mul_vec3([0.0, 0.0, 1.0].into()).to_array();
        let hit = query(BoomSweep {
            origin: pivot,
            direction,
            distance: s.distance,
            radius: s.radius,
        });
        let limit = hit
            .filter(|v| v.is_finite() && *v >= 0.0 && *v <= s.distance)
            .map_or(s.distance, |v| {
                (v - s.collision_margin).max(s.minimum_distance)
            });
        if limit < self.distance {
            self.distance = limit;
        } else {
            let dt = if delta.is_finite() {
                delta.clamp(0.0, s.maximum_delta)
            } else {
                0.0
            };
            self.distance += (limit - self.distance) * (1.0 - (-s.restoration_rate * dt).exp());
        }
        Camera3d {
            position: std::array::from_fn(|i| pivot[i] + direction[i] * self.distance),
            orientation,
            vertical_fov_radians: s.vertical_fov_radians,
            near: s.near,
            far: s.far,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn settings() -> OrbitSettings {
        OrbitSettings {
            sensitivity: [-0.01, 0.01],
            pitch_limits: [-1.0, 1.0],
            distance: 8.0,
            minimum_distance: 0.1,
            radius: 0.2,
            collision_margin: 0.02,
            restoration_rate: 10.0,
            maximum_delta: 0.1,
            vertical_fov_radians: 1.0,
            near: 0.05,
            far: 100.0,
        }
    }
    #[test]
    fn controller_shortens_at_a_wall_and_restores_without_overshoot() {
        let mut camera = OrbitCamera::new(settings(), 0.0, 0.0).unwrap();
        let view = camera.view([0.0, 1.0, 0.0], 0.016, |sweep| {
            sweep.cast_aabb([-2.0, 0.0, -3.1], [2.0, 4.0, -3.0])
        });
        assert!((camera.distance() - 2.78).abs() < 1e-5);
        assert!((view.position[2] + 2.78).abs() < 1e-5);
        assert!(view.has_valid_pose());
        let shortened = camera.distance();
        camera.view([4.0, 2.0, 0.0], 0.016, |sweep| {
            assert_eq!(sweep.distance, 8.0);
            sweep.cast_aabb([-2.0, 0.0, -3.1], [2.0, 4.0, -3.0])
        });
        assert!(camera.distance() > shortened && camera.distance() < 8.0);
        let restored = camera.distance();
        camera.view([4.0, 2.0, 0.0], 0.0, |_| None);
        assert_eq!(camera.distance(), restored);
        camera.view([4.0, 2.0, 0.0], 0.016, |_| Some(1.0));
        assert!((camera.distance() - 0.98).abs() < 1e-5);
    }
    #[test]
    fn orbit_uses_caller_tuning_and_ignores_nonfinite_input() {
        let mut camera = OrbitCamera::new(settings(), 0.0, 0.0).unwrap();
        camera.orbit([10000.0, 10000.0]);
        assert!(camera.yaw().abs() <= std::f32::consts::PI);
        assert_eq!(camera.pitch(), 1.0);
        let yaw = camera.yaw();
        camera.orbit([f32::NAN, 0.0]);
        assert_eq!(camera.yaw(), yaw);
        let view = camera.view([3.0, 4.0, 5.0], 0.016, |_| Some(f32::NAN));
        let forward = view
            .orientation
            .mul_vec3([0.0, 0.0, -1.0].into())
            .to_array();
        for i in 0..3 {
            assert!(
                (view.position[i] + forward[i] * camera.distance() - [3.0, 4.0, 5.0][i]).abs()
                    < 1e-5
            );
        }
        assert!(view.has_valid_pose());
        assert_eq!(camera.distance(), 8.0);
    }
    #[test]
    fn orbit_poles_produce_valid_quaternion_poses() {
        let mut s = settings();
        s.pitch_limits = [-std::f32::consts::FRAC_PI_2, std::f32::consts::FRAC_PI_2];
        for pitch in s.pitch_limits {
            let mut camera = OrbitCamera::new(s, 0.7, pitch).unwrap();
            let view = camera.view([0.0; 3], 0.016, |_| None);
            assert!(view.has_valid_pose());
            let forward = view.orientation.mul_vec3([0.0, 0.0, -1.0].into());
            assert!(forward.x.abs() < 1e-5 && forward.z.abs() < 1e-5);
            assert!((forward.y.abs() - 1.0).abs() < 1e-5);
        }
    }
    #[test]
    fn invalid_projection_and_boom_settings_are_rejected() {
        let mut s = settings();
        s.near = 0.0;
        assert!(OrbitCamera::new(s, 0.0, 0.0).is_err());
        s = settings();
        s.minimum_distance = 9.0;
        assert!(OrbitCamera::new(s, 0.0, 0.0).is_err());
        s = settings();
        s.pitch_limits = [1.0, -1.0];
        assert!(OrbitCamera::new(s, 0.0, 0.0).is_err());
        s = settings();
        s.radius = f32::INFINITY;
        assert!(OrbitCamera::new(s, 0.0, 0.0).is_err());
    }
}
