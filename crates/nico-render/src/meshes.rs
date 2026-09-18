use crate::materials::Materials;
use crate::{DEFAULT_CLEAR_COLOR, QuadRenderPipeline, RenderStatus, acquired_frame};
use glam::{Mat4, Vec3};
use nico_assets::{Mesh, MeshVertex, model::AlphaMode};
use nico_presentation::{Camera3d, MeshInstance, Scene2d, Scene3d, UiScene};
use nico_rhi::*;
use std::{
    num::NonZeroU64,
    sync::{Arc, Weak},
};

const MAX_INSTANCES: usize = 256;
struct Uploaded<D: RhiDevice> {
    source: Weak<Mesh>,
    vertices: D::Buffer,
    indices: D::Buffer,
    vertex_bytes: u64,
    index_count: u32,
}
struct Uniform<D: RhiDevice> {
    buffer: D::Buffer,
    binding: D::BindGroup,
}
struct SkinPipeline<D: RhiDevice> {
    shader: D::ShaderModule,
    pipeline: Vec<D::RenderPipeline>,
    layout: D::BindGroupLayout,
    uniforms: Vec<Option<Uniform<D>>>,
    vertex_entry: String,
    fragment_entry: String,
}
const PALETTE_BYTES: u64 = 256 * 64;

struct Depth<D: RhiDevice> {
    _texture: D::Texture,
    view: D::TextureView,
    extent: Extent3d,
}

/// Metallic/roughness scene passes with persistent materials and per-frame data,
/// followed by the canvas. Static and skinned variants share material policy.
pub struct MeshRenderPipeline<D: RhiDevice> {
    canvas: QuadRenderPipeline<D>,
    materials: Materials<D>,
    frame_layout: D::BindGroupLayout,
    frame: Uniform<D>,
    skin: Option<SkinPipeline<D>>,
    shader: D::ShaderModule,
    pipeline: Vec<D::RenderPipeline>,
    uniform_layout: D::BindGroupLayout,
    uniforms: Vec<Uniform<D>>,
    meshes: Vec<Uploaded<D>>,
    fallback: Arc<Mesh>,
    depth: Option<Depth<D>>,
    format: TextureFormat,
    vertex_entry: String,
    fragment_entry: String,
}

impl<D: RhiDevice> MeshRenderPipeline<D> {
    pub fn new(
        device: &D,
        format: TextureFormat,
        mesh_shader: GraphicsShaderArtifact<'_>,
        quad_shader: GraphicsShaderArtifact<'_>,
    ) -> Result<Self, RhiError> {
        validate_target_format(format)?;
        let limits = device.capabilities().limits;
        if limits.max_bind_groups < 3
            || limits.max_uniform_buffer_binding_size < 208
            || limits.max_vertex_buffers < 1
            || limits.max_vertex_attributes < 3
        {
            return Err(RhiError::new(
                RhiErrorKind::Unsupported,
                "device cannot bind PBR scene data",
            ));
        }
        let canvas = QuadRenderPipeline::new(device, format, quad_shader)?;
        let shader = device.create_shader_module(mesh_shader.module)?;
        let materials = Materials::new(device)?;
        let frame_layout = device.create_bind_group_layout(BindGroupLayoutDescriptor {
            label: Some("camera and lights layout"),
            entries: &[BindGroupLayoutEntry {
                binding: 0,
                visibility: ShaderStages::FRAGMENT,
                binding_type: BindingType::Buffer {
                    kind: BufferBindingKind::Uniform,
                    dynamic_offset: false,
                    minimum_size: NonZeroU64::new(64),
                },
            }],
        })?;
        let frame_buffer = device.create_buffer(BufferDescriptor {
            label: Some("camera and lights"),
            size: 64,
            usages: BufferUsages::UNIFORM | BufferUsages::COPY_DESTINATION,
        })?;
        let frame_binding = device.create_bind_group(BindGroupDescriptor {
            label: Some("frame binding"),
            layout: &frame_layout,
            entries: &[BindGroupEntry {
                binding: 0,
                resource: BindingResource::Buffer {
                    buffer: &frame_buffer,
                    offset: 0,
                    size: NonZeroU64::new(64),
                },
            }],
        })?;
        let frame = Uniform {
            buffer: frame_buffer,
            binding: frame_binding,
        };
        let uniform_layout = device.create_bind_group_layout(BindGroupLayoutDescriptor {
            label: Some("mesh transform layout"),
            entries: &[BindGroupLayoutEntry {
                binding: 0,
                visibility: ShaderStages::VERTEX | ShaderStages::FRAGMENT,
                binding_type: BindingType::Buffer {
                    kind: BufferBindingKind::Uniform,
                    dynamic_offset: false,
                    minimum_size: NonZeroU64::new(208),
                },
            }],
        })?;
        let pipeline = pipeline(
            device,
            &shader,
            &materials.layout,
            &uniform_layout,
            &frame_layout,
            format,
            mesh_shader.vertex_entry_point,
            mesh_shader.fragment_entry_point,
            None,
        )?;
        let fallback = Arc::new(
            Mesh::triangles(
                vec![
                    MeshVertex {
                        position: [0.0, 0.6, 0.0],
                        uv: [0.5, 0.0],
                    },
                    MeshVertex {
                        position: [-0.5, -0.4, 0.4],
                        uv: [0.0, 1.0],
                    },
                    MeshVertex {
                        position: [0.5, -0.4, 0.4],
                        uv: [1.0, 1.0],
                    },
                    MeshVertex {
                        position: [0.0, -0.4, -0.5],
                        uv: [0.5, 1.0],
                    },
                ],
                vec![0, 1, 2, 0, 2, 3, 0, 3, 1, 1, 3, 2],
            )
            .expect("valid fallback tetrahedron"),
        );
        Ok(Self {
            canvas,
            materials,
            frame_layout,
            frame,
            skin: None,
            shader,
            pipeline,
            uniform_layout,
            uniforms: Vec::new(),
            meshes: Vec::new(),
            fallback,
            depth: None,
            format,
            vertex_entry: mesh_shader.vertex_entry_point.into(),
            fragment_entry: mesh_shader.fragment_entry_point.into(),
        })
    }

    /// Installs the independently compiled GPU skinning shader. Static draws keep
    /// their existing vertex format and pipeline. Palette buffers persist per draw.
    pub fn enable_skinning(
        &mut self,
        device: &D,
        artifact: GraphicsShaderArtifact<'_>,
    ) -> Result<(), RhiError> {
        if device.capabilities().limits.max_uniform_buffer_binding_size < PALETTE_BYTES
            || device.capabilities().limits.max_bind_groups < 4
            || device.capabilities().limits.max_vertex_attributes < 5
        {
            return Err(invalid("device cannot bind a 256-joint palette"));
        }
        let shader = device.create_shader_module(artifact.module)?;
        let layout = device.create_bind_group_layout(BindGroupLayoutDescriptor {
            label: Some("skin palette layout"),
            entries: &[BindGroupLayoutEntry {
                binding: 0,
                visibility: ShaderStages::VERTEX,
                binding_type: BindingType::Buffer {
                    kind: BufferBindingKind::Uniform,
                    dynamic_offset: false,
                    minimum_size: NonZeroU64::new(PALETTE_BYTES),
                },
            }],
        })?;
        let pipeline = pipeline(
            device,
            &shader,
            &self.materials.layout,
            &self.uniform_layout,
            &self.frame_layout,
            self.format,
            artifact.vertex_entry_point,
            artifact.fragment_entry_point,
            Some(&layout),
        )?;
        self.skin = Some(SkinPipeline {
            shader,
            pipeline,
            layout,
            uniforms: Vec::new(),
            vertex_entry: artifact.vertex_entry_point.into(),
            fragment_entry: artifact.fragment_entry_point.into(),
        });
        Ok(())
    }

    /// Physical extent owns depth allocation; logical viewport controls HUD sizing.
    #[allow(clippy::too_many_arguments)]
    pub fn render<Q: RhiQueue<D>, S: RhiSurface<D, Q>>(
        &mut self,
        device: &D,
        queue: &Q,
        surface: &mut S,
        scene: &Scene3d,
        canvas_scene: &Scene2d,
        ui: &UiScene,
        viewport: [f32; 2],
        extent: Extent3d,
    ) -> Result<RenderStatus, RhiError> {
        if extent.is_zero() || viewport.iter().any(|v| *v <= 0.0 || !v.is_finite()) {
            return Ok(RenderStatus::ZeroSized);
        }
        validate_target_format(surface.format())?;
        if extent.depth_or_layers != 1
            || extent.width > device.capabilities().limits.max_texture_dimension_2d
            || extent.height > device.capabilities().limits.max_texture_dimension_2d
        {
            return Err(invalid("invalid depth target extent"));
        }
        if scene.meshes.len() > MAX_INSTANCES {
            return Err(invalid("mesh instance limit exceeded"));
        }
        if !scene.lighting.is_valid() {
            return Err(invalid("invalid scene lighting"));
        }
        for mesh in &scene.meshes {
            if mesh.material.as_ref().is_some_and(|m| !m.is_valid()) {
                return Err(invalid("invalid PBR material"));
            }
            validate_skin(mesh, self.skin.is_some())?;
            if let Some(source) = &mesh.mesh {
                validate_geometry(source)?;
            }
            if let Some(material) = &mesh.material {
                for texture in material.textures().into_iter().flatten() {
                    crate::validate_texture(device, &texture.image)?;
                }
            } else if let Some(texture) = &mesh.texture {
                crate::validate_texture(device, texture)?;
            }
        }
        let camera = camera_matrix(scene.camera, viewport[0] / viewport[1])?;
        let transforms: Vec<_> = scene
            .meshes
            .iter()
            .map(|mesh| {
                let model = transform(Mat4::IDENTITY, mesh)?;
                let normal = model.inverse().transpose();
                let clip = camera * model;
                if !normal.is_finite() || !clip.is_finite() {
                    return Err(invalid("mesh normal or clip transform overflow"));
                }
                Ok((clip, model, normal))
            })
            .collect::<Result<_, _>>()?;
        let order = draw_order(scene, &self.fallback)?;
        crate::quads::geometry(canvas_scene, ui, viewport)?;
        crate::quads::validate_resources(device, canvas_scene, ui)?;
        let (frame, view) = match acquired_frame(surface.acquire(device)?) {
            Ok(value) => value,
            Err(status) => return Ok(status),
        };
        if surface.format() != self.format {
            self.pipeline = pipeline(
                device,
                &self.shader,
                &self.materials.layout,
                &self.uniform_layout,
                &self.frame_layout,
                surface.format(),
                &self.vertex_entry,
                &self.fragment_entry,
                None,
            )?;
            if let Some(skin) = &mut self.skin {
                skin.pipeline = pipeline(
                    device,
                    &skin.shader,
                    &self.materials.layout,
                    &self.uniform_layout,
                    &self.frame_layout,
                    surface.format(),
                    &skin.vertex_entry,
                    &skin.fragment_entry,
                    Some(&skin.layout),
                )?;
            }
            self.format = surface.format();
        }
        if self.depth.as_ref().is_none_or(|d| d.extent != extent) {
            let texture = device.create_texture(TextureDescriptor {
                label: Some("mesh depth"),
                extent,
                mip_levels: 1,
                samples: 1,
                dimension: TextureDimension::Two,
                format: TextureFormat::Depth32Float,
                usages: TextureUsages::RENDER_ATTACHMENT,
            })?;
            let depth_view =
                device.create_texture_view(&texture, TextureViewDescriptor::default())?;
            self.depth = Some(Depth {
                _texture: texture,
                view: depth_view,
                extent,
            });
        }
        let light = scene.lighting;
        let frame_bytes: Vec<_> = scene
            .camera
            .position
            .into_iter()
            .chain([0.])
            .chain(light.direction)
            .chain([0.])
            .chain(light.radiance)
            .chain([0.])
            .chain(light.ambient)
            .chain([0.])
            .flat_map(f32::to_le_bytes)
            .collect();
        queue.write_buffer(&self.frame.buffer, 0, &frame_bytes);
        self.materials.retain_live_sources();
        let sources: Vec<_> = canvas_scene
            .world
            .iter()
            .chain(&ui.quads)
            .filter_map(|q| q.texture.clone())
            .collect();
        self.canvas.retain_sources(&sources);
        // Visibility is not asset lifetime: culled scenery still owns its source.
        // Weak references avoid pinning assets after a world is unloaded.
        self.meshes
            .retain(|uploaded| uploaded.source.strong_count() > 0);
        self.uniforms.truncate(scene.meshes.len());
        if let Some(skin) = &mut self.skin {
            skin.uniforms.resize_with(scene.meshes.len(), || None);
        }
        let mut draws = Vec::with_capacity(scene.meshes.len());
        for (index, instance) in scene.meshes.iter().enumerate() {
            let mesh = instance.mesh.as_ref().unwrap_or(&self.fallback);
            let mesh_slot = if let Some(slot) = self
                .meshes
                .iter()
                .position(|u| u.source.ptr_eq(&Arc::downgrade(mesh)))
            {
                slot
            } else {
                self.meshes.push(upload(device, queue, mesh)?);
                self.meshes.len() - 1
            };
            let texture_slot = self.materials.slot(device, queue, instance)?;
            if index == self.uniforms.len() {
                let buffer = device.create_buffer(BufferDescriptor {
                    label: Some("mesh transform and color"),
                    size: 208,
                    usages: BufferUsages::UNIFORM | BufferUsages::COPY_DESTINATION,
                })?;
                let binding = device.create_bind_group(BindGroupDescriptor {
                    label: Some("mesh transform binding"),
                    layout: &self.uniform_layout,
                    entries: &[BindGroupEntry {
                        binding: 0,
                        resource: BindingResource::Buffer {
                            buffer: &buffer,
                            offset: 0,
                            size: NonZeroU64::new(208),
                        },
                    }],
                })?;
                self.uniforms.push(Uniform { buffer, binding });
            }
            let mut bytes = Vec::with_capacity(208);
            let (clip, model, normal) = transforms[index];
            for value in clip
                .to_cols_array()
                .into_iter()
                .chain(model.to_cols_array())
                .chain(normal.to_cols_array())
                .chain(instance.color)
            {
                bytes.extend_from_slice(&value.to_le_bytes());
            }
            queue.write_buffer(&self.uniforms[index].buffer, 0, &bytes);
            if let Some(skin) = &mut self.skin {
                if let Some(palette) = &instance.skin_palette {
                    if skin.uniforms[index].is_none() {
                        let buffer = device.create_buffer(BufferDescriptor {
                            label: Some("skin palette"),
                            size: PALETTE_BYTES,
                            usages: BufferUsages::UNIFORM | BufferUsages::COPY_DESTINATION,
                        })?;
                        let binding = device.create_bind_group(BindGroupDescriptor {
                            label: Some("skin palette binding"),
                            layout: &skin.layout,
                            entries: &[BindGroupEntry {
                                binding: 0,
                                resource: BindingResource::Buffer {
                                    buffer: &buffer,
                                    offset: 0,
                                    size: NonZeroU64::new(PALETTE_BYTES),
                                },
                            }],
                        })?;
                        skin.uniforms[index] = Some(Uniform { buffer, binding });
                    }
                    let mut bytes = [0u8; PALETTE_BYTES as usize];
                    for (dst, value) in bytes
                        .as_chunks_mut::<4>()
                        .0
                        .iter_mut()
                        .zip(palette.iter().flatten().flatten())
                    {
                        dst.copy_from_slice(&value.to_le_bytes());
                    }
                    queue.write_buffer(
                        &skin.uniforms[index].as_ref().unwrap().buffer,
                        0,
                        &bytes[..palette.len() * 64],
                    );
                } else {
                    skin.uniforms[index] = None;
                }
            }
            draws.push((mesh_slot, texture_slot));
        }
        let mut encoder = device.create_command_encoder(Some("PBR scene encoder"));
        for transparent_pass in [false, true] {
            let colors = [Some(RenderPassColorAttachment {
                view: &view,
                resolve_target: None,
                operations: Operations {
                    load: if transparent_pass {
                        LoadOp::Load
                    } else {
                        LoadOp::Clear(DEFAULT_CLEAR_COLOR)
                    },
                    store: StoreOp::Store,
                },
            })];
            let mut pass = encoder.begin_render_pass(RenderPassDescriptor {
                label: Some(if transparent_pass {
                    "3D transparent"
                } else {
                    "3D opaque and masked"
                }),
                color_attachments: &colors,
                depth_stencil_attachment: Some(RenderPassDepthStencilAttachment {
                    view: &self.depth.as_ref().unwrap().view,
                    depth_operations: Some(Operations {
                        load: if transparent_pass {
                            LoadOp::Load
                        } else {
                            LoadOp::Clear(1.)
                        },
                        store: StoreOp::Store,
                    }),
                    stencil_operations: None,
                }),
            });
            for &index in &order {
                let instance = &scene.meshes[index];
                if transparent(instance) != transparent_pass {
                    continue;
                }
                let (mesh_slot, material_slot) = draws[index];
                let mesh = &self.meshes[mesh_slot];
                let variant = usize::from(transparent_pass) * 4
                    + usize::from(instance.material.as_ref().is_none_or(|m| m.double_sided)) * 2
                    + usize::from(instance.mirrored);
                if instance.skin_palette.is_some() {
                    let skin = self.skin.as_ref().unwrap();
                    pass.set_pipeline(&skin.pipeline[variant]);
                    pass.set_bind_group(3, &skin.uniforms[index].as_ref().unwrap().binding, &[]);
                } else {
                    pass.set_pipeline(&self.pipeline[variant]);
                }
                pass.set_bind_group(0, self.materials.binding(material_slot), &[]);
                pass.set_bind_group(1, &self.uniforms[index].binding, &[]);
                pass.set_bind_group(2, &self.frame.binding, &[]);
                pass.set_vertex_buffer(0, &mesh.vertices, 0..mesh.vertex_bytes);
                pass.set_index_buffer(
                    &mesh.indices,
                    IndexFormat::Uint32,
                    0..u64::from(mesh.index_count) * 4,
                );
                pass.draw_indexed(0..mesh.index_count, 0, 0..1);
            }
        }
        queue.submit(vec![encoder.finish()]);
        self.canvas.draw_into(
            device,
            queue,
            &view,
            self.format,
            canvas_scene,
            ui,
            viewport,
            LoadOp::Load,
        )?;
        surface.present(device, queue, frame);
        Ok(RenderStatus::Presented)
    }
}

fn validate_target_format(format: TextureFormat) -> Result<(), RhiError> {
    if !matches!(
        format,
        TextureFormat::Rgba8UnormSrgb | TextureFormat::Bgra8UnormSrgb
    ) {
        return Err(RhiError::new(
            RhiErrorKind::Unsupported,
            "PBR surface must use an sRGB color format",
        ));
    }
    Ok(())
}
fn validate_geometry(source: &Mesh) -> Result<(), RhiError> {
    if source.vertices().len() > 250_000 || source.indices().len() > 750_000 {
        return Err(invalid("GPU mesh geometry limit exceeded"));
    }
    Ok(())
}

fn upload<D: RhiDevice, Q: RhiQueue<D>>(
    device: &D,
    queue: &Q,
    source: &Arc<Mesh>,
) -> Result<Uploaded<D>, RhiError> {
    validate_geometry(source)?;
    let stride = if source.skin().is_some() { 64 } else { 32 };
    let mut vertices = Vec::with_capacity(source.vertices().len() * stride);
    for (index, vertex) in source.vertices().iter().enumerate() {
        for value in vertex
            .position
            .into_iter()
            .chain(vertex.uv)
            .chain(source.normals()[index])
        {
            vertices.extend_from_slice(&value.to_le_bytes());
        }
        if let Some(skin) = source.skin() {
            for joint in skin[index].joints {
                vertices.extend_from_slice(&u32::from(joint).to_le_bytes());
            }
            for weight in skin[index].weights {
                vertices.extend_from_slice(&weight.to_le_bytes());
            }
        }
    }
    let indices: Vec<_> = source
        .indices()
        .iter()
        .flat_map(|v| v.to_le_bytes())
        .collect();
    let vertex_buffer = device.create_buffer(BufferDescriptor {
        label: Some("mesh vertices"),
        size: vertices.len() as u64,
        usages: BufferUsages::VERTEX | BufferUsages::COPY_DESTINATION,
    })?;
    let index_buffer = device.create_buffer(BufferDescriptor {
        label: Some("mesh indices"),
        size: indices.len() as u64,
        usages: BufferUsages::INDEX | BufferUsages::COPY_DESTINATION,
    })?;
    queue.write_buffer(&vertex_buffer, 0, &vertices);
    queue.write_buffer(&index_buffer, 0, &indices);
    Ok(Uploaded {
        source: Arc::downgrade(source),
        vertices: vertex_buffer,
        indices: index_buffer,
        vertex_bytes: vertices.len() as u64,
        index_count: source.indices().len() as u32,
    })
}

#[allow(clippy::too_many_arguments)]
fn pipeline<D: RhiDevice>(
    device: &D,
    shader: &D::ShaderModule,
    texture_layout: &D::BindGroupLayout,
    uniform_layout: &D::BindGroupLayout,
    frame_layout: &D::BindGroupLayout,
    format: TextureFormat,
    vertex: &str,
    fragment: &str,
    skin_layout: Option<&D::BindGroupLayout>,
) -> Result<Vec<D::RenderPipeline>, RhiError> {
    let mut layouts = vec![texture_layout, uniform_layout, frame_layout];
    if let Some(skin) = skin_layout {
        layouts.push(skin);
    }
    let mut attributes = vec![
        VertexAttribute {
            format: VertexFormat::Float32x3,
            offset: 0,
            shader_location: 0,
        },
        VertexAttribute {
            format: VertexFormat::Float32x2,
            offset: 12,
            shader_location: 1,
        },
        VertexAttribute {
            format: VertexFormat::Float32x3,
            offset: 20,
            shader_location: 2,
        },
    ];
    if skin_layout.is_some() {
        attributes.extend([
            VertexAttribute {
                format: VertexFormat::Uint32x4,
                offset: 32,
                shader_location: 3,
            },
            VertexAttribute {
                format: VertexFormat::Float32x4,
                offset: 48,
                shader_location: 4,
            },
        ]);
    }
    let layout = device.create_pipeline_layout(PipelineLayoutDescriptor {
        label: Some("mesh pipeline layout"),
        bind_group_layouts: &layouts,
    })?;
    (0..8)
        .map(|variant| {
            device.create_render_pipeline(RenderPipelineDescriptor {
                label: Some("PBR material pipeline"),
                layout: &layout,
                vertex: VertexState {
                    shader,
                    entry_point: vertex,
                    buffers: &[VertexBufferLayout {
                        stride: if skin_layout.is_some() { 64 } else { 32 },
                        step_mode: VertexStepMode::Vertex,
                        attributes: &attributes,
                    }],
                },
                fragment: Some(FragmentState {
                    shader,
                    entry_point: fragment,
                    targets: &[Some(ColorTargetState {
                        format,
                        blend: (variant >= 4).then_some(BlendState {
                            color: BlendComponent {
                                source: BlendFactor::SourceAlpha,
                                destination: BlendFactor::OneMinusSourceAlpha,
                                operation: BlendOperation::Add,
                            },
                            alpha: BlendComponent {
                                source: BlendFactor::One,
                                destination: BlendFactor::OneMinusSourceAlpha,
                                operation: BlendOperation::Add,
                            },
                        }),
                        write_mask: ColorWrites::ALL,
                    })],
                }),
                primitive: PrimitiveState {
                    front_face: if variant % 2 == 1 {
                        FrontFace::Clockwise
                    } else {
                        FrontFace::CounterClockwise
                    },
                    cull_mode: if (variant / 2) % 2 == 0 {
                        Some(Face::Back)
                    } else {
                        None
                    },
                    ..PrimitiveState::default()
                },
                depth_stencil: Some(DepthStencilState {
                    format: TextureFormat::Depth32Float,
                    depth_write_enabled: variant < 4,
                    depth_compare: CompareFunction::Less,
                }),
                multisample: MultisampleState::default(),
            })
        })
        .collect()
}

fn draw_order(scene: &Scene3d, fallback: &Mesh) -> Result<Vec<usize>, RhiError> {
    let forward = scene.camera.orientation * -Vec3::Z;
    let mut depths = Vec::with_capacity(scene.meshes.len());
    for instance in &scene.meshes {
        if !transparent(instance) {
            depths.push(0.);
            continue;
        }
        let mesh = instance.mesh.as_deref().unwrap_or(fallback);
        let center = if let Some(palette) = &instance.skin_palette {
            mesh.skin_centroid_weights()
                .iter()
                .zip(palette.iter())
                .fold(glam::Vec4::ZERO, |sum, (center, matrix)| {
                    sum + Mat4::from_cols_array_2d(matrix) * glam::Vec4::from(*center)
                })
        } else {
            Vec3::from(mesh.centroid()).extend(1.)
        };
        let world = (transform(Mat4::IDENTITY, instance)? * center).truncate();
        let depth = (world - Vec3::from(scene.camera.position)).dot(forward);
        if !depth.is_finite() {
            return Err(invalid("transparent center overflow"));
        }
        depths.push(depth);
    }
    let mut order: Vec<_> = (0..scene.meshes.len()).collect();
    order.sort_by(|&a, &b| {
        let blend_a = transparent(&scene.meshes[a]);
        let blend_b = transparent(&scene.meshes[b]);
        blend_a.cmp(&blend_b).then_with(|| {
            if blend_a {
                depths[b].total_cmp(&depths[a])
            } else {
                std::cmp::Ordering::Equal
            }
        })
    });
    Ok(order)
}

fn transparent(mesh: &MeshInstance) -> bool {
    mesh.material
        .as_ref()
        .is_some_and(|m| m.alpha == AlphaMode::Blend)
}

fn validate_skin(instance: &MeshInstance, enabled: bool) -> Result<(), RhiError> {
    let joints = instance.mesh.as_ref().map_or(0, |m| m.joint_count());
    match (joints, &instance.skin_palette) {
        (0, None) => Ok(()),
        (1..=256, Some(palette))
            if enabled
                && palette.len() >= joints
                && palette.len() <= 256
                && palette.iter().all(|m| {
                    let m = Mat4::from_cols_array_2d(m);
                    m.is_finite() && m.row(3) == glam::Vec4::W
                }) =>
        {
            Ok(())
        }
        _ => Err(invalid("missing, unsupported, or invalid skin palette")),
    }
}

fn invalid(message: &str) -> RhiError {
    RhiError::new(RhiErrorKind::InvalidDescriptor, message)
}
fn camera_matrix(camera: Camera3d, aspect: f32) -> Result<Mat4, RhiError> {
    camera
        .view_projection(aspect)
        .ok_or_else(|| invalid("invalid perspective camera or camera transform overflow"))
}
fn transform(camera: Mat4, mesh: &MeshInstance) -> Result<Mat4, RhiError> {
    if !mesh.scale.is_finite()
        || mesh.scale <= 0.0
        || (!mesh.orientation.is_finite() || !mesh.orientation.is_normalized())
        || mesh
            .position
            .iter()
            .chain(&mesh.color)
            .any(|v| !v.is_finite())
    {
        return Err(invalid("invalid mesh transform/color"));
    }
    let result = camera
        * Mat4::from_scale_rotation_translation(
            Vec3::splat(mesh.scale),
            mesh.orientation,
            Vec3::from(mesh.position),
        );
    if !result.is_finite() {
        return Err(invalid("mesh transform overflow"));
    }
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn transparent_sort_uses_geometry_and_pose_centers_in_camera_space() {
        let geometry = |z| {
            vec![
                MeshVertex {
                    position: [-1., 0., z],
                    uv: [0.; 2],
                },
                MeshVertex {
                    position: [1., 0., z],
                    uv: [0.; 2],
                },
                MeshVertex {
                    position: [0., 1., z],
                    uv: [0.; 2],
                },
            ]
        };
        let near = Arc::new(Mesh::triangles(geometry(0.), vec![0, 1, 2]).unwrap());
        let far = Arc::new(Mesh::triangles(geometry(-2.), vec![0, 1, 2]).unwrap());
        let draw = MeshInstance {
            mirrored: false,
            material: Some(Arc::new(nico_assets::PbrMaterial {
                alpha: AlphaMode::Blend,
                ..Default::default()
            })),
            mesh: Some(near.clone()),
            skin_palette: None,
            texture: None,
            position: [0.; 3],
            orientation: glam::Quat::IDENTITY,
            scale: 1.,
            color: [1.; 4],
        };
        let mut scene = Scene3d {
            camera: Camera3d {
                position: [0., 0., 3.],
                orientation: glam::Quat::IDENTITY,
                ..Default::default()
            },
            meshes: vec![
                draw.clone(),
                MeshInstance {
                    mesh: Some(far),
                    ..draw.clone()
                },
                MeshInstance {
                    material: None,
                    ..draw
                },
            ],
            ..Default::default()
        };
        assert_eq!(draw_order(&scene, &near).unwrap(), [2, 1, 0]);
        let skin = Mesh::skinned_triangles(
            geometry(0.),
            vec![0, 1, 2],
            vec![
                nico_assets::SkinWeights {
                    joints: [0; 4],
                    weights: [1., 0., 0., 0.]
                };
                3
            ],
            1,
        )
        .unwrap();
        scene.meshes[1].mesh = Some(Arc::new(skin));
        scene.meshes[1].skin_palette = Some(Arc::new(vec![
            Mat4::from_translation(Vec3::Z).to_cols_array_2d(),
        ]));
        assert_eq!(draw_order(&scene, &near).unwrap(), [2, 0, 1]);
        scene.camera.orientation = glam::Quat::from_rotation_y(std::f32::consts::PI);
        assert_eq!(draw_order(&scene, &near).unwrap(), [2, 1, 0]);
    }
    #[test]
    fn skin_palettes_require_supported_matching_geometry_and_finite_matrices() {
        let mesh = Mesh::skinned_triangles(
            vec![
                MeshVertex {
                    position: [0.; 3],
                    uv: [0.; 2]
                };
                3
            ],
            vec![0, 1, 2],
            vec![
                nico_assets::SkinWeights {
                    joints: [0; 4],
                    weights: [1., 0., 0., 0.]
                };
                3
            ],
            1,
        )
        .unwrap();
        let mut draw = MeshInstance {
            mirrored: false,
            material: None,
            mesh: Some(Arc::new(mesh)),
            skin_palette: None,
            texture: None,
            position: [0.; 3],
            orientation: glam::Quat::IDENTITY,
            scale: 1.,
            color: [1.; 4],
        };
        assert!(validate_skin(&draw, true).is_err());
        draw.skin_palette = Some(Arc::new(vec![nico_assets::model::IDENTITY]));
        assert!(validate_skin(&draw, true).is_ok());
        assert!(validate_skin(&draw, false).is_err());
        draw.skin_palette = Some(Arc::new(vec![]));
        assert!(validate_skin(&draw, true).is_err());
        let mut matrix = nico_assets::model::IDENTITY;
        matrix[0][0] = f32::NAN;
        draw.skin_palette = Some(Arc::new(vec![matrix]));
        assert!(validate_skin(&draw, true).is_err());
        draw.mesh = None;
        assert!(validate_skin(&draw, true).is_err());
    }

    #[test]
    fn quaternion_camera_supports_vertical_views_and_roll() {
        use nico_presentation::Quaternion;
        for orientation in [
            Quaternion::from_rotation_x(std::f32::consts::FRAC_PI_2),
            Quaternion::from_rotation_x(-std::f32::consts::FRAC_PI_2),
            Quaternion::from_rotation_z(std::f32::consts::FRAC_PI_2),
        ] {
            let camera = Camera3d {
                position: [0.0; 3],
                orientation,
                ..Camera3d::default()
            };
            let matrix = camera_matrix(camera, 1.0).unwrap();
            let center = matrix.project_point3(orientation * Vec3::new(0.0, 0.0, -2.0));
            assert!(center.x.abs() < 1e-5 && center.y.abs() < 1e-5);
            let right = matrix.project_point3(orientation * Vec3::new(1.0, 0.0, -2.0));
            assert!(right.x > 0.0 && right.y.abs() < 1e-5);
        }
    }
    #[test]
    fn mesh_transforms_use_full_quaternion_rotation_and_reject_invalid_units() {
        use nico_presentation::Quaternion;
        let mut mesh = MeshInstance {
            mirrored: false,
            material: None,
            skin_palette: None,
            mesh: None,
            texture: None,
            position: [1.0, 2.0, 3.0],
            scale: 2.0,
            orientation: Quaternion::from_rotation_x(std::f32::consts::FRAC_PI_2),
            color: [1.0; 4],
        };
        let point = transform(Mat4::IDENTITY, &mesh)
            .unwrap()
            .transform_point3(Vec3::Y);
        assert!((point - Vec3::new(1.0, 2.0, 5.0)).length() < 1e-5);
        mesh.orientation = Quaternion::from_xyzw(0.0, 0.0, 0.0, 2.0);
        assert!(transform(Mat4::IDENTITY, &mesh).is_err());
    }
    #[test]
    fn perspective_uses_zero_to_one_depth_and_rejects_invalid_poses() {
        use nico_presentation::Quaternion;
        let mut camera = Camera3d {
            position: [0.0, 0.0, 3.0],
            orientation: Quaternion::IDENTITY,
            ..Camera3d::default()
        };
        let matrix = camera_matrix(camera, 1.0).unwrap();
        let near = matrix.project_point3(Vec3::new(0.0, 0.0, 3.0 - camera.near));
        assert!(near.z.abs() < 0.0001);
        let far = matrix.project_point3(Vec3::new(0.0, 0.0, 3.0 - camera.far));
        assert!((far.z - 1.0).abs() < 0.0001);
        for orientation in [
            Quaternion::from_xyzw(0.0, 0.0, 0.0, 0.0),
            Quaternion::from_xyzw(0.0, 0.0, 0.0, 2.0),
            Quaternion::from_xyzw(f32::NAN, 0.0, 0.0, 1.0),
        ] {
            camera.orientation = orientation;
            assert!(camera_matrix(camera, 1.0).is_err());
        }
        camera.orientation = Quaternion::IDENTITY;
        camera.position[0] = f32::INFINITY;
        assert!(camera_matrix(camera, 1.0).is_err());
    }
}
