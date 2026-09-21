use super::*;
mod ranges;
use crate::visibility::{
    GpuVisibilityKernel, GpuVisibilityPage, VisibilityGroup, VisibilityLayout, VisibilityRecord,
};
type UploadInput<'a, D> = (
    usize,
    &'a InstanceBatch,
    &'a <D as RhiDevice>::Buffer,
    Option<&'a <D as RhiDevice>::Buffer>,
);
type ViewKey = std::sync::Mutex<Option<(Mat4, Vec3, [u32; 16])>>;
type UploadResult<D> = (Vec<(usize, GpuBatch<D>)>, u64);
struct PageEntry<D: RhiDevice> {
    page: Weak<GpuVisibilityPage<D>>,
    view: Weak<ViewKey>,
    leases: Vec<Weak<ranges::Reservation>>,
}
fn page_bytes(layout: VisibilityLayout) -> u64 {
    u64::from(layout.record_count().max(1)) * 32
        + u64::from(layout.group_count()) * 16
        + layout.bytes()
        + crate::visibility::VISIBILITY_VIEW_BYTES
}

pub(in crate::meshes) struct GpuInstances<D: RhiDevice> {
    shader: D::ShaderModule,
    vertex: String,
    fragment: String,
    pub pipelines: Vec<D::RenderPipeline>,
    pub compact_pipelines: Vec<D::RenderPipeline>,
    layout: D::BindGroupLayout,
    kernel: Arc<GpuVisibilityKernel<D>>,
    pages: std::sync::Mutex<Vec<PageEntry<D>>>,
}
pub(in crate::meshes) struct GpuBatch<D: RhiDevice> {
    pub page: Arc<GpuVisibilityPage<D>>,
    pub visibility_view: Arc<ViewKey>,
    pub group: u32,
    pub records: u32,
    pub binding: D::BindGroup,
    _selection: D::Buffer,
    _lease: Arc<ranges::Reservation>,
    pub page_bytes: u64,
}
pub(in crate::meshes) struct ViewPage<D: RhiDevice> {
    pub page: Arc<GpuVisibilityPage<D>>,
    pub view: Arc<ViewKey>,
    pub groups: [u32; 16],
    pub batches: u32,
}
impl<D: RhiDevice> GpuInstances<D> {
    pub fn supported(device: &D, count: usize) -> bool {
        VisibilityLayout::new(count, 1)
            .is_ok_and(|layout| GpuVisibilityPage::<D>::supported(device, layout))
            && device.capabilities().limits.max_bind_groups >= 4
            && count as u64 * RECORD_BYTES
                <= device.capabilities().limits.max_storage_buffer_binding_size
    }
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        device: &D,
        graphics: GraphicsShaderArtifact<'_>,
        compute: ShaderModuleDescriptor<'_>,
        material: &D::BindGroupLayout,
        view: &D::BindGroupLayout,
        frame: &D::BindGroupLayout,
        format: TextureFormat,
        foliage: bool,
    ) -> Result<Self, RhiError> {
        if !Self::supported(device, 1) {
            return Err(RhiError::new(
                RhiErrorKind::Unsupported,
                "GPU instance storage layout unavailable",
            ));
        }
        let kernel = Arc::new(GpuVisibilityKernel::new(device, compute)?);
        let mut entries = vec![
            // Full and compact entry points require different element sizes.
            // Defer minimum-size validation to the selected draw pipeline.
            layout_entry(0, BufferBindingKind::Storage { read_only: true }, 0),
            layout_entry(1, BufferBindingKind::Storage { read_only: true }, 4),
            layout_entry(2, BufferBindingKind::Uniform, 16),
        ];
        if foliage {
            entries.push(layout_entry(
                3,
                BufferBindingKind::Uniform,
                crate::foliage::FOLIAGE_UNIFORM_BYTES as u64,
            ));
        }
        let layout = device.create_bind_group_layout(BindGroupLayoutDescriptor {
            label: Some("instance storage fetch"),
            entries: &entries,
        })?;
        let shader = device.create_shader_module(graphics.module)?;
        let pipelines = pipeline(
            device,
            &shader,
            material,
            view,
            frame,
            format,
            graphics.vertex_entry_point,
            graphics.fragment_entry_point,
            None,
            false,
            Some(&layout),
        )?;
        Ok(Self {
            shader,
            vertex: graphics.vertex_entry_point.into(),
            fragment: graphics.fragment_entry_point.into(),
            pipelines,
            compact_pipelines: Vec::new(),
            layout,
            kernel,
            pages: std::sync::Mutex::new(Vec::new()),
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
        self.compact_pipelines = pipeline(
            device,
            &self.shader,
            material,
            view,
            frame,
            format,
            "vertex_compact_main",
            &self.fragment,
            None,
            false,
            Some(&self.layout),
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
            false,
            Some(&self.layout),
        )?;
        if !self.compact_pipelines.is_empty() {
            self.enable_compact(device, material, view, frame, format)?;
        }
        Ok(())
    }
    pub fn upload_group<Q: RhiQueue<D>>(
        &self,
        device: &D,
        queue: &Q,
        inputs: &[UploadInput<'_, D>],
        available_bytes: u64,
    ) -> Result<UploadResult<D>, RhiError> {
        let mut records = Vec::new();
        let mut groups = Vec::new();
        let mut bases = Vec::new();
        for (group, (_, batch, _, _)) in inputs.iter().enumerate() {
            bases.push(records.len() as u32);
            records.extend((0..batch.records().len()).map(|index| VisibilityRecord {
                bounds: batch.record_bounds(index).expect("validated bounds"),
                group: group as u32,
            }));
            groups.push(VisibilityGroup {
                index_count: batch.mesh().indices().len() as u32,
                first_index: 0,
                base_vertex: 0,
                max_distance: batch.max_draw_distance(),
            });
        }
        let selection_bytes = inputs.len() as u64 * 16;
        if selection_bytes > available_bytes {
            return Err(RhiError::new(
                RhiErrorKind::OutOfMemory,
                "instance selection budget exceeded",
            ));
        }
        let mut pages = self.pages.lock().unwrap();
        pages.retain(|entry| entry.page.strong_count() > 0 && entry.view.strong_count() > 0);
        let counts: Vec<_> = inputs
            .iter()
            .map(|(_, batch, _, _)| batch.records().len() as u32)
            .collect();
        let existing = pages.iter_mut().enumerate().find_map(|(index, entry)| {
            entry.leases.retain(|lease| lease.strong_count() > 0);
            let page = entry.page.upgrade()?;
            let view = entry.view.upgrade()?;
            let plan = ranges::plan(
                (page.layout().record_count(), page.layout().group_count()),
                entry
                    .leases
                    .iter()
                    .filter_map(|lease| lease.upgrade())
                    .map(|lease| (*lease).clone()),
                &counts,
            )?;
            Some((page, view, plan, Some(index)))
        });
        let (page, visibility_view, reservations, existing_index) = if let Some(existing) = existing
        {
            existing
        } else {
            let exact = VisibilityLayout::new(records.len(), groups.len())?;
            let spare = if records.len() >= 1024 {
                // Larger cohorts amortize changed-view dispatches across later
                // upload waves. Preserve compact reservations for small cohorts
                // and retry that size before exact allocation under limits.
                let compact = (records.len().next_power_of_two() * 4)
                    .min(65536)
                    .max(records.len());
                let preferred = if records.len() >= 4096 {
                    (records.len().next_power_of_two() * 16)
                        .min(262144)
                        .max(records.len())
                } else {
                    compact
                };
                [preferred, compact]
                    .into_iter()
                    .filter_map(|count| VisibilityLayout::new(count, groups.len().max(64)).ok())
                    .find(|layout| {
                        GpuVisibilityPage::<D>::supported(device, *layout)
                            && page_bytes(*layout) + selection_bytes <= available_bytes
                    })
            } else {
                None
            };
            let capacity = spare.unwrap_or(exact);
            if page_bytes(capacity) + selection_bytes > available_bytes {
                return Err(RhiError::new(
                    RhiErrorKind::OutOfMemory,
                    "instance page capacity budget exceeded",
                ));
            }
            (
                Arc::new(GpuVisibilityPage::with_reusable_capacity(
                    device,
                    queue,
                    self.kernel.clone(),
                    &records,
                    &groups,
                    capacity,
                )?),
                Arc::new(std::sync::Mutex::new(None)),
                bases
                    .iter()
                    .zip(&counts)
                    .enumerate()
                    .map(|(group, (&start, &count))| ranges::Reservation {
                        group: group as u32,
                        records: start..start + count,
                    })
                    .collect(),
                None,
            )
        };
        let page_bytes = page_bytes(page.layout());
        let mut result = Vec::with_capacity(inputs.len());
        for (group, &(slot, batch, source, foliage)) in inputs.iter().enumerate() {
            let lease = Arc::new(reservations[group].clone());
            let selection = device.create_buffer(BufferDescriptor {
                label: Some("instance group selection"),
                size: 16,
                usages: BufferUsages::UNIFORM | BufferUsages::COPY_DESTINATION,
            })?;
            let bytes: Vec<_> = [
                lease.group,
                page.layout().offsets_word(),
                page.layout().ids_word(),
                lease.records.start,
            ]
            .into_iter()
            .flat_map(u32::to_le_bytes)
            .collect();
            queue.write_buffer(&selection, 0, &bytes);
            let mut entries = vec![
                binding(0, source),
                binding(1, page.output()),
                binding(2, &selection),
            ];
            if let Some(fields) = foliage {
                entries.push(binding(3, fields));
            }
            let binding = device.create_bind_group(BindGroupDescriptor {
                label: Some("instance source and visible IDs"),
                layout: &self.layout,
                entries: &entries,
            })?;
            result.push((
                slot,
                GpuBatch {
                    page: page.clone(),
                    visibility_view: visibility_view.clone(),
                    group: lease.group,
                    records: batch.records().len() as u32,
                    binding,
                    _selection: selection,
                    _lease: lease,
                    page_bytes,
                },
            ));
        }
        // Existing residents remain untouched if any selection/binding allocation
        // above fails. Publish appended ranges only once all new bindings exist.
        let leases: Vec<_> = result
            .iter()
            .map(|(_, batch)| Arc::downgrade(&batch._lease))
            .collect();
        let mut retirement_bytes = 0;
        if let Some(index) = existing_index {
            let live: std::collections::HashSet<_> = pages[index]
                .leases
                .iter()
                .filter_map(|lease| lease.upgrade())
                .map(|lease| lease.group)
                .collect();
            let retired: Vec<_> = (0..page.active_counts().1)
                .filter(|group| {
                    !live.contains(group)
                        && !reservations
                            .iter()
                            .any(|reservation| reservation.group == *group)
                })
                .collect();
            retirement_bytes = page.disable_groups(queue, &retired)?;
            for (group, reservation) in reservations.iter().enumerate() {
                let start = bases[group] as usize;
                let local: Vec<_> = records[start..start + counts[group] as usize]
                    .iter()
                    .map(|record| VisibilityRecord {
                        bounds: record.bounds,
                        group: 0,
                    })
                    .collect();
                page.replace_group(
                    queue,
                    reservation.group,
                    reservation.records.clone(),
                    &local,
                    groups[group],
                )?;
            }
            *visibility_view.lock().unwrap() = None;
            pages[index].leases.extend(leases);
        } else {
            pages.push(PageEntry {
                page: Arc::downgrade(&page),
                view: Arc::downgrade(&visibility_view),
                leases,
            });
        }
        Ok((result, retirement_bytes))
    }
}
fn layout_entry(binding: u32, kind: BufferBindingKind, size: u64) -> BindGroupLayoutEntry {
    BindGroupLayoutEntry {
        binding,
        visibility: ShaderStages::VERTEX,
        binding_type: BindingType::Buffer {
            kind,
            dynamic_offset: false,
            minimum_size: NonZeroU64::new(size),
        },
    }
}
fn binding<B, T, S>(binding: u32, buffer: &B) -> BindGroupEntry<'_, B, T, S> {
    BindGroupEntry {
        binding,
        resource: BindingResource::Buffer {
            buffer,
            offset: 0,
            size: None,
        },
    }
}
