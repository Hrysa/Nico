use crate::{DEFAULT_CLEAR_COLOR, QuadRenderPipeline, RenderStatus, acquired_frame};
use glam::{Mat4, Vec3};
use nico_assets::{Mesh, MeshVertex};
use nico_presentation::{Camera3d, MeshInstance, Scene2d, Scene3d};
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
struct Depth<D: RhiDevice> {
    _texture: D::Texture,
    view: D::TextureView,
    extent: Extent3d,
}

/// Unlit indexed mesh rendering with depth and alpha cutoff, followed by the shared
/// quad/HUD pass. Both passes share GPU texture uploads, bindings, and retirement.
pub struct MeshRenderPipeline<D: RhiDevice> {
    hud: QuadRenderPipeline<D>,
    shader: D::ShaderModule,
    pipeline: D::RenderPipeline,
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
        let hud = QuadRenderPipeline::new(device, format, quad_shader)?;
        let shader = device.create_shader_module(mesh_shader.module)?;
        let uniform_layout = device.create_bind_group_layout(BindGroupLayoutDescriptor {
            label: Some("mesh transform layout"),
            entries: &[BindGroupLayoutEntry {
                binding: 0,
                visibility: ShaderStages::VERTEX | ShaderStages::FRAGMENT,
                binding_type: BindingType::Buffer {
                    kind: BufferBindingKind::Uniform,
                    dynamic_offset: false,
                    minimum_size: NonZeroU64::new(80),
                },
            }],
        })?;
        let pipeline = pipeline(
            device,
            &shader,
            hud.texture_layout(),
            &uniform_layout,
            format,
            mesh_shader.vertex_entry_point,
            mesh_shader.fragment_entry_point,
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
            hud,
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

    /// Physical extent owns depth allocation; logical viewport controls HUD sizing.
    #[allow(clippy::too_many_arguments)]
    pub fn render<Q: RhiQueue<D>, S: RhiSurface<D, Q>>(
        &mut self,
        device: &D,
        queue: &Q,
        surface: &mut S,
        scene: &Scene3d,
        hud_scene: &Scene2d,
        viewport: [f32; 2],
        extent: Extent3d,
    ) -> Result<RenderStatus, RhiError> {
        if extent.is_zero() || viewport.iter().any(|v| *v <= 0.0 || !v.is_finite()) {
            return Ok(RenderStatus::ZeroSized);
        }
        if scene.meshes.len() > MAX_INSTANCES {
            return Err(invalid("mesh instance limit exceeded"));
        }
        let camera = camera_matrix(scene.camera, viewport[0] / viewport[1])?;
        let transforms: Vec<_> = scene
            .meshes
            .iter()
            .map(|mesh| transform(camera, mesh))
            .collect::<Result<_, _>>()?;
        let (frame, view) = match acquired_frame(surface.acquire(device)?) {
            Ok(value) => value,
            Err(status) => return Ok(status),
        };
        if surface.format() != self.format {
            self.pipeline = pipeline(
                device,
                &self.shader,
                self.hud.texture_layout(),
                &self.uniform_layout,
                surface.format(),
                &self.vertex_entry,
                &self.fragment_entry,
            )?;
            self.format = surface.format();
        }
        if self.depth.as_ref().is_none_or(|d| d.extent != extent) {
            if extent.depth_or_layers != 1
                || extent.width > device.capabilities().limits.max_texture_dimension_2d
                || extent.height > device.capabilities().limits.max_texture_dimension_2d
            {
                return Err(invalid("invalid depth target extent"));
            }
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
        let sources: Vec<_> = scene
            .meshes
            .iter()
            .filter_map(|m| m.texture.clone())
            .chain(
                hud_scene
                    .world
                    .iter()
                    .chain(&hud_scene.hud)
                    .filter_map(|q| q.texture.clone()),
            )
            .collect();
        self.hud.retain_sources(&sources);
        self.meshes.retain(|uploaded| {
            scene.meshes.iter().any(|m| {
                uploaded
                    .source
                    .ptr_eq(&Arc::downgrade(m.mesh.as_ref().unwrap_or(&self.fallback)))
            })
        });
        self.uniforms.truncate(scene.meshes.len());
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
            let texture_slot = self.hud.texture_slot(
                device,
                queue,
                if instance.mesh.is_some() {
                    instance.texture.as_ref()
                } else {
                    None
                },
            )?;
            if index == self.uniforms.len() {
                let buffer = device.create_buffer(BufferDescriptor {
                    label: Some("mesh transform and color"),
                    size: 80,
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
                            size: NonZeroU64::new(80),
                        },
                    }],
                })?;
                self.uniforms.push(Uniform { buffer, binding });
            }
            let mut bytes = Vec::with_capacity(80);
            for value in transforms[index]
                .to_cols_array()
                .into_iter()
                .chain(instance.color)
            {
                bytes.extend_from_slice(&value.to_le_bytes());
            }
            queue.write_buffer(&self.uniforms[index].buffer, 0, &bytes);
            draws.push((mesh_slot, texture_slot));
        }
        let colors = [Some(RenderPassColorAttachment {
            view: &view,
            resolve_target: None,
            operations: Operations {
                load: LoadOp::Clear(DEFAULT_CLEAR_COLOR),
                store: StoreOp::Store,
            },
        })];
        let mut encoder = device.create_command_encoder(Some("mesh encoder"));
        {
            let mut pass = encoder.begin_render_pass(RenderPassDescriptor {
                label: Some("unlit meshes"),
                color_attachments: &colors,
                depth_stencil_attachment: Some(RenderPassDepthStencilAttachment {
                    view: &self.depth.as_ref().unwrap().view,
                    depth_operations: Some(Operations {
                        load: LoadOp::Clear(1.0),
                        store: StoreOp::Discard,
                    }),
                    stencil_operations: None,
                }),
            });
            pass.set_pipeline(&self.pipeline);
            for (index, (mesh_slot, texture_slot)) in draws.into_iter().enumerate() {
                let mesh = &self.meshes[mesh_slot];
                pass.set_bind_group(0, self.hud.texture_binding(texture_slot), &[]);
                pass.set_bind_group(1, &self.uniforms[index].binding, &[]);
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
        self.hud.draw_into(
            device,
            queue,
            &view,
            self.format,
            hud_scene,
            viewport,
            LoadOp::Load,
        )?;
        surface.present(device, queue, frame);
        Ok(RenderStatus::Presented)
    }
}

fn upload<D: RhiDevice, Q: RhiQueue<D>>(
    device: &D,
    queue: &Q,
    source: &Arc<Mesh>,
) -> Result<Uploaded<D>, RhiError> {
    if source.vertices().len() > 250_000 || source.indices().len() > 750_000 {
        return Err(invalid("GPU mesh geometry limit exceeded"));
    }
    let mut vertices = Vec::with_capacity(source.vertices().len() * 20);
    for vertex in source.vertices() {
        for value in vertex.position.into_iter().chain(vertex.uv) {
            vertices.extend_from_slice(&value.to_le_bytes());
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

fn pipeline<D: RhiDevice>(
    device: &D,
    shader: &D::ShaderModule,
    texture_layout: &D::BindGroupLayout,
    uniform_layout: &D::BindGroupLayout,
    format: TextureFormat,
    vertex: &str,
    fragment: &str,
) -> Result<D::RenderPipeline, RhiError> {
    let layout = device.create_pipeline_layout(PipelineLayoutDescriptor {
        label: Some("mesh pipeline layout"),
        bind_group_layouts: &[texture_layout, uniform_layout],
    })?;
    device.create_render_pipeline(RenderPipelineDescriptor {
        label: Some("unlit alpha-cutoff meshes"),
        layout: &layout,
        vertex: VertexState {
            shader,
            entry_point: vertex,
            buffers: &[VertexBufferLayout {
                stride: 20,
                step_mode: VertexStepMode::Vertex,
                attributes: &[
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
                ],
            }],
        },
        fragment: Some(FragmentState {
            shader,
            entry_point: fragment,
            targets: &[Some(ColorTargetState {
                format,
                blend: None,
                write_mask: ColorWrites::ALL,
            })],
        }),
        primitive: PrimitiveState::default(),
        depth_stencil: Some(DepthStencilState {
            format: TextureFormat::Depth32Float,
            depth_write_enabled: true,
            depth_compare: CompareFunction::Less,
        }),
        multisample: MultisampleState::default(),
    })
}

fn invalid(message: &str) -> RhiError {
    RhiError::new(RhiErrorKind::InvalidDescriptor, message)
}
fn camera_matrix(camera: Camera3d, aspect: f32) -> Result<Mat4, RhiError> {
    let position = Vec3::from(camera.position);
    if !camera.has_valid_pose()
        || !aspect.is_finite()
        || aspect <= 0.0
        || !camera.near.is_finite()
        || !camera.far.is_finite()
        || camera.near <= 0.0
        || camera.far <= camera.near
        || !(0.01..3.13).contains(&camera.vertical_fov_radians)
    {
        return Err(invalid("invalid perspective camera"));
    }
    let result = Mat4::perspective_rh(camera.vertical_fov_radians, aspect, camera.near, camera.far)
        * Mat4::from_quat(camera.orientation.conjugate())
        * Mat4::from_translation(-position);
    if !result.is_finite() {
        return Err(invalid("camera transform overflow"));
    }
    Ok(result)
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
