use crate::{DEFAULT_CLEAR_COLOR, RenderStatus, acquired_frame};
use nico_assets::Texture;
use nico_presentation::{Quad, Scene2d, UiScene};
use nico_rhi::*;
use std::{
    ops::Range,
    sync::{Arc, Weak},
};

const MAX_QUADS: usize = 4096;
const STRIDE: u64 = 32;

struct Uploaded<D: RhiDevice> {
    source: Weak<Texture>,
    _texture: D::Texture,
    _view: D::TextureView,
    binding: D::BindGroup,
}

/// Shared world/HUD textured-quad pipeline. Texture identity is the immutable Arc
/// allocation, so replacing CPU content uploads a new resource. Only textures used
/// by the current frame remain cached. RHI providers retain submitted resources.
pub struct QuadRenderPipeline<D: RhiDevice> {
    shader: D::ShaderModule,
    pipeline: D::RenderPipeline,
    layout: D::BindGroupLayout,
    sampler: D::Sampler,
    vertices: D::Buffer,
    textures: Vec<Uploaded<D>>,
    fallback: Arc<Texture>,
    format: TextureFormat,
    vertex_entry: String,
    fragment_entry: String,
}

impl<D: RhiDevice> QuadRenderPipeline<D> {
    pub fn new(
        device: &D,
        format: TextureFormat,
        artifact: GraphicsShaderArtifact<'_>,
    ) -> Result<Self, RhiError> {
        let shader = device.create_shader_module(artifact.module)?;
        let layout = device.create_bind_group_layout(BindGroupLayoutDescriptor {
            label: Some("quad texture layout"),
            entries: &[
                BindGroupLayoutEntry {
                    binding: 0,
                    visibility: ShaderStages::FRAGMENT,
                    binding_type: BindingType::Texture {
                        sample_kind: TextureSampleKind::Float { filterable: true },
                        dimension: TextureViewDimension::Two,
                        multisampled: false,
                    },
                },
                BindGroupLayoutEntry {
                    binding: 1,
                    visibility: ShaderStages::FRAGMENT,
                    binding_type: BindingType::Sampler(SamplerBindingKind::Filtering),
                },
            ],
        })?;
        let pipeline = pipeline(
            device,
            &shader,
            &layout,
            format,
            artifact.vertex_entry_point,
            artifact.fragment_entry_point,
        )?;
        let sampler = device.create_sampler(SamplerDescriptor {
            label: Some("quad nearest sampler"),
            lod_max: 0.0,
            ..SamplerDescriptor::default()
        })?;
        let vertices = device.create_buffer(BufferDescriptor {
            label: Some("quad vertices"),
            size: MAX_QUADS as u64 * 6 * STRIDE,
            usages: BufferUsages::VERTEX | BufferUsages::COPY_DESTINATION,
        })?;
        let fallback = Arc::new(
            Texture::rgba8(
                2,
                2,
                vec![
                    255, 0, 255, 255, 30, 30, 30, 255, 30, 30, 30, 255, 255, 0, 255, 255,
                ],
            )
            .expect("valid fallback"),
        );
        Ok(Self {
            shader,
            pipeline,
            layout,
            sampler,
            vertices,
            textures: Vec::new(),
            fallback,
            format,
            vertex_entry: artifact.vertex_entry_point.into(),
            fragment_entry: artifact.fragment_entry_point.into(),
        })
    }

    /// Logical viewport dimensions include DPI scaling supplied by the host.
    #[allow(clippy::too_many_arguments)]
    pub fn render<Q: RhiQueue<D>, S: RhiSurface<D, Q>>(
        &mut self,
        device: &D,
        queue: &Q,
        surface: &mut S,
        scene: &Scene2d,
        ui: &UiScene,
        viewport: [f32; 2],
    ) -> Result<RenderStatus, RhiError> {
        if viewport.iter().any(|v| *v <= 0.0 || !v.is_finite()) {
            return Ok(RenderStatus::ZeroSized);
        }
        geometry(scene, ui, viewport)?;
        validate_resources(device, scene, ui)?;
        let (frame, view) = match acquired_frame(surface.acquire(device)?) {
            Ok(frame) => frame,
            Err(status) => return Ok(status),
        };
        let sources: Vec<_> = scene
            .world
            .iter()
            .chain(&ui.quads)
            .filter_map(|q| q.texture.clone())
            .collect();
        self.retain_sources(&sources);
        self.draw_into(
            device,
            queue,
            &view,
            surface.format(),
            scene,
            ui,
            viewport,
            LoadOp::Clear(DEFAULT_CLEAR_COLOR),
        )?;
        surface.present(device, queue, frame);
        Ok(RenderStatus::Presented)
    }

    pub(crate) fn retain_sources(&mut self, sources: &[Arc<Texture>]) {
        self.textures.retain(|uploaded| {
            uploaded.source.ptr_eq(&Arc::downgrade(&self.fallback))
                || sources
                    .iter()
                    .any(|source| uploaded.source.ptr_eq(&Arc::downgrade(source)))
        });
    }
    pub(crate) fn texture_slot<Q: RhiQueue<D>>(
        &mut self,
        device: &D,
        queue: &Q,
        source: Option<&Arc<Texture>>,
    ) -> Result<usize, RhiError> {
        let source = source.unwrap_or(&self.fallback);
        if let Some(slot) = self
            .textures
            .iter()
            .position(|u| u.source.ptr_eq(&Arc::downgrade(source)))
        {
            return Ok(slot);
        }
        self.textures
            .push(upload(device, queue, &self.layout, &self.sampler, source)?);
        Ok(self.textures.len() - 1)
    }
    #[allow(clippy::too_many_arguments)]
    pub(crate) fn draw_into<Q: RhiQueue<D>>(
        &mut self,
        device: &D,
        queue: &Q,
        view: &D::TextureView,
        format: TextureFormat,
        scene: &Scene2d,
        ui: &UiScene,
        viewport: [f32; 2],
        load: LoadOp<Color>,
    ) -> Result<(), RhiError> {
        let geometry = geometry(scene, ui, viewport)?;
        if format != self.format {
            self.pipeline = pipeline(
                device,
                &self.shader,
                &self.layout,
                format,
                &self.vertex_entry,
                &self.fragment_entry,
            )?;
            self.format = format;
        }
        let mut draws: Vec<(usize, Range<u32>)> = Vec::new();
        for (index, quad) in scene.world.iter().chain(&ui.quads).enumerate() {
            let slot = self.texture_slot(device, queue, quad.texture.as_ref())?;
            let start = index as u32 * 6;
            if let Some((last_slot, range)) = draws.last_mut()
                && index != scene.world.len()
                && *last_slot == slot
            {
                range.end = start + 6;
            } else {
                draws.push((slot, start..start + 6));
            }
        }
        if !geometry.is_empty() {
            queue.write_buffer(&self.vertices, 0, &geometry);
        }
        let mut encoder = device.create_command_encoder(Some("canvas encoder"));
        let boundary = scene.world.len() as u32 * 6;
        for ui_pass in [false, true] {
            let attachments = [Some(RenderPassColorAttachment {
                view,
                resolve_target: None,
                operations: Operations {
                    load: if ui_pass { LoadOp::Load } else { load },
                    store: StoreOp::Store,
                },
            })];
            let mut pass = encoder.begin_render_pass(RenderPassDescriptor {
                label: Some(if ui_pass { "UI and HUD" } else { "Scene2D" }),
                color_attachments: &attachments,
                depth_stencil_attachment: None,
            });
            pass.set_pipeline(&self.pipeline);
            if !geometry.is_empty() {
                pass.set_vertex_buffer(0, &self.vertices, 0..geometry.len() as u64);
            }
            for (slot, range) in &draws {
                if (range.start >= boundary) != ui_pass {
                    continue;
                }
                pass.set_bind_group(0, &self.textures[*slot].binding, &[]);
                pass.draw(range.clone(), 0..1);
            }
        }
        queue.submit(vec![encoder.finish()]);
        Ok(())
    }
}

pub(crate) fn validate_resources<D: RhiDevice>(
    device: &D,
    scene: &Scene2d,
    ui: &UiScene,
) -> Result<(), RhiError> {
    for texture in scene
        .world
        .iter()
        .chain(&ui.quads)
        .filter_map(|q| q.texture.as_deref())
    {
        crate::validate_texture(device, texture)?;
    }
    Ok(())
}

fn upload<D: RhiDevice, Q: RhiQueue<D>>(
    device: &D,
    queue: &Q,
    layout: &D::BindGroupLayout,
    sampler: &D::Sampler,
    source: &Arc<Texture>,
) -> Result<Uploaded<D>, RhiError> {
    crate::validate_texture(device, source)?;
    let extent = Extent3d::surface(source.width(), source.height());
    let texture = device.create_texture(TextureDescriptor {
        label: Some("quad sRGB texture"),
        extent,
        mip_levels: 1,
        samples: 1,
        dimension: TextureDimension::Two,
        format: TextureFormat::Rgba8UnormSrgb,
        usages: TextureUsages::SAMPLED | TextureUsages::COPY_DESTINATION,
    })?;
    queue.write_texture(
        TextureCopy {
            texture: &texture,
            mip_level: 0,
            origin: Origin3d::default(),
        },
        source.pixels(),
        TextureDataLayout {
            offset: 0,
            bytes_per_row: Some(source.width() * 4),
            rows_per_image: Some(source.height()),
        },
        extent,
    );
    let view = device.create_texture_view(&texture, TextureViewDescriptor::default())?;
    let binding = device.create_bind_group(BindGroupDescriptor {
        label: Some("quad texture binding"),
        layout,
        entries: &[
            BindGroupEntry {
                binding: 0,
                resource: BindingResource::TextureView(&view),
            },
            BindGroupEntry {
                binding: 1,
                resource: BindingResource::Sampler(sampler),
            },
        ],
    })?;
    Ok(Uploaded {
        source: Arc::downgrade(source),
        _texture: texture,
        _view: view,
        binding,
    })
}

fn pipeline<D: RhiDevice>(
    device: &D,
    shader: &D::ShaderModule,
    texture_layout: &D::BindGroupLayout,
    format: TextureFormat,
    vertex: &str,
    fragment: &str,
) -> Result<D::RenderPipeline, RhiError> {
    let layout = device.create_pipeline_layout(PipelineLayoutDescriptor {
        label: Some("quad pipeline layout"),
        bind_group_layouts: &[texture_layout],
    })?;
    device.create_render_pipeline(RenderPipelineDescriptor {
        label: Some("straight-alpha quad pipeline"),
        layout: &layout,
        vertex: VertexState {
            shader,
            entry_point: vertex,
            buffers: &[VertexBufferLayout {
                stride: STRIDE,
                step_mode: VertexStepMode::Vertex,
                attributes: &[
                    VertexAttribute {
                        format: VertexFormat::Float32x2,
                        offset: 0,
                        shader_location: 0,
                    },
                    VertexAttribute {
                        format: VertexFormat::Float32x2,
                        offset: 8,
                        shader_location: 1,
                    },
                    VertexAttribute {
                        format: VertexFormat::Float32x4,
                        offset: 16,
                        shader_location: 2,
                    },
                ],
            }],
        },
        fragment: Some(FragmentState {
            shader,
            entry_point: fragment,
            targets: &[Some(ColorTargetState {
                format,
                write_mask: ColorWrites::ALL,
                blend: Some(BlendState {
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
            })],
        }),
        primitive: PrimitiveState::default(),
        depth_stencil: None,
        multisample: MultisampleState::default(),
    })
}

fn invalid(message: &str) -> RhiError {
    RhiError::new(RhiErrorKind::InvalidDescriptor, message)
}

pub(crate) fn geometry(
    scene: &Scene2d,
    ui: &UiScene,
    viewport: [f32; 2],
) -> Result<Vec<u8>, RhiError> {
    let count = scene.world.len().saturating_add(ui.quads.len());
    if count > MAX_QUADS {
        return Err(invalid("quad limit exceeded"));
    }
    if !scene.world.is_empty()
        && (!scene.camera.pixels_per_unit.is_finite()
            || scene.camera.pixels_per_unit <= 0.0
            || scene.camera.center.iter().any(|v| !v.is_finite()))
    {
        return Err(invalid("invalid 2D camera"));
    }
    let mut bytes = Vec::with_capacity(count * 6 * STRIDE as usize);
    for (quad, world) in scene
        .world
        .iter()
        .map(|q| (q, true))
        .chain(ui.quads.iter().map(|q| (q, false)))
    {
        append_quad(&mut bytes, quad, scene, viewport, world)?;
    }
    Ok(bytes)
}

fn append_quad(
    bytes: &mut Vec<u8>,
    quad: &Quad,
    scene: &Scene2d,
    viewport: [f32; 2],
    world: bool,
) -> Result<(), RhiError> {
    if quad
        .center
        .iter()
        .chain(&quad.size)
        .chain(&quad.color)
        .any(|v| !v.is_finite())
        || quad.size.iter().any(|v| *v < 0.0)
    {
        return Err(invalid(
            "quad coordinates/color must be finite and size nonnegative",
        ));
    }
    let scale = if world {
        scene.camera.pixels_per_unit
    } else {
        1.0
    };
    let center = if world {
        [
            (quad.center[0] - scene.camera.center[0]) * scale + viewport[0] * 0.5,
            -(quad.center[1] - scene.camera.center[1]) * scale + viewport[1] * 0.5,
        ]
    } else {
        quad.center
    };
    for [u, v] in [
        [0.0, 0.0],
        [0.0, 1.0],
        [1.0, 1.0],
        [0.0, 0.0],
        [1.0, 1.0],
        [1.0, 0.0],
    ] {
        let x = (center[0] + (u - 0.5) * quad.size[0] * scale) * 2.0 / viewport[0] - 1.0;
        let y = 1.0 - (center[1] + (v - 0.5) * quad.size[1] * scale) * 2.0 / viewport[1];
        if !x.is_finite() || !y.is_finite() {
            return Err(invalid("quad transform overflow"));
        }
        for value in [
            x,
            y,
            u,
            v,
            quad.color[0],
            quad.color[1],
            quad.color[2],
            quad.color[3],
        ] {
            bytes.extend_from_slice(&value.to_le_bytes());
        }
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn quad() -> Quad {
        Quad {
            center: [0.0, 0.0],
            size: [1.0, 1.0],
            color: [1.0; 4],
            texture: None,
        }
    }
    fn float(bytes: &[u8], offset: usize) -> f32 {
        f32::from_le_bytes(bytes[offset..offset + 4].try_into().unwrap())
    }
    #[test]
    fn ui_only_geometry_does_not_depend_on_an_unused_world_camera() {
        let mut scene = Scene2d::default();
        let ui = UiScene {
            quads: vec![quad()],
        };
        let expected = geometry(&scene, &ui, [100.; 2]).unwrap();
        scene.camera.pixels_per_unit = f32::NAN;
        scene.camera.center = [f32::INFINITY; 2];
        assert_eq!(geometry(&scene, &ui, [100.; 2]).unwrap(), expected);
        scene.world.push(quad());
        assert!(geometry(&scene, &ui, [100.; 2]).is_err());
        let invalid = UiScene {
            quads: vec![Quad {
                size: [-1.; 2],
                ..quad()
            }],
        };
        assert!(geometry(&Scene2d::default(), &invalid, [100.; 2]).is_err());
        assert!(
            geometry(
                &Scene2d::default(),
                &UiScene {
                    quads: vec![quad(); MAX_QUADS + 1]
                },
                [100.; 2]
            )
            .is_err()
        );
    }
    #[test]
    fn world_camera_moves_sprites_but_not_hud() {
        let mut scene = Scene2d {
            world: vec![quad()],
            ..Scene2d::default()
        };
        let ui = UiScene {
            quads: vec![Quad {
                center: [48.0, 48.0],
                size: [48.0, 48.0],
                ..quad()
            }],
        };
        let first = geometry(&scene, &ui, [1280.0, 720.0]).unwrap();
        scene.camera.center[0] = 1.0;
        let second = geometry(&scene, &ui, [1280.0, 720.0]).unwrap();
        assert!((float(&second, 0) - float(&first, 0) + 0.2).abs() < 0.00001);
        assert_eq!(&first[192..], &second[192..]);
        assert_eq!(float(&first, 8), 0.0);
        assert_eq!(float(&first, 12), 0.0);
    }
    #[test]
    fn entity_removal_and_empty_scene_remove_geometry() {
        let mut scene = Scene2d {
            world: vec![quad()],
            ..Scene2d::default()
        };
        assert_eq!(
            geometry(&scene, &UiScene::default(), [100.0, 100.0])
                .unwrap()
                .len(),
            192
        );
        scene.world.clear();
        assert!(
            geometry(&scene, &UiScene::default(), [100.0, 100.0])
                .unwrap()
                .is_empty()
        );
    }
    #[test]
    fn invalid_geometry_and_excessive_draws_are_rejected() {
        let mut scene = Scene2d {
            world: vec![quad()],
            ..Scene2d::default()
        };
        scene.world[0].center[0] = f32::NAN;
        assert!(geometry(&scene, &UiScene::default(), [100.0, 100.0]).is_err());
        scene.world = vec![quad(); MAX_QUADS + 1];
        assert!(geometry(&scene, &UiScene::default(), [100.0, 100.0]).is_err());
    }
}
