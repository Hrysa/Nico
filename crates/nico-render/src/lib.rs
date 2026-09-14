//! Backend-neutral rendering policy built on Nico's RHI.

mod quads;
pub use quads::QuadRenderPipeline;
mod meshes;
pub use meshes::MeshRenderPipeline;

use nico_rhi::{
    Color, ColorTargetState, ColorWrites, FragmentState, GraphicsShaderArtifact, LoadOp,
    MultisampleState, Operations, PipelineLayoutDescriptor, PrimitiveState,
    RenderPassColorAttachment, RenderPassDescriptor, RenderPipelineDescriptor, RhiCommandEncoder,
    RhiDevice, RhiError, RhiQueue, RhiRenderPass, RhiSurface, StoreOp, SurfaceAcquire,
    TextureFormat, VertexState,
};

const DEFAULT_CLEAR_COLOR: Color = Color::new(0.055, 0.065, 0.085, 1.0);

/// Non-fatal outcome of attempting to render one presentation frame.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RenderStatus {
    Presented,
    ZeroSized,
    Timeout,
    Occluded,
}

/// First render pipeline used to exercise the backend-neutral rendering path.
pub struct BootstrapRenderPipeline<D: RhiDevice> {
    shader: D::ShaderModule,
    pipeline: D::RenderPipeline,
    target_format: TextureFormat,
    vertex_entry_point: Box<str>,
    fragment_entry_point: Box<str>,
    clear_color: Color,
}

impl<D: RhiDevice> BootstrapRenderPipeline<D> {
    /// Creates the shader module and graphics pipeline for the current surface format.
    pub fn new(
        device: &D,
        target_format: TextureFormat,
        artifact: GraphicsShaderArtifact<'_>,
    ) -> Result<Self, RhiError> {
        let shader = device.create_shader_module(artifact.module)?;
        let vertex_entry_point = Box::<str>::from(artifact.vertex_entry_point);
        let fragment_entry_point = Box::<str>::from(artifact.fragment_entry_point);
        let pipeline = create_pipeline(
            device,
            &shader,
            target_format,
            &vertex_entry_point,
            &fragment_entry_point,
        )?;
        Ok(Self {
            shader,
            pipeline,
            target_format,
            vertex_entry_point,
            fragment_entry_point,
            clear_color: DEFAULT_CLEAR_COLOR,
        })
    }

    /// Changes the color loaded before the bootstrap triangle is drawn.
    pub fn set_clear_color(&mut self, color: Color) {
        self.clear_color = color;
    }

    /// Acquires, records, submits, and presents one frame.
    pub fn render<Q, S>(
        &mut self,
        device: &D,
        queue: &Q,
        surface: &mut S,
    ) -> Result<RenderStatus, RhiError>
    where
        Q: RhiQueue<D>,
        S: RhiSurface<D, Q>,
    {
        let (frame, view) = match acquired_frame(surface.acquire(device)?) {
            Ok(frame) => frame,
            Err(status) => return Ok(status),
        };

        let surface_format = surface.format();
        if self.target_format != surface_format {
            self.pipeline = create_pipeline(
                device,
                &self.shader,
                surface_format,
                &self.vertex_entry_point,
                &self.fragment_entry_point,
            )?;
            self.target_format = surface_format;
        }

        let attachments = [Some(RenderPassColorAttachment {
            view: &view,
            resolve_target: None,
            operations: Operations {
                load: LoadOp::Clear(self.clear_color),
                store: StoreOp::Store,
            },
        })];
        let mut encoder = device.create_command_encoder(Some("Nico bootstrap render encoder"));
        {
            let mut pass = encoder.begin_render_pass(RenderPassDescriptor {
                label: Some("Nico bootstrap render pass"),
                color_attachments: &attachments,
                depth_stencil_attachment: None,
            });
            pass.set_pipeline(&self.pipeline);
            pass.draw(0..3, 0..1);
        }
        queue.submit(vec![encoder.finish()]);
        surface.present(device, queue, frame);
        Ok(RenderStatus::Presented)
    }
}

fn acquired_frame<F, V>(acquired: SurfaceAcquire<F, V>) -> Result<(F, V), RenderStatus> {
    match acquired {
        SurfaceAcquire::Acquired { frame, view } => Ok((frame, view)),
        SurfaceAcquire::ZeroSized => Err(RenderStatus::ZeroSized),
        SurfaceAcquire::Timeout => Err(RenderStatus::Timeout),
        SurfaceAcquire::Occluded => Err(RenderStatus::Occluded),
    }
}

fn create_pipeline<D: RhiDevice>(
    device: &D,
    shader: &D::ShaderModule,
    target_format: TextureFormat,
    vertex_entry_point: &str,
    fragment_entry_point: &str,
) -> Result<D::RenderPipeline, RhiError> {
    let layout = device.create_pipeline_layout(PipelineLayoutDescriptor {
        label: Some("Nico bootstrap pipeline layout"),
        bind_group_layouts: &[],
    })?;
    let targets = [Some(ColorTargetState {
        format: target_format,
        blend: None,
        write_mask: ColorWrites::ALL,
    })];
    device.create_render_pipeline(RenderPipelineDescriptor {
        label: Some("Nico bootstrap pipeline"),
        layout: &layout,
        vertex: VertexState {
            shader,
            entry_point: vertex_entry_point,
            buffers: &[],
        },
        fragment: Some(FragmentState {
            shader,
            entry_point: fragment_entry_point,
            targets: &targets,
        }),
        primitive: PrimitiveState::default(),
        depth_stencil: None,
        multisample: MultisampleState::default(),
    })
}

#[cfg(test)]
mod tests {
    use nico_rhi::SurfaceAcquire;

    use super::{RenderStatus, acquired_frame};

    #[test]
    fn non_presentable_surface_outcomes_remain_non_fatal() {
        assert_eq!(
            acquired_frame::<(), ()>(SurfaceAcquire::ZeroSized),
            Err(RenderStatus::ZeroSized)
        );
        assert_eq!(
            acquired_frame::<(), ()>(SurfaceAcquire::Timeout),
            Err(RenderStatus::Timeout)
        );
        assert_eq!(
            acquired_frame::<(), ()>(SurfaceAcquire::Occluded),
            Err(RenderStatus::Occluded)
        );
    }

    #[test]
    fn acquired_surface_frame_is_forwarded_to_rendering() {
        assert_eq!(
            acquired_frame(SurfaceAcquire::Acquired {
                frame: "frame",
                view: "view",
            }),
            Ok(("frame", "view"))
        );
    }
}
