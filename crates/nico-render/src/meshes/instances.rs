pub(super) mod encoding;
pub(super) mod foliage;
use encoding::Encoding;
pub(super) mod gpu;
pub mod readback;
use super::*;
use nico_presentation::InstanceBatch;

const MAX_RESIDENT_BYTES: u64 = 64 * 1024 * 1024;
const RECORD_BYTES: u64 = 112;

/// Explicit diagnostic path override. Auto selects the available path per batch.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum InstanceRenderMode {
    #[default]
    Auto,
    Cpu,
    Gpu,
}

/// CPU-known counters from the most recently prepared instance view. These are
/// submission/upload counts, not completed GPU work or display scanout.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct InstanceRenderStats {
    /// Batches reusing visibility for the same clip matrix, eye and immutable source.
    pub visibility_reused_batches: u32,
    /// Unique GPU visibility pages encoded for this prepared view, excluding reuse.
    pub visibility_dispatched_pages: u32,
    /// Encoded compute dispatches; does not imply GPU completion.
    pub visibility_dispatches: u32,
    pub gpu_readback: readback::GpuReadbackStats,
    pub prepared_view: u64,
    pub visible_chunks: u32,
    pub culled_chunks: u32,
    pub submitted_instances: u32,
    pub submitted_draws: u32,
    pub indirect_draws: u32,
    pub gpu_candidate_instances: u32,
    pub visibility_upload_bytes: u64,
    /// Dynamic output-range retirement writes, outside immutable source pacing.
    pub visibility_retirement_upload_bytes: u64,
    /// CPU uploads of bounded per-chunk foliage uniforms for this prepared view.
    pub foliage_upload_bytes: u64,
    /// Sum of omitted field/chunk pairs, not unique world influence IDs.
    pub influence_overflow: u32,
    pub visible_record_upload_bytes: u64,
    pub instance_upload_bytes: u64,
    pub mesh_upload_bytes: u64,
    /// Visible chunks awaiting source-record/visibility-source upload allowance.
    pub deferred_upload_chunks: u32,
    pub retained_instance_bytes: u64,
    /// CPU segment batch/record payload and segment-reference allocation capacity.
    /// Excludes original provider records, shared assets and allocator/Arc metadata.
    pub retained_split_cpu_bytes: u64,
    pub retained_batches: u32,
    pub ordinary_fallback: bool,
}

pub(super) struct UploadedInstances<D: RhiDevice> {
    /// CPU results are valid after preparation; GPU results only after submission.
    pub visibility_view: Option<(Mat4, Vec3)>,
    source: Weak<InstanceBatch>,
    pub buffer: Option<D::Buffer>,
    pub fallback_uniforms: Vec<Uniform<D>>,
    pub visible_buffer: Option<D::Buffer>,
    pub visible_ids: Vec<usize>,
    pub visible_count: u32,
    pub bytes: u64,
    pub encoding: Encoding,
    pub gpu: Option<gpu::GpuBatch<D>>,
    gpu_pending: bool,
    pub foliage: Option<Uniform<D>>,
    foliage_bytes: Option<Box<[u8; crate::foliage::FOLIAGE_UNIFORM_BYTES]>>,
}
pub(super) struct InstanceRenderer<D: RhiDevice> {
    pub readbacks: readback::Readbacks,
    splits: Vec<SplitBatch>,
    pub source_upload_budget: Option<u64>,
    pub mode: InstanceRenderMode,
    pub auto_gpu_min_records: usize,
    shader: D::ShaderModule,
    vertex_entry: String,
    fragment_entry: String,
    pub pipeline: Vec<D::RenderPipeline>,
    pub view: Uniform<D>,
    pub compact_enabled: bool,
    pub compact_pipeline: Vec<D::RenderPipeline>,
    pub uploads: Vec<UploadedInstances<D>>,
    pub stats: InstanceRenderStats,
    pub gpu: Option<gpu::GpuInstances<D>>,
    pub gpu_foliage: Option<gpu::GpuInstances<D>>,
    pub foliage: Option<foliage::FoliageRenderer<D>>,
    pub influences: Arc<nico_presentation::foliage::InfluenceSnapshot>,
}
struct SplitBatch {
    source: Weak<InstanceBatch>,
    limit: usize,
    segments: Vec<Arc<InstanceBatch>>,
}

fn record_limit<D: RhiDevice>(
    device: &D,
    state: &InstanceRenderer<D>,
    foliage: bool,
    mode: InstanceRenderMode,
    count: usize,
) -> Result<usize, RhiError> {
    let installed = if foliage {
        state.gpu_foliage.is_some()
    } else {
        state.gpu.is_some()
    };
    let gpu = prefer_gpu(
        mode,
        count,
        state.auto_gpu_min_records,
        direct_supported(device),
    ) && installed
        && gpu::GpuInstances::<D>::supported(device, 1);
    if mode == InstanceRenderMode::Gpu && !gpu {
        return Err(RhiError::new(
            RhiErrorKind::Unsupported,
            "GPU instance path unavailable",
        ));
    }
    let mut limit = nico_presentation::MAX_BATCH_INSTANCES
        .min((device.capabilities().limits.max_buffer_size / RECORD_BYTES) as usize);
    if gpu {
        limit = limit.min(gpu_record_capacity(&device.capabilities().limits));
    } else if !direct_supported(device) {
        limit = limit.min(MAX_INSTANCES);
    }
    if (gpu || direct_supported(device))
        && let Some(budget) = state.source_upload_budget
    {
        let available = if gpu {
            budget.saturating_sub(40) / 144
        } else {
            budget / RECORD_BYTES
        };
        limit = limit.min(available.min(usize::MAX as u64) as usize);
    }
    if limit == 0 {
        return Err(RhiError::new(
            RhiErrorKind::Unsupported,
            "instance limits cannot fit one record",
        ));
    }
    Ok(limit)
}

// Called only after single-record GPU capability validation. For one group,
// the 112-byte source record dominates both 32-byte bounds and the 8*N+32
// output allocation for every N >= 1. Dispatch width is the other count limit.
fn gpu_record_capacity(limits: &Limits) -> usize {
    (nico_presentation::MAX_BATCH_INSTANCES as u64)
        .min(limits.max_buffer_size / RECORD_BYTES)
        .min(limits.max_storage_buffer_binding_size / RECORD_BYTES)
        .min(u64::from(limits.max_compute_workgroups_per_dimension) * 64) as usize
}
pub(super) struct InstanceDraw {
    pub batch: usize,
    pub upload: usize,
    pub mesh: usize,
    pub material: usize,
    pub fallback_record: Option<usize>,
    /// Draw the resident source directly when its entire bound is visible.
    pub direct_all: bool,
}

fn direct_supported<D: RhiDevice>(device: &D) -> bool {
    let limits = device.capabilities().limits;
    limits.max_vertex_attributes >= 10
        && limits.max_vertex_buffers >= 2
        && limits.max_vertex_buffer_array_stride >= RECORD_BYTES as u32
}
pub(super) fn validate_mode<D: RhiDevice>(
    device: &D,
    scene: &Scene3d,
    state: &InstanceRenderer<D>,
    mode: InstanceRenderMode,
) -> Result<(), RhiError> {
    if state.foliage.is_none()
        && scene
            .instance_batches
            .iter()
            .any(|batch| batch.foliage().is_some())
    {
        return Err(RhiError::new(
            RhiErrorKind::Unsupported,
            "foliage pipelines are not installed",
        ));
    }
    let limits = scene
        .instance_batches
        .iter()
        .map(|batch| {
            record_limit(
                device,
                state,
                batch.foliage().is_some(),
                mode,
                batch.records().len(),
            )
            .map(|limit| (batch.records().len(), limit))
        })
        .collect::<Result<Vec<_>, _>>()?;
    validate_segment_budget(limits.into_iter())?;
    if mode == InstanceRenderMode::Gpu {
        if state.gpu.is_none() && state.gpu_foliage.is_none() {
            return Err(RhiError::new(
                RhiErrorKind::Unsupported,
                "GPU instance shaders are not installed",
            ));
        }
        for batch in &scene.instance_batches {
            let installed = if batch.foliage().is_some() {
                state.gpu_foliage.is_some()
            } else {
                state.gpu.is_some()
            };
            if !installed
                || record_limit(
                    device,
                    state,
                    batch.foliage().is_some(),
                    mode,
                    batch.records().len(),
                )
                .is_err()
            {
                return Err(RhiError::new(
                    RhiErrorKind::Unsupported,
                    "forced GPU mode cannot render this batch",
                ));
            }
        }
    }
    if mode == InstanceRenderMode::Cpu && !direct_supported(device) {
        let count = scene
            .instance_batches
            .iter()
            .try_fold(scene.meshes.len(), |n, b| n.checked_add(b.records().len()));
        if count.is_none_or(|n| n > MAX_INSTANCES)
            || scene.instance_batches.iter().any(|b| b.foliage().is_some())
        {
            return Err(RhiError::new(
                RhiErrorKind::Unsupported,
                "forced CPU mode exceeds fallback capabilities",
            ));
        }
    }
    Ok(())
}
fn validate_segment_budget(counts: impl Iterator<Item = (usize, usize)>) -> Result<(), RhiError> {
    let mut records = 0usize;
    let mut groups = 0usize;
    for (count, limit) in counts {
        if limit == 0 {
            return Err(invalid("instance segment limit is zero"));
        }
        records = records
            .checked_add(count)
            .ok_or_else(|| invalid("instance count overflow"))?;
        groups = groups
            .checked_add(count.div_ceil(limit).max(1))
            .ok_or_else(|| invalid("instance segment count overflow"))?;
        if records > nico_presentation::MAX_BATCH_INSTANCES || groups > 512 {
            return Err(invalid("split instance draw or record budget exceeded"));
        }
    }
    Ok(())
}
pub(super) fn validate<D: RhiDevice>(
    device: &D,
    scene: &Scene3d,
    state: Option<&InstanceRenderer<D>>,
) -> Result<(), RhiError> {
    if let Some(state) = state {
        validate_mode(device, scene, state, state.mode)?;
    }
    if !scene.instance_batches.is_empty() && state.is_none() {
        return Err(RhiError::new(
            RhiErrorKind::Unsupported,
            "static instancing shader is not enabled",
        ));
    }
    let count = scene
        .instance_batches
        .iter()
        .try_fold(0usize, |sum, b| sum.checked_add(b.records().len()))
        .ok_or_else(|| invalid("instance count overflow"))?;
    if scene.instance_batches.len() > 512 || count > nico_presentation::MAX_BATCH_INSTANCES {
        return Err(invalid("instance batch or record budget exceeded"));
    }
    if !direct_supported(device)
        && state.is_none_or(|s| s.gpu.is_none())
        && count + scene.meshes.len() > MAX_INSTANCES
    {
        return Err(RhiError::new(
            RhiErrorKind::Unsupported,
            "instance layout unavailable and ordinary fallback draw budget exceeded",
        ));
    }
    for batch in &scene.instance_batches {
        if batch.foliage().is_some() && state.is_none_or(|s| s.foliage.is_none()) {
            return Err(RhiError::new(
                RhiErrorKind::Unsupported,
                "foliage pipelines are not installed",
            ));
        }
        if !direct_supported(device)
            && state.is_some_and(|s| s.gpu.is_some())
            && !gpu::GpuInstances::<D>::supported(device, batch.records().len())
        {
            return Err(RhiError::new(
                RhiErrorKind::Unsupported,
                "GPU batch exceeds storage limits and direct layout is unavailable",
            ));
        }
        validate_geometry(batch.mesh())?;
        let limits = device.capabilities().limits;
        if batch.records().len() as u64 * RECORD_BYTES > limits.max_buffer_size
            || batch.mesh().vertices().len() as u64 * 32 > limits.max_buffer_size
            || batch.mesh().indices().len() as u64 * 4 > limits.max_buffer_size
        {
            return Err(RhiError::new(
                RhiErrorKind::Unsupported,
                "instance geometry exceeds enabled buffer size limit",
            ));
        }
        for texture in batch.material().textures().into_iter().flatten() {
            crate::validate_texture(device, &texture.image)?;
        }
    }
    Ok(())
}

fn uniform<D: RhiDevice>(device: &D, layout: &D::BindGroupLayout) -> Result<Uniform<D>, RhiError> {
    let buffer = device.create_buffer(BufferDescriptor {
        label: Some("instance transform"),
        size: 208,
        usages: BufferUsages::UNIFORM | BufferUsages::COPY_DESTINATION,
    })?;
    let binding = device.create_bind_group(BindGroupDescriptor {
        label: Some("instance transform binding"),
        layout,
        entries: &[BindGroupEntry {
            binding: 0,
            resource: BindingResource::Buffer {
                buffer: &buffer,
                offset: 0,
                size: NonZeroU64::new(208),
            },
        }],
    })?;
    Ok(Uniform { buffer, binding })
}
impl<D: RhiDevice> InstanceRenderer<D> {
    pub(super) fn segmented_scene(
        &mut self,
        device: &D,
        scene: &Scene3d,
    ) -> Result<Option<Scene3d>, RhiError> {
        validate_mode(device, scene, self, self.mode)?;
        self.splits.retain(|s| s.source.strong_count() > 0);
        let count = scene
            .instance_batches
            .iter()
            .try_fold(0usize, |n, b| n.checked_add(b.records().len()))
            .ok_or_else(|| invalid("instance count overflow"))?;
        if scene.instance_batches.len() > 512 || count > nico_presentation::MAX_BATCH_INSTANCES {
            return Err(invalid("instance batch or record budget exceeded"));
        }
        let mut output = Vec::new();
        let mut changed = false;
        for batch in &scene.instance_batches {
            let limit = record_limit(
                device,
                self,
                batch.foliage().is_some(),
                self.mode,
                batch.records().len(),
            )?;
            if batch.records().len() <= limit {
                // A larger allowance or changed mode may make the source fit
                // again. Release obsolete segment owners before GPU retirement.
                let source = Arc::downgrade(batch);
                self.splits.retain(|s| !s.source.ptr_eq(&source));
                output.push(batch.clone());
                continue;
            }
            changed = true;
            let source = Arc::downgrade(batch);
            let index = self
                .splits
                .iter()
                .position(|s| s.source.ptr_eq(&source) && s.limit == limit);
            let index = if let Some(index) = index {
                index
            } else {
                self.splits.retain(|s| !s.source.ptr_eq(&source));
                let retained: usize = self
                    .splits
                    .iter()
                    .flat_map(|s| &s.segments)
                    .map(|b| b.records().len())
                    .sum();
                if self.splits.len() >= 512
                    || retained + batch.records().len() > nico_presentation::MAX_BATCH_INSTANCES
                {
                    return Err(invalid("split instance cache budget exceeded"));
                }
                let segments = batch
                    .as_ref()
                    .clone()
                    .split(limit)
                    .map_err(|e| invalid(&e.to_string()))?
                    .into_iter()
                    .map(Arc::new)
                    .collect();
                self.splits.push(SplitBatch {
                    source,
                    limit,
                    segments,
                });
                self.splits.len() - 1
            };
            output.extend(self.splits[index].segments.iter().cloned());
            if output.len() > 512 {
                return Err(invalid("split instance draw budget exceeded"));
            }
        }
        if !changed {
            return Ok(None);
        }
        let mut result = scene.clone();
        result.instance_batches = output;
        Ok(Some(result))
    }
    pub(super) fn new(
        device: &D,
        artifact: GraphicsShaderArtifact<'_>,
        material: &D::BindGroupLayout,
        uniform_layout: &D::BindGroupLayout,
        frame: &D::BindGroupLayout,
        format: TextureFormat,
    ) -> Result<Self, RhiError> {
        let shader = device.create_shader_module(artifact.module)?;
        let pipeline = if direct_supported(device) {
            pipeline(
                device,
                &shader,
                material,
                uniform_layout,
                frame,
                format,
                artifact.vertex_entry_point,
                artifact.fragment_entry_point,
                None,
                true,
                None,
            )?
        } else {
            Vec::new()
        };
        Ok(Self {
            readbacks: readback::Readbacks::default(),
            splits: Vec::new(),
            source_upload_budget: None,
            mode: InstanceRenderMode::Auto,
            auto_gpu_min_records: 0,
            shader,
            pipeline,
            view: uniform(device, uniform_layout)?,
            compact_enabled: false,
            compact_pipeline: Vec::new(),
            uploads: Vec::new(),
            vertex_entry: artifact.vertex_entry_point.into(),
            fragment_entry: artifact.fragment_entry_point.into(),
            stats: InstanceRenderStats::default(),
            gpu: None,
            gpu_foliage: None,
            foliage: None,
            influences: Arc::new(
                nico_presentation::foliage::InfluenceSnapshot::new(0., Vec::new()).unwrap(),
            ),
        })
    }
    pub(super) fn enable_compact(
        &mut self,
        device: &D,
        material: &D::BindGroupLayout,
        uniform_layout: &D::BindGroupLayout,
        frame: &D::BindGroupLayout,
        format: TextureFormat,
    ) -> Result<(), RhiError> {
        let compact_pipeline = if self.pipeline.is_empty() {
            Vec::new()
        } else {
            pipeline_with_stride(
                device,
                &self.shader,
                material,
                uniform_layout,
                frame,
                format,
                "vertex_compact_main",
                &self.fragment_entry,
                None,
                true,
                None,
                80,
            )?
        };
        for gpu in [&mut self.gpu, &mut self.gpu_foliage].into_iter().flatten() {
            gpu.enable_compact(device, material, uniform_layout, frame, format)?;
        }
        if let Some(foliage) = &mut self.foliage {
            foliage.enable_compact(device, material, uniform_layout, frame, format)?;
        }
        self.compact_pipeline = compact_pipeline;
        self.compact_enabled = true;
        self.uploads.clear();
        Ok(())
    }
    #[allow(clippy::too_many_arguments)]
    pub(super) fn enable_gpu(
        &mut self,
        device: &D,
        graphics: GraphicsShaderArtifact<'_>,
        compute: ShaderModuleDescriptor<'_>,
        material: &D::BindGroupLayout,
        view: &D::BindGroupLayout,
        frame: &D::BindGroupLayout,
        format: TextureFormat,
    ) -> Result<(), RhiError> {
        let gpu = gpu::GpuInstances::new(
            device, graphics, compute, material, view, frame, format, false,
        )?;
        self.uploads.clear();
        self.gpu = Some(gpu);
        Ok(())
    }
    pub(super) fn rebuild(
        &mut self,
        device: &D,
        material: &D::BindGroupLayout,
        uniform: &D::BindGroupLayout,
        frame: &D::BindGroupLayout,
        format: TextureFormat,
    ) -> Result<(), RhiError> {
        if !self.pipeline.is_empty() {
            self.pipeline = pipeline(
                device,
                &self.shader,
                material,
                uniform,
                frame,
                format,
                &self.vertex_entry,
                &self.fragment_entry,
                None,
                true,
                None,
            )?;
        }
        if self.compact_enabled && !self.pipeline.is_empty() {
            self.compact_pipeline = pipeline_with_stride(
                device,
                &self.shader,
                material,
                uniform,
                frame,
                format,
                "vertex_compact_main",
                &self.fragment_entry,
                None,
                true,
                None,
                80,
            )?;
        }
        if let Some(gpu) = &mut self.gpu {
            gpu.rebuild(device, material, uniform, frame, format)?;
        }
        if let Some(gpu) = &mut self.gpu_foliage {
            gpu.rebuild(device, material, uniform, frame, format)?;
        }
        if let Some(foliage) = &mut self.foliage {
            foliage.rebuild(device, material, uniform, frame, format)?;
        }
        Ok(())
    }
    #[allow(clippy::too_many_arguments)]
    pub(super) fn prepare<Q: RhiQueue<D>>(
        &mut self,
        device: &D,
        queue: &Q,
        scene: &Scene3d,
        camera: Mat4,
        meshes: &mut Vec<Uploaded<D>>,
        materials: &mut Materials<D>,
        uniform_layout: &D::BindGroupLayout,
    ) -> Result<Vec<InstanceDraw>, RhiError> {
        let result = self.prepare_inner(
            device,
            queue,
            scene,
            camera,
            meshes,
            materials,
            uniform_layout,
        );
        if result.is_err() {
            // New source buffers must not become resident without their visibility
            // bindings if a grouped allocation or earlier preparation step fails.
            self.uploads.retain(|upload| !upload.gpu_pending);
        }
        result
    }
    fn retained_bytes(&self) -> u64 {
        let mut pages = std::collections::HashSet::new();
        self.uploads
            .iter()
            .map(|u| {
                u.bytes
                    + if u.foliage.is_some() {
                        crate::foliage::FOLIAGE_UNIFORM_BYTES as u64
                    } else {
                        0
                    }
                    + if u.visible_buffer.is_some() {
                        u.bytes
                    } else {
                        0
                    }
                    + u.gpu.as_ref().map_or(0, |gpu| {
                        16 + if pages.insert(Arc::as_ptr(&gpu.page)) {
                            gpu.page_bytes
                        } else {
                            0
                        }
                    })
            })
            .sum()
    }
    fn finish_gpu_uploads<Q: RhiQueue<D>>(
        &mut self,
        device: &D,
        queue: &Q,
    ) -> Result<u64, RhiError> {
        let mut retirement_bytes = 0;
        for foliage in [false, true] {
            let pending: Vec<_> = self
                .uploads
                .iter()
                .enumerate()
                .filter(|(_, u)| u.gpu_pending && u.foliage.is_some() == foliage)
                .map(|(slot, u)| {
                    (
                        slot,
                        u.source
                            .upgrade()
                            .expect("current scene owns pending batch"),
                    )
                })
                .collect();
            let mut start = 0;
            while start < pending.len() {
                let mut end = start;
                let mut records = 0;
                while end < pending.len() {
                    let count = records + pending[end].1.records().len();
                    let supported =
                        crate::visibility::VisibilityLayout::new(count, end - start + 1).is_ok_and(
                            |layout| {
                                crate::visibility::GpuVisibilityPage::<D>::supported(device, layout)
                            },
                        );
                    if !supported {
                        break;
                    }
                    records = count;
                    end += 1;
                }
                if end == start {
                    return Err(invalid("pending visibility group exceeds device limits"));
                }
                let inputs: Vec<_> = pending[start..end]
                    .iter()
                    .map(|(slot, batch)| {
                        let upload = &self.uploads[*slot];
                        (
                            *slot,
                            batch.as_ref(),
                            upload.buffer.as_ref().unwrap(),
                            upload.foliage.as_ref().map(|u| &u.buffer),
                        )
                    })
                    .collect();
                let renderer = if foliage {
                    self.gpu_foliage.as_ref()
                } else {
                    self.gpu.as_ref()
                }
                .unwrap();
                // Protect the minimum allocation needed by other pending chunks
                // before reserving spare capacity for this upload cohort.
                let other_pending: u64 = self
                    .uploads
                    .iter()
                    .enumerate()
                    .filter(|(slot, upload)| {
                        upload.gpu_pending && !inputs.iter().any(|input| input.0 == *slot)
                    })
                    .map(|(_, upload)| upload.bytes / upload.encoding.bytes() * 40 + 224)
                    .sum();
                let available = MAX_RESIDENT_BYTES
                    .saturating_sub(self.retained_bytes())
                    .saturating_sub(other_pending);
                let (uploaded, retired) =
                    renderer.upload_group(device, queue, &inputs, available)?;
                retirement_bytes += retired;
                for (slot, gpu) in uploaded {
                    self.uploads[slot].gpu = Some(gpu);
                    self.uploads[slot].gpu_pending = false;
                }
                start = end;
            }
        }
        Ok(retirement_bytes)
    }
    #[allow(clippy::too_many_arguments)]
    fn prepare_inner<Q: RhiQueue<D>>(
        &mut self,
        device: &D,
        queue: &Q,
        scene: &Scene3d,
        camera: Mat4,
        meshes: &mut Vec<Uploaded<D>>,
        materials: &mut Materials<D>,
        uniform_layout: &D::BindGroupLayout,
    ) -> Result<Vec<InstanceDraw>, RhiError> {
        self.readbacks.poll();
        self.uploads.retain(|u| u.source.strong_count() > 0);
        let fallback = self.pipeline.is_empty()
            && (self.gpu.is_none() || self.mode == InstanceRenderMode::Cpu);
        let mut stats = InstanceRenderStats {
            prepared_view: self.stats.prepared_view.saturating_add(1),
            ordinary_fallback: fallback,
            ..Default::default()
        };
        let mut retained = self.retained_bytes();
        let bytes: Vec<_> = camera
            .to_cols_array()
            .into_iter()
            .chain(Mat4::IDENTITY.to_cols_array())
            .chain(Mat4::IDENTITY.to_cols_array())
            .chain([1.; 4])
            .flat_map(f32::to_le_bytes)
            .collect();
        queue.write_buffer(&self.view.buffer, 0, &bytes);

        let mut draws = Vec::new();
        let mut source_upload_bytes = 0u64;
        for (batch_index, batch) in scene.instance_batches.iter().enumerate() {
            let Some(bounds) = batch.bounds() else {
                continue;
            };
            let camera_position = Vec3::from(scene.camera.position);
            if !bounds.visible_in_view(camera, camera_position, batch.max_draw_distance()) {
                stats.culled_chunks += 1;
                continue;
            }
            let resident = self
                .uploads
                .iter()
                .position(|u| u.source.as_ptr() == Arc::as_ptr(batch));
            let encoding = resident
                .map(|index| self.uploads[index].encoding)
                .unwrap_or_else(|| {
                    if !fallback
                        && self.compact_enabled
                        && (if batch.foliage().is_some() {
                            self.gpu_foliage.as_ref()
                        } else {
                            self.gpu.as_ref()
                        })
                        .is_none_or(|g| !g.compact_pipelines.is_empty())
                        && batch.foliage().is_none_or(|_| {
                            self.foliage
                                .as_ref()
                                .is_some_and(|f| !f.compact_pipelines.is_empty())
                        })
                    {
                        Encoding::for_batch(batch)
                    } else {
                        Encoding::Full
                    }
                });
            let new_bytes =
                batch.records().len() as u64 * if fallback { 208 } else { encoding.bytes() };
            let gpu_renderer = if batch.foliage().is_some() {
                self.gpu_foliage.as_ref()
            } else {
                self.gpu.as_ref()
            };
            let use_gpu = prefer_gpu(
                self.mode,
                batch.records().len(),
                self.auto_gpu_min_records,
                direct_supported(device),
            ) && gpu_renderer.is_some()
                && gpu::GpuInstances::<D>::supported(device, batch.records().len());
            let direct_all = use_gpu
                && self.mode == InstanceRenderMode::Auto
                && !self.pipeline.is_empty()
                && batch.foliage().is_none_or(|_| {
                    self.foliage
                        .as_ref()
                        .is_some_and(|f| !f.pipelines.is_empty())
                })
                && bounds.fully_visible(
                    camera,
                    scene.camera.position.into(),
                    batch.max_draw_distance(),
                );
            // Immutable source writes only; dynamic uniforms, CPU compaction,
            // mesh and texture uploads have separate accounting/lifecycles.
            let source_cost = if fallback {
                0
            } else {
                batch.records().len() as u64 * RECORD_BYTES
            } + if use_gpu {
                batch.records().len() as u64 * 32 + 40
            } else {
                0
            };
            if resident.is_none() {
                if let Some(budget) = self.source_upload_budget {
                    if source_cost > budget {
                        return Err(invalid(
                            "instance chunk exceeds source upload budget; split the provider chunk or increase the budget",
                        ));
                    }
                    if source_upload_bytes + source_cost > budget {
                        stats.deferred_upload_chunks += 1;
                        continue;
                    }
                }
                source_upload_bytes += source_cost;
            }
            let extra_bytes = if batch.foliage().is_some() {
                crate::foliage::FOLIAGE_UNIFORM_BYTES as u64
            } else {
                0
            } + if use_gpu {
                batch.records().len() as u64 * 40 + 224
            } else {
                0
            };
            if resident.is_none() && retained + new_bytes + extra_bytes > MAX_RESIDENT_BYTES {
                return Err(RhiError::new(
                    RhiErrorKind::OutOfMemory,
                    "instance residency budget exceeded",
                ));
            }
            let mesh = if let Some(i) = meshes
                .iter()
                .position(|m| m.source.as_ptr() == Arc::as_ptr(batch.mesh()))
            {
                i
            } else {
                meshes.push(upload(device, queue, batch.mesh())?);
                let uploaded = meshes.last().unwrap();
                stats.mesh_upload_bytes +=
                    uploaded.vertex_bytes + u64::from(uploaded.index_count) * 4;
                meshes.len() - 1
            };
            let draw = MeshInstance {
                mirrored: batch.mirrored(),
                material: Some(batch.material().clone()),
                mesh: Some(batch.mesh().clone()),
                skin_palette: None,
                texture: None,
                position: [0.; 3],
                orientation: nico_presentation::Quaternion::IDENTITY,
                scale: 1.,
                color: [1.; 4],
            };
            let material = materials.slot(device, queue, &draw)?;
            let slot = if let Some(i) = resident {
                i
            } else {
                let (buffer, fallback_uniforms) = if fallback {
                    (
                        None,
                        (0..batch.records().len())
                            .map(|_| uniform(device, uniform_layout))
                            .collect::<Result<Vec<_>, _>>()?,
                    )
                } else {
                    let bytes = packed_records(batch, encoding);
                    let buffer = device.create_buffer(BufferDescriptor {
                        label: Some("static instance records"),
                        size: new_bytes,
                        usages: BufferUsages::VERTEX
                            | BufferUsages::COPY_DESTINATION
                            | if use_gpu {
                                BufferUsages::STORAGE
                            } else {
                                BufferUsages::EMPTY
                            },
                    })?;
                    queue.write_buffer(&buffer, 0, &bytes);
                    stats.instance_upload_bytes += new_bytes;
                    (Some(buffer), Vec::new())
                };
                let foliage = if batch.foliage().is_some() {
                    Some(self.foliage.as_ref().unwrap().allocate(device)?)
                } else {
                    None
                };
                if use_gpu {
                    stats.visibility_upload_bytes += batch.records().len() as u64 * 32 + 40;
                }
                self.uploads.push(UploadedInstances {
                    visibility_view: None,
                    foliage,
                    foliage_bytes: None,
                    source: Arc::downgrade(batch),
                    buffer,
                    fallback_uniforms,
                    visible_buffer: None,
                    visible_ids: (0..batch.records().len()).collect(),
                    visible_count: batch.records().len() as u32,
                    bytes: new_bytes,
                    encoding,
                    gpu: None,
                    gpu_pending: use_gpu,
                });
                retained += new_bytes + extra_bytes;
                self.uploads.len() - 1
            };
            let uploaded = &mut self.uploads[slot];
            if let Some(uniform) = &uploaded.foliage {
                let (overflow, written) = foliage::FoliageRenderer::<D>::write(
                    queue,
                    uniform,
                    &mut uploaded.foliage_bytes,
                    batch,
                    scene
                        .foliage_influences
                        .as_deref()
                        .unwrap_or(&self.influences),
                );
                stats.influence_overflow += overflow;
                if written {
                    stats.foliage_upload_bytes += crate::foliage::FOLIAGE_UNIFORM_BYTES as u64;
                }
            }
            let visibility_view = (camera, Vec3::from(scene.camera.position));
            let cached_view = self.uploads[slot].visibility_view;
            if !fallback && !use_gpu && cached_view == Some(visibility_view) {
                stats.visibility_reused_batches += 1;
            }
            if !fallback && !use_gpu && self.uploads[slot].visibility_view != Some(visibility_view)
            {
                let visible: Vec<_> = batch
                    .records()
                    .iter()
                    .enumerate()
                    .filter_map(|(i, _)| {
                        let bounds = batch.record_bounds(i).expect("validated bounds");
                        bounds
                            .visible_in_view(camera, camera_position, batch.max_draw_distance())
                            .then_some(i)
                    })
                    .collect();
                let uploaded = &mut self.uploads[slot];
                if visible != uploaded.visible_ids {
                    uploaded.visible_count = visible.len() as u32;
                    if !visible.is_empty() && visible.len() != batch.records().len() {
                        if uploaded.visible_buffer.is_none() {
                            if retained + uploaded.bytes > MAX_RESIDENT_BYTES {
                                return Err(RhiError::new(
                                    RhiErrorKind::OutOfMemory,
                                    "CPU visibility buffer budget exceeded",
                                ));
                            }
                            uploaded.visible_buffer =
                                Some(device.create_buffer(BufferDescriptor {
                                    label: Some("CPU visible instance records"),
                                    size: uploaded.bytes,
                                    usages: BufferUsages::VERTEX | BufferUsages::COPY_DESTINATION,
                                })?);
                            retained += uploaded.bytes;
                        }
                        let bytes = packed_indices(batch, &visible, uploaded.encoding);
                        queue.write_buffer(uploaded.visible_buffer.as_ref().unwrap(), 0, &bytes);
                        stats.visible_record_upload_bytes += bytes.len() as u64;
                    }
                    uploaded.visible_ids = visible;
                }
                uploaded.visibility_view = Some(visibility_view);
            }
            if !fallback && !use_gpu && self.uploads[slot].visible_count == 0 {
                stats.culled_chunks += 1;
                continue;
            }
            stats.visible_chunks += 1;
            if fallback {
                for (index, record) in batch.records().iter().enumerate() {
                    let bounds = batch
                        .record_bounds(index)
                        .expect("validated instance bounds");
                    if !bounds.visible_in_view(camera, camera_position, batch.max_draw_distance()) {
                        continue;
                    }
                    let bytes: Vec<_> = (camera * record.transform())
                        .to_cols_array()
                        .into_iter()
                        .chain(record.transform().to_cols_array())
                        .chain(Mat4::from_mat3(record.normal_transform()).to_cols_array())
                        .chain(record.tint())
                        .flat_map(f32::to_le_bytes)
                        .collect();
                    queue.write_buffer(
                        &self.uploads[slot].fallback_uniforms[index].buffer,
                        0,
                        &bytes,
                    );
                    draws.push(InstanceDraw {
                        batch: batch_index,
                        upload: slot,
                        mesh,
                        material,
                        fallback_record: Some(index),
                        direct_all: false,
                    });
                    stats.submitted_instances += 1;
                }
            } else {
                draws.push(InstanceDraw {
                    batch: batch_index,
                    upload: slot,
                    mesh,
                    material,
                    fallback_record: None,
                    direct_all,
                });
                if use_gpu && !direct_all {
                    stats.indirect_draws += 1;
                    stats.gpu_candidate_instances += batch.records().len() as u32;
                } else {
                    stats.submitted_instances += if direct_all {
                        batch.records().len() as u32
                    } else {
                        self.uploads[slot].visible_count
                    };
                }
            }
        }
        stats.visibility_retirement_upload_bytes = self.finish_gpu_uploads(device, queue)?;
        // GPU reuse is counted after the draw list determines each page selection.
        stats.submitted_draws = draws.len() as u32;
        stats.retained_batches = self.uploads.len() as u32;
        stats.retained_instance_bytes = self.retained_bytes();
        stats.retained_split_cpu_bytes = self
            .splits
            .iter()
            .map(|split| {
                (split.segments.capacity() * std::mem::size_of::<Arc<InstanceBatch>>()) as u64
                    + split
                        .segments
                        .iter()
                        .map(|batch| batch.decoded_bytes() as u64)
                        .sum::<u64>()
            })
            .sum();
        self.stats = stats;
        Ok(draws)
    }
}

fn prefer_gpu(mode: InstanceRenderMode, count: usize, minimum: usize, direct: bool) -> bool {
    match mode {
        InstanceRenderMode::Cpu => false,
        InstanceRenderMode::Gpu => true,
        InstanceRenderMode::Auto => !direct || count >= minimum,
    }
}

fn packed_records(batch: &InstanceBatch, encoding: Encoding) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(batch.records().len() * encoding.bytes() as usize);
    for record in batch.records() {
        encoding.pack(&mut bytes, record);
    }
    bytes
}

fn packed_indices(batch: &InstanceBatch, indices: &[usize], encoding: Encoding) -> Vec<u8> {
    let mut bytes = Vec::with_capacity(indices.len() * encoding.bytes() as usize);
    for &i in indices {
        encoding.pack(&mut bytes, &batch.records()[i]);
    }
    bytes
}

#[cfg(test)]
fn pack_record(bytes: &mut Vec<u8>, record: &nico_presentation::InstanceRecord) {
    Encoding::Full.pack(bytes, record);
}

#[cfg(test)]
mod segment_tests {
    #[test]
    #[ignore = "manual packing elapsed-time measurement using Arena startup batches"]
    fn arena_startup_packing_measurement() {
        use arena_arpg_presentation::environment::Environment;
        use nico_presentation::{Camera3d, Scene3d};
        use std::{
            hint::black_box,
            time::{Duration, Instant},
        };

        let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../games/arena-arpg");
        let content = arena_arpg_shared::project::ProjectContent::open(&root).unwrap();
        let mut environment =
            Environment::load(&root.join("assets/presentation/worlds/meadow.world-vis.toml"))
                .unwrap();
        environment.apply_scene(&content.scene).unwrap();
        environment.bind(&content.zone).unwrap();
        let mut camera =
            Camera3d::looking_at([0., 2.97536, -25.25435], [0., 1.35, -20.], [0., 1., 0.]).unwrap();
        camera.far = 200.;
        let projection = camera.view_projection(840. / 764.).unwrap();
        let deadline = Instant::now() + Duration::from_secs(30);
        let scene = loop {
            let mut scene = Scene3d {
                camera,
                ..Default::default()
            };
            environment.decorate(Some(projection), &mut scene);
            assert_eq!(environment.inspection["grass_streaming"]["failed"], 0);
            if environment.inspection["grass_streaming"]["resident"] == 64 {
                break scene;
            }
            assert!(Instant::now() < deadline, "streaming did not settle");
            std::thread::sleep(Duration::from_millis(1));
        };
        let batches: Vec<_> = scene
            .instance_batches
            .iter()
            .filter(|batch| {
                batch.bounds().is_some_and(|bounds| {
                    bounds.visible_in_view(
                        projection,
                        camera.position.into(),
                        batch.max_draw_distance(),
                    )
                })
            })
            .collect();
        let records: usize = batches.iter().map(|batch| batch.records().len()).sum();
        let prepared: Vec<_> = batches
            .iter()
            .map(|batch| super::packed_records(batch, super::Encoding::Full))
            .collect();
        for trial in 0..5 {
            let start = Instant::now();
            let mut bytes = 0;
            for batch in &batches {
                let packed = super::packed_records(black_box(batch), super::Encoding::Full);
                bytes += packed.len();
                black_box(packed);
            }
            eprintln!(
                "arena_packing trial={trial} batches={} records={records} bytes={bytes} elapsed_ms={:.3}",
                batches.len(),
                start.elapsed().as_secs_f64() * 1000.
            );
            assert_eq!(bytes, records * 112);
            let start = Instant::now();
            for bytes in &prepared {
                black_box(black_box(bytes).clone());
            }
            eprintln!(
                "arena_packed_copy trial={trial} elapsed_ms={:.3}",
                start.elapsed().as_secs_f64() * 1000.
            );
        }
    }

    #[test]
    #[ignore = "manual resident identity lookup comparison"]
    fn resident_identity_lookup_measurement() {
        use std::{hint::black_box, sync::Arc, time::Instant};
        let sources: Vec<_> = (0..128).map(Arc::new).collect();
        let residents: Vec<_> = sources.iter().map(Arc::downgrade).collect();
        for trial in 0..3 {
            for order in 0..2 {
                let raw = (trial + order) % 2 == 1;
                let start = Instant::now();
                for _ in 0..1000 {
                    for expected in (0..128).step_by(2) {
                        let source = black_box(&sources[expected]);
                        let found = black_box(&residents).iter().position(|resident| {
                            if raw {
                                resident.as_ptr() == Arc::as_ptr(source)
                            } else {
                                resident.ptr_eq(&Arc::downgrade(source))
                            }
                        });
                        assert_eq!(black_box(found), Some(expected));
                    }
                }
                eprintln!(
                    "resident_lookup trial={trial} pointer_comparison={raw} us_per_64_lookups={:.3}",
                    start.elapsed().as_secs_f64() * 1000.
                );
            }
        }
    }
    use super::validate_segment_budget;
    #[test]
    fn gpu_capacity_respects_source_bounds_output_and_dispatch_limits() {
        for buffer in [112, 223, 224, 7168, 7169, 56_000_000, u64::MAX] {
            for storage in [112, 223, 224, 7168, 7169, 56_000_000, u64::MAX] {
                for dispatch in [1, 2, 100, 7812, 7813, u32::MAX] {
                    let limits = nico_rhi::Limits {
                        max_buffer_size: buffer,
                        max_storage_buffer_binding_size: storage,
                        max_compute_workgroups_per_dimension: dispatch,
                        max_vertex_buffer_array_stride: 112,
                        max_storage_buffers_per_shader_stage: 3,
                        max_compute_workgroup_size_x: 64,
                        max_compute_invocations_per_workgroup: 64,
                        max_texture_dimension_2d: 1,
                        max_bind_groups: 4,
                        max_uniform_buffer_binding_size: crate::visibility::VISIBILITY_VIEW_BYTES,
                        max_vertex_buffers: 2,
                        max_vertex_attributes: 10,
                    };
                    let supported = |count: usize| {
                        let Ok(layout) = crate::visibility::VisibilityLayout::new(count, 1) else {
                            return false;
                        };
                        count.div_ceil(64) as u64 <= u64::from(dispatch)
                            && [count as u64 * 112, count as u64 * 32, 16, layout.bytes()]
                                .into_iter()
                                .all(|bytes| bytes <= buffer && bytes <= storage)
                    };
                    let capacity = super::gpu_record_capacity(&limits);
                    assert!(supported(capacity), "{limits:?}: {capacity}");
                    assert!(!supported(capacity + 1), "{limits:?}: {capacity}");
                }
            }
        }
    }

    #[test]
    fn automatic_threshold_preserves_overrides_and_capability_fallback() {
        use super::{InstanceRenderMode::*, prefer_gpu};
        assert!(!prefer_gpu(Auto, 4095, 4096, true));
        assert!(prefer_gpu(Auto, 4096, 4096, true));
        assert!(prefer_gpu(Auto, 1, 4096, false));
        assert!(prefer_gpu(Gpu, 1, 4096, true));
        assert!(!prefer_gpu(Cpu, 8192, 4096, false));
    }
    #[test]
    fn foliage_response_uses_padding_without_changing_source_record_stride() {
        let response =
            nico_presentation::foliage::FoliageResponse::from_seed(42, 0.6, 0.8).unwrap();
        let record = nico_presentation::InstanceRecord::new(9, 42, glam::Mat4::IDENTITY, [1.; 4])
            .unwrap()
            .with_foliage_response(response);
        let mut bytes = Vec::new();
        super::pack_record(&mut bytes, &record);
        assert_eq!(bytes.len(), super::RECORD_BYTES as usize);
        for (row, expected) in response.parameters().into_iter().enumerate() {
            let start = 48 + row * 16 + 12;
            assert_eq!(
                f32::from_le_bytes(bytes[start..start + 4].try_into().unwrap()),
                expected
            );
        }
    }
    #[test]
    fn preflight_accounts_for_all_segments_before_accepting_the_scene() {
        assert!(validate_segment_budget([(511, 1), (1, 1)].into_iter()).is_ok());
        assert!(validate_segment_budget([(511, 1), (2, 1)].into_iter()).is_err());
        assert!(validate_segment_budget([(500_000, 1000)].into_iter()).is_ok());
        assert!(validate_segment_budget([(500_000, 1000), (1, 1)].into_iter()).is_err());
        assert!(validate_segment_budget([(1, 0)].into_iter()).is_err());
        assert!(validate_segment_budget(std::iter::repeat_n((0, 1), 512)).is_ok());
        assert!(validate_segment_budget(std::iter::repeat_n((0, 1), 513)).is_err());
        assert!(validate_segment_budget([(usize::MAX, 1)].into_iter()).is_err());
    }
}
