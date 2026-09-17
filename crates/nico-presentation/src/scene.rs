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

/// Camera-dependent 2D world snapshot. Extraction order is painter order;
/// callers sort world layers before publication. Units are world units, Y up.
#[derive(Clone, Debug, Default)]
pub struct Scene2d {
    pub camera: Camera2d,
    pub world: Vec<Quad>,
}

/// Screen-space UI/HUD snapshot, drawn after world scenes without depth writes.
/// Coordinates are logical pixels, X right and Y down from the viewport top-left.
/// Extraction order is painter order. Layout, DPI policy, and interaction belong
/// to the caller; world-anchored widgets project into these coordinates.
#[derive(Clone, Debug, Default)]
pub struct UiScene {
    pub quads: Vec<Quad>,
}

/// Right-handed perspective camera with local -Z forward and +Y up.
/// Orientation maps camera-local axes into world space. Clip depth is zero to one.
#[derive(Clone, Copy, Debug)]
pub struct Camera3d {
    pub position: [f32; 3],
    pub orientation: crate::Quaternion,
    pub vertical_fov_radians: f32,
    pub near: f32,
    pub far: f32,
}
impl Camera3d {
    /// The zero-to-one depth transform shared by rendering and visibility queries.
    /// Returns None for invalid parameters or arithmetic overflow.
    pub fn view_projection(&self, aspect: f32) -> Option<glam::Mat4> {
        use glam::{Mat4, Vec3};
        if !self.has_valid_pose()
            || !aspect.is_finite()
            || aspect <= 0.
            || !self.near.is_finite()
            || !self.far.is_finite()
            || self.near <= 0.
            || self.far <= self.near
            || !(0.01..3.13).contains(&self.vertical_fov_radians)
        {
            return None;
        }
        let result = Mat4::perspective_rh(self.vertical_fov_radians, aspect, self.near, self.far)
            * Mat4::from_quat(self.orientation.conjugate())
            * Mat4::from_translation(-Vec3::from(self.position));
        result.is_finite().then_some(result)
    }
    /// Validates the pose, independently of projection parameters. Vertical views
    /// and roll are supported; no fixed world-up vector is reconstructed.
    pub fn has_valid_pose(&self) -> bool {
        self.position.iter().all(|v| v.is_finite())
            && self.orientation.is_finite()
            && self.orientation.is_normalized()
    }
    /// Convenience for target-based controls. Parallel/degenerate forward and up
    /// vectors return None; explicit quaternion poses have no such restriction.
    pub fn looking_at(position: [f32; 3], target: [f32; 3], up: [f32; 3]) -> Option<Self> {
        use glam::{Mat3, Quat, Vec3};
        let position_vector = Vec3::from(position);
        let backward = position_vector - Vec3::from(target);
        let up = Vec3::from(up);
        if !position_vector.is_finite()
            || !backward.is_finite()
            || !up.is_finite()
            || !backward.length_squared().is_finite()
            || backward.length_squared() < 1e-8
            || !up.length_squared().is_finite()
            || up.length_squared() < 1e-8
        {
            return None;
        }
        let backward = backward.normalize();
        let right = up.normalize().cross(backward);
        if right.length_squared() < 1e-8 {
            return None;
        }
        let right = right.normalize();
        let orientation =
            Quat::from_mat3(&Mat3::from_cols(right, backward.cross(right), backward)).normalize();
        Some(Self {
            position,
            orientation,
            vertical_fov_radians: std::f32::consts::FRAC_PI_3,
            near: 0.1,
            far: 100.0,
        })
    }
}
impl Default for Camera3d {
    fn default() -> Self {
        Self::looking_at([2.0, 1.5, 3.0], [0.0; 3], [0.0, 1.0, 0.0])
            .expect("valid default camera pose")
    }
}

/// One mesh instance. Missing mesh/texture uses renderer fallback content.
/// Orientation is a finite unit quaternion. Scale must be positive.
#[derive(Clone, Debug)]
pub struct MeshInstance {
    /// Reverse front-face winding for a reflected model/node transform. Importers
    /// derive this from the node's global determinant, not individual joint bends.
    /// Also controls front/back normal selection for double-sided materials.
    pub mirrored: bool,
    /// None uses the legacy texture/tint as a rough dielectric material.
    pub material: Option<Arc<nico_assets::PbrMaterial>>,
    pub mesh: Option<Arc<Mesh>>,
    /// Model-space joint matrices, then instance transform. Required for skinned
    /// geometry; absent for static geometry. Immutable snapshots own their data.
    pub skin_palette: Option<Arc<Vec<nico_assets::model::Matrix4>>>,
    pub texture: Option<Arc<Texture>>,
    pub position: [f32; 3],
    pub orientation: crate::Quaternion,
    pub scale: f32,
    pub color: [f32; 4],
}

/// Immutable 3D draw snapshot. The host draws Scene2d and then UiScene after it.
#[derive(Clone, Debug, Default)]
pub struct Scene3d {
    pub lighting: SceneLighting,
    pub camera: Camera3d,
    pub meshes: Vec<MeshInstance>,
}

/// One directional light plus diffuse ambient illumination, in linear RGB.
/// Direction points from the surface toward the light and must be a unit vector.
#[derive(Clone, Copy, Debug)]
pub struct SceneLighting {
    pub direction: [f32; 3],
    pub radiance: [f32; 3],
    pub ambient: [f32; 3],
}
impl Default for SceneLighting {
    fn default() -> Self {
        Self {
            direction: [0., 1., 0.],
            radiance: [2.; 3],
            ambient: [0.15; 3],
        }
    }
}
impl SceneLighting {
    pub fn is_valid(&self) -> bool {
        let direction = glam::Vec3::from(self.direction);
        direction.is_finite()
            && (direction.length_squared() - 1.).abs() < 1e-3
            && self
                .radiance
                .iter()
                .chain(&self.ambient)
                .all(|v| v.is_finite() && *v >= 0.)
    }
}
