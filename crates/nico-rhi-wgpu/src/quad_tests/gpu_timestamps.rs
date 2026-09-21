//! Opt-in pass timestamps for ignored Arena and tiny-prototype benchmarks only.
use super::*;

const QUERY_COUNT: u32 = 4096;

#[derive(Debug, Default)]
struct Samples {
    slot: Option<usize>,
    entries: Vec<(usize, usize)>,
}

#[derive(Debug)]
pub(crate) struct PassTimestamps {
    queries: wgpu::QuerySet,
    dispatches: bool,
    samples: Mutex<Samples>,
}

impl PassTimestamps {
    pub(super) fn new(device: &WgpuDevice) -> Arc<Self> {
        assert!(
            device
                .inner
                .features()
                .contains(wgpu::Features::TIMESTAMP_QUERY)
        );
        Arc::new(Self {
            dispatches: device
                .inner
                .features()
                .contains(wgpu::Features::TIMESTAMP_QUERY_INSIDE_PASSES),
            queries: device.inner.create_query_set(&wgpu::QuerySetDescriptor {
                label: Some("ignored benchmark pass timestamps"),
                ty: wgpu::QueryType::Timestamp,
                count: QUERY_COUNT,
            }),
            samples: Mutex::new(Samples::default()),
        })
    }

    pub(super) fn select(&self, slot: Option<usize>) {
        self.samples.lock().unwrap().slot = slot;
    }

    pub(crate) fn allocate(&self, label: Option<&str>) -> Option<(wgpu::QuerySet, u32)> {
        let kind = match label? {
            "instance visibility" => 0,
            "3D opaque and masked" | "tiny instance tile experiment" => 1,
            "visibility_reset" => 2,
            "visibility_count" => 3,
            _ => return None,
        };
        let mut samples = self.samples.lock().unwrap();
        let slot = samples.slot?;
        let index = u32::try_from(samples.entries.len()).unwrap() * 2;
        assert!(
            index + 1 < QUERY_COUNT,
            "benchmark timestamp capacity exceeded"
        );
        samples.entries.push((slot, kind));
        Some((self.queries.clone(), index))
    }

    pub(crate) fn allocate_dispatch(&self, label: Option<&str>) -> Option<(wgpu::QuerySet, u32)> {
        if self.dispatches {
            self.allocate(label)
        } else {
            None
        }
    }

    /// Resolve after the entire round; mapping and its wait are outside frame timings.
    pub(super) fn report(
        &self,
        device: &WgpuDevice,
        queue: &WgpuQueue,
        context: &str,
        expected_frames: [u32; 2],
        variant_labels: [&str; 2],
    ) {
        let mut samples = self.samples.lock().unwrap();
        samples.slot = None;
        let count = samples.entries.len() as u32 * 2;
        if count == 0 {
            return;
        }
        let size = u64::from(count) * 8;
        let resolved = device.inner.create_buffer(&wgpu::BufferDescriptor {
            label: Some("benchmark timestamp resolve"),
            size,
            usage: wgpu::BufferUsages::QUERY_RESOLVE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let staging = device.inner.create_buffer(&wgpu::BufferDescriptor {
            label: Some("benchmark timestamp readback"),
            size,
            usage: wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        let mut encoder = device.inner.create_command_encoder(&Default::default());
        encoder.resolve_query_set(&self.queries, 0..count, &resolved, 0);
        encoder.copy_buffer_to_buffer(&resolved, 0, &staging, 0, size);
        queue.inner.submit([encoder.finish()]);
        let (tx, rx) = std::sync::mpsc::channel();
        staging.slice(..).map_async(wgpu::MapMode::Read, move |r| {
            tx.send(r).unwrap();
        });
        device
            .inner
            .poll(wgpu::PollType::Wait {
                submission_index: None,
                timeout: Some(std::time::Duration::from_secs(10)),
            })
            .unwrap();
        rx.recv_timeout(std::time::Duration::from_secs(10))
            .unwrap()
            .unwrap();
        let bytes = staging.slice(..).get_mapped_range().unwrap();
        let mut totals = [[(0_u64, 0_u32); 4]; 2];
        for (pair, &(slot, kind)) in bytes.chunks_exact(16).zip(&samples.entries) {
            let begin = u64::from_le_bytes(pair[..8].try_into().unwrap());
            let end = u64::from_le_bytes(pair[8..].try_into().unwrap());
            let elapsed = end.checked_sub(begin).expect("nonmonotonic GPU timestamps");
            totals[slot][kind].0 += elapsed;
            totals[slot][kind].1 += 1;
        }
        let period = queue.inner.get_timestamp_period();
        assert!(period.is_finite() && period > 0.);
        for (slot, kinds) in totals.iter().enumerate() {
            assert_eq!(
                kinds[1].1, expected_frames[slot],
                "every measured frame must have an opaque pass timestamp"
            );
            for (kind, &(ticks, count)) in kinds.iter().enumerate() {
                if count != 0 {
                    eprintln!(
                        "gpu_pass {context} {} pass={} samples={count} mean_ms={:.6}",
                        variant_labels[slot],
                        [
                            "visibility",
                            "opaque_and_masked",
                            "visibility_reset",
                            "visibility_count"
                        ][kind],
                        ticks as f64 * f64::from(period) / f64::from(count) / 1e6
                    );
                }
            }
        }
        drop(bytes);
        staging.unmap();
        samples.entries.clear();
    }
}
