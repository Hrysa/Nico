//! Bounded asynchronous observations of submitted visibility, never draw inputs.
use super::*;
use std::time::Instant;

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct GpuVisibilitySample {
    pub prepared_view: u64,
    pub visible_instances: u32,
    pub candidate_instances: u32,
    pub indirect_draws: u32,
    /// Wall time since the sampled view was submitted, not GPU execution time.
    pub age_ms: u64,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct GpuReadbackStats {
    pub sample: Option<GpuVisibilitySample>,
    pub pending: u32,
    pub skipped: u64,
    pub failed: u64,
}
struct Pending {
    ticket: Box<dyn nico_rhi::BufferReadback>,
    sample: GpuVisibilitySample,
    capacities: Vec<u32>,
    submitted: Instant,
}
#[derive(Default)]
pub(in crate::meshes) struct Readbacks {
    pending: Vec<Pending>,
    latest: Option<(GpuVisibilitySample, Instant)>,
    skipped: u64,
    failed: u64,
}
pub(in crate::meshes) struct Staged<D: RhiDevice> {
    buffer: Option<D::Buffer>,
    sample: GpuVisibilitySample,
    capacities: Vec<u32>,
}
impl Readbacks {
    fn can_sample(&mut self, supported: bool, prepared_view: u64) -> bool {
        if !supported || prepared_view % 8 != 1 {
            return false;
        }
        if self.pending.len() >= 3 {
            self.skipped = self.skipped.saturating_add(1);
            return false;
        }
        true
    }
    pub fn stats(&self) -> GpuReadbackStats {
        GpuReadbackStats {
            sample: self.latest.map(|(mut sample, submitted)| {
                sample.age_ms = submitted.elapsed().as_millis().min(u64::MAX as u128) as u64;
                sample
            }),
            pending: self.pending.len() as u32,
            skipped: self.skipped,
            failed: self.failed,
        }
    }
    fn publish(&mut self, sample: GpuVisibilitySample, submitted: Instant) {
        if self
            .latest
            .is_none_or(|(old, _)| old.prepared_view < sample.prepared_view)
        {
            self.latest = Some((sample, submitted));
        }
    }
    pub fn poll(&mut self) {
        let mut index = 0;
        while index < self.pending.len() {
            let result = self.pending[index].ticket.poll();
            if matches!(result, Ok(None)) {
                index += 1;
                continue;
            }
            let mut pending = self.pending.swap_remove(index);
            match result
                .ok()
                .flatten()
                .and_then(|bytes| visible_count(&bytes, &pending.capacities))
            {
                Some(count) => {
                    pending.sample.visible_instances = count;
                    self.publish(pending.sample, pending.submitted);
                }
                None => self.failed = self.failed.saturating_add(1),
            }
        }
    }
    pub fn stage<'a, D>(
        &mut self,
        device: &D,
        encoder: &mut D::CommandEncoder,
        pages: impl Iterator<Item = (&'a crate::visibility::GpuVisibilityPage<D>, u32, u32)>,
        prepared_view: u64,
    ) -> Option<Staged<D>>
    where
        D: RhiDevice + 'a,
    {
        // Sample one in eight prepared views, at most three maps and 6 KiB of
        // staging in flight. Sampling pressure must never delay rendering.
        if !self.can_sample(device.supports_buffer_readback(), prepared_view) {
            return None;
        }
        let pages: Vec<_> = pages.take(513).collect();
        if pages.len() > 512
            || pages.iter().any(|(p, group, count)| {
                *group >= p.layout().group_count() || *count > p.layout().record_count()
            })
        {
            self.failed = self.failed.saturating_add(1);
            return None;
        }
        let capacities: Vec<_> = pages.iter().map(|(_, _, count)| *count).collect();
        let candidate_instances = capacities.iter().copied().sum();
        let buffer = if pages.is_empty() {
            None
        } else {
            match device.create_buffer(BufferDescriptor {
                label: Some("instance count readback"),
                size: (pages.len() * 4) as u64,
                usages: BufferUsages::MAP_READ | BufferUsages::COPY_DESTINATION,
            }) {
                Ok(buffer) => Some(buffer),
                Err(_) => {
                    self.failed = self.failed.saturating_add(1);
                    return None;
                }
            }
        };
        if let Some(buffer) = &buffer {
            for (index, (page, group, _)) in pages.iter().enumerate() {
                encoder.copy_buffer_to_buffer(
                    page.output(),
                    u64::from(page.layout().counts_word() + group) * 4,
                    buffer,
                    index as u64 * 4,
                    4,
                );
            }
        }
        Some(Staged {
            buffer,
            capacities,
            sample: GpuVisibilitySample {
                prepared_view,
                candidate_instances,
                indirect_draws: pages.len() as u32,
                ..Default::default()
            },
        })
    }
    pub fn submitted<D: RhiDevice>(&mut self, device: &D, staged: Staged<D>) {
        let submitted = Instant::now();
        let Some(buffer) = staged.buffer else {
            self.publish(staged.sample, submitted);
            return;
        };
        match device.read_buffer_async(&buffer, 0..staged.capacities.len() as u64 * 4) {
            Ok(ticket) => self.pending.push(Pending {
                ticket,
                sample: staged.sample,
                capacities: staged.capacities,
                submitted,
            }),
            Err(_) => self.failed = self.failed.saturating_add(1),
        }
    }
}
fn visible_count(bytes: &[u8], capacities: &[u32]) -> Option<u32> {
    if bytes.len() != capacities.len() * 4 {
        return None;
    }
    bytes
        .chunks_exact(4)
        .zip(capacities)
        .try_fold(0_u32, |sum, (bytes, capacity)| {
            let count = u32::from_le_bytes(bytes.try_into().ok()?);
            if count > *capacity {
                return None;
            }
            sum.checked_add(count)
        })
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    struct ControlledReply {
        state: Arc<AtomicUsize>,
        drops: Arc<AtomicUsize>,
        polls: Arc<AtomicUsize>,
    }
    impl nico_rhi::BufferReadback for ControlledReply {
        fn poll(&mut self) -> Result<Option<Vec<u8>>, RhiError> {
            self.polls.fetch_add(1, Ordering::Relaxed);
            match self.state.load(Ordering::Relaxed) {
                0 => Ok(None),
                1 => Ok(Some(1_u32.to_le_bytes().to_vec())),
                _ => Err(RhiError::new(
                    RhiErrorKind::DeviceLost,
                    "injected readback failure",
                )),
            }
        }
    }
    impl Drop for ControlledReply {
        fn drop(&mut self) {
            self.drops.fetch_add(1, Ordering::Relaxed);
        }
    }
    #[test]
    fn stalled_maps_bound_sampling_failure_frees_capacity_and_shutdown_releases_tickets() {
        let mut readbacks = Readbacks::default();
        let drops = Arc::new(AtomicUsize::new(0));
        let polls = Arc::new(AtomicUsize::new(0));
        let states: Vec<_> = (0..3).map(|_| Arc::new(AtomicUsize::new(0))).collect();
        for (index, state) in states.iter().enumerate() {
            assert!(readbacks.can_sample(true, index as u64 * 8 + 1));
            readbacks.pending.push(Pending {
                ticket: Box::new(ControlledReply {
                    state: state.clone(),
                    drops: drops.clone(),
                    polls: polls.clone(),
                }),
                sample: GpuVisibilitySample {
                    prepared_view: index as u64 * 8 + 1,
                    ..Default::default()
                },
                capacities: vec![1],
                submitted: Instant::now(),
            });
        }
        assert!(!readbacks.can_sample(false, 25));
        assert!(!readbacks.can_sample(true, 24));
        assert_eq!(readbacks.stats().skipped, 0);
        assert!(!readbacks.can_sample(true, 25));
        readbacks.poll();
        assert_eq!(polls.load(Ordering::Relaxed), 3);
        assert_eq!(readbacks.stats().pending, 3);
        assert_eq!(readbacks.stats().skipped, 1);
        assert_eq!(drops.load(Ordering::Relaxed), 0);
        states[1].store(2, Ordering::Relaxed);
        states[2].store(1, Ordering::Relaxed);
        readbacks.poll();
        assert_eq!(readbacks.stats().failed, 1);
        assert_eq!(readbacks.stats().pending, 1);
        assert_eq!(readbacks.stats().sample.unwrap().prepared_view, 17);
        assert_eq!(drops.load(Ordering::Relaxed), 2);
        assert!(readbacks.can_sample(true, 25));
        drop(readbacks);
        assert_eq!(drops.load(Ordering::Relaxed), 3);
    }
    struct Reply(Option<Vec<u8>>);
    impl nico_rhi::BufferReadback for Reply {
        fn poll(&mut self) -> Result<Option<Vec<u8>>, RhiError> {
            Ok(self.0.take())
        }
    }
    #[test]
    fn out_of_order_and_invalid_samples_cannot_replace_newer_valid_observations() {
        let mut readbacks = Readbacks::default();
        for (view, bytes) in [
            (9, 2_u32.to_le_bytes().to_vec()),
            (1, 1_u32.to_le_bytes().to_vec()),
            (17, vec![0]),
        ] {
            readbacks.pending.push(Pending {
                ticket: Box::new(Reply(Some(bytes))),
                capacities: vec![2],
                submitted: Instant::now(),
                sample: GpuVisibilitySample {
                    prepared_view: view,
                    candidate_instances: 2,
                    indirect_draws: 1,
                    ..Default::default()
                },
            });
        }
        readbacks.poll();
        let stats = readbacks.stats();
        assert_eq!(stats.pending, 0);
        assert_eq!(stats.failed, 1);
        let sample = stats.sample.unwrap();
        assert_eq!(sample.prepared_view, 9);
        assert_eq!(sample.visible_instances, 2);
    }
    #[test]
    fn readback_counts_reject_truncation_overflow_and_out_of_capacity_results() {
        let bytes: Vec<_> = [2_u32, 0, 3]
            .into_iter()
            .flat_map(u32::to_le_bytes)
            .collect();
        assert_eq!(visible_count(&bytes, &[2, 4, 3]), Some(5));
        assert_eq!(visible_count(&bytes, &[1, 4, 3]), None);
        assert_eq!(visible_count(&bytes[..8], &[2, 4, 3]), None);
        let overflow: Vec<_> = [u32::MAX, 1]
            .into_iter()
            .flat_map(u32::to_le_bytes)
            .collect();
        assert_eq!(visible_count(&overflow, &[u32::MAX, 1]), None);
    }
}
