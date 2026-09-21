//! Provider-independent chunk publication. Call on the presentation owner; workers
//! receive cancellation tickets and return complete immutable batch sets.
pub mod streaming;
pub mod workers;
use nico_presentation::{InstanceBatch, InstanceBounds};
use std::{
    collections::BTreeMap,
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
};

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct ChunkKey {
    pub provider: u32,
    pub chunk: u64,
}
#[derive(Clone, Debug)]
pub struct ChunkRequest {
    key: ChunkKey,
    generation: u64,
    cancelled: Arc<AtomicBool>,
}
impl ChunkRequest {
    pub(crate) fn same_ticket(&self, other: &Self) -> bool {
        self.key == other.key
            && self.generation == other.generation
            && Arc::ptr_eq(&self.cancelled, &other.cancelled)
    }
    pub(crate) fn cancel(&self) {
        self.cancelled.store(true, Ordering::Release);
    }
    pub fn key(&self) -> ChunkKey {
        self.key
    }
    pub fn generation(&self) -> u64 {
        self.generation
    }
    pub fn cancelled(&self) -> bool {
        self.cancelled.load(Ordering::Acquire)
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ChunkError {
    Closed,
    Capacity,
    GenerationExhausted,
    Stale,
    ProviderFailed,
    InvalidSchedule,
}

#[derive(Clone, Copy, Debug)]
pub struct StreamingPolicy {
    load: f32,
    unload: f32,
    requests_per_update: usize,
}
impl StreamingPolicy {
    pub fn new(load: f32, unload: f32, requests_per_update: usize) -> Option<Self> {
        (load.is_finite()
            && unload.is_finite()
            && load >= 0.
            && unload > load
            && (1..=32).contains(&requests_per_update))
        .then_some(Self {
            load,
            unload,
            requests_per_update,
        })
    }
}
impl std::fmt::Display for ChunkError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "instance chunk: {self:?}")
    }
}
impl std::error::Error for ChunkError {}

struct Entry {
    pending: Option<ChunkRequest>,
    resident: Option<Arc<[Arc<InstanceBatch>]>>,
    records: usize,
    decoded_bytes: usize,
    failed: bool,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ChunkStatus {
    /// Cumulative tracked entries explicitly unloaded by this owner, including
    /// pending/failed entries. Repeated unloads of absent keys and close do not count.
    pub evicted: u64,
    pub pending: usize,
    pub resident: usize,
    pub failed: usize,
    pub records: usize,
    pub batches: usize,
    /// Charged resident batch payload; shared batch references are charged each
    /// time. Excludes assets, allocator metadata and externally retained snapshots.
    pub decoded_bytes: usize,
}

/// Bounds tracked keys, in-flight requests, resident records and 512 total batches. Prototype
/// asset memory is shared and remains owned by the asset system. Visibility never
/// implicitly unloads a chunk; explicit unload drops only this owner's references.
pub struct InstanceChunks {
    evicted: u64,
    entries: BTreeMap<ChunkKey, Entry>,
    generation: u64,
    max_chunks: usize,
    max_pending: usize,
    max_records: usize,
    max_decoded_bytes: usize,
    closed: bool,
}
impl InstanceChunks {
    /// Reconcile a complete bounded provider catalog against world-space distance.
    /// Bounds must include maximum deformation. Missing catalog entries unload;
    /// failed entries retry only after explicit request or unload/reentry.
    /// Returns tickets for the provider executor, not completed/resident data.
    pub fn schedule(
        &mut self,
        eye: glam::Vec3,
        policy: StreamingPolicy,
        catalog: &[(ChunkKey, InstanceBounds)],
    ) -> Result<Vec<ChunkRequest>, ChunkError> {
        if self.closed {
            return Err(ChunkError::Closed);
        }
        if !eye.is_finite() || catalog.len() > 512 {
            return Err(ChunkError::InvalidSchedule);
        }
        let mut ordered = BTreeMap::new();
        for &(key, bounds) in catalog {
            if ordered.insert(key, bounds).is_some() {
                return Err(ChunkError::InvalidSchedule);
            }
        }
        // Validate before evicting, so a malformed catalog cannot unload good data.
        let evicted: Vec<_> = self
            .entries
            .keys()
            .filter(|key| {
                ordered
                    .get(key)
                    .is_none_or(|bounds| !bounds.within_distance(eye, policy.unload))
            })
            .copied()
            .collect();
        for key in evicted {
            self.unload(key);
        }
        let mut candidates: Vec<_> = ordered
            .into_iter()
            .filter(|(key, bounds)| {
                !self.entries.contains_key(key) && bounds.within_distance(eye, policy.load)
            })
            .map(|(key, bounds)| {
                // f64 avoids squaring overflow for otherwise finite world inputs.
                let nearest = eye.clamp(
                    glam::Vec3::from(bounds.min()),
                    glam::Vec3::from(bounds.max()),
                );
                let distance = eye.as_dvec3().distance_squared(nearest.as_dvec3());
                (distance, key)
            })
            .collect();
        candidates.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(&b.1)));
        let request_count = candidates
            .len()
            .min(policy.requests_per_update)
            .min(self.max_chunks - self.entries.len())
            .min(self.max_pending - self.status().pending);
        // Reserve the complete generation range before creating tickets. Otherwise
        // a later failure would discard earlier tickets while leaving them pending.
        self.generation
            .checked_add(request_count as u64)
            .ok_or(ChunkError::GenerationExhausted)?;
        let mut requests = Vec::new();
        for (_, key) in candidates.into_iter().take(request_count) {
            match self.request(key) {
                Ok(request) => requests.push(request),
                Err(ChunkError::Capacity) => break,
                Err(error) => return Err(error),
            }
        }
        Ok(requests)
    }
    pub fn new(max_chunks: usize, max_pending: usize, max_records: usize) -> Option<Self> {
        Self::with_byte_budget(max_chunks, max_pending, max_records, 128 * 1024 * 1024)
    }
    /// Budget applies to registry residency, not provider scratch space or old
    /// snapshots retained by consumers. Byte accounting includes spare record capacity.
    pub fn with_byte_budget(
        max_chunks: usize,
        max_pending: usize,
        max_records: usize,
        max_decoded_bytes: usize,
    ) -> Option<Self> {
        if !(1..=512).contains(&max_chunks)
            || !(1..=32).contains(&max_pending)
            || !(1..=nico_presentation::MAX_BATCH_INSTANCES).contains(&max_records)
            || max_decoded_bytes == 0
        {
            return None;
        }
        Some(Self {
            entries: BTreeMap::new(),
            evicted: 0,
            generation: 0,
            max_chunks,
            max_pending,
            max_records,
            max_decoded_bytes,
            closed: false,
        })
    }
    pub fn request(&mut self, key: ChunkKey) -> Result<ChunkRequest, ChunkError> {
        if self.closed {
            return Err(ChunkError::Closed);
        }
        let replacing = self.entries.get(&key).is_some_and(|e| e.pending.is_some());
        if (!self.entries.contains_key(&key) && self.entries.len() >= self.max_chunks)
            || (!replacing && self.status().pending >= self.max_pending)
        {
            return Err(ChunkError::Capacity);
        }
        let generation = self
            .generation
            .checked_add(1)
            .ok_or(ChunkError::GenerationExhausted)?;
        let entry = self.entries.entry(key).or_insert(Entry {
            pending: None,
            resident: None,
            records: 0,
            decoded_bytes: 0,
            failed: false,
        });
        if let Some(old) = entry.pending.take() {
            old.cancelled.store(true, Ordering::Release);
        }
        let request = ChunkRequest {
            key,
            generation,
            cancelled: Arc::new(AtomicBool::new(false)),
        };
        entry.pending = Some(request.clone());
        entry.failed = false;
        self.generation = generation;
        Ok(request)
    }
    /// Failure or overload retains last-good residency. A cancelled/stale result
    /// cannot alter a newer request or a reused key. Publication swaps all groups.
    pub fn complete(
        &mut self,
        request: &ChunkRequest,
        result: Result<Vec<Arc<InstanceBatch>>, ChunkError>,
    ) -> Result<(), ChunkError> {
        if self.closed {
            return Err(ChunkError::Closed);
        }
        let current = self
            .entries
            .get(&request.key)
            .and_then(|e| e.pending.as_ref());
        if request.cancelled()
            || current.is_none_or(|r| {
                r.generation != request.generation || !Arc::ptr_eq(&r.cancelled, &request.cancelled)
            })
        {
            return Err(ChunkError::Stale);
        }
        let previous_records = self.entries[&request.key].records;
        let status = self.status();
        let other_records = status.records - previous_records;
        let other_bytes = status.decoded_bytes - self.entries[&request.key].decoded_bytes;
        let previous_batches = self.entries[&request.key]
            .resident
            .as_ref()
            .map_or(0, |batches| batches.len());
        let other_batches = status.batches - previous_batches;
        let validated = result.and_then(|batches| {
            let bytes = batches
                .iter()
                .try_fold(0usize, |total, batch| {
                    total
                        .checked_add(batch.decoded_bytes())?
                        .checked_add(std::mem::size_of::<Arc<InstanceBatch>>())
                })
                .ok_or(ChunkError::Capacity)?;
            let count = batches
                .iter()
                .try_fold(0usize, |n, b| n.checked_add(b.records().len()))
                .ok_or(ChunkError::Capacity)?;
            if batches.len() > 512 - other_batches
                || count > self.max_records - other_records
                || bytes > self.max_decoded_bytes - other_bytes
            {
                return Err(ChunkError::Capacity);
            }
            Ok((batches, count, bytes))
        });
        let entry = self.entries.get_mut(&request.key).unwrap();
        entry.pending = None;
        match validated {
            Ok((batches, count, bytes)) => {
                entry.resident = Some(batches.into());
                entry.records = count;
                entry.decoded_bytes = bytes;
                entry.failed = false;
                Ok(())
            }
            Err(error) => {
                entry.failed = true;
                Err(error)
            }
        }
    }
    pub fn resident(&self, key: ChunkKey) -> Option<Arc<[Arc<InstanceBatch>]>> {
        self.entries.get(&key)?.resident.clone()
    }
    pub fn unload(&mut self, key: ChunkKey) {
        if let Some(entry) = self.entries.remove(&key) {
            self.evicted = self.evicted.saturating_add(1);
            if let Some(request) = entry.pending {
                request.cancelled.store(true, Ordering::Release);
            }
        }
    }
    pub fn status(&self) -> ChunkStatus {
        let mut status = ChunkStatus {
            evicted: self.evicted,
            ..Default::default()
        };
        for entry in self.entries.values() {
            status.pending += usize::from(entry.pending.is_some());
            status.resident += usize::from(entry.resident.is_some());
            status.failed += usize::from(entry.failed);
            status.records += entry.records;
            status.decoded_bytes += entry.decoded_bytes;
            status.batches += entry.resident.as_ref().map_or(0, |batches| batches.len());
        }
        status
    }
    pub fn close(&mut self) {
        self.closed = true;
        for entry in self.entries.values() {
            if let Some(request) = &entry.pending {
                request.cancelled.store(true, Ordering::Release);
            }
        }
        self.entries.clear();
    }
}
impl Drop for InstanceChunks {
    fn drop(&mut self) {
        self.close();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    pub(super) fn batch(records: usize) -> Arc<InstanceBatch> {
        let mesh = nico_assets::Mesh::triangles(
            vec![
                nico_assets::MeshVertex {
                    position: [0., 0., 0.],
                    uv: [0.; 2],
                },
                nico_assets::MeshVertex {
                    position: [1., 0., 0.],
                    uv: [0.; 2],
                },
                nico_assets::MeshVertex {
                    position: [0., 1., 0.],
                    uv: [0.; 2],
                },
            ],
            vec![0, 1, 2],
        )
        .unwrap();
        Arc::new(
            InstanceBatch::new(
                Arc::new(mesh),
                Arc::new(nico_assets::PbrMaterial::default()),
                (0..records)
                    .map(|id| {
                        nico_presentation::InstanceRecord::new(
                            id as u64,
                            0,
                            glam::Mat4::IDENTITY,
                            [1.; 4],
                        )
                        .unwrap()
                    })
                    .collect(),
                100.,
            )
            .unwrap(),
        )
    }
    #[test]
    fn aggregate_limits_preserve_last_good_data_and_unload_releases_capacity() {
        let mut chunks = InstanceChunks::new(2, 2, 514).unwrap();
        let other = ChunkKey {
            provider: 2,
            chunk: 0,
        };
        let single = batch(1);
        let first = chunks.request(KEY).unwrap();
        chunks
            .complete(&first, Ok(vec![single.clone(); 511]))
            .unwrap();
        let second = chunks.request(other).unwrap();
        chunks.complete(&second, Ok(vec![single.clone()])).unwrap();
        let good = chunks.resident(other).unwrap();
        assert_eq!(chunks.status().batches, 512);
        // Each chunk is individually within bounds, but their combined groups are not.
        let replacement = chunks.request(other).unwrap();
        assert_eq!(
            chunks.complete(&replacement, Ok(vec![single.clone(); 2])),
            Err(ChunkError::Capacity)
        );
        assert!(Arc::ptr_eq(&good, &chunks.resident(other).unwrap()));
        assert_eq!(chunks.status().records, 512);
        // Replacing a chunk subtracts its old records, without dropping them on failure.
        let replacement = chunks.request(other).unwrap();
        assert_eq!(
            chunks.complete(&replacement, Ok(vec![batch(4)])),
            Err(ChunkError::Capacity)
        );
        assert!(Arc::ptr_eq(&good, &chunks.resident(other).unwrap()));
        let replacement = chunks.request(other).unwrap();
        chunks.complete(&replacement, Ok(vec![batch(3)])).unwrap();
        assert_eq!(chunks.status().records, 514);
        chunks.unload(KEY);
        let replacement = chunks.request(other).unwrap();
        chunks
            .complete(&replacement, Ok(vec![single; 512]))
            .unwrap();
        assert_eq!(chunks.status().records, 512);
        assert_eq!(chunks.status().batches, 512);
        assert_eq!(good[0].records().len(), 1);
    }
    const KEY: ChunkKey = ChunkKey {
        provider: 1,
        chunk: 2,
    };
    #[test]
    fn eviction_counts_tracked_unloads_without_counting_replacement_or_close() {
        let mut chunks = InstanceChunks::new(2, 2, 8).unwrap();
        let pending = chunks.request(KEY).unwrap();
        chunks.unload(KEY);
        assert!(pending.cancelled());
        chunks.unload(KEY);
        assert_eq!(chunks.status().evicted, 1);
        let replacement = chunks.request(KEY).unwrap();
        chunks.complete(&replacement, Ok(vec![batch(1)])).unwrap();
        let replacement = chunks.request(KEY).unwrap();
        chunks.complete(&replacement, Ok(vec![batch(2)])).unwrap();
        assert_eq!(chunks.status().evicted, 1);
        chunks.unload(KEY);
        assert_eq!(chunks.status().evicted, 2);
        let _pending = chunks.request(KEY).unwrap();
        chunks.close();
        assert_eq!(chunks.status().evicted, 2);
        assert_eq!(chunks.status().pending, 0);
    }
    #[test]
    fn generation_exhaustion_cannot_leave_unreturned_pending_tickets() {
        let mut chunks = InstanceChunks::new(4, 4, 10).unwrap();
        chunks.generation = u64::MAX - 1;
        let bounds = InstanceBounds::new([0.; 3], [1.; 3]).unwrap();
        let other = ChunkKey {
            provider: 1,
            chunk: 3,
        };
        let policy = StreamingPolicy::new(2., 4., 2).unwrap();
        assert_eq!(
            chunks
                .schedule(glam::Vec3::ZERO, policy, &[(KEY, bounds), (other, bounds)])
                .unwrap_err(),
            ChunkError::GenerationExhausted
        );
        assert_eq!(chunks.status().pending, 0);
        assert_eq!(chunks.generation, u64::MAX - 1);
        let requests = chunks
            .schedule(glam::Vec3::ZERO, policy, &[(KEY, bounds)])
            .unwrap();
        assert_eq!(requests.len(), 1);
        assert_eq!(requests[0].generation(), u64::MAX);
        chunks.complete(&requests[0], Ok(Vec::new())).unwrap();
        assert!(
            chunks
                .schedule(glam::Vec3::ZERO, policy, &[(KEY, bounds)])
                .unwrap()
                .is_empty()
        );
    }
    #[test]
    fn decoded_budget_rejects_replacement_atomically_and_recovers_after_unload() {
        let single = batch(1);
        let charge = single.decoded_bytes() + std::mem::size_of::<Arc<InstanceBatch>>();
        let mut chunks = InstanceChunks::with_byte_budget(2, 2, 100, charge * 2).unwrap();
        let other = ChunkKey {
            provider: 9,
            chunk: 0,
        };
        for key in [KEY, other] {
            let request = chunks.request(key).unwrap();
            chunks.complete(&request, Ok(vec![single.clone()])).unwrap();
        }
        assert_eq!(chunks.status().decoded_bytes, charge * 2);
        let retained = chunks.resident(KEY).unwrap();
        let request = chunks.request(KEY).unwrap();
        assert_eq!(
            chunks.complete(&request, Ok(vec![single.clone(); 2])),
            Err(ChunkError::Capacity)
        );
        assert!(Arc::ptr_eq(&retained, &chunks.resident(KEY).unwrap()));
        assert_eq!(chunks.status().decoded_bytes, charge * 2);
        chunks.unload(other);
        assert_eq!(chunks.status().decoded_bytes, charge);
        let request = chunks.request(KEY).unwrap();
        chunks.complete(&request, Ok(vec![single; 2])).unwrap();
        assert_eq!(chunks.status().decoded_bytes, charge * 2);
        chunks.close();
        assert_eq!(chunks.status().decoded_bytes, 0);
        assert_eq!(retained[0].records().len(), 1);
    }
    #[test]
    fn distance_hysteresis_retains_pending_and_resident_chunks_until_unload_boundary() {
        let mut chunks = InstanceChunks::new(4, 2, 10).unwrap();
        let policy = StreamingPolicy::new(2., 4., 1).unwrap();
        let catalog = [(KEY, InstanceBounds::new([0.; 3], [0.; 3]).unwrap())];
        let first = chunks
            .schedule(glam::Vec3::X * 2., policy, &catalog)
            .unwrap()
            .pop()
            .unwrap();
        assert!(
            chunks
                .schedule(glam::Vec3::X * 3., policy, &catalog)
                .unwrap()
                .is_empty()
        );
        assert!(!first.cancelled());
        chunks.complete(&first, Ok(Vec::new())).unwrap();
        let retained = chunks.resident(KEY).unwrap();
        chunks
            .schedule(glam::Vec3::X * 4., policy, &catalog)
            .unwrap();
        assert!(Arc::ptr_eq(&retained, &chunks.resident(KEY).unwrap()));
        chunks
            .schedule(glam::Vec3::X * 4.1, policy, &catalog)
            .unwrap();
        assert!(chunks.resident(KEY).is_none());
        let reentry = chunks
            .schedule(glam::Vec3::ZERO, policy, &catalog)
            .unwrap()
            .pop()
            .unwrap();
        assert!(reentry.generation() > first.generation());
        chunks.schedule(glam::Vec3::ZERO, policy, &[]).unwrap();
        assert!(reentry.cancelled());
    }
    #[test]
    fn requests_are_nearest_first_bounded_and_bad_catalogs_preserve_pending_work() {
        let mut chunks = InstanceChunks::new(4, 2, 10).unwrap();
        let policy = StreamingPolicy::new(10., 12., 1).unwrap();
        let far = ChunkKey {
            provider: 1,
            chunk: 3,
        };
        let point = |x| InstanceBounds::new([x, 0., 0.], [x, 0., 0.]).unwrap();
        let catalog = [(far, point(5.)), (KEY, point(1.))];
        let requests = chunks.schedule(glam::Vec3::ZERO, policy, &catalog).unwrap();
        assert_eq!(requests.len(), 1);
        assert_eq!(requests[0].key(), KEY);
        assert_eq!(
            chunks
                .schedule(glam::Vec3::ZERO, policy, &[catalog[0], catalog[0]])
                .unwrap_err(),
            ChunkError::InvalidSchedule
        );
        assert!(!requests[0].cancelled());
        assert_eq!(
            chunks.schedule(glam::Vec3::ZERO, policy, &catalog).unwrap()[0].key(),
            far
        );
        assert!(
            chunks
                .schedule(glam::Vec3::ZERO, policy, &catalog)
                .unwrap()
                .is_empty()
        );
    }
    #[test]
    fn replacement_cancellation_and_reused_keys_reject_stale_results() {
        let mut chunks = InstanceChunks::new(2, 1, 10).unwrap();
        let first = chunks.request(KEY).unwrap();
        let mut other = InstanceChunks::new(2, 1, 10).unwrap();
        let foreign = other.request(KEY).unwrap();
        assert_eq!(
            chunks.complete(&foreign, Ok(Vec::new())),
            Err(ChunkError::Stale)
        );
        chunks.complete(&first, Ok(Vec::new())).unwrap();
        let good = chunks.resident(KEY).unwrap();
        let second = chunks.request(KEY).unwrap();
        let third = chunks.request(KEY).unwrap();
        assert!(second.cancelled());
        assert_eq!(
            chunks.complete(&second, Ok(Vec::new())),
            Err(ChunkError::Stale)
        );
        assert_eq!(
            chunks.complete(&third, Err(ChunkError::ProviderFailed)),
            Err(ChunkError::ProviderFailed)
        );
        assert!(Arc::ptr_eq(&good, &chunks.resident(KEY).unwrap()));
        let pending = chunks.request(KEY).unwrap();
        chunks.unload(KEY);
        assert!(pending.cancelled());
        let reused = chunks.request(KEY).unwrap();
        assert!(reused.generation() > pending.generation());
        assert_eq!(
            chunks.complete(&pending, Ok(Vec::new())),
            Err(ChunkError::Stale)
        );
        chunks.complete(&reused, Ok(Vec::new())).unwrap();
        assert!(!Arc::ptr_eq(&good, &chunks.resident(KEY).unwrap()));
    }
    #[test]
    fn pending_limits_and_shutdown_cancel_work_without_invalidating_snapshots() {
        let mut chunks = InstanceChunks::new(2, 1, 10).unwrap();
        let first = chunks.request(KEY).unwrap();
        assert_eq!(
            chunks
                .request(ChunkKey {
                    provider: 1,
                    chunk: 3
                })
                .unwrap_err(),
            ChunkError::Capacity
        );
        chunks.complete(&first, Ok(Vec::new())).unwrap();
        let snapshot = chunks.resident(KEY).unwrap();
        let pending = chunks.request(KEY).unwrap();
        chunks.close();
        assert!(pending.cancelled());
        assert!(snapshot.is_empty());
        assert_eq!(chunks.status(), ChunkStatus::default());
        assert_eq!(
            chunks.complete(&pending, Ok(Vec::new())),
            Err(ChunkError::Closed)
        );
        assert_eq!(chunks.request(KEY).unwrap_err(), ChunkError::Closed);
    }
}
