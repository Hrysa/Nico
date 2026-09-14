//! `wgpu` implementation of Nico's rendering hardware interface.

#[cfg(test)]
mod quad_tests;

mod snapshot;
pub use snapshot::CapturedPixels;

use std::{
    borrow::Cow,
    fmt::Debug,
    sync::{Arc, Mutex},
};

use nico_rhi::*;
use raw_window_handle::{HasDisplayHandle, HasWindowHandle};

#[derive(Clone, Debug)]
pub struct WgpuBuffer(wgpu::Buffer);
#[derive(Clone, Debug)]
pub struct WgpuTexture(wgpu::Texture);
#[derive(Clone, Debug)]
pub struct WgpuTextureView(wgpu::TextureView);
#[derive(Clone, Debug)]
pub struct WgpuSampler(wgpu::Sampler);
#[derive(Clone, Debug)]
pub struct WgpuShaderModule(wgpu::ShaderModule);
#[derive(Clone, Debug)]
pub struct WgpuBindGroupLayout(wgpu::BindGroupLayout);
#[derive(Clone, Debug)]
pub struct WgpuBindGroup(wgpu::BindGroup);
#[derive(Clone, Debug)]
pub struct WgpuPipelineLayout(wgpu::PipelineLayout);
#[derive(Clone, Debug)]
pub struct WgpuRenderPipeline(wgpu::RenderPipeline);
#[derive(Clone, Debug)]
pub struct WgpuComputePipeline(wgpu::ComputePipeline);
#[derive(Debug)]
pub struct WgpuCommandBuffer(wgpu::CommandBuffer);
#[derive(Debug)]
pub struct WgpuCommandEncoder(wgpu::CommandEncoder);

/// Resource factory for the selected wgpu adapter.
pub struct WgpuDevice {
    inner: wgpu::Device,
    capabilities: Capabilities,
    failure: Arc<Mutex<Option<BackendFailure>>>,
}

/// Upload and submission queue paired with [`WgpuDevice`].
pub struct WgpuQueue {
    inner: wgpu::Queue,
}

/// Native presentation surface and its recovery state.
pub struct WgpuSurface<W> {
    instance: wgpu::Instance,
    window: Arc<W>,
    inner: wgpu::Surface<'static>,
    adapter: wgpu::Adapter,
    configuration: wgpu::SurfaceConfiguration,
    extent: Extent3d,
    configured: bool,
    capture_requested: bool,
    capture_result: Option<Result<CapturedPixels, String>>,
    failure: Arc<Mutex<Option<BackendFailure>>>,
}

/// Concrete composition of wgpu device, queue, and surface providers.
pub struct WgpuBackend<W> {
    device: WgpuDevice,
    queue: WgpuQueue,
    surface: WgpuSurface<W>,
}

pub struct WgpuSurfaceFrame {
    texture: wgpu::SurfaceTexture,
    reconfigure_after_present: bool,
}

pub struct WgpuRenderPass<'pass>(wgpu::RenderPass<'pass>);
pub struct WgpuComputePass<'pass>(wgpu::ComputePass<'pass>);

#[derive(Clone)]
struct BackendFailure {
    kind: RhiErrorKind,
    message: String,
}

enum AcquiredSurfaceTexture {
    Texture(wgpu::SurfaceTexture, bool),
    Timeout,
    Occluded,
}

impl<W> WgpuBackend<W>
where
    W: HasWindowHandle + Send + Sync + 'static,
{
    /// Creates the adapter, device, queue, and surface for an owned window.
    pub async fn new<D>(window: Arc<W>, display: D, extent: Extent3d) -> Result<Self, RhiError>
    where
        D: HasDisplayHandle + Debug + Send + Sync + 'static,
    {
        let descriptor = wgpu::InstanceDescriptor {
            backends: wgpu::Backends::PRIMARY,
            ..wgpu::InstanceDescriptor::new_with_display_handle(Box::new(display))
        }
        .with_env();
        let instance = wgpu::Instance::new(descriptor);
        let surface = instance
            .create_surface(wgpu::SurfaceTarget::from_window_without_display(
                window.clone(),
            ))
            .map_err(|error| backend_error("failed to create presentation surface", error))?;
        let adapter = instance
            .request_adapter(&wgpu::RequestAdapterOptions {
                power_preference: wgpu::PowerPreference::HighPerformance,
                force_fallback_adapter: false,
                compatible_surface: Some(&surface),
                apply_limit_buckets: false,
            })
            .await
            .map_err(|error| {
                RhiError::new(
                    RhiErrorKind::Unsupported,
                    format!("failed to find a compatible graphics adapter: {error}"),
                )
            })?;
        let (inner_device, inner_queue) = adapter
            .request_device(&wgpu::DeviceDescriptor {
                label: Some("Nico RHI device"),
                required_features: wgpu::Features::empty(),
                required_limits: wgpu::Limits::default(),
                memory_hints: wgpu::MemoryHints::Performance,
                trace: wgpu::Trace::Off,
                experimental_features: wgpu::ExperimentalFeatures::disabled(),
            })
            .await
            .map_err(|error| backend_error("failed to create graphics device", error))?;

        let failure = Arc::new(Mutex::new(None));
        let lost = failure.clone();
        inner_device.set_device_lost_callback(move |reason, message| {
            record_failure(
                &lost,
                RhiErrorKind::DeviceLost,
                format!("graphics device lost ({reason:?}): {message}"),
            );
        });
        let uncaptured = failure.clone();
        inner_device.on_uncaptured_error(Arc::new(move |error| {
            let kind = if matches!(&error, wgpu::Error::OutOfMemory { .. }) {
                RhiErrorKind::OutOfMemory
            } else {
                RhiErrorKind::Backend
            };
            record_failure(
                &uncaptured,
                kind,
                format!("uncaptured graphics error: {error}"),
            );
        }));

        let adapter_info = adapter.get_info();
        let limits = adapter.limits();
        let capabilities = Capabilities {
            adapter: AdapterInfo {
                name: adapter_info.name.clone(),
                api: graphics_api(adapter_info.backend),
                kind: adapter_kind(adapter_info.device_type),
            },
            limits: Limits {
                max_texture_dimension_2d: limits.max_texture_dimension_2d,
                max_bind_groups: limits.max_bind_groups,
                max_uniform_buffer_binding_size: limits.max_uniform_buffer_binding_size,
                max_storage_buffer_binding_size: limits.max_storage_buffer_binding_size,
                max_vertex_buffers: limits.max_vertex_buffers,
                max_vertex_attributes: limits.max_vertex_attributes,
            },
        };
        tracing::info!(adapter = %adapter_info.name, backend = ?adapter_info.backend,
            device_type = ?adapter_info.device_type, "graphics device created");

        let config_extent = presentable_extent(extent);
        let configuration = surface_configuration(&surface, &adapter, config_extent)?;

        let device = WgpuDevice {
            inner: inner_device,
            capabilities,
            failure: failure.clone(),
        };
        let queue = WgpuQueue { inner: inner_queue };
        let mut surface = WgpuSurface {
            instance,
            window,
            inner: surface,
            adapter,
            configuration,
            extent,
            configured: false,
            capture_requested: false,
            capture_result: None,
            failure,
        };
        surface.configure_if_presentable(&device);
        Ok(Self {
            device,
            queue,
            surface,
        })
    }

    #[must_use]
    pub const fn device(&self) -> &WgpuDevice {
        &self.device
    }
    #[must_use]
    pub const fn queue(&self) -> &WgpuQueue {
        &self.queue
    }
    #[must_use]
    pub const fn surface(&self) -> &WgpuSurface<W> {
        &self.surface
    }

    /// Borrows the three provider roles together for custom frame recording.
    pub const fn parts(&mut self) -> (&WgpuDevice, &WgpuQueue, &mut WgpuSurface<W>) {
        (&self.device, &self.queue, &mut self.surface)
    }

    pub fn resize(&mut self, extent: Extent3d) {
        self.surface.resize(&self.device, extent);
    }
}

impl<W> WgpuSurface<W>
where
    W: HasWindowHandle + Send + Sync + 'static,
{
    fn configure_if_presentable(&mut self, device: &WgpuDevice) {
        if self.extent.is_zero() {
            self.configured = false;
            return;
        }
        self.configuration.width = self.extent.width;
        self.configuration.height = self.extent.height;
        self.inner.configure(&device.inner, &self.configuration);
        self.configured = true;
    }

    fn recreate(&mut self, device: &WgpuDevice) -> Result<(), RhiError> {
        let surface = self
            .instance
            .create_surface(wgpu::SurfaceTarget::from_window_without_display(
                self.window.clone(),
            ))
            .map_err(|error| backend_error("failed to recreate lost surface", error))?;
        let extent = presentable_extent(self.extent);
        let configuration = surface_configuration(&surface, &self.adapter, extent)?;
        self.inner = surface;
        self.configuration = configuration;
        self.configure_if_presentable(device);
        Ok(())
    }

    fn acquire_texture(&mut self, device: &WgpuDevice) -> Result<AcquiredSurfaceTexture, RhiError> {
        match self.inner.get_current_texture() {
            wgpu::CurrentSurfaceTexture::Success(texture) => {
                Ok(AcquiredSurfaceTexture::Texture(texture, false))
            }
            wgpu::CurrentSurfaceTexture::Suboptimal(texture) => {
                Ok(AcquiredSurfaceTexture::Texture(texture, true))
            }
            wgpu::CurrentSurfaceTexture::Outdated => {
                self.configure_if_presentable(device);
                self.retry_texture()
            }
            wgpu::CurrentSurfaceTexture::Lost => {
                self.recreate(device)?;
                self.retry_texture()
            }
            wgpu::CurrentSurfaceTexture::Timeout => Ok(AcquiredSurfaceTexture::Timeout),
            wgpu::CurrentSurfaceTexture::Occluded => Ok(AcquiredSurfaceTexture::Occluded),
            wgpu::CurrentSurfaceTexture::Validation => Err(RhiError::new(
                RhiErrorKind::Backend,
                "graphics validation failed while acquiring a surface image",
            )),
        }
    }

    fn retry_texture(&self) -> Result<AcquiredSurfaceTexture, RhiError> {
        match self.inner.get_current_texture() {
            wgpu::CurrentSurfaceTexture::Success(texture) => {
                Ok(AcquiredSurfaceTexture::Texture(texture, false))
            }
            wgpu::CurrentSurfaceTexture::Suboptimal(texture) => {
                Ok(AcquiredSurfaceTexture::Texture(texture, true))
            }
            wgpu::CurrentSurfaceTexture::Timeout => Ok(AcquiredSurfaceTexture::Timeout),
            wgpu::CurrentSurfaceTexture::Occluded => Ok(AcquiredSurfaceTexture::Occluded),
            status => Err(RhiError::new(
                RhiErrorKind::Backend,
                format!("surface remained unavailable after recovery: {status:?}"),
            )),
        }
    }

    fn current_failure(&self) -> Option<RhiError> {
        current_failure(&self.failure)
    }
}

impl RhiDevice for WgpuDevice {
    type Buffer = WgpuBuffer;
    type Texture = WgpuTexture;
    type TextureView = WgpuTextureView;
    type Sampler = WgpuSampler;
    type ShaderModule = WgpuShaderModule;
    type BindGroupLayout = WgpuBindGroupLayout;
    type BindGroup = WgpuBindGroup;
    type PipelineLayout = WgpuPipelineLayout;
    type RenderPipeline = WgpuRenderPipeline;
    type ComputePipeline = WgpuComputePipeline;
    type CommandBuffer = WgpuCommandBuffer;
    type CommandEncoder = WgpuCommandEncoder;

    fn capabilities(&self) -> &Capabilities {
        &self.capabilities
    }

    fn create_buffer(&self, descriptor: BufferDescriptor<'_>) -> Result<Self::Buffer, RhiError> {
        self.check_failure()?;
        if descriptor.size == 0 {
            return Err(RhiError::new(
                RhiErrorKind::InvalidDescriptor,
                "buffer size must be non-zero",
            ));
        }
        Ok(WgpuBuffer(self.inner.create_buffer(
            &wgpu::BufferDescriptor {
                label: descriptor.label,
                size: descriptor.size,
                usage: buffer_usages(descriptor.usages),
                mapped_at_creation: false,
            },
        )))
    }

    fn create_texture(&self, descriptor: TextureDescriptor<'_>) -> Result<Self::Texture, RhiError> {
        self.check_failure()?;
        if descriptor.extent.is_zero() || descriptor.mip_levels == 0 || descriptor.samples == 0 {
            return Err(RhiError::new(
                RhiErrorKind::InvalidDescriptor,
                "texture extent, mip levels, and sample count must be non-zero",
            ));
        }
        Ok(WgpuTexture(self.inner.create_texture(
            &wgpu::TextureDescriptor {
                label: descriptor.label,
                size: extent3d(descriptor.extent),
                mip_level_count: descriptor.mip_levels,
                sample_count: descriptor.samples,
                dimension: texture_dimension(descriptor.dimension),
                format: texture_format(descriptor.format),
                usage: texture_usages(descriptor.usages),
                view_formats: &[],
            },
        )))
    }

    fn create_texture_view(
        &self,
        texture: &Self::Texture,
        descriptor: TextureViewDescriptor<'_>,
    ) -> Result<Self::TextureView, RhiError> {
        self.check_failure()?;
        Ok(WgpuTextureView(texture.0.create_view(
            &wgpu::TextureViewDescriptor {
                label: descriptor.label,
                format: descriptor.format.map(texture_format),
                dimension: descriptor.dimension.map(texture_view_dimension),
                base_mip_level: descriptor.base_mip_level,
                mip_level_count: descriptor.mip_level_count,
                base_array_layer: descriptor.base_array_layer,
                array_layer_count: descriptor.array_layer_count,
                ..Default::default()
            },
        )))
    }

    fn create_sampler(&self, descriptor: SamplerDescriptor<'_>) -> Result<Self::Sampler, RhiError> {
        self.check_failure()?;
        Ok(WgpuSampler(self.inner.create_sampler(
            &wgpu::SamplerDescriptor {
                label: descriptor.label,
                address_mode_u: address_mode(descriptor.address_u),
                address_mode_v: address_mode(descriptor.address_v),
                address_mode_w: address_mode(descriptor.address_w),
                mag_filter: filter_mode(descriptor.mag_filter),
                min_filter: filter_mode(descriptor.min_filter),
                mipmap_filter: mipmap_filter_mode(descriptor.mipmap_filter),
                lod_min_clamp: descriptor.lod_min,
                lod_max_clamp: descriptor.lod_max,
                compare: descriptor.compare.map(compare_function),
                anisotropy_clamp: descriptor.max_anisotropy,
                ..Default::default()
            },
        )))
    }

    fn create_shader_module(
        &self,
        descriptor: ShaderModuleDescriptor<'_>,
    ) -> Result<Self::ShaderModule, RhiError> {
        self.check_failure()?;
        if descriptor.format != ShaderFormat::Wgsl {
            return Err(RhiError::new(
                RhiErrorKind::Unsupported,
                "the safe wgpu backend currently accepts generated WGSL shader artifacts",
            ));
        }
        let code = std::str::from_utf8(descriptor.code).map_err(|error| {
            RhiError::new(
                RhiErrorKind::InvalidDescriptor,
                format!("WGSL artifact is not UTF-8: {error}"),
            )
        })?;
        Ok(WgpuShaderModule(self.inner.create_shader_module(
            wgpu::ShaderModuleDescriptor {
                label: descriptor.label,
                source: wgpu::ShaderSource::Wgsl(Cow::Borrowed(code)),
            },
        )))
    }

    fn create_bind_group_layout(
        &self,
        descriptor: BindGroupLayoutDescriptor<'_>,
    ) -> Result<Self::BindGroupLayout, RhiError> {
        self.check_failure()?;
        let entries = descriptor
            .entries
            .iter()
            .map(|entry| wgpu::BindGroupLayoutEntry {
                binding: entry.binding,
                visibility: shader_stages(entry.visibility),
                ty: binding_type(entry.binding_type),
                count: None,
            })
            .collect::<Vec<_>>();
        Ok(WgpuBindGroupLayout(self.inner.create_bind_group_layout(
            &wgpu::BindGroupLayoutDescriptor {
                label: descriptor.label,
                entries: &entries,
            },
        )))
    }

    fn create_bind_group(
        &self,
        descriptor: BindGroupDescriptor<
            '_,
            Self::BindGroupLayout,
            Self::Buffer,
            Self::TextureView,
            Self::Sampler,
        >,
    ) -> Result<Self::BindGroup, RhiError> {
        self.check_failure()?;
        let entries = descriptor
            .entries
            .iter()
            .map(|entry| wgpu::BindGroupEntry {
                binding: entry.binding,
                resource: match &entry.resource {
                    BindingResource::Buffer {
                        buffer,
                        offset,
                        size,
                    } => wgpu::BindingResource::Buffer(wgpu::BufferBinding {
                        buffer: &buffer.0,
                        offset: *offset,
                        size: *size,
                    }),
                    BindingResource::TextureView(view) => {
                        wgpu::BindingResource::TextureView(&view.0)
                    }
                    BindingResource::Sampler(sampler) => wgpu::BindingResource::Sampler(&sampler.0),
                },
            })
            .collect::<Vec<_>>();
        Ok(WgpuBindGroup(self.inner.create_bind_group(
            &wgpu::BindGroupDescriptor {
                label: descriptor.label,
                layout: &descriptor.layout.0,
                entries: &entries,
            },
        )))
    }

    fn create_pipeline_layout(
        &self,
        descriptor: PipelineLayoutDescriptor<'_, Self::BindGroupLayout>,
    ) -> Result<Self::PipelineLayout, RhiError> {
        self.check_failure()?;
        let layouts = descriptor
            .bind_group_layouts
            .iter()
            .map(|layout| Some(&layout.0))
            .collect::<Vec<_>>();
        Ok(WgpuPipelineLayout(self.inner.create_pipeline_layout(
            &wgpu::PipelineLayoutDescriptor {
                label: descriptor.label,
                bind_group_layouts: &layouts,
                immediate_size: 0,
            },
        )))
    }

    fn create_render_pipeline(
        &self,
        descriptor: RenderPipelineDescriptor<'_, Self::PipelineLayout, Self::ShaderModule>,
    ) -> Result<Self::RenderPipeline, RhiError> {
        self.check_failure()?;
        let attributes = descriptor
            .vertex
            .buffers
            .iter()
            .map(|buffer| {
                buffer
                    .attributes
                    .iter()
                    .map(|attribute| wgpu::VertexAttribute {
                        format: vertex_format(attribute.format),
                        offset: attribute.offset,
                        shader_location: attribute.shader_location,
                    })
                    .collect::<Vec<_>>()
            })
            .collect::<Vec<_>>();
        let buffers = descriptor
            .vertex
            .buffers
            .iter()
            .zip(&attributes)
            .map(|(buffer, attributes)| {
                Some(wgpu::VertexBufferLayout {
                    array_stride: buffer.stride,
                    step_mode: vertex_step_mode(buffer.step_mode),
                    attributes,
                })
            })
            .collect::<Vec<_>>();
        let targets = descriptor.fragment.as_ref().map(|fragment| {
            fragment
                .targets
                .iter()
                .map(|target| {
                    target.map(|target| wgpu::ColorTargetState {
                        format: texture_format(target.format),
                        blend: target.blend.map(blend_state),
                        write_mask: color_writes(target.write_mask),
                    })
                })
                .collect::<Vec<_>>()
        });
        let fragment = descriptor
            .fragment
            .as_ref()
            .map(|fragment| wgpu::FragmentState {
                module: &fragment.shader.0,
                entry_point: Some(fragment.entry_point),
                compilation_options: Default::default(),
                targets: targets.as_deref().unwrap_or_default(),
            });
        Ok(WgpuRenderPipeline(self.inner.create_render_pipeline(
            &wgpu::RenderPipelineDescriptor {
                label: descriptor.label,
                layout: Some(&descriptor.layout.0),
                vertex: wgpu::VertexState {
                    module: &descriptor.vertex.shader.0,
                    entry_point: Some(descriptor.vertex.entry_point),
                    compilation_options: Default::default(),
                    buffers: &buffers,
                },
                primitive: primitive_state(descriptor.primitive),
                depth_stencil: descriptor.depth_stencil.map(depth_stencil_state),
                multisample: multisample_state(descriptor.multisample),
                fragment,
                multiview_mask: None,
                cache: None,
            },
        )))
    }

    fn create_compute_pipeline(
        &self,
        descriptor: ComputePipelineDescriptor<'_, Self::PipelineLayout, Self::ShaderModule>,
    ) -> Result<Self::ComputePipeline, RhiError> {
        self.check_failure()?;
        Ok(WgpuComputePipeline(self.inner.create_compute_pipeline(
            &wgpu::ComputePipelineDescriptor {
                label: descriptor.label,
                layout: Some(&descriptor.layout.0),
                module: &descriptor.shader.0,
                entry_point: Some(descriptor.entry_point),
                compilation_options: Default::default(),
                cache: None,
            },
        )))
    }

    fn create_command_encoder(&self, label: Option<&str>) -> Self::CommandEncoder {
        WgpuCommandEncoder(
            self.inner
                .create_command_encoder(&wgpu::CommandEncoderDescriptor { label }),
        )
    }
}

impl WgpuDevice {
    fn check_failure(&self) -> Result<(), RhiError> {
        current_failure(&self.failure).map_or(Ok(()), Err)
    }
}

impl RhiQueue<WgpuDevice> for WgpuQueue {
    fn write_buffer(&self, buffer: &WgpuBuffer, offset: u64, data: &[u8]) {
        self.inner.write_buffer(&buffer.0, offset, data);
    }

    fn write_texture(
        &self,
        destination: TextureCopy<'_, WgpuTexture>,
        data: &[u8],
        layout: TextureDataLayout,
        extent: Extent3d,
    ) {
        self.inner.write_texture(
            texel_copy_texture(destination),
            data,
            texel_layout(layout),
            extent3d(extent),
        );
    }

    fn submit(&self, command_buffers: Vec<WgpuCommandBuffer>) {
        self.inner
            .submit(command_buffers.into_iter().map(|buffer| buffer.0));
    }
}

impl<W> RhiSurface<WgpuDevice, WgpuQueue> for WgpuSurface<W>
where
    W: HasWindowHandle + Send + Sync + 'static,
{
    type Frame = WgpuSurfaceFrame;

    fn format(&self) -> TextureFormat {
        try_rhi_texture_format(self.configuration.format)
            .expect("surface configuration is restricted to Nico RHI formats")
    }

    fn resize(&mut self, device: &WgpuDevice, extent: Extent3d) {
        if self.extent == extent {
            return;
        }
        self.extent = extent;
        self.configure_if_presentable(device);
    }

    fn acquire(
        &mut self,
        device: &WgpuDevice,
    ) -> Result<SurfaceAcquire<Self::Frame, WgpuTextureView>, RhiError> {
        if let Some(error) = self.current_failure() {
            return Err(error);
        }
        if self.extent.is_zero() || !self.configured {
            return Ok(SurfaceAcquire::ZeroSized);
        }
        match self.acquire_texture(device)? {
            AcquiredSurfaceTexture::Texture(texture, reconfigure_after_present) => {
                let view = WgpuTextureView(
                    texture
                        .texture
                        .create_view(&wgpu::TextureViewDescriptor::default()),
                );
                Ok(SurfaceAcquire::Acquired {
                    frame: WgpuSurfaceFrame {
                        texture,
                        reconfigure_after_present,
                    },
                    view,
                })
            }
            AcquiredSurfaceTexture::Timeout => Ok(SurfaceAcquire::Timeout),
            AcquiredSurfaceTexture::Occluded => Ok(SurfaceAcquire::Occluded),
        }
    }

    fn present(&mut self, device: &WgpuDevice, queue: &WgpuQueue, frame: Self::Frame) {
        if self.capture_requested {
            self.capture_requested = false;
            self.capture_result = Some(snapshot::readback(device, queue, &frame.texture.texture));
        }
        queue.inner.present(frame.texture);
        if frame.reconfigure_after_present {
            self.configure_if_presentable(device);
        }
    }
}

impl RhiCommandEncoder for WgpuCommandEncoder {
    type Buffer = WgpuBuffer;
    type Texture = WgpuTexture;
    type TextureView = WgpuTextureView;
    type BindGroup = WgpuBindGroup;
    type RenderPipeline = WgpuRenderPipeline;
    type ComputePipeline = WgpuComputePipeline;
    type CommandBuffer = WgpuCommandBuffer;
    type RenderPass<'pass> = WgpuRenderPass<'pass>;
    type ComputePass<'pass> = WgpuComputePass<'pass>;

    fn copy_buffer_to_buffer(
        &mut self,
        source: &WgpuBuffer,
        source_offset: u64,
        destination: &WgpuBuffer,
        destination_offset: u64,
        size: u64,
    ) {
        self.0.copy_buffer_to_buffer(
            &source.0,
            source_offset,
            &destination.0,
            destination_offset,
            size,
        );
    }

    fn copy_buffer_to_texture(
        &mut self,
        source: BufferTextureCopy<'_, WgpuBuffer>,
        destination: TextureCopy<'_, WgpuTexture>,
        extent: Extent3d,
    ) {
        self.0.copy_buffer_to_texture(
            texel_copy_buffer(source),
            texel_copy_texture(destination),
            extent3d(extent),
        );
    }

    fn copy_texture_to_buffer(
        &mut self,
        source: TextureCopy<'_, WgpuTexture>,
        destination: BufferTextureCopy<'_, WgpuBuffer>,
        extent: Extent3d,
    ) {
        self.0.copy_texture_to_buffer(
            texel_copy_texture(source),
            texel_copy_buffer(destination),
            extent3d(extent),
        );
    }

    fn begin_render_pass<'pass>(
        &'pass mut self,
        descriptor: RenderPassDescriptor<'pass, WgpuTextureView>,
    ) -> Self::RenderPass<'pass> {
        let colors = descriptor
            .color_attachments
            .iter()
            .map(|attachment| {
                attachment
                    .as_ref()
                    .map(|attachment| wgpu::RenderPassColorAttachment {
                        view: &attachment.view.0,
                        depth_slice: None,
                        resolve_target: attachment.resolve_target.map(|view| &view.0),
                        ops: color_operations(attachment.operations),
                    })
            })
            .collect::<Vec<_>>();
        let depth = descriptor
            .depth_stencil_attachment
            .as_ref()
            .map(|attachment| wgpu::RenderPassDepthStencilAttachment {
                view: &attachment.view.0,
                depth_ops: attachment.depth_operations.map(depth_operations),
                stencil_ops: attachment.stencil_operations.map(stencil_operations),
            });
        WgpuRenderPass(self.0.begin_render_pass(&wgpu::RenderPassDescriptor {
            label: descriptor.label,
            color_attachments: &colors,
            depth_stencil_attachment: depth,
            timestamp_writes: None,
            occlusion_query_set: None,
            multiview_mask: None,
        }))
    }

    fn begin_compute_pass<'pass>(
        &'pass mut self,
        descriptor: ComputePassDescriptor<'pass>,
    ) -> Self::ComputePass<'pass> {
        WgpuComputePass(self.0.begin_compute_pass(&wgpu::ComputePassDescriptor {
            label: descriptor.label,
            timestamp_writes: None,
        }))
    }

    fn finish(self) -> Self::CommandBuffer {
        WgpuCommandBuffer(self.0.finish())
    }
}

impl<'pass> RhiRenderPass<'pass> for WgpuRenderPass<'pass> {
    type Buffer = WgpuBuffer;
    type BindGroup = WgpuBindGroup;
    type Pipeline = WgpuRenderPipeline;

    fn set_pipeline(&mut self, pipeline: &'pass WgpuRenderPipeline) {
        self.0.set_pipeline(&pipeline.0);
    }
    fn set_bind_group(&mut self, index: u32, bind_group: &'pass WgpuBindGroup, offsets: &[u32]) {
        self.0.set_bind_group(index, &bind_group.0, offsets);
    }
    fn set_vertex_buffer(
        &mut self,
        slot: u32,
        buffer: &'pass WgpuBuffer,
        range: std::ops::Range<u64>,
    ) {
        self.0.set_vertex_buffer(slot, buffer.0.slice(range));
    }
    fn set_index_buffer(
        &mut self,
        buffer: &'pass WgpuBuffer,
        format: IndexFormat,
        range: std::ops::Range<u64>,
    ) {
        self.0
            .set_index_buffer(buffer.0.slice(range), index_format(format));
    }
    fn set_viewport(
        &mut self,
        x: f32,
        y: f32,
        width: f32,
        height: f32,
        min_depth: f32,
        max_depth: f32,
    ) {
        self.0
            .set_viewport(x, y, width, height, min_depth, max_depth);
    }
    fn set_scissor_rect(&mut self, x: u32, y: u32, width: u32, height: u32) {
        self.0.set_scissor_rect(x, y, width, height);
    }
    fn draw(&mut self, vertices: std::ops::Range<u32>, instances: std::ops::Range<u32>) {
        self.0.draw(vertices, instances);
    }
    fn draw_indexed(
        &mut self,
        indices: std::ops::Range<u32>,
        base_vertex: i32,
        instances: std::ops::Range<u32>,
    ) {
        self.0.draw_indexed(indices, base_vertex, instances);
    }
}

impl<'pass> RhiComputePass<'pass> for WgpuComputePass<'pass> {
    type BindGroup = WgpuBindGroup;
    type Pipeline = WgpuComputePipeline;
    fn set_pipeline(&mut self, pipeline: &'pass WgpuComputePipeline) {
        self.0.set_pipeline(&pipeline.0);
    }
    fn set_bind_group(&mut self, index: u32, bind_group: &'pass WgpuBindGroup, offsets: &[u32]) {
        self.0.set_bind_group(index, &bind_group.0, offsets);
    }
    fn dispatch(&mut self, x: u32, y: u32, z: u32) {
        self.0.dispatch_workgroups(x, y, z);
    }
}

fn buffer_usages(value: BufferUsages) -> wgpu::BufferUsages {
    let mut result = wgpu::BufferUsages::empty();
    for (rhi, backend) in [
        (BufferUsages::COPY_SOURCE, wgpu::BufferUsages::COPY_SRC),
        (BufferUsages::COPY_DESTINATION, wgpu::BufferUsages::COPY_DST),
        (BufferUsages::INDEX, wgpu::BufferUsages::INDEX),
        (BufferUsages::VERTEX, wgpu::BufferUsages::VERTEX),
        (BufferUsages::UNIFORM, wgpu::BufferUsages::UNIFORM),
        (BufferUsages::STORAGE, wgpu::BufferUsages::STORAGE),
        (BufferUsages::INDIRECT, wgpu::BufferUsages::INDIRECT),
    ] {
        if value.contains(rhi) {
            result |= backend;
        }
    }
    result
}
fn texture_usages(value: TextureUsages) -> wgpu::TextureUsages {
    let mut result = wgpu::TextureUsages::empty();
    for (rhi, backend) in [
        (TextureUsages::COPY_SOURCE, wgpu::TextureUsages::COPY_SRC),
        (
            TextureUsages::COPY_DESTINATION,
            wgpu::TextureUsages::COPY_DST,
        ),
        (TextureUsages::SAMPLED, wgpu::TextureUsages::TEXTURE_BINDING),
        (TextureUsages::STORAGE, wgpu::TextureUsages::STORAGE_BINDING),
        (
            TextureUsages::RENDER_ATTACHMENT,
            wgpu::TextureUsages::RENDER_ATTACHMENT,
        ),
    ] {
        if value.contains(rhi) {
            result |= backend;
        }
    }
    result
}
fn shader_stages(value: ShaderStages) -> wgpu::ShaderStages {
    let mut result = wgpu::ShaderStages::empty();
    if value.contains(ShaderStages::VERTEX) {
        result |= wgpu::ShaderStages::VERTEX;
    }
    if value.contains(ShaderStages::FRAGMENT) {
        result |= wgpu::ShaderStages::FRAGMENT;
    }
    if value.contains(ShaderStages::COMPUTE) {
        result |= wgpu::ShaderStages::COMPUTE;
    }
    result
}
fn color_writes(value: ColorWrites) -> wgpu::ColorWrites {
    let mut result = wgpu::ColorWrites::empty();
    if value.contains(ColorWrites::RED) {
        result |= wgpu::ColorWrites::RED;
    }
    if value.contains(ColorWrites::GREEN) {
        result |= wgpu::ColorWrites::GREEN;
    }
    if value.contains(ColorWrites::BLUE) {
        result |= wgpu::ColorWrites::BLUE;
    }
    if value.contains(ColorWrites::ALPHA) {
        result |= wgpu::ColorWrites::ALPHA;
    }
    result
}

fn extent3d(value: Extent3d) -> wgpu::Extent3d {
    wgpu::Extent3d {
        width: value.width,
        height: value.height,
        depth_or_array_layers: value.depth_or_layers,
    }
}
fn presentable_extent(value: Extent3d) -> Extent3d {
    if value.is_zero() {
        Extent3d::surface(1, 1)
    } else {
        value
    }
}
fn origin(value: Origin3d) -> wgpu::Origin3d {
    wgpu::Origin3d {
        x: value.x,
        y: value.y,
        z: value.z,
    }
}
fn texel_layout(value: TextureDataLayout) -> wgpu::TexelCopyBufferLayout {
    wgpu::TexelCopyBufferLayout {
        offset: value.offset,
        bytes_per_row: value.bytes_per_row,
        rows_per_image: value.rows_per_image,
    }
}
fn texel_copy_texture(value: TextureCopy<'_, WgpuTexture>) -> wgpu::TexelCopyTextureInfo<'_> {
    wgpu::TexelCopyTextureInfo {
        texture: &value.texture.0,
        mip_level: value.mip_level,
        origin: origin(value.origin),
        aspect: wgpu::TextureAspect::All,
    }
}
fn texel_copy_buffer(value: BufferTextureCopy<'_, WgpuBuffer>) -> wgpu::TexelCopyBufferInfo<'_> {
    wgpu::TexelCopyBufferInfo {
        buffer: &value.buffer.0,
        layout: texel_layout(value.layout),
    }
}

fn texture_dimension(value: TextureDimension) -> wgpu::TextureDimension {
    match value {
        TextureDimension::One => wgpu::TextureDimension::D1,
        TextureDimension::Two => wgpu::TextureDimension::D2,
        TextureDimension::Three => wgpu::TextureDimension::D3,
    }
}
fn texture_view_dimension(value: TextureViewDimension) -> wgpu::TextureViewDimension {
    match value {
        TextureViewDimension::One => wgpu::TextureViewDimension::D1,
        TextureViewDimension::Two => wgpu::TextureViewDimension::D2,
        TextureViewDimension::TwoArray => wgpu::TextureViewDimension::D2Array,
        TextureViewDimension::Cube => wgpu::TextureViewDimension::Cube,
        TextureViewDimension::CubeArray => wgpu::TextureViewDimension::CubeArray,
        TextureViewDimension::Three => wgpu::TextureViewDimension::D3,
    }
}
fn texture_format(value: TextureFormat) -> wgpu::TextureFormat {
    match value {
        TextureFormat::R8Unorm => wgpu::TextureFormat::R8Unorm,
        TextureFormat::Rgba8Unorm => wgpu::TextureFormat::Rgba8Unorm,
        TextureFormat::Rgba8UnormSrgb => wgpu::TextureFormat::Rgba8UnormSrgb,
        TextureFormat::Bgra8Unorm => wgpu::TextureFormat::Bgra8Unorm,
        TextureFormat::Bgra8UnormSrgb => wgpu::TextureFormat::Bgra8UnormSrgb,
        TextureFormat::Rgba16Float => wgpu::TextureFormat::Rgba16Float,
        TextureFormat::R32Float => wgpu::TextureFormat::R32Float,
        TextureFormat::Depth24Plus => wgpu::TextureFormat::Depth24Plus,
        TextureFormat::Depth24PlusStencil8 => wgpu::TextureFormat::Depth24PlusStencil8,
        TextureFormat::Depth32Float => wgpu::TextureFormat::Depth32Float,
    }
}
fn try_rhi_texture_format(value: wgpu::TextureFormat) -> Option<TextureFormat> {
    Some(match value {
        wgpu::TextureFormat::R8Unorm => TextureFormat::R8Unorm,
        wgpu::TextureFormat::Rgba8Unorm => TextureFormat::Rgba8Unorm,
        wgpu::TextureFormat::Rgba8UnormSrgb => TextureFormat::Rgba8UnormSrgb,
        wgpu::TextureFormat::Bgra8Unorm => TextureFormat::Bgra8Unorm,
        wgpu::TextureFormat::Bgra8UnormSrgb => TextureFormat::Bgra8UnormSrgb,
        wgpu::TextureFormat::Rgba16Float => TextureFormat::Rgba16Float,
        wgpu::TextureFormat::R32Float => TextureFormat::R32Float,
        wgpu::TextureFormat::Depth24Plus => TextureFormat::Depth24Plus,
        wgpu::TextureFormat::Depth24PlusStencil8 => TextureFormat::Depth24PlusStencil8,
        wgpu::TextureFormat::Depth32Float => TextureFormat::Depth32Float,
        _ => return None,
    })
}
fn surface_configuration(
    surface: &wgpu::Surface<'_>,
    adapter: &wgpu::Adapter,
    extent: Extent3d,
) -> Result<wgpu::SurfaceConfiguration, RhiError> {
    let mut configuration = surface
        .get_default_config(adapter, extent.width, extent.height)
        .ok_or_else(|| {
            RhiError::new(
                RhiErrorKind::Unsupported,
                "graphics adapter has no compatible surface configuration",
            )
        })?;
    let capabilities = surface.get_capabilities(adapter);
    configuration.format = capabilities
        .formats
        .iter()
        .copied()
        .find(|format| format.is_srgb() && try_rhi_texture_format(*format).is_some())
        .or_else(|| {
            capabilities
                .formats
                .iter()
                .copied()
                .find(|format| try_rhi_texture_format(*format).is_some())
        })
        .ok_or_else(|| {
            RhiError::new(
                RhiErrorKind::Unsupported,
                "surface exposes no texture format supported by the Nico RHI profile",
            )
        })?;
    if capabilities.usages.contains(wgpu::TextureUsages::COPY_SRC) {
        configuration.usage |= wgpu::TextureUsages::COPY_SRC;
    }
    configuration.view_formats = vec![configuration.format];
    Ok(configuration)
}
fn address_mode(value: AddressMode) -> wgpu::AddressMode {
    match value {
        AddressMode::ClampToEdge => wgpu::AddressMode::ClampToEdge,
        AddressMode::Repeat => wgpu::AddressMode::Repeat,
        AddressMode::MirrorRepeat => wgpu::AddressMode::MirrorRepeat,
    }
}
fn filter_mode(value: FilterMode) -> wgpu::FilterMode {
    match value {
        FilterMode::Nearest => wgpu::FilterMode::Nearest,
        FilterMode::Linear => wgpu::FilterMode::Linear,
    }
}
fn mipmap_filter_mode(value: FilterMode) -> wgpu::MipmapFilterMode {
    match value {
        FilterMode::Nearest => wgpu::MipmapFilterMode::Nearest,
        FilterMode::Linear => wgpu::MipmapFilterMode::Linear,
    }
}
fn compare_function(value: CompareFunction) -> wgpu::CompareFunction {
    match value {
        CompareFunction::Never => wgpu::CompareFunction::Never,
        CompareFunction::Less => wgpu::CompareFunction::Less,
        CompareFunction::Equal => wgpu::CompareFunction::Equal,
        CompareFunction::LessEqual => wgpu::CompareFunction::LessEqual,
        CompareFunction::Greater => wgpu::CompareFunction::Greater,
        CompareFunction::NotEqual => wgpu::CompareFunction::NotEqual,
        CompareFunction::GreaterEqual => wgpu::CompareFunction::GreaterEqual,
        CompareFunction::Always => wgpu::CompareFunction::Always,
    }
}
fn index_format(value: IndexFormat) -> wgpu::IndexFormat {
    match value {
        IndexFormat::Uint16 => wgpu::IndexFormat::Uint16,
        IndexFormat::Uint32 => wgpu::IndexFormat::Uint32,
    }
}

fn binding_type(value: BindingType) -> wgpu::BindingType {
    match value {
        BindingType::Buffer {
            kind,
            dynamic_offset,
            minimum_size,
        } => wgpu::BindingType::Buffer {
            ty: match kind {
                BufferBindingKind::Uniform => wgpu::BufferBindingType::Uniform,
                BufferBindingKind::Storage { read_only } => {
                    wgpu::BufferBindingType::Storage { read_only }
                }
            },
            has_dynamic_offset: dynamic_offset,
            min_binding_size: minimum_size,
        },
        BindingType::Texture {
            sample_kind,
            dimension,
            multisampled,
        } => wgpu::BindingType::Texture {
            sample_type: match sample_kind {
                TextureSampleKind::Float { filterable } => {
                    wgpu::TextureSampleType::Float { filterable }
                }
                TextureSampleKind::Depth => wgpu::TextureSampleType::Depth,
                TextureSampleKind::Sint => wgpu::TextureSampleType::Sint,
                TextureSampleKind::Uint => wgpu::TextureSampleType::Uint,
            },
            view_dimension: texture_view_dimension(dimension),
            multisampled,
        },
        BindingType::StorageTexture {
            access,
            format,
            dimension,
        } => wgpu::BindingType::StorageTexture {
            access: match access {
                StorageTextureAccess::WriteOnly => wgpu::StorageTextureAccess::WriteOnly,
            },
            format: texture_format(format),
            view_dimension: texture_view_dimension(dimension),
        },
        BindingType::Sampler(kind) => wgpu::BindingType::Sampler(match kind {
            SamplerBindingKind::Filtering => wgpu::SamplerBindingType::Filtering,
            SamplerBindingKind::NonFiltering => wgpu::SamplerBindingType::NonFiltering,
            SamplerBindingKind::Comparison => wgpu::SamplerBindingType::Comparison,
        }),
    }
}

fn vertex_format(value: VertexFormat) -> wgpu::VertexFormat {
    match value {
        VertexFormat::Uint32 => wgpu::VertexFormat::Uint32,
        VertexFormat::Sint32 => wgpu::VertexFormat::Sint32,
        VertexFormat::Float32 => wgpu::VertexFormat::Float32,
        VertexFormat::Float32x2 => wgpu::VertexFormat::Float32x2,
        VertexFormat::Float32x3 => wgpu::VertexFormat::Float32x3,
        VertexFormat::Float32x4 => wgpu::VertexFormat::Float32x4,
        VertexFormat::Uint32x2 => wgpu::VertexFormat::Uint32x2,
        VertexFormat::Uint32x3 => wgpu::VertexFormat::Uint32x3,
        VertexFormat::Uint32x4 => wgpu::VertexFormat::Uint32x4,
    }
}
fn vertex_step_mode(value: VertexStepMode) -> wgpu::VertexStepMode {
    match value {
        VertexStepMode::Vertex => wgpu::VertexStepMode::Vertex,
        VertexStepMode::Instance => wgpu::VertexStepMode::Instance,
    }
}
fn primitive_state(value: PrimitiveState) -> wgpu::PrimitiveState {
    wgpu::PrimitiveState {
        topology: match value.topology {
            PrimitiveTopology::PointList => wgpu::PrimitiveTopology::PointList,
            PrimitiveTopology::LineList => wgpu::PrimitiveTopology::LineList,
            PrimitiveTopology::LineStrip => wgpu::PrimitiveTopology::LineStrip,
            PrimitiveTopology::TriangleList => wgpu::PrimitiveTopology::TriangleList,
            PrimitiveTopology::TriangleStrip => wgpu::PrimitiveTopology::TriangleStrip,
        },
        strip_index_format: value.strip_index_format.map(index_format),
        front_face: match value.front_face {
            FrontFace::CounterClockwise => wgpu::FrontFace::Ccw,
            FrontFace::Clockwise => wgpu::FrontFace::Cw,
        },
        cull_mode: value.cull_mode.map(|face| match face {
            Face::Front => wgpu::Face::Front,
            Face::Back => wgpu::Face::Back,
        }),
        unclipped_depth: false,
        polygon_mode: wgpu::PolygonMode::Fill,
        conservative: false,
    }
}
fn blend_factor(value: BlendFactor) -> wgpu::BlendFactor {
    match value {
        BlendFactor::Zero => wgpu::BlendFactor::Zero,
        BlendFactor::One => wgpu::BlendFactor::One,
        BlendFactor::Source => wgpu::BlendFactor::Src,
        BlendFactor::OneMinusSource => wgpu::BlendFactor::OneMinusSrc,
        BlendFactor::SourceAlpha => wgpu::BlendFactor::SrcAlpha,
        BlendFactor::OneMinusSourceAlpha => wgpu::BlendFactor::OneMinusSrcAlpha,
        BlendFactor::Destination => wgpu::BlendFactor::Dst,
        BlendFactor::OneMinusDestination => wgpu::BlendFactor::OneMinusDst,
        BlendFactor::DestinationAlpha => wgpu::BlendFactor::DstAlpha,
        BlendFactor::OneMinusDestinationAlpha => wgpu::BlendFactor::OneMinusDstAlpha,
    }
}
fn blend_operation(value: BlendOperation) -> wgpu::BlendOperation {
    match value {
        BlendOperation::Add => wgpu::BlendOperation::Add,
        BlendOperation::Subtract => wgpu::BlendOperation::Subtract,
        BlendOperation::ReverseSubtract => wgpu::BlendOperation::ReverseSubtract,
        BlendOperation::Min => wgpu::BlendOperation::Min,
        BlendOperation::Max => wgpu::BlendOperation::Max,
    }
}
fn blend_component(value: BlendComponent) -> wgpu::BlendComponent {
    wgpu::BlendComponent {
        src_factor: blend_factor(value.source),
        dst_factor: blend_factor(value.destination),
        operation: blend_operation(value.operation),
    }
}
fn blend_state(value: BlendState) -> wgpu::BlendState {
    wgpu::BlendState {
        color: blend_component(value.color),
        alpha: blend_component(value.alpha),
    }
}
fn depth_stencil_state(value: DepthStencilState) -> wgpu::DepthStencilState {
    wgpu::DepthStencilState {
        format: texture_format(value.format),
        depth_write_enabled: Some(value.depth_write_enabled),
        depth_compare: Some(compare_function(value.depth_compare)),
        stencil: Default::default(),
        bias: Default::default(),
    }
}
fn multisample_state(value: MultisampleState) -> wgpu::MultisampleState {
    wgpu::MultisampleState {
        count: value.count,
        mask: value.mask,
        alpha_to_coverage_enabled: value.alpha_to_coverage_enabled,
    }
}
fn color_operations(value: Operations<Color>) -> wgpu::Operations<wgpu::Color> {
    wgpu::Operations {
        load: match value.load {
            LoadOp::Load => wgpu::LoadOp::Load,
            LoadOp::Clear(color) => wgpu::LoadOp::Clear(wgpu::Color {
                r: color.red,
                g: color.green,
                b: color.blue,
                a: color.alpha,
            }),
        },
        store: match value.store {
            StoreOp::Store => wgpu::StoreOp::Store,
            StoreOp::Discard => wgpu::StoreOp::Discard,
        },
    }
}
fn depth_operations(value: Operations<f32>) -> wgpu::Operations<f32> {
    wgpu::Operations {
        load: match value.load {
            LoadOp::Load => wgpu::LoadOp::Load,
            LoadOp::Clear(value) => wgpu::LoadOp::Clear(value),
        },
        store: match value.store {
            StoreOp::Store => wgpu::StoreOp::Store,
            StoreOp::Discard => wgpu::StoreOp::Discard,
        },
    }
}
fn stencil_operations(value: Operations<u32>) -> wgpu::Operations<u32> {
    wgpu::Operations {
        load: match value.load {
            LoadOp::Load => wgpu::LoadOp::Load,
            LoadOp::Clear(value) => wgpu::LoadOp::Clear(value),
        },
        store: match value.store {
            StoreOp::Store => wgpu::StoreOp::Store,
            StoreOp::Discard => wgpu::StoreOp::Discard,
        },
    }
}

fn graphics_api(value: wgpu::Backend) -> GraphicsApi {
    match value {
        wgpu::Backend::Vulkan => GraphicsApi::Vulkan,
        wgpu::Backend::Dx12 => GraphicsApi::Direct3d12,
        wgpu::Backend::Metal => GraphicsApi::Metal,
        wgpu::Backend::BrowserWebGpu => GraphicsApi::BrowserWebGpu,
        _ => GraphicsApi::Other,
    }
}
fn adapter_kind(value: wgpu::DeviceType) -> AdapterKind {
    match value {
        wgpu::DeviceType::IntegratedGpu => AdapterKind::IntegratedGpu,
        wgpu::DeviceType::DiscreteGpu => AdapterKind::DiscreteGpu,
        wgpu::DeviceType::VirtualGpu => AdapterKind::VirtualGpu,
        wgpu::DeviceType::Cpu => AdapterKind::Cpu,
        _ => AdapterKind::Other,
    }
}

fn backend_error(context: &str, error: impl std::fmt::Display) -> RhiError {
    RhiError::new(RhiErrorKind::Backend, format!("{context}: {error}"))
}
fn current_failure(source: &Mutex<Option<BackendFailure>>) -> Option<RhiError> {
    source
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner)
        .as_ref()
        .map(|failure| RhiError::new(failure.kind, failure.message.clone()))
}
fn record_failure(
    destination: &Mutex<Option<BackendFailure>>,
    kind: RhiErrorKind,
    message: String,
) {
    let mut failure = destination
        .lock()
        .unwrap_or_else(std::sync::PoisonError::into_inner);
    if failure.is_none() {
        *failure = Some(BackendFailure { kind, message });
    }
}

impl<W> WgpuSurface<W> {
    /// Capture the next rendered surface image before presentation.
    pub fn request_snapshot(&mut self) {
        self.capture_requested = true;
        self.capture_result = None;
    }
    /// Finish this render attempt; skipped acquisition is an explicit capture failure.
    pub fn take_snapshot(&mut self) -> Result<CapturedPixels, String> {
        self.capture_requested = false;
        self.capture_result.take().unwrap_or_else(|| {
            Err(
                "no surface image rendered (suspended, occluded, zero-sized, or render failure)"
                    .into(),
            )
        })
    }
}

#[cfg(test)]
mod tests {
    use super::{
        buffer_usages, color_writes, presentable_extent, record_failure, try_rhi_texture_format,
    };
    use nico_rhi::{BufferUsages, ColorWrites, RhiErrorKind, TextureFormat};
    use std::sync::Mutex;

    #[test]
    fn zero_surface_extent_has_safe_configuration_extent() {
        let extent = presentable_extent(nico_rhi::Extent3d::surface(0, 0));
        assert_eq!(extent, nico_rhi::Extent3d::surface(1, 1));
    }

    #[test]
    fn provider_maps_flags_without_assuming_matching_bit_positions() {
        assert_eq!(
            buffer_usages(BufferUsages::VERTEX | BufferUsages::COPY_DESTINATION),
            wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::COPY_DST
        );
        assert_eq!(
            color_writes(ColorWrites::RED | ColorWrites::ALPHA),
            wgpu::ColorWrites::RED | wgpu::ColorWrites::ALPHA
        );
    }

    #[test]
    fn surface_format_filter_rejects_formats_outside_the_rhi_profile() {
        assert_eq!(
            try_rhi_texture_format(wgpu::TextureFormat::Bgra8UnormSrgb),
            Some(TextureFormat::Bgra8UnormSrgb)
        );
        assert_eq!(
            try_rhi_texture_format(wgpu::TextureFormat::Rgb10a2Unorm),
            None
        );
    }

    #[test]
    fn asynchronous_backend_failure_preserves_the_first_cause() {
        let failure = Mutex::new(None);
        record_failure(
            &failure,
            RhiErrorKind::OutOfMemory,
            "first failure".to_owned(),
        );
        record_failure(&failure, RhiErrorKind::Backend, "later failure".to_owned());
        let failure = failure.lock().unwrap();
        let failure = failure.as_ref().unwrap();
        assert_eq!(failure.kind, RhiErrorKind::OutOfMemory);
        assert_eq!(failure.message, "first failure");
    }
}
