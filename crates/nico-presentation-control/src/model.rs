//! Shared model geometry and immutable presentation extraction.
//! Loading and animation policy stay with the caller. Node matrices are model-space.
use glam::{Mat4, Vec3};
use nico_assets::{Mesh, MeshVertex, SkinWeights, Texture, model::Model};
use nico_presentation::MeshInstance;
use std::sync::Arc;

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ModelVisualError(pub &'static str);
impl std::fmt::Display for ModelVisualError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.0)
    }
}
impl std::error::Error for ModelVisualError {}

/// Axis-aligned render bounds. These do not replace gameplay collision geometry.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ModelBounds {
    pub min: [f32; 3],
    pub max: [f32; 3],
}
impl ModelBounds {
    /// Conservative frustum test in zero-to-one clip depth. False means every
    /// corner lies outside one common plane. Invalid input stays visible.
    /// Intersections can return false positives, but objects crossing a plane
    /// or surrounding the camera are retained.
    pub fn intersects_clip(&self, view_projection: Mat4) -> bool {
        if !view_projection.is_finite()
            || (0..3).any(|i| {
                !self.min[i].is_finite() || !self.max[i].is_finite() || self.min[i] > self.max[i]
            })
        {
            return true;
        }
        let mut outside = [true; 6];
        for bits in 0..8 {
            let point = Vec3::from_array(std::array::from_fn(|i| {
                if bits & (1 << i) == 0 {
                    self.min[i]
                } else {
                    self.max[i]
                }
            }));
            let p = view_projection * point.extend(1.);
            if !p.is_finite() {
                return true;
            }
            let distances = [p.x + p.w, p.w - p.x, p.y + p.w, p.w - p.y, p.z, p.w - p.z];
            let tolerance = p.abs().max_element().max(1.) * 1e-5;
            for (out, distance) in outside.iter_mut().zip(distances) {
                *out &= distance < -tolerance;
            }
        }
        !outside.into_iter().any(|out| out)
    }
    fn point(point: Vec3) -> Self {
        Self {
            min: point.to_array(),
            max: point.to_array(),
        }
    }
    fn include(&mut self, point: Vec3) {
        self.min = Vec3::from(self.min).min(point).to_array();
        self.max = Vec3::from(self.max).max(point).to_array();
    }
    fn transformed(self, matrix: Mat4) -> Result<Self, ModelVisualError> {
        if !matrix.is_finite() || matrix.row(3) != glam::Vec4::W {
            return Err(ModelVisualError("bounds require finite affine transforms"));
        }
        let mut result = None;
        for bits in 0..8 {
            let point = Vec3::from_array(std::array::from_fn(|i| {
                if bits & (1 << i) == 0 {
                    self.min[i]
                } else {
                    self.max[i]
                }
            }));
            let point = matrix.transform_point3(point);
            if !point.is_finite() {
                return Err(ModelVisualError("render bounds overflow"));
            }
            include(&mut result, point);
        }
        Ok(result.unwrap())
    }
}
fn include(bounds: &mut Option<ModelBounds>, point: Vec3) {
    match bounds {
        Some(bounds) => bounds.include(point),
        None => *bounds = Some(ModelBounds::point(point)),
    }
}

/// Share one visual across instances. Each extraction owns its palette snapshot;
/// mesh and texture allocations remain shared. Supports at most 256 primitives.
pub struct ModelVisual {
    model: Arc<Model>,
    geometry: Vec<Geometry>,
    textures: Vec<Option<Arc<Texture>>>,
    white: Arc<Texture>,
}
impl ModelVisual {
    /// Texture slots use the model texture indices; None selects opaque white.
    pub fn new(
        model: Arc<Model>,
        textures: Vec<Option<Arc<Texture>>>,
    ) -> Result<Self, ModelVisualError> {
        if textures.len() != model.data().textures.len() {
            return Err(ModelVisualError("texture slot count does not match model"));
        }
        Ok(Self {
            geometry: geometry(&model)?,
            model,
            textures,
            white: Arc::new(Texture::rgba8(1, 1, vec![255; 4]).unwrap()),
        })
    }
    pub fn model(&self) -> &Arc<Model> {
        &self.model
    }
    pub fn primitive_count(&self) -> usize {
        self.geometry.len()
    }
    /// Bounds for the supplied pose and instance placement. Import-time influence
    /// boxes contain every vertex with a positive weight for each joint. Their
    /// transformed union contains the convex weighted skin result, including
    /// hierarchy scale/shear. Evaluation is bounded by joints, not vertex count,
    /// and allocates no storage. Only finite affine matrices are supported.
    ///
    /// Includes a relative numerical margin; does not cover future poses, morphs,
    /// or attachments. Recompute after pose changes and union attachment bounds.
    /// On error callers must keep the object visible rather than cull it.
    pub fn bounds(
        &self,
        globals: &[Mat4],
        placement: Mat4,
    ) -> Result<ModelBounds, ModelVisualError> {
        if globals.len() != self.model.data().nodes.len() {
            return Err(ModelVisualError("invalid model node matrices"));
        }
        let data = self.model.data();
        let mut result = None;
        for binding in &self.geometry {
            let skin = data.nodes[binding.node]
                .skin
                .filter(|_| binding.skinned)
                .map(|i| &data.skins[i]);
            for (joint, bounds) in binding.influence_bounds.iter().enumerate() {
                let Some(bounds) = bounds else {
                    continue;
                };
                let matrix = if let Some(skin) = skin {
                    globals[skin.joints[joint]]
                        * Mat4::from_cols_array_2d(&skin.inverse_bind[joint])
                } else {
                    globals[binding.node]
                };
                let bounds = bounds.transformed(placement * matrix)?;
                include(&mut result, bounds.min.into());
                include(&mut result, bounds.max.into());
            }
        }
        let mut result = result.ok_or(ModelVisualError("empty render bounds"))?;
        let magnitude = Vec3::from(result.min)
            .abs()
            .max(Vec3::from(result.max).abs())
            .max(Vec3::ONE);
        let margin = magnitude * 1e-4;
        result.min = (Vec3::from(result.min) - margin).to_array();
        result.max = (Vec3::from(result.max) + margin).to_array();
        if result.min.iter().chain(&result.max).any(|v| !v.is_finite()) {
            return Err(ModelVisualError("render bounds overflow"));
        }
        Ok(result)
    }
    /// Caller must supply this model's node order. Full affine matrices preserve
    /// hierarchy scale/shear; instance placement can be applied to returned draws.
    pub fn meshes(&self, globals: &[Mat4]) -> Result<Vec<MeshInstance>, ModelVisualError> {
        if globals.len() != self.model.data().nodes.len() || globals.iter().any(|m| !m.is_finite())
        {
            return Err(ModelVisualError("invalid model node matrices"));
        }
        let data = self.model.data();
        let mut palettes = std::collections::BTreeMap::new();
        let mut output = Vec::with_capacity(self.geometry.len());
        for binding in &self.geometry {
            let palette = palettes
                .entry((binding.node, binding.skinned))
                .or_insert_with(|| {
                    let node = &data.nodes[binding.node];
                    let matrices = if let Some(skin) = node.skin.filter(|_| binding.skinned) {
                        data.skins[skin]
                            .joints
                            .iter()
                            .zip(&data.skins[skin].inverse_bind)
                            .map(|(&joint, bind)| {
                                (globals[joint] * Mat4::from_cols_array_2d(bind)).to_cols_array_2d()
                            })
                            .collect()
                    } else {
                        vec![globals[binding.node].to_cols_array_2d()]
                    };
                    Arc::new(matrices)
                });
            if palette.iter().flatten().flatten().any(|v| !v.is_finite()) {
                return Err(ModelVisualError("skin palette overflow"));
            }
            let material = binding.material.map(|m| &data.materials[m]);
            let texture = material
                .and_then(|m| m.base_color_texture)
                .and_then(|t| self.textures[t].clone())
                .unwrap_or_else(|| self.white.clone());
            output.push(MeshInstance {
                mesh: Some(binding.mesh.clone()),
                skin_palette: Some(palette.clone()),
                texture: Some(texture),
                position: [0.; 3],
                orientation: glam::Quat::IDENTITY,
                scale: 1.,
                color: material.map_or([1.; 4], |m| m.base_color),
            });
        }
        Ok(output)
    }
}

struct Geometry {
    node: usize,
    skinned: bool,
    mesh: Arc<Mesh>,
    material: Option<usize>,
    influence_bounds: Vec<Option<ModelBounds>>,
}
fn geometry(model: &Model) -> std::result::Result<Vec<Geometry>, ModelVisualError> {
    let data = model.data();
    let mut active = vec![false; data.nodes.len()];
    let mut stack = if let Some(scene) = data.default_scene.or(if data.scenes.is_empty() {
        None
    } else {
        Some(0)
    }) {
        data.scenes[scene].roots.clone()
    } else {
        model
            .parents()
            .iter()
            .enumerate()
            .filter_map(|(i, p)| p.is_none().then_some(i))
            .collect()
    };
    while let Some(i) = stack.pop() {
        active[i] = true;
        stack.extend(&data.nodes[i].children);
    }
    let mut output = Vec::new();
    for (node_index, node) in data.nodes.iter().enumerate() {
        if !active[node_index] {
            continue;
        }
        let Some(mesh) = node.mesh else {
            continue;
        };
        for primitive in &data.meshes[mesh].primitives {
            if output.len() >= 256 {
                return Err(ModelVisualError("model draw limit exceeded"));
            }
            let joint_count = if primitive.skinned {
                data.skins[node.skin.ok_or(ModelVisualError("missing skin"))?]
                    .joints
                    .len()
            } else {
                1
            };
            let vertices = primitive
                .vertices
                .iter()
                .map(|v| MeshVertex {
                    position: v.position,
                    uv: v.uv,
                })
                .collect();
            let skin: Vec<_> = primitive
                .vertices
                .iter()
                .map(|v| {
                    if primitive.skinned {
                        SkinWeights {
                            joints: v.joints,
                            weights: v.weights,
                        }
                    } else {
                        SkinWeights {
                            joints: [0; 4],
                            weights: [1., 0., 0., 0.],
                        }
                    }
                })
                .collect();
            let mut influence_bounds = vec![None; joint_count];
            for (vertex, weights) in primitive.vertices.iter().zip(&skin) {
                for (&joint, &weight) in weights.joints.iter().zip(&weights.weights) {
                    if weight > 0. {
                        include(
                            &mut influence_bounds[joint as usize],
                            vertex.position.into(),
                        );
                    }
                }
            }
            output.push(Geometry {
                node: node_index,
                skinned: primitive.skinned,
                mesh: Arc::new(
                    Mesh::skinned_triangles(vertices, primitive.indices.clone(), skin, joint_count)
                        .ok_or(ModelVisualError("invalid skin geometry"))?,
                ),
                material: primitive.material,
                influence_bounds,
            });
        }
    }
    if output.is_empty() {
        return Err(ModelVisualError(
            "selected scene contains no mesh primitives",
        ));
    }
    Ok(output)
}

#[cfg(test)]
mod tests {
    use super::*;
    use nico_assets::model::*;
    fn fixture() -> Arc<Model> {
        let vertex = |x, y| ModelVertex {
            position: [x, y, 0.],
            normal: [0., 0., 1.],
            uv: [0.; 2],
            joints: [0; 4],
            weights: [1., 0., 0., 0.],
        };
        let data = ModelData {
            nodes: vec![
                Node {
                    name: "mesh".into(),
                    children: vec![],
                    transform: Transform::default(),
                    mesh: Some(0),
                    skin: Some(0),
                },
                Node {
                    name: "joint".into(),
                    children: vec![],
                    transform: Transform::default(),
                    mesh: None,
                    skin: None,
                },
                Node {
                    name: "hidden".into(),
                    children: vec![],
                    transform: Transform::default(),
                    mesh: Some(0),
                    skin: Some(0),
                },
            ],
            meshes: vec![ModelMesh {
                name: "triangle".into(),
                primitives: vec![Primitive {
                    vertices: vec![vertex(0., 0.), vertex(1., 0.), vertex(0., 1.)],
                    indices: vec![0, 1, 2],
                    material: None,
                    skinned: true,
                    has_normals: true,
                }],
            }],
            skins: vec![Skin {
                name: "skin".into(),
                joints: vec![1],
                inverse_bind: vec![IDENTITY],
                skeleton: Some(1),
            }],
            clips: vec![Clip {
                name: "move".into(),
                tracks: vec![Track {
                    node: 1,
                    times: vec![0., 2.],
                    values: TrackValues::Translation(vec![[0.; 3], [2., 0., 0.]]),
                    interpolation: Interpolation::Linear,
                }],
            }],
            scenes: vec![Scene {
                name: "visible".into(),
                roots: vec![0, 1],
            }],
            default_scene: Some(0),
            ..Default::default()
        };
        Arc::new(Model::new(data).unwrap())
    }
    #[test]
    fn immutable_instances_share_geometry_but_keep_palettes_independent() {
        let model = fixture();
        let visual = ModelVisual::new(model.clone(), vec![]).unwrap();
        assert!(Arc::ptr_eq(visual.model(), &model));
        assert_eq!(visual.primitive_count(), 1); // Other scene excluded.
        let mut globals = vec![Mat4::IDENTITY; 3];
        let a = visual.meshes(&globals).unwrap();
        globals[1] = Mat4::from_translation(glam::Vec3::X);
        let b = visual.meshes(&globals).unwrap();
        assert!(Arc::ptr_eq(
            a[0].mesh.as_ref().unwrap(),
            b[0].mesh.as_ref().unwrap()
        ));
        assert_eq!(a[0].skin_palette.as_ref().unwrap()[0], IDENTITY);
        assert_eq!(
            b[0].skin_palette.as_ref().unwrap()[0],
            globals[1].to_cols_array_2d()
        );
        let weak_mesh = Arc::downgrade(a[0].mesh.as_ref().unwrap());
        drop(visual);
        assert!(weak_mesh.upgrade().is_some()); // Published snapshots retain assets.
        drop(a);
        drop(b);
        assert!(weak_mesh.upgrade().is_none());
    }
    #[test]
    fn invalid_matrices_texture_slots_and_palette_overflow_are_rejected() {
        let model = fixture();
        assert!(ModelVisual::new(model.clone(), vec![None]).is_err());
        let visual = ModelVisual::new(model.clone(), vec![]).unwrap();
        assert!(visual.meshes(&[]).is_err());
        assert!(
            visual
                .meshes(&[Mat4::from_translation(glam::Vec3::splat(f32::NAN)); 3])
                .is_err()
        );
        let mut data = model.data().clone();
        data.skins[0].inverse_bind[0] = Mat4::from_scale(glam::Vec3::splat(2.)).to_cols_array_2d();
        let visual = ModelVisual::new(Arc::new(Model::new(data).unwrap()), vec![]).unwrap();
        let mut globals = vec![Mat4::IDENTITY; 3];
        globals[1] = Mat4::from_scale(glam::Vec3::splat(f32::MAX));
        assert!(visual.meshes(&globals).is_err());
    }

    #[test]
    fn animated_bounds_contain_weighted_vertices_with_affine_placement() {
        let mut data = fixture().data().clone();
        data.nodes.push(Node {
            name: "second_joint".into(),
            children: vec![],
            transform: Transform::default(),
            mesh: None,
            skin: None,
        });
        data.skins[0].skeleton = None;
        data.skins[0].joints.push(3);
        data.skins[0]
            .inverse_bind
            .push(Mat4::from_translation(Vec3::new(-0.5, 1., 0.)).to_cols_array_2d());
        for (i, vertex) in data.meshes[0].primitives[0].vertices.iter_mut().enumerate() {
            vertex.joints = [0, 1, 0, 0];
            vertex.weights = match i {
                0 => [0.2, 0.8, 0., 0.],
                1 => [1., 0., 0., 0.],
                _ => [0.4, 0.6005, 0., 0.],
            };
        }
        let mut rigid = data.meshes[0].primitives[0].clone();
        rigid.skinned = false;
        data.meshes.push(ModelMesh {
            name: "rigid".into(),
            primitives: vec![rigid],
        });
        data.nodes.push(Node {
            name: "rigid".into(),
            children: vec![],
            transform: Transform::default(),
            mesh: Some(1),
            skin: None,
        });
        data.scenes[0].roots.push(4);
        let model = Arc::new(Model::new(data).unwrap());
        let visual = ModelVisual::new(model.clone(), vec![]).unwrap();
        for frame in 0..100 {
            let angle = frame as f32 * 0.1;
            let shear = Mat4::from_cols(
                glam::Vec4::X,
                glam::Vec4::new(0.7, 1., 0., 0.),
                glam::Vec4::Z,
                glam::Vec4::W,
            );
            let globals = [
                Mat4::from_translation(Vec3::new(3., -2., 0.)),
                Mat4::from_scale_rotation_translation(
                    Vec3::new(-2., 0.3, 1.),
                    glam::Quat::from_rotation_z(angle),
                    Vec3::new(angle, 2., 1.),
                ),
                Mat4::from_translation(Vec3::splat(10000.)), // Unselected scene must not enlarge bounds.
                shear * Mat4::from_rotation_y(-angle),
                Mat4::from_translation(Vec3::new(3., -2., 0.)),
            ];
            let placement = Mat4::from_scale_rotation_translation(
                Vec3::new(0.8, 2., 0.5),
                glam::Quat::from_rotation_x(0.4),
                Vec3::new(-5., 1., 2.),
            );
            let bounds = visual.bounds(&globals, placement).unwrap();
            assert!(bounds.max.into_iter().all(|v| v < 100.));
            for draw in visual.meshes(&globals).unwrap() {
                let mesh = draw.mesh.unwrap();
                let palette = draw.skin_palette.unwrap();
                for (vertex, skin) in mesh.vertices().iter().zip(mesh.skin().unwrap()) {
                    let point = Vec3::from(vertex.position).extend(1.);
                    let skinned = skin.joints.iter().zip(skin.weights).fold(
                        glam::Vec4::ZERO,
                        |sum, (&j, w)| {
                            sum + Mat4::from_cols_array_2d(&palette[j as usize]) * point * w
                        },
                    );
                    let world = placement * skinned;
                    let point = world.truncate() / world.w;
                    assert!(
                        point.cmpge(bounds.min.into()).all()
                            && point.cmple(bounds.max.into()).all(),
                        "{point:?} outside {bounds:?}"
                    );
                }
            }
        }
    }

    #[test]
    fn bounds_fail_explicitly_on_invalid_or_overflowing_transforms() {
        let visual = ModelVisual::new(fixture(), vec![]).unwrap();
        assert!(visual.bounds(&[], Mat4::IDENTITY).is_err());
        let globals = [Mat4::IDENTITY; 3];
        assert!(
            visual
                .bounds(&globals, Mat4::perspective_rh(1., 1., 0.1, 100.))
                .is_err()
        );
        assert!(
            visual
                .bounds(&globals, Mat4::from_translation(Vec3::splat(f32::NAN)))
                .is_err()
        );
        assert!(
            visual
                .bounds(&globals, Mat4::from_scale(Vec3::splat(f32::MAX)))
                .is_err()
        );
    }

    #[test]
    fn frustum_keeps_crossing_and_enclosing_boxes_and_fails_open() {
        let camera =
            nico_presentation::Camera3d::looking_at([0.; 3], [0., 0., -1.], [0., 1., 0.]).unwrap();
        let matrix = camera.view_projection(1.).unwrap();
        for bounds in [
            ModelBounds {
                min: [-0.1, -0.1, -2.],
                max: [0.1, 0.1, -1.],
            },
            ModelBounds {
                min: [-1000.; 3],
                max: [1000.; 3],
            },
            ModelBounds {
                min: [-0.1, -0.1, -0.2],
                max: [0.1, 0.1, 0.1],
            },
            ModelBounds {
                min: [f32::NAN; 3],
                max: [0.; 3],
            },
            ModelBounds {
                min: [1.; 3],
                max: [-1.; 3],
            },
        ] {
            assert!(bounds.intersects_clip(matrix));
        }
        for bounds in [
            ModelBounds {
                min: [-0.1, -0.1, 1.],
                max: [0.1, 0.1, 2.],
            },
            ModelBounds {
                min: [100., 0., -2.],
                max: [101., 1., -1.],
            },
            ModelBounds {
                min: [-0.1, -0.1, -200.],
                max: [0.1, 0.1, -199.],
            },
            ModelBounds {
                min: [-0.01, -0.01, -0.05],
                max: [0.01, 0.01, -0.01],
            },
        ] {
            assert!(!bounds.intersects_clip(matrix));
            assert!(bounds.intersects_clip(Mat4::from_cols_array(&[f32::NAN; 16])));
        }
    }
}
