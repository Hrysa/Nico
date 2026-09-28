//! Engine-owned authored components. These contain data and no host or rendering state.
use crate::SceneComponent;
use serde::{Deserialize, Serialize};
use std::io;
fn require(ok: bool, field: &str) -> io::Result<()> {
    if ok {
        Ok(())
    } else {
        Err(io::Error::other(format!("invalid {field}")))
    }
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Transform {
    pub position: [f64; 3],
    #[serde(default)]
    pub rotation_radians: [f32; 3],
    #[serde(default = "unit")]
    pub scale: f32,
}
fn unit() -> f32 {
    1.
}
impl Default for Transform {
    fn default() -> Self {
        Self {
            position: [0.; 3],
            rotation_radians: [0.; 3],
            scale: 1.,
        }
    }
}
impl SceneComponent for Transform {
    fn validate(&self) -> io::Result<()> {
        require(
            self.position
                .iter()
                .all(|v| v.is_finite() && v.abs() <= 10000.)
                && self.rotation_radians.iter().all(|v| v.is_finite())
                && self.scale.is_finite()
                && (0.001..=1000.).contains(&self.scale),
            "transform",
        )
    }
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Camera {
    pub vertical_fov_radians: f32,
    pub near: f32,
    pub far: f32,
    pub active: bool,
}
impl SceneComponent for Camera {
    fn validate(&self) -> io::Result<()> {
        require(
            self.vertical_fov_radians.is_finite()
                && (0.01..3.13).contains(&self.vertical_fov_radians)
                && self.near.is_finite()
                && self.far.is_finite()
                && self.near > 0.
                && self.far > self.near,
            "camera",
        )
    }
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct OrbitController {
    pub sensitivity: [f32; 2],
    pub pitch_limits: [f32; 2],
    pub distance: f32,
    pub minimum_distance: f32,
    pub radius: f32,
    pub collision_margin: f32,
    pub restoration_rate: f32,
    pub maximum_delta: f32,
    pub yaw: f32,
    pub pitch: f32,
}
impl SceneComponent for OrbitController {
    fn validate(&self) -> io::Result<()> {
        let values = [
            self.distance,
            self.minimum_distance,
            self.radius,
            self.collision_margin,
            self.restoration_rate,
            self.maximum_delta,
            self.yaw,
            self.pitch,
        ];
        require(
            values
                .iter()
                .chain(self.sensitivity.iter())
                .chain(self.pitch_limits.iter())
                .all(|v| v.is_finite())
                && self.distance > 0.
                && self.minimum_distance > 0.
                && self.distance >= self.minimum_distance
                && self.radius > 0.
                && self.collision_margin >= 0.
                && self.restoration_rate > 0.
                && self.maximum_delta > 0.
                && self.pitch_limits[0] <= self.pitch
                && self.pitch <= self.pitch_limits[1]
                && self.pitch_limits[0] >= -1.55
                && self.pitch_limits[1] <= 1.55,
            "orbit controller",
        )
    }
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct DirectionalLight {
    pub direction: [f32; 3],
    pub radiance: [f32; 3],
}
impl SceneComponent for DirectionalLight {
    fn validate(&self) -> io::Result<()> {
        require(
            self.direction
                .iter()
                .map(|v| v * v)
                .sum::<f32>()
                .is_finite()
                && self.direction.iter().map(|v| v * v).sum::<f32>() > 1e-8
                && self.radiance.iter().all(|v| v.is_finite() && *v >= 0.),
            "directional light",
        )
    }
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct AmbientLight {
    pub radiance: [f32; 3],
}
impl SceneComponent for AmbientLight {
    fn validate(&self) -> io::Result<()> {
        require(
            self.radiance.iter().all(|v| v.is_finite() && *v >= 0.),
            "ambient light",
        )
    }
}
pub fn register(registry: &mut crate::ComponentRegistry) -> io::Result<()> {
    use crate::ComponentScope::{Client, Shared};
    registry.register::<Transform>("nico.transform", Shared)?;
    registry.register::<Camera>("nico.camera", Client)?;
    registry.register::<OrbitController>("nico.orbit", Client)?;
    registry.register::<DirectionalLight>("nico.directional_light", Client)?;
    registry.register::<AmbientLight>("nico.ambient_light", Client)
}
