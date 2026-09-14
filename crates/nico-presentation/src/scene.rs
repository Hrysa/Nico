use nico_assets::{Mesh, Texture};
use std::sync::Arc;

/// World X points right, Y up. Camera center maps to the viewport center.
#[derive(Clone, Copy, Debug)]
pub struct Camera2d {
    pub center: [f32; 2],
    pub pixels_per_unit: f32,
}
impl Default for Camera2d {
    fn default() -> Self {
        Self {
            center: [0.0; 2],
            pixels_per_unit: 128.0,
        }
    }
}

/// Centered axis-aligned textured quad. Color is linear RGBA; UV origin is top-left.
/// Missing content uses the renderer's fallback. An Arc pins immutable CPU content.
#[derive(Clone, Debug)]
pub struct Quad {
    pub center: [f32; 2],
    pub size: [f32; 2],
    pub color: [f32; 4],
    pub texture: Option<Arc<Texture>>,
}

/// Game-published render snapshot. World quads draw in list order, then HUD quads.
/// HUD units are logical pixels, with X right and Y down from the viewport top-left.
/// This is a drawing boundary, not a UI layout or interaction system.
#[derive(Clone, Debug, Default)]
pub struct Scene2d {
    pub camera: Camera2d,
    pub world: Vec<Quad>,
    pub hud: Vec<Quad>,
}

/// Right-handed perspective camera; Y is up and clip depth is zero to one.
#[derive(Clone, Copy, Debug)]
pub struct Camera3d {
    pub position: [f32; 3],
    pub target: [f32; 3],
    pub vertical_fov_radians: f32,
    pub near: f32,
    pub far: f32,
}
impl Camera3d {
    /// Validates the stored f32 view direction, including the fixed Y-up singularity.
    /// Callers must also validate projection and target-specific constraints.
    pub fn has_valid_view_direction(&self) -> bool {
        if self
            .position
            .iter()
            .chain(&self.target)
            .any(|v| !v.is_finite())
        {
            return false;
        }
        let [x, y, z] = std::array::from_fn(|i| self.target[i] - self.position[i]);
        let length_squared = x * x + y * y + z * z;
        length_squared.is_finite()
            && length_squared >= 1e-8
            && (x * x + z * z) / length_squared >= 1e-8
    }
}
impl Default for Camera3d {
    fn default() -> Self {
        Self {
            position: [2.0, 1.5, 3.0],
            target: [0.0; 3],
            vertical_fov_radians: std::f32::consts::FRAC_PI_3,
            near: 0.1,
            far: 100.0,
        }
    }
}

/// One static mesh instance. Missing mesh/texture uses renderer fallback content.
/// Rotation is around world Y, in radians. Scale must be positive.
#[derive(Clone, Debug)]
pub struct MeshInstance {
    pub mesh: Option<Arc<Mesh>>,
    pub texture: Option<Arc<Texture>>,
    pub position: [f32; 3],
    pub yaw_radians: f32,
    pub scale: f32,
    pub color: [f32; 4],
}

/// Immutable 3D draw snapshot. The host draws Scene2d after this scene for HUD use.
#[derive(Clone, Debug, Default)]
pub struct Scene3d {
    pub camera: Camera3d,
    pub meshes: Vec<MeshInstance>,
}
