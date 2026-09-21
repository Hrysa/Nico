use super::*;
use crate::foliage::{FOLIAGE_UNIFORM_BYTES, encode_foliage_uniform};
use nico_presentation::foliage::InfluenceSnapshot;

pub(in crate::meshes) struct FoliageRenderer<D: RhiDevice> {
    shader: D::ShaderModule,
    vertex: String,
    fragment: String,
    pub pipelines: Vec<D::RenderPipeline>,
    pub compact_pipelines: Vec<D::RenderPipeline>,
    layout: D::BindGroupLayout,
}
impl<D: RhiDevice> FoliageRenderer<D> {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        device: &D,
        artifact: GraphicsShaderArtifact<'_>,
        material: &D::BindGroupLayout,
        view: &D::BindGroupLayout,
        frame: &D::BindGroupLayout,
        format: TextureFormat,
    ) -> Result<Self, RhiError> {
        let limits = device.capabilities().limits;
        if !direct_supported(device)
            || limits.max_bind_groups < 4
            || limits.max_uniform_buffer_binding_size < FOLIAGE_UNIFORM_BYTES as u64
            || limits.max_buffer_size < FOLIAGE_UNIFORM_BYTES as u64
        {
            return Err(RhiError::new(
                RhiErrorKind::Unsupported,
                "foliage direct layout unavailable",
            ));
        }
        let layout = device.create_bind_group_layout(BindGroupLayoutDescriptor {
            label: Some("foliage fields"),
            entries: &[BindGroupLayoutEntry {
                binding: 3,
                visibility: ShaderStages::VERTEX,
                binding_type: BindingType::Buffer {
                    kind: BufferBindingKind::Uniform,
                    dynamic_offset: false,
                    minimum_size: NonZeroU64::new(FOLIAGE_UNIFORM_BYTES as u64),
                },
            }],
        })?;
        let shader = device.create_shader_module(artifact.module)?;
        let pipelines = pipeline(
            device,
            &shader,
            material,
            view,
            frame,
            format,
            artifact.vertex_entry_point,
            artifact.fragment_entry_point,
            None,
            true,
            Some(&layout),
        )?;
        Ok(Self {
            shader,
            vertex: artifact.vertex_entry_point.into(),
            fragment: artifact.fragment_entry_point.into(),
            pipelines,
            compact_pipelines: Vec::new(),
            layout,
        })
    }
    pub fn enable_compact(
        &mut self,
        device: &D,
        material: &D::BindGroupLayout,
        view: &D::BindGroupLayout,
        frame: &D::BindGroupLayout,
        format: TextureFormat,
    ) -> Result<(), RhiError> {
        self.compact_pipelines = pipeline_with_stride(
            device,
            &self.shader,
            material,
            view,
            frame,
            format,
            "vertex_compact_main",
            &self.fragment,
            None,
            true,
            Some(&self.layout),
            80,
        )?;
        Ok(())
    }
    pub fn rebuild(
        &mut self,
        device: &D,
        material: &D::BindGroupLayout,
        view: &D::BindGroupLayout,
        frame: &D::BindGroupLayout,
        format: TextureFormat,
    ) -> Result<(), RhiError> {
        self.pipelines = pipeline(
            device,
            &self.shader,
            material,
            view,
            frame,
            format,
            &self.vertex,
            &self.fragment,
            None,
            true,
            Some(&self.layout),
        )?;
        if !self.compact_pipelines.is_empty() {
            self.enable_compact(device, material, view, frame, format)?;
        }
        Ok(())
    }
    pub fn allocate(&self, device: &D) -> Result<Uniform<D>, RhiError> {
        let buffer = device.create_buffer(BufferDescriptor {
            label: Some("foliage influence snapshot"),
            size: FOLIAGE_UNIFORM_BYTES as u64,
            usages: BufferUsages::UNIFORM | BufferUsages::COPY_DESTINATION,
        })?;
        let binding = device.create_bind_group(BindGroupDescriptor {
            label: Some("foliage influence binding"),
            layout: &self.layout,
            entries: &[BindGroupEntry {
                binding: 3,
                resource: BindingResource::Buffer {
                    buffer: &buffer,
                    offset: 0,
                    size: NonZeroU64::new(FOLIAGE_UNIFORM_BYTES as u64),
                },
            }],
        })?;
        Ok(Uniform { buffer, binding })
    }
    pub fn write<Q: RhiQueue<D>>(
        queue: &Q,
        uniform: &Uniform<D>,
        previous: &mut Option<Box<[u8; FOLIAGE_UNIFORM_BYTES]>>,
        batch: &InstanceBatch,
        snapshot: &InfluenceSnapshot,
    ) -> (u32, bool) {
        let fields = snapshot.for_chunk(batch.bounds().expect("nonempty visible batch"));
        let bytes = encode_foliage_uniform(batch.foliage().unwrap(), &fields);
        let changed = previous.as_deref() != Some(&bytes);
        if changed {
            queue.write_buffer(&uniform.buffer, 0, &bytes);
            match previous {
                Some(previous) => **previous = bytes,
                None => *previous = Some(Box::new(bytes)),
            }
        }
        (fields.overflow() as u32, changed)
    }
}
