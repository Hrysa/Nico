use crate::{
    import::{AssetImporter, ImportContext, ImportError, ImportErrorKind, ImporterDescriptor},
    model::*,
};
use gltf::accessor::{DataType as D, Dimensions as Dim};

#[cfg(test)]
#[path = "model_tests.rs"]
mod tests;

#[cfg_attr(feature = "import-cache", derive(serde::Serialize))]
#[derive(Clone, Debug)]
pub struct ModelGlbSettings {
    pub max_nodes: usize,
    pub max_objects: usize,
    pub max_joints_per_skin: usize,
    pub max_vertices: usize,
    pub max_indices: usize,
    pub max_tracks: usize,
    pub max_keyframes: usize,
    pub max_json_bytes: usize,
    /// Permit core material fallback for optional specular/IOR extensions; omissions
    /// remain listed in the imported bundle. Required extensions are always rejected.
    pub allow_material_fallback: bool,
}
impl Default for ModelGlbSettings {
    fn default() -> Self {
        Self {
            max_nodes: 4096,
            max_objects: 8192,
            max_joints_per_skin: 256,
            max_vertices: 1_000_000,
            max_indices: 3_000_000,
            max_tracks: 4096,
            max_keyframes: 2_000_000,
            max_json_bytes: 8 * 1024 * 1024,
            allow_material_fallback: false,
        }
    }
}
pub struct ModelGlbImporter;
fn malformed(code: &str) -> ImportError {
    ImportError::new(ImportErrorKind::Malformed, code, code)
}
fn unsupported(code: &str) -> ImportError {
    ImportError::new(ImportErrorKind::Unsupported, code, code)
}
fn limit() -> ImportError {
    ImportError::new(
        ImportErrorKind::LimitExceeded,
        "model_limit",
        "model import limit exceeded",
    )
}
fn claim<T>(c: &mut ImportContext<'_>, n: usize) -> Result<(), ImportError> {
    c.claim_decoded(n.checked_mul(std::mem::size_of::<T>()).ok_or_else(limit)?)
}
fn name(c: &mut ImportContext<'_>, s: Option<&str>) -> Result<String, ImportError> {
    let s = s.unwrap_or("");
    c.claim_decoded(s.len())?;
    Ok(s.into())
}
fn shape(
    a: &gltf::Accessor<'_>,
    types: &[D],
    dimensions: Dim,
    normalized: bool,
) -> Result<(), ImportError> {
    if !types.contains(&a.data_type())
        || a.dimensions() != dimensions
        || a.normalized() != normalized
    {
        return Err(unsupported("accessor_format"));
    }
    Ok(())
}
fn count(total: &mut usize, n: usize, max: usize) -> Result<(), ImportError> {
    *total = total.checked_add(n).ok_or_else(limit)?;
    if *total > max {
        return Err(limit());
    }
    Ok(())
}

impl AssetImporter for ModelGlbImporter {
    fn cache_settings(&self, s: &Self::Settings) -> Result<Option<Vec<u8>>, ImportError> {
        crate::cache::encode(&("model-v1", s)).map(Some)
    }
    fn cache_encode(&self, value: &Self::Output) -> Result<Vec<u8>, ImportError> {
        crate::cache::encode(value.data())
    }
    fn cache_decode(&self, bytes: &[u8], _s: &Self::Settings) -> Result<Self::Output, ImportError> {
        Model::new(crate::cache::decode(bytes)?).map_err(|_| malformed("cache_model"))
    }

    type Output = Model;
    type Settings = ModelGlbSettings;
    fn descriptor(&self) -> ImporterDescriptor {
        ImporterDescriptor {
            id: "nico.model_glb",
            version: "1",
            extensions: &["glb"],
        }
    }
    fn validate_settings(&self, s: &Self::Settings) -> Result<(), ImportError> {
        if [
            s.max_nodes,
            s.max_objects,
            s.max_joints_per_skin,
            s.max_vertices,
            s.max_indices,
            s.max_tracks,
            s.max_keyframes,
            s.max_json_bytes,
        ]
        .contains(&0)
        {
            return Err(ImportError::new(
                ImportErrorKind::InvalidSettings,
                "model_limits",
                "model limits must be nonzero",
            ));
        }
        Ok(())
    }
    fn import(&self, c: &mut ImportContext<'_>, s: &Self::Settings) -> Result<Model, ImportError> {
        self.validate_settings(s)?;
        c.check_cancelled()?;
        let bytes = c.bytes();
        if bytes.len() < 20 || &bytes[..4] != b"glTF" {
            return Err(malformed("glb_header"));
        }
        let json_length = u32::from_le_bytes(bytes[12..16].try_into().unwrap()) as usize;
        if json_length > s.max_json_bytes {
            return Err(limit());
        }
        let gltf = gltf::Gltf::from_slice(bytes).map_err(|e| {
            ImportError::new(ImportErrorKind::Malformed, "glb_document", &e.to_string())
        })?;
        let d = &gltf.document;
        let blob = gltf
            .blob
            .as_deref()
            .ok_or_else(|| unsupported("embedded_buffer_required"))?;
        if d.extensions_required().next().is_some()
            || d.buffers().len() != 1
            || d.cameras().len() != 0
        {
            return Err(unsupported("document_features"));
        }
        let buffer = d.buffers().next().unwrap();
        if !matches!(buffer.source(), gltf::buffer::Source::Bin) || buffer.length() > blob.len() {
            return Err(unsupported("external_buffer"));
        }
        let mut out = ModelData::default();
        let mut extensions = std::collections::BTreeSet::new();
        for ext in d.extensions_used() {
            if !extensions.insert(ext) {
                return Err(malformed("duplicate_extension"));
            }
            if s.allow_material_fallback
                && matches!(ext, "KHR_materials_specular" | "KHR_materials_ior")
            {
                claim::<String>(c, 1)?;
                out.omitted_extensions.push(name(c, Some(ext))?);
            } else {
                return Err(unsupported("optional_extension").at(ext));
            }
        }
        let objects = [
            d.nodes().len(),
            d.meshes().len(),
            d.skins().len(),
            d.animations().len(),
            d.materials().len(),
            d.images().len(),
            d.textures().len(),
            d.scenes().len(),
        ];
        let total = objects
            .iter()
            .try_fold(0usize, |a, b| a.checked_add(*b))
            .ok_or_else(limit)?;
        if d.nodes().len() > s.max_nodes || total > s.max_objects {
            return Err(limit());
        }
        for view in d.views() {
            if view.buffer().index() != 0
                || view
                    .offset()
                    .checked_add(view.length())
                    .is_none_or(|end| end > buffer.length())
            {
                return Err(malformed("buffer_view_range"));
            }
        }
        for a in d.accessors() {
            if a.sparse().is_some() || a.count() == 0 {
                return Err(unsupported("sparse_or_empty_accessor"));
            }
            if matches!(a.dimensions(), Dim::Mat2 | Dim::Mat3)
                || (a.dimensions() == Dim::Mat4 && a.data_type() != D::F32)
            {
                return Err(unsupported("matrix_accessor"));
            }
            let view = a
                .view()
                .ok_or_else(|| unsupported("accessor_without_view"))?;
            let stride = view.stride().unwrap_or(a.size());
            let end = (a.count() - 1)
                .checked_mul(stride)
                .and_then(|v| v.checked_add(a.offset()))
                .and_then(|v| v.checked_add(a.size()));
            let aligned = view
                .offset()
                .checked_add(a.offset())
                .is_some_and(|n| n.is_multiple_of(a.data_type().size()));
            if stride < a.size()
                || !stride.is_multiple_of(a.data_type().size())
                || !aligned
                || end.is_none_or(|end| end > view.length())
            {
                return Err(malformed("accessor_range"));
            }
        }
        claim::<Node>(c, d.nodes().len())?;
        claim::<usize>(c, d.nodes().len())?;
        claim::<Option<usize>>(c, d.nodes().len())?;
        let mut edges = 0;
        for n in d.nodes() {
            count(&mut edges, n.children().len(), s.max_nodes)?;
            claim::<usize>(c, n.children().len())?;
            if n.weights().is_some() {
                return Err(unsupported("node_morph_weights"));
            }
            let gltf::scene::Transform::Decomposed {
                translation,
                rotation,
                scale,
            } = n.transform()
            else {
                return Err(unsupported("matrix_node"));
            };
            let children = n.children().map(|n| n.index()).collect();
            out.nodes.push(Node {
                name: name(c, n.name())?,
                children,
                transform: Transform {
                    translation,
                    rotation,
                    scale,
                },
                mesh: n.mesh().map(|m| m.index()),
                skin: n.skin().map(|s| s.index()),
            });
        }
        claim::<Scene>(c, d.scenes().len())?;
        for scene in d.scenes() {
            claim::<usize>(c, scene.nodes().count())?;
            out.scenes.push(Scene {
                name: name(c, scene.name())?,
                roots: scene.nodes().map(|n| n.index()).collect(),
            });
        }
        out.default_scene = d.default_scene().map(|scene| scene.index());
        claim::<ModelImage>(c, d.images().len())?;
        for image in d.images() {
            let gltf::image::Source::View { view, mime_type } = image.source() else {
                return Err(unsupported("external_image"));
            };
            let encoding = match mime_type {
                "image/png" => ImageEncoding::Png,
                "image/jpeg" => ImageEncoding::Jpeg,
                _ => return Err(unsupported("image_encoding")),
            };
            c.claim_decoded(view.length())?;
            out.images.push(ModelImage {
                name: name(c, image.name())?,
                encoding,
                bytes: blob[view.offset()..view.offset() + view.length()].to_vec(),
            });
        }
        claim::<ModelTexture>(c, d.textures().len())?;
        for texture in d.textures() {
            use gltf::texture::{MagFilter as Mag, MinFilter as Min, WrappingMode as W};
            let wrap = |w| match w {
                W::ClampToEdge => WrapMode::Clamp,
                W::MirroredRepeat => WrapMode::Mirror,
                W::Repeat => WrapMode::Repeat,
            };
            let sampler = texture.sampler();
            out.textures.push(ModelTexture {
                image: texture.source().index(),
                wrap_s: wrap(sampler.wrap_s()),
                wrap_t: wrap(sampler.wrap_t()),
                mag_filter: sampler.mag_filter().map(|f| match f {
                    Mag::Nearest => Filter::Nearest,
                    Mag::Linear => Filter::Linear,
                }),
                min_filter: sampler.min_filter().map(|f| match f {
                    Min::Nearest => Filter::Nearest,
                    Min::Linear => Filter::Linear,
                    Min::NearestMipmapNearest => Filter::NearestMipmapNearest,
                    Min::LinearMipmapNearest => Filter::LinearMipmapNearest,
                    Min::NearestMipmapLinear => Filter::NearestMipmapLinear,
                    Min::LinearMipmapLinear => Filter::LinearMipmapLinear,
                }),
            });
        }
        claim::<Material>(c, d.materials().len())?;
        for material in d.materials() {
            let p = material.pbr_metallic_roughness();
            let tex =
                |info: Option<gltf::texture::Info<'_>>| -> Result<Option<usize>, ImportError> {
                    info.map(|t| {
                        if t.tex_coord() != 0 {
                            Err(unsupported("texture_uv_set"))
                        } else {
                            Ok(t.texture().index())
                        }
                    })
                    .transpose()
                };
            if material
                .normal_texture()
                .is_some_and(|t| t.tex_coord() != 0)
                || material
                    .occlusion_texture()
                    .is_some_and(|t| t.tex_coord() != 0)
            {
                return Err(unsupported("texture_uv_set"));
            }
            out.materials.push(Material {
                name: name(c, material.name())?,
                base_color: p.base_color_factor(),
                metallic: p.metallic_factor(),
                roughness: p.roughness_factor(),
                base_color_texture: tex(p.base_color_texture())?,
                metallic_roughness_texture: tex(p.metallic_roughness_texture())?,
                normal_texture: material.normal_texture().map(|t| t.texture().index()),
                normal_scale: material.normal_texture().map_or(1., |t| t.scale()),
                occlusion_texture: material.occlusion_texture().map(|t| t.texture().index()),
                occlusion_strength: material.occlusion_texture().map_or(1., |t| t.strength()),
                emissive_texture: tex(material.emissive_texture())?,
                emissive: material.emissive_factor(),
                alpha: match material.alpha_mode() {
                    gltf::material::AlphaMode::Opaque => AlphaMode::Opaque,
                    gltf::material::AlphaMode::Mask => AlphaMode::Mask,
                    gltf::material::AlphaMode::Blend => AlphaMode::Blend,
                },
                alpha_cutoff: material.alpha_cutoff().unwrap_or(0.5),
                double_sided: material.double_sided(),
            });
        }
        let mut vertices = 0;
        let mut indices = 0;
        let mut primitives = 0;
        claim::<ModelMesh>(c, d.meshes().len())?;
        for mesh in d.meshes() {
            if mesh.weights().is_some() {
                return Err(unsupported("morph_weights"));
            }
            let mut result = ModelMesh {
                name: name(c, mesh.name())?,
                primitives: Vec::new(),
            };
            count(&mut primitives, mesh.primitives().len(), s.max_objects)?;
            claim::<Primitive>(c, mesh.primitives().len())?;
            for p in mesh.primitives() {
                c.check_cancelled()?;
                if p.mode() != gltf::mesh::Mode::Triangles || p.morph_targets().len() != 0 {
                    return Err(unsupported("primitive_mode_or_morph"));
                }
                for (semantic, _) in p.attributes() {
                    if !matches!(
                        semantic,
                        gltf::Semantic::Positions
                            | gltf::Semantic::Normals
                            | gltf::Semantic::TexCoords(0)
                            | gltf::Semantic::Joints(0)
                            | gltf::Semantic::Weights(0)
                    ) {
                        return Err(unsupported("vertex_semantic"));
                    }
                }
                let position = p
                    .get(&gltf::Semantic::Positions)
                    .ok_or_else(|| malformed("positions"))?;
                shape(&position, &[D::F32], Dim::Vec3, false)?;
                let index = p
                    .indices()
                    .ok_or_else(|| unsupported("nonindexed_primitive"))?;
                shape(&index, &[D::U8, D::U16, D::U32], Dim::Scalar, false)?;
                count(&mut vertices, position.count(), s.max_vertices)?;
                count(&mut indices, index.count(), s.max_indices)?;
                claim::<ModelVertex>(c, position.count())?;
                claim::<u32>(c, index.count())?;
                let normal = p.get(&gltf::Semantic::Normals);
                let uv = p.get(&gltf::Semantic::TexCoords(0));
                let joints = p.get(&gltf::Semantic::Joints(0));
                let weights = p.get(&gltf::Semantic::Weights(0));
                if joints.is_some() != weights.is_some() {
                    return Err(malformed("joint_weight_pair"));
                }
                for a in [&normal, &uv, &joints, &weights].into_iter().flatten() {
                    if a.count() != position.count() {
                        return Err(malformed("vertex_counts"));
                    }
                }
                if let Some(a) = &normal {
                    shape(a, &[D::F32], Dim::Vec3, false)?;
                }
                if let Some(a) = &uv {
                    if a.data_type() == D::F32 {
                        shape(a, &[D::F32], Dim::Vec2, false)?;
                    } else {
                        shape(a, &[D::U8, D::U16], Dim::Vec2, true)?;
                    }
                }
                if let Some(a) = &joints {
                    shape(a, &[D::U8, D::U16], Dim::Vec4, false)?;
                }
                if let Some(a) = &weights {
                    if a.data_type() == D::F32 {
                        shape(a, &[D::F32], Dim::Vec4, false)?;
                    } else {
                        shape(a, &[D::U8, D::U16], Dim::Vec4, true)?;
                    }
                }
                let reader = p.reader(|_| Some(blob));
                let mut ns = reader.read_normals();
                let mut uvs = reader.read_tex_coords(0).map(|v| v.into_f32());
                let mut js = reader.read_joints(0).map(|v| v.into_u16());
                let mut ws = reader.read_weights(0).map(|v| v.into_f32());
                let mut vs = Vec::with_capacity(position.count());
                for position in reader
                    .read_positions()
                    .ok_or_else(|| malformed("position_data"))?
                {
                    let weights = ws.as_mut().and_then(Iterator::next).unwrap_or([0.; 4]);
                    vs.push(ModelVertex {
                        position,
                        normal: ns.as_mut().and_then(Iterator::next).unwrap_or([0.; 3]),
                        uv: uvs.as_mut().and_then(Iterator::next).unwrap_or([0.; 2]),
                        joints: js.as_mut().and_then(Iterator::next).unwrap_or([0; 4]),
                        weights,
                    });
                }
                result.primitives.push(Primitive {
                    vertices: vs,
                    indices: reader
                        .read_indices()
                        .ok_or_else(|| malformed("index_data"))?
                        .into_u32()
                        .collect(),
                    material: p.material().index(),
                    skinned: joints.is_some(),
                    has_normals: normal.is_some(),
                });
            }
            out.meshes.push(result);
        }
        claim::<Skin>(c, d.skins().len())?;
        for skin in d.skins() {
            let n = skin.joints().len();
            if n > s.max_joints_per_skin {
                return Err(limit());
            }
            claim::<usize>(c, n)?;
            claim::<Matrix4>(c, n)?;
            if let Some(a) = skin.inverse_bind_matrices() {
                shape(&a, &[D::F32], Dim::Mat4, false)?;
                if a.count() != n {
                    return Err(malformed("inverse_bind_count"));
                }
            }
            let reader = skin.reader(|_| Some(blob));
            out.skins.push(Skin {
                name: name(c, skin.name())?,
                joints: skin.joints().map(|j| j.index()).collect(),
                inverse_bind: reader
                    .read_inverse_bind_matrices()
                    .map_or_else(|| vec![IDENTITY; n], Iterator::collect),
                skeleton: skin.skeleton().map(|n| n.index()),
            });
        }
        let mut tracks = 0;
        let mut keys = 0;
        claim::<Clip>(c, d.animations().len())?;
        for animation in d.animations() {
            count(&mut tracks, animation.channels().count(), s.max_tracks)?;
            claim::<Track>(c, animation.channels().count())?;
            let mut clip = Clip {
                name: name(c, animation.name())?,
                tracks: Vec::new(),
            };
            for channel in animation.channels() {
                use gltf::animation::util::ReadOutputs;
                use gltf::animation::{Interpolation as I, Property};
                let sampler = channel.sampler();
                let input = sampler.input();
                let output = sampler.output();
                shape(&input, &[D::F32], Dim::Scalar, false)?;
                let interpolation = match sampler.interpolation() {
                    I::Step => Interpolation::Step,
                    I::Linear => Interpolation::Linear,
                    _ => return Err(unsupported("cubic_spline")),
                };
                let dim = match channel.target().property() {
                    Property::Translation | Property::Scale => Dim::Vec3,
                    Property::Rotation => Dim::Vec4,
                    _ => return Err(unsupported("morph_animation")),
                };
                shape(&output, &[D::F32], dim, false)?;
                if input.count() != output.count() {
                    return Err(malformed("animation_sample_count"));
                }
                count(&mut keys, input.count(), s.max_keyframes)?;
                claim::<f32>(c, input.count())?;
                claim::<[f32; 4]>(c, output.count())?;
                let reader = channel.reader(|_| Some(blob));
                let values = match reader
                    .read_outputs()
                    .ok_or_else(|| malformed("animation_output"))?
                {
                    ReadOutputs::Translations(v) => TrackValues::Translation(v.collect()),
                    ReadOutputs::Rotations(v) => TrackValues::Rotation(v.into_f32().collect()),
                    ReadOutputs::Scales(v) => TrackValues::Scale(v.collect()),
                    _ => return Err(unsupported("morph_animation")),
                };
                clip.tracks.push(Track {
                    node: channel.target().node().index(),
                    times: reader
                        .read_inputs()
                        .ok_or_else(|| malformed("animation_input"))?
                        .collect(),
                    values,
                    interpolation,
                });
            }
            out.clips.push(clip);
        }
        c.check_cancelled()?;
        Model::new(out)
            .map_err(|e| ImportError::new(ImportErrorKind::Malformed, e.code, &e.to_string()))
    }
}
