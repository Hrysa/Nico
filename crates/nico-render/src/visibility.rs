//! Bounded GPU visibility pages. Each page owns its input ranges, output and view
//! uniform. Submit consumers before changing ranges or recording the next view.
//! A page must not be shared by independently recorded/in-flight views.
use glam::{Mat4, Vec3};
use nico_presentation::InstanceBounds;
use nico_rhi::*;
use std::num::NonZeroU64;

/// Clip/eye/layout fields followed by a 512-group selection bitset.
pub const VISIBILITY_VIEW_BYTES: u64 = 160;

#[derive(Clone, Copy, Debug)]
pub struct VisibilityRecord {
    pub bounds: InstanceBounds,
    pub group: u32,
}
#[derive(Clone, Copy, Debug)]
pub struct VisibilityGroup {
    pub index_count: u32,
    pub first_index: u32,
    pub base_vertex: i32,
    pub max_distance: f32,
}

/// Packed u32 output regions shared with instance_visibility.slang. Group offsets
/// address the visible-ID region, not bytes or persistent instance records.
#[derive(Clone, Copy, Debug)]
pub struct VisibilityLayout {
    records: u32,
    groups: u32,
}
impl VisibilityLayout {
    pub fn record_count(self) -> u32 {
        self.records
    }
    pub fn group_count(self) -> u32 {
        self.groups
    }
    pub fn new(records: usize, groups: usize) -> Result<Self, RhiError> {
        if records > nico_presentation::MAX_BATCH_INSTANCES || groups == 0 || groups > 512 {
            return Err(invalid("visibility page count exceeds budget"));
        }
        Ok(Self {
            records: records as u32,
            groups: groups as u32,
        })
    }
    /// Start of the contiguous group counts. Single-group compaction writes the
    /// indirect instance count directly, avoiding duplicate atomic increments.
    pub fn counts_word(self) -> u32 {
        if self.groups == 1 {
            2 * self.records + 4
        } else {
            self.records
        }
    }
    pub fn offsets_word(self) -> u32 {
        self.records + self.groups
    }
    pub fn ids_word(self) -> u32 {
        self.records + 3 * self.groups
    }
    pub fn indirect_byte(self, group: u32) -> Option<u64> {
        (group < self.groups)
            .then_some(u64::from(2 * self.records + 3 * self.groups + 5 * group) * 4)
    }
    pub fn bytes(self) -> u64 {
        u64::from(2 * self.records + 8 * self.groups) * 4
    }
}
fn invalid(message: &str) -> RhiError {
    RhiError::new(RhiErrorKind::InvalidDescriptor, message)
}
fn validate_ordered_groups(records: &[VisibilityRecord]) -> Result<(), RhiError> {
    if records.windows(2).any(|pair| pair[0].group > pair[1].group) {
        return Err(invalid(
            "reusable visibility groups must occupy ordered contiguous ranges",
        ));
    }
    Ok(())
}

/// Reference used by the CPU fallback and validation. Ordering is source order;
/// the GPU may scatter a different ordering within an opaque/masked draw group.
pub fn visible_ids(
    records: &[VisibilityRecord],
    groups: &[VisibilityGroup],
    clip: Mat4,
    eye: Vec3,
) -> Result<Vec<Vec<u32>>, RhiError> {
    validate(records, groups)?;
    if !clip.is_finite() || !eye.is_finite() {
        return Err(invalid("invalid visibility view"));
    }
    let mut result = vec![Vec::new(); groups.len()];
    for (id, record) in records.iter().enumerate() {
        if record.bounds.intersects_clip(clip)
            && record
                .bounds
                .within_distance(eye, groups[record.group as usize].max_distance)
        {
            result[record.group as usize].push(id as u32);
        }
    }
    Ok(result)
}
fn validate(
    records: &[VisibilityRecord],
    groups: &[VisibilityGroup],
) -> Result<VisibilityLayout, RhiError> {
    let layout = VisibilityLayout::new(records.len(), groups.len())?;
    if records.iter().any(|r| r.group as usize >= groups.len())
        || groups.iter().any(|g| {
            !g.max_distance.is_finite()
                || g.max_distance <= 0.
                || g.index_count == 0
                || g.first_index.checked_add(g.index_count).is_none()
        })
    {
        return Err(invalid("invalid visibility group or draw range"));
    }
    Ok(layout)
}

pub struct GpuVisibilityKernel<D: RhiDevice> {
    bindings: D::BindGroupLayout,
    pipelines: Vec<D::ComputePipeline>,
}
impl<D: RhiDevice> GpuVisibilityKernel<D> {
    pub fn new(device: &D, shader: ShaderModuleDescriptor<'_>) -> Result<Self, RhiError> {
        if !GpuVisibilityPage::<D>::supported(device, VisibilityLayout::new(0, 1)?) {
            return Err(RhiError::new(
                RhiErrorKind::Unsupported,
                "GPU visibility capabilities unavailable",
            ));
        }
        let shader = device.create_shader_module(shader)?;
        let bindings = device.create_bind_group_layout(BindGroupLayoutDescriptor {
            label: Some("instance visibility"),
            entries: &[
                entry(0, BufferBindingKind::Storage { read_only: true }, 32),
                entry(1, BufferBindingKind::Storage { read_only: true }, 16),
                entry(2, BufferBindingKind::Storage { read_only: false }, 4),
                entry(3, BufferBindingKind::Uniform, VISIBILITY_VIEW_BYTES),
            ],
        })?;
        let pipeline_layout = device.create_pipeline_layout(PipelineLayoutDescriptor {
            label: Some("instance visibility"),
            bind_group_layouts: &[&bindings],
        })?;
        let pipelines = ["reset_main", "count_main", "scan_main", "scatter_main"]
            .into_iter()
            .map(|entry_point| {
                device.create_compute_pipeline(ComputePipelineDescriptor {
                    label: Some(entry_point),
                    layout: &pipeline_layout,
                    shader: &shader,
                    entry_point,
                })
            })
            .collect::<Result<Vec<_>, _>>()?;
        Ok(Self {
            bindings,
            pipelines,
        })
    }
}

pub struct GpuVisibilityPage<D: RhiDevice> {
    partitioned: u32,
    layout: VisibilityLayout,
    _records: D::Buffer,
    _groups: D::Buffer,
    output: D::Buffer,
    view: D::Buffer,
    binding: D::BindGroup,
    kernel: std::sync::Arc<GpuVisibilityKernel<D>>,
    active: std::sync::Mutex<(u32, u32)>,
}
impl<D: RhiDevice> GpuVisibilityPage<D> {
    /// Reject unsupported enabled limits before allocating resources. At least
    /// three compute storage bindings and one view uniform are required.
    pub fn supported(device: &D, layout: VisibilityLayout) -> bool {
        let c = device.capabilities();
        c.compute
            && c.indexed_indirect
            && c.vertex_storage
            && c.limits.max_bind_groups >= 1
            && c.limits.max_buffer_size >= VISIBILITY_VIEW_BYTES
            && c.limits.max_storage_buffers_per_shader_stage >= 3
            && c.limits.max_uniform_buffer_binding_size >= VISIBILITY_VIEW_BYTES
            && c.limits.max_compute_workgroup_size_x >= 64
            && c.limits.max_compute_invocations_per_workgroup >= 64
            && c.limits.max_compute_workgroups_per_dimension
                >= layout.records.max(layout.groups).div_ceil(64)
            && [
                layout.bytes(),
                u64::from(layout.records.max(1)) * 32,
                u64::from(layout.groups) * 16,
            ]
            .into_iter()
            .all(|bytes| {
                bytes <= c.limits.max_buffer_size
                    && bytes <= c.limits.max_storage_buffer_binding_size
            })
    }
    pub fn new<Q: RhiQueue<D>>(
        device: &D,
        queue: &Q,
        shader: ShaderModuleDescriptor<'_>,
        records: &[VisibilityRecord],
        groups: &[VisibilityGroup],
    ) -> Result<Self, RhiError> {
        let layout = validate(records, groups)?;
        if !Self::supported(device, layout) {
            return Err(RhiError::new(
                RhiErrorKind::Unsupported,
                "GPU visibility capabilities or limits unavailable",
            ));
        }
        let kernel = std::sync::Arc::new(GpuVisibilityKernel::new(device, shader)?);
        Self::with_kernel(device, queue, kernel, records, groups)
    }
    pub fn with_kernel<Q: RhiQueue<D>>(
        device: &D,
        queue: &Q,
        kernel: std::sync::Arc<GpuVisibilityKernel<D>>,
        records: &[VisibilityRecord],
        groups: &[VisibilityGroup],
    ) -> Result<Self, RhiError> {
        Self::with_output_mode(device, queue, kernel, records, groups, 0, None)
    }
    /// Reserves disjoint visible-ID ranges from immutable group capacities.
    /// Each range is compacted independently; unused tails are never drawn.
    /// Uploads an additional four-byte offset per group once at construction.
    pub fn with_partitioned_kernel<Q: RhiQueue<D>>(
        device: &D,
        queue: &Q,
        kernel: std::sync::Arc<GpuVisibilityKernel<D>>,
        records: &[VisibilityRecord],
        groups: &[VisibilityGroup],
    ) -> Result<Self, RhiError> {
        Self::with_output_mode(device, queue, kernel, records, groups, 1, None)
    }
    /// Allocates fixed output addresses with spare capacity for later groups.
    /// Only supplied records and groups are uploaded or dispatched. Allocation
    /// capacity, including unused space, must be charged to the owner's budget.
    pub fn with_partitioned_capacity<Q: RhiQueue<D>>(
        device: &D,
        queue: &Q,
        kernel: std::sync::Arc<GpuVisibilityKernel<D>>,
        records: &[VisibilityRecord],
        groups: &[VisibilityGroup],
        capacity: VisibilityLayout,
    ) -> Result<Self, RhiError> {
        Self::with_output_mode(device, queue, kernel, records, groups, 1, Some(capacity))
    }
    /// Creates independently replaceable contiguous groups. Records must be
    /// ordered by group; empty groups are allowed. An additional four-byte range
    /// end per group is uploaded into existing output scratch storage.
    pub fn with_reusable_capacity<Q: RhiQueue<D>>(
        device: &D,
        queue: &Q,
        kernel: std::sync::Arc<GpuVisibilityKernel<D>>,
        records: &[VisibilityRecord],
        groups: &[VisibilityGroup],
        capacity: VisibilityLayout,
    ) -> Result<Self, RhiError> {
        Self::with_output_mode(device, queue, kernel, records, groups, 2, Some(capacity))
    }
    fn with_output_mode<Q: RhiQueue<D>>(
        device: &D,
        queue: &Q,
        kernel: std::sync::Arc<GpuVisibilityKernel<D>>,
        records: &[VisibilityRecord],
        groups: &[VisibilityGroup],
        partitioned: u32,
        capacity: Option<VisibilityLayout>,
    ) -> Result<Self, RhiError> {
        let populated = validate(records, groups)?;
        if partitioned == 2 {
            validate_ordered_groups(records)?;
        }
        let layout = capacity.unwrap_or(populated);
        if populated.records > layout.records || populated.groups > layout.groups {
            return Err(invalid("visibility contents exceed allocation capacity"));
        }
        if !Self::supported(device, layout) {
            return Err(RhiError::new(
                RhiErrorKind::Unsupported,
                "GPU visibility page limits unavailable",
            ));
        }
        let input = buffer(
            device,
            u64::from(layout.records.max(1)) * 32,
            BufferUsages::STORAGE | BufferUsages::COPY_DESTINATION,
        )?;
        let group_buffer = buffer(
            device,
            u64::from(layout.groups) * 16,
            BufferUsages::STORAGE | BufferUsages::COPY_DESTINATION,
        )?;
        let output = buffer(
            device,
            layout.bytes(),
            BufferUsages::STORAGE
                | BufferUsages::INDIRECT
                | BufferUsages::COPY_SOURCE
                | BufferUsages::COPY_DESTINATION,
        )?;
        let view = buffer(
            device,
            VISIBILITY_VIEW_BYTES,
            BufferUsages::UNIFORM | BufferUsages::COPY_DESTINATION,
        )?;
        let mut bytes = Vec::with_capacity(records.len() * 32);
        for record in records {
            for f in record.bounds.min() {
                bytes.extend_from_slice(&f.to_le_bytes());
            }
            bytes.extend_from_slice(&record.group.to_le_bytes());
            for f in record.bounds.max() {
                bytes.extend_from_slice(&f.to_le_bytes());
            }
            bytes.extend_from_slice(&0u32.to_le_bytes());
        }
        if !bytes.is_empty() {
            queue.write_buffer(&input, 0, &bytes);
        }
        bytes.clear();
        for group in groups {
            bytes.extend_from_slice(&group.index_count.to_le_bytes());
            bytes.extend_from_slice(&group.first_index.to_le_bytes());
            bytes.extend_from_slice(&group.base_vertex.to_le_bytes());
            bytes.extend_from_slice(&group.max_distance.to_le_bytes());
        }
        queue.write_buffer(&group_buffer, 0, &bytes);
        if partitioned != 0 {
            let mut capacities = vec![0_u32; groups.len()];
            for record in records {
                capacities[record.group as usize] += 1;
            }
            bytes.clear();
            let mut offset = 0_u32;
            for &capacity in &capacities {
                bytes.extend_from_slice(&offset.to_le_bytes());
                offset += capacity;
            }
            queue.write_buffer(&output, u64::from(layout.offsets_word()) * 4, &bytes);
            if partitioned == 2 {
                bytes.clear();
                let mut end = 0_u32;
                for capacity in capacities {
                    end += capacity;
                    bytes.extend_from_slice(&end.to_le_bytes());
                }
                queue.write_buffer(
                    &output,
                    u64::from(layout.records + 2 * layout.groups) * 4,
                    &bytes,
                );
            }
        }
        let binding = device.create_bind_group(BindGroupDescriptor {
            label: Some("instance visibility"),
            layout: &kernel.bindings,
            entries: &[
                bind(0, &input),
                bind(1, &group_buffer),
                bind(2, &output),
                bind(3, &view),
            ],
        })?;
        Ok(Self {
            partitioned,
            layout,
            _records: input,
            _groups: group_buffer,
            output,
            view,
            binding,
            kernel,
            active: std::sync::Mutex::new((populated.records, populated.groups)),
        })
    }
    /// Current populated prefix, independent of allocation/layout capacity.
    pub fn active_counts(&self) -> (u32, u32) {
        *self.active.lock().unwrap()
    }
    /// Appends new groups with locally numbered record groups. Existing input
    /// ranges and output addresses stay fixed. Returns (first record, first group).
    /// Reusable pages also upload a four-byte exclusive range end per group.
    ///
    /// The render owner must serialize this with recording and submit previously
    /// recorded consumers before appending on the same queue. Any cached visibility
    /// result is invalid after a successful append; active_counts changes its key.
    /// Rejected appends perform no writes. This does not reclaim retired ranges.
    pub fn append<Q: RhiQueue<D>>(
        &self,
        queue: &Q,
        records: &[VisibilityRecord],
        groups: &[VisibilityGroup],
    ) -> Result<(u32, u32), RhiError> {
        let added = validate(records, groups)?;
        let mut active = self.active.lock().unwrap();
        if self.partitioned == 0
            || added.records > self.layout.records - active.0
            || added.groups > self.layout.groups - active.1
        {
            return Err(invalid(
                "visibility append exceeds partitioned page capacity",
            ));
        }
        let base = *active;
        if self.partitioned == 2 {
            validate_ordered_groups(records)?;
        }
        let mut bytes = Vec::with_capacity(records.len() * 32);
        let mut capacities = vec![0_u32; groups.len()];
        for record in records {
            for f in record.bounds.min() {
                bytes.extend_from_slice(&f.to_le_bytes());
            }
            bytes.extend_from_slice(&(record.group + base.1).to_le_bytes());
            for f in record.bounds.max() {
                bytes.extend_from_slice(&f.to_le_bytes());
            }
            bytes.extend_from_slice(&0_u32.to_le_bytes());
            capacities[record.group as usize] += 1;
        }
        if !bytes.is_empty() {
            queue.write_buffer(&self._records, u64::from(base.0) * 32, &bytes);
        }
        bytes.clear();
        for group in groups {
            bytes.extend_from_slice(&group.index_count.to_le_bytes());
            bytes.extend_from_slice(&group.first_index.to_le_bytes());
            bytes.extend_from_slice(&group.base_vertex.to_le_bytes());
            bytes.extend_from_slice(&group.max_distance.to_le_bytes());
        }
        queue.write_buffer(&self._groups, u64::from(base.1) * 16, &bytes);
        bytes.clear();
        let mut offset = base.0;
        for &capacity in &capacities {
            bytes.extend_from_slice(&offset.to_le_bytes());
            offset += capacity;
        }
        queue.write_buffer(
            &self.output,
            u64::from(self.layout.offsets_word() + base.1) * 4,
            &bytes,
        );
        *active = (base.0 + added.records, base.1 + added.groups);
        if self.partitioned == 2 {
            bytes.clear();
            let mut end = base.0;
            for capacity in capacities {
                end += capacity;
                bytes.extend_from_slice(&end.to_le_bytes());
            }
            queue.write_buffer(
                &self.output,
                u64::from(self.layout.records + 2 * self.layout.groups + base.1) * 4,
                &bytes,
            );
        }
        Ok(base)
    }
    pub fn layout(&self) -> VisibilityLayout {
        self.layout
    }
    /// Disables retired groups before their output intervals are reassigned.
    /// Returns dynamic retirement-upload bytes, separate from new source uploads.
    /// The owner must hold no live lease for these groups and invalidate visibility.
    pub fn disable_groups<Q: RhiQueue<D>>(
        &self,
        queue: &Q,
        groups: &[u32],
    ) -> Result<u64, RhiError> {
        let active = self.active.lock().unwrap();
        if self.partitioned != 2
            || groups.len() > active.1 as usize
            || groups.iter().any(|&group| group >= active.1)
        {
            return Err(invalid("invalid retired visibility groups"));
        }
        let mut groups = groups.to_vec();
        groups.sort_unstable();
        groups.dedup();
        let mut start = 0;
        while start < groups.len() {
            let mut end = start + 1;
            while end < groups.len() && groups[end] == groups[end - 1] + 1 {
                end += 1;
            }
            queue.write_buffer(
                &self.output,
                u64::from(self.layout.records + 2 * self.layout.groups + groups[start]) * 4,
                &vec![0; (end - start) * 4],
            );
            start = end;
        }
        Ok(groups.len() as u64 * 4)
    }
    /// Writes an existing group or the next group inside an owner-reserved interval.
    /// Records use local group zero. An empty replacement disables the group;
    /// stale records outside its new active interval cannot contribute IDs.
    ///
    /// The owner must prove that the interval is free of other live groups, submit
    /// prior consumers before writing, and invalidate cached visibility even when
    /// active_counts is unchanged. Rejected replacements perform no writes.
    /// Upload cost is 32 bytes per supplied record plus 24 bytes of group data.
    pub fn replace_group<Q: RhiQueue<D>>(
        &self,
        queue: &Q,
        group_index: u32,
        reserved: std::ops::Range<u32>,
        records: &[VisibilityRecord],
        group: VisibilityGroup,
    ) -> Result<(), RhiError> {
        validate(records, &[group])?;
        let mut active = self.active.lock().unwrap();
        if self.partitioned != 2
            || group_index > active.1
            || group_index >= self.layout.groups
            || reserved.start > reserved.end
            || reserved.start > active.0
            || reserved.end > self.layout.records
            || records.len() as u64 > u64::from(reserved.end - reserved.start)
        {
            return Err(invalid("invalid reusable visibility group interval"));
        }
        let mut bytes = Vec::with_capacity(records.len() * 32);
        for record in records {
            for f in record.bounds.min() {
                bytes.extend_from_slice(&f.to_le_bytes());
            }
            bytes.extend_from_slice(&group_index.to_le_bytes());
            for f in record.bounds.max() {
                bytes.extend_from_slice(&f.to_le_bytes());
            }
            bytes.extend_from_slice(&0_u32.to_le_bytes());
        }
        if !bytes.is_empty() {
            queue.write_buffer(&self._records, u64::from(reserved.start) * 32, &bytes);
        }
        bytes.clear();
        bytes.extend_from_slice(&group.index_count.to_le_bytes());
        bytes.extend_from_slice(&group.first_index.to_le_bytes());
        bytes.extend_from_slice(&group.base_vertex.to_le_bytes());
        bytes.extend_from_slice(&group.max_distance.to_le_bytes());
        queue.write_buffer(&self._groups, u64::from(group_index) * 16, &bytes);
        queue.write_buffer(
            &self.output,
            u64::from(self.layout.offsets_word() + group_index) * 4,
            &reserved.start.to_le_bytes(),
        );
        let end = reserved.start + records.len() as u32;
        queue.write_buffer(
            &self.output,
            u64::from(self.layout.records + 2 * self.layout.groups + group_index) * 4,
            &end.to_le_bytes(),
        );
        *active = (active.0.max(end), active.1.max(group_index + 1));
        Ok(())
    }
    /// Bind this output as read-only u32 vertex storage or indexed indirect input.
    pub fn output(&self) -> &D::Buffer {
        &self.output
    }
    /// Writes the view and records ordered dispatches. Submit this encoder before
    /// another call updates the same page's uniform. Separate views need separate
    /// pages; subsequent submissions on the same queue may safely reuse a page.
    pub fn record<Q: RhiQueue<D>>(
        &self,
        queue: &Q,
        encoder: &mut D::CommandEncoder,
        clip: Mat4,
        eye: Vec3,
    ) -> Result<(), RhiError> {
        Self::record_pages(&[self], queue, encoder, clip, eye).map(|_| ())
    }

    /// Records independent pages in one compute pass. Each page has the same
    /// submission-before-update requirement as [`Self::record`].
    /// Returns the number of encoded dispatches, not completed GPU work.
    pub fn record_pages<Q: RhiQueue<D>>(
        pages: &[&Self],
        queue: &Q,
        encoder: &mut D::CommandEncoder,
        clip: Mat4,
        eye: Vec3,
    ) -> Result<u32, RhiError> {
        let selected: Vec<_> = pages.iter().map(|page| (*page, [u32::MAX; 16])).collect();
        Self::record_selected_pages(&selected, queue, encoder, clip, eye)
    }

    /// Cull only groups whose bit is set in each page's 512-group selection.
    /// Other group counts are reset to zero. Selection is part of the view and
    /// must be included in any caller-owned visibility cache key.
    pub fn record_selected_pages<Q: RhiQueue<D>>(
        pages: &[(&Self, [u32; 16])],
        queue: &Q,
        encoder: &mut D::CommandEncoder,
        clip: Mat4,
        eye: Vec3,
    ) -> Result<u32, RhiError> {
        if !clip.is_finite() || !eye.is_finite() {
            return Err(invalid("invalid visibility view"));
        }
        if pages.is_empty() {
            return Ok(0);
        }
        let mut identities = std::collections::HashSet::with_capacity(pages.len());
        if pages
            .iter()
            .any(|(page, _)| !identities.insert(std::ptr::from_ref(*page)))
        {
            return Err(invalid("duplicate visibility page"));
        }
        let populated: Vec<_> = pages.iter().map(|(page, _)| page.active_counts()).collect();
        for ((page, selection), &(active_records, active_groups)) in pages.iter().zip(&populated) {
            let mut bytes: Vec<_> = clip
                .to_cols_array()
                .into_iter()
                .chain(eye.to_array())
                .flat_map(f32::to_le_bytes)
                .collect();
            for word in [
                page.layout.records,
                page.layout.groups,
                active_records,
                page.partitioned,
                active_groups,
            ] {
                bytes.extend_from_slice(&word.to_le_bytes());
            }
            for word in selection {
                bytes.extend_from_slice(&word.to_le_bytes());
            }
            queue.write_buffer(&page.view, 0, &bytes);
        }
        let mut pass = encoder.begin_compute_pass(ComputePassDescriptor {
            label: Some("instance visibility"),
        });
        let mut dispatches = 0;
        for stage in 0..4 {
            for ((page, _), &(active_records, active_groups)) in pages.iter().zip(&populated) {
                if (page.partitioned != 0 || page.layout.groups == 1) && stage >= 2 {
                    continue;
                }
                let count = match stage {
                    0 => active_groups.div_ceil(64),
                    1 | 3 => active_records.div_ceil(64),
                    _ => 1,
                };
                if count == 0 {
                    continue;
                }
                pass.set_pipeline(&page.kernel.pipelines[stage]);
                pass.set_bind_group(0, &page.binding, &[]);
                pass.dispatch(count, 1, 1);
                dispatches += 1;
            }
        }
        Ok(dispatches)
    }
}
fn entry(binding: u32, kind: BufferBindingKind, size: u64) -> BindGroupLayoutEntry {
    BindGroupLayoutEntry {
        binding,
        visibility: ShaderStages::COMPUTE,
        binding_type: BindingType::Buffer {
            kind,
            dynamic_offset: false,
            minimum_size: NonZeroU64::new(size),
        },
    }
}
fn bind<B, T, S>(binding: u32, buffer: &B) -> BindGroupEntry<'_, B, T, S> {
    BindGroupEntry {
        binding,
        resource: BindingResource::Buffer {
            buffer,
            offset: 0,
            size: None,
        },
    }
}
fn buffer<D: RhiDevice>(
    device: &D,
    size: u64,
    usages: BufferUsages,
) -> Result<D::Buffer, RhiError> {
    device.create_buffer(BufferDescriptor {
        label: Some("instance visibility page"),
        size,
        usages,
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn visibility_layout_bounds_every_output_region_and_rejects_overflow() {
        for (n, g) in [(0, 1), (129, 1), (500_000, 1), (1, 512), (500_000, 512)] {
            let layout = VisibilityLayout::new(n, g).unwrap();
            if g == 1 {
                assert_eq!(
                    u64::from(layout.counts_word()) * 4,
                    layout.indirect_byte(0).unwrap() + 4
                );
            } else {
                assert_eq!(layout.counts_word() as usize, n);
            }
            assert_eq!(layout.offsets_word() as usize, n + g);
            assert_eq!(layout.ids_word() as usize, n + 3 * g);
            assert_eq!(
                layout.indirect_byte(g as u32 - 1).unwrap() + 20,
                layout.bytes()
            );
            assert!(layout.indirect_byte(g as u32).is_none());
        }
        for (n, g) in [(usize::MAX, 1), (0, 0), (0, 513), (500_001, 1)] {
            assert!(VisibilityLayout::new(n, g).is_err());
        }
    }
    #[test]
    fn cpu_visibility_keeps_boundary_bounds_and_rejects_invalid_group_data() {
        let records = [VisibilityRecord {
            bounds: InstanceBounds::new([1., 0., 0.], [2., 1., 1.]).unwrap(),
            group: 0,
        }];
        let mut groups = [VisibilityGroup {
            index_count: 3,
            first_index: 0,
            base_vertex: 0,
            max_distance: 1.,
        }];
        assert_eq!(
            visible_ids(&records, &groups, Mat4::IDENTITY, Vec3::ZERO).unwrap(),
            vec![vec![0]]
        );
        groups[0].max_distance = 0.99;
        assert!(visible_ids(&records, &groups, Mat4::IDENTITY, Vec3::ZERO).unwrap()[0].is_empty());
        groups[0].max_distance = f32::NAN;
        assert!(visible_ids(&records, &groups, Mat4::IDENTITY, Vec3::ZERO).is_err());
        groups[0].max_distance = 1.;
        groups[0].first_index = u32::MAX;
        assert!(visible_ids(&records, &groups, Mat4::IDENTITY, Vec3::ZERO).is_err());
        groups[0].first_index = 0;
        let invalid = [VisibilityRecord {
            group: 1,
            ..records[0]
        }];
        assert!(visible_ids(&invalid, &groups, Mat4::IDENTITY, Vec3::ZERO).is_err());
    }
}
