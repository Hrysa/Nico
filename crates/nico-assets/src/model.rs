//! Validated, immutable model bundles. No runtime, GPU, or source-format types.
use std::collections::BTreeSet;

pub type Matrix4 = [[f32; 4]; 4];
pub const IDENTITY: Matrix4 = [
    [1., 0., 0., 0.],
    [0., 1., 0., 0.],
    [0., 0., 1., 0.],
    [0., 0., 0., 1.],
];

/// Right-handed, Y-up TRS; rotations are unit quaternions in XYZW order.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Transform {
    pub translation: [f32; 3],
    pub rotation: [f32; 4],
    pub scale: [f32; 3],
}
impl Default for Transform {
    fn default() -> Self {
        Self {
            translation: [0.; 3],
            rotation: [0., 0., 0., 1.],
            scale: [1.; 3],
        }
    }
}
impl Transform {
    #[must_use]
    pub fn is_valid(&self) -> bool {
        self.translation.iter().all(|v| v.is_finite())
            && unit(self.rotation)
            && self.scale.iter().all(|v| v.is_finite() && v.abs() > 1e-8)
    }
}
fn unit(q: [f32; 4]) -> bool {
    q.iter().all(|v| v.is_finite()) && (q.iter().map(|v| v * v).sum::<f32>() - 1.).abs() < 1e-3
}

#[derive(Clone, Debug)]
pub struct Node {
    pub name: String,
    pub children: Vec<usize>,
    pub transform: Transform,
    pub mesh: Option<usize>,
    pub skin: Option<usize>,
}
#[derive(Clone, Copy, Debug)]
pub struct ModelVertex {
    pub position: [f32; 3],
    pub normal: [f32; 3],
    pub uv: [f32; 2],
    pub joints: [u16; 4],
    pub weights: [f32; 4],
}
#[derive(Clone, Debug)]
pub struct Primitive {
    pub vertices: Vec<ModelVertex>,
    pub indices: Vec<u32>,
    pub material: Option<usize>,
    pub skinned: bool,
    pub has_normals: bool,
}
#[derive(Clone, Debug)]
pub struct ModelMesh {
    pub name: String,
    pub primitives: Vec<Primitive>,
}
#[derive(Clone, Debug)]
pub struct Skin {
    pub name: String,
    pub joints: Vec<usize>,
    pub inverse_bind: Vec<Matrix4>,
    pub skeleton: Option<usize>,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Interpolation {
    Step,
    Linear,
}
#[derive(Clone, Debug)]
pub enum TrackValues {
    Translation(Vec<[f32; 3]>),
    Rotation(Vec<[f32; 4]>),
    Scale(Vec<[f32; 3]>),
}
#[derive(Clone, Debug)]
pub struct Track {
    pub node: usize,
    pub times: Vec<f32>,
    pub values: TrackValues,
    pub interpolation: Interpolation,
}
#[derive(Clone, Debug)]
pub struct Clip {
    pub name: String,
    pub tracks: Vec<Track>,
}
impl Clip {
    #[must_use]
    pub fn time_range(&self) -> (f32, f32) {
        self.tracks
            .iter()
            .fold((f32::INFINITY, f32::NEG_INFINITY), |(a, b), t| {
                (
                    a.min(t.times.first().copied().unwrap_or(0.)),
                    b.max(t.times.last().copied().unwrap_or(0.)),
                )
            })
    }
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ImageEncoding {
    Png,
    Jpeg,
}
/// Encoded bytes, not decoded pixels. Image decoding may subsequently fail.
#[derive(Clone, Debug)]
pub struct ModelImage {
    pub name: String,
    pub encoding: ImageEncoding,
    pub bytes: Vec<u8>,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum WrapMode {
    Clamp,
    Mirror,
    Repeat,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Filter {
    Nearest,
    Linear,
    NearestMipmapNearest,
    LinearMipmapNearest,
    NearestMipmapLinear,
    LinearMipmapLinear,
}
#[derive(Clone, Debug)]
pub struct ModelTexture {
    pub image: usize,
    pub wrap_s: WrapMode,
    pub wrap_t: WrapMode,
    pub min_filter: Option<Filter>,
    pub mag_filter: Option<Filter>,
}
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AlphaMode {
    Opaque,
    Mask,
    Blend,
}
#[derive(Clone, Debug)]
pub struct Material {
    pub name: String,
    pub base_color: [f32; 4],
    pub metallic: f32,
    pub roughness: f32,
    pub base_color_texture: Option<usize>,
    pub metallic_roughness_texture: Option<usize>,
    pub normal_texture: Option<usize>,
    pub normal_scale: f32,
    pub occlusion_texture: Option<usize>,
    pub occlusion_strength: f32,
    pub emissive_texture: Option<usize>,
    pub emissive: [f32; 3],
    pub alpha: AlphaMode,
    pub alpha_cutoff: f32,
    pub double_sided: bool,
}
impl Material {
    /// Base color, metallic/roughness, normal, occlusion, and emissive slots.
    pub fn texture_indices(&self) -> [Option<usize>; 5] {
        [
            self.base_color_texture,
            self.metallic_roughness_texture,
            self.normal_texture,
            self.occlusion_texture,
            self.emissive_texture,
        ]
    }
}
#[derive(Clone, Debug)]
pub struct Scene {
    pub name: String,
    pub roots: Vec<usize>,
}
/// Public builder data lets userland importers construct the same validated bundle.
#[derive(Clone, Debug, Default)]
pub struct ModelData {
    pub nodes: Vec<Node>,
    pub meshes: Vec<ModelMesh>,
    pub skins: Vec<Skin>,
    pub clips: Vec<Clip>,
    pub materials: Vec<Material>,
    pub images: Vec<ModelImage>,
    pub textures: Vec<ModelTexture>,
    pub scenes: Vec<Scene>,
    pub default_scene: Option<usize>,
    /// Optional extensions explicitly omitted by the importer, never silently hidden.
    pub omitted_extensions: Vec<String>,
}
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ModelError {
    pub code: &'static str,
    pub index: usize,
}
impl std::fmt::Display for ModelError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{} at {}", self.code, self.index)
    }
}
impl std::error::Error for ModelError {}
#[derive(Debug)]
pub struct Model {
    data: ModelData,
    parents: Vec<Option<usize>>,
    order: Vec<usize>,
}
impl Model {
    pub fn new(data: ModelData) -> Result<Self, ModelError> {
        let err = |code, index| ModelError { code, index };
        let mut parents = vec![None; data.nodes.len()];
        for (i, n) in data.nodes.iter().enumerate() {
            if !n.transform.is_valid() {
                return Err(err("node_transform", i));
            }
            if n.mesh.is_some_and(|m| m >= data.meshes.len())
                || n.skin.is_some_and(|s| s >= data.skins.len())
                || (n.skin.is_some() && n.mesh.is_none())
            {
                return Err(err("node_binding", i));
            }
            for &c in &n.children {
                if c >= data.nodes.len() || c == i || parents[c].replace(i).is_some() {
                    return Err(err("node_parent", i));
                }
            }
        }
        let mut order: Vec<_> = parents
            .iter()
            .enumerate()
            .filter_map(|(i, p)| p.is_none().then_some(i))
            .collect();
        let mut cursor = 0;
        while cursor < order.len() {
            let node = order[cursor];
            order.extend_from_slice(&data.nodes[node].children);
            cursor += 1;
        }
        if order.len() != data.nodes.len() {
            return Err(err("node_cycle", 0));
        }
        for (i, scene) in data.scenes.iter().enumerate() {
            let mut roots = BTreeSet::new();
            if scene
                .roots
                .iter()
                .any(|r| *r >= parents.len() || parents[*r].is_some() || !roots.insert(*r))
            {
                return Err(err("scene_root", i));
            }
        }
        if data.default_scene.is_some_and(|s| s >= data.scenes.len()) {
            return Err(err("default_scene", 0));
        }
        for (i, s) in data.skins.iter().enumerate() {
            let mut unique = BTreeSet::new();
            if s.joints.is_empty()
                || s.joints.len() != s.inverse_bind.len()
                || s.skeleton.is_some_and(|n| n >= data.nodes.len())
                || s.joints
                    .iter()
                    .any(|j| *j >= data.nodes.len() || !unique.insert(*j))
            {
                return Err(err("skin_joints", i));
            }
            for m in &s.inverse_bind {
                let det = m[0][0] * (m[1][1] * m[2][2] - m[2][1] * m[1][2])
                    - m[1][0] * (m[0][1] * m[2][2] - m[2][1] * m[0][2])
                    + m[2][0] * (m[0][1] * m[1][2] - m[1][1] * m[0][2]);
                if !m.iter().flatten().all(|x| x.is_finite())
                    || !det.is_finite()
                    || det.abs() < 1e-20
                    || m[0][3] != 0.
                    || m[1][3] != 0.
                    || m[2][3] != 0.
                    || (m[3][3] - 1.).abs() > 1e-4
                {
                    return Err(err("inverse_bind", i));
                }
            }
            if let Some(root) = s.skeleton {
                for &j in &s.joints {
                    let mut current = Some(j);
                    while current.is_some_and(|n| n != root) {
                        current = parents[current.unwrap()];
                    }
                    if current.is_none() {
                        return Err(err("skeleton_ancestor", i));
                    }
                }
            }
        }
        for (i, m) in data.meshes.iter().enumerate() {
            if m.primitives.is_empty() {
                return Err(err("empty_mesh", i));
            }
            for p in &m.primitives {
                if p.vertices.is_empty()
                    || p.indices.is_empty()
                    || !p.indices.len().is_multiple_of(3)
                    || p.indices.iter().any(|j| *j as usize >= p.vertices.len())
                    || p.material.is_some_and(|j| j >= data.materials.len())
                {
                    return Err(err("primitive", i));
                }
                for v in &p.vertices {
                    if !v
                        .position
                        .iter()
                        .chain(&v.normal)
                        .chain(&v.uv)
                        .all(|x| x.is_finite())
                        || !v.weights.iter().all(|w| w.is_finite() && *w >= 0.)
                        || (p.skinned && (v.weights.iter().sum::<f32>() - 1.).abs() > 1e-3)
                    {
                        return Err(err("vertex", i));
                    }
                }
            }
        }
        for (i, n) in data.nodes.iter().enumerate() {
            if let Some(mesh) = n.mesh {
                for p in &data.meshes[mesh].primitives {
                    if p.skinned != n.skin.is_some() {
                        return Err(err("skin_attribute_binding", i));
                    }
                    if let Some(s) = n.skin
                        && p.vertices.iter().any(|v| {
                            v.joints
                                .iter()
                                .any(|j| *j as usize >= data.skins[s].joints.len())
                        })
                    {
                        return Err(err("vertex_joint", i));
                    }
                }
            }
        }
        for (i, c) in data.clips.iter().enumerate() {
            if c.tracks.is_empty() {
                return Err(err("empty_clip", i));
            }
            let mut seen = BTreeSet::new();
            for t in &c.tracks {
                if t.node >= data.nodes.len()
                    || t.times.is_empty()
                    || t.times.iter().any(|x| !x.is_finite() || *x < 0.)
                    || t.times.windows(2).any(|w| w[1] <= w[0])
                {
                    return Err(err("track_time", i));
                }
                let (kind, len, valid) = match &t.values {
                    TrackValues::Translation(v) => {
                        (0, v.len(), v.iter().flatten().all(|x| x.is_finite()))
                    }
                    TrackValues::Rotation(v) => (1, v.len(), v.iter().all(|q| unit(*q))),
                    TrackValues::Scale(v) => (
                        2,
                        v.len(),
                        v.iter().flatten().all(|x| x.is_finite() && *x > 1e-8),
                    ),
                };
                if len != t.times.len() || !valid || !seen.insert((t.node, kind)) {
                    return Err(err("track_values", i));
                }
            }
        }
        for (i, t) in data.textures.iter().enumerate() {
            if t.image >= data.images.len() {
                return Err(err("texture_image", i));
            }
        }
        for (i, image) in data.images.iter().enumerate() {
            if image.bytes.is_empty() {
                return Err(err("empty_image", i));
            }
        }
        for (i, m) in data.materials.iter().enumerate() {
            if [
                m.base_color_texture,
                m.metallic_roughness_texture,
                m.normal_texture,
                m.occlusion_texture,
                m.emissive_texture,
            ]
            .iter()
            .flatten()
            .any(|t| *t >= data.textures.len())
                || m.base_color
                    .iter()
                    .chain(&m.emissive)
                    .any(|v| !v.is_finite() || !(0. ..=1.).contains(v))
                || [m.metallic, m.roughness, m.occlusion_strength]
                    .iter()
                    .any(|v| !v.is_finite() || !(0. ..=1.).contains(v))
                || !m.normal_scale.is_finite()
                || !m.alpha_cutoff.is_finite()
                || m.alpha_cutoff < 0.
            {
                return Err(err("material", i));
            }
        }
        Ok(Self {
            data,
            parents,
            order,
        })
    }
    #[must_use]
    pub const fn data(&self) -> &ModelData {
        &self.data
    }
    #[must_use]
    pub fn parents(&self) -> &[Option<usize>] {
        &self.parents
    }
    #[must_use]
    pub fn traversal(&self) -> &[usize] {
        &self.order
    }
}
