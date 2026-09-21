//! Bounded provider execution. Only the presentation owner publishes results.
use super::{ChunkError, ChunkRequest};
use nico_presentation::InstanceBatch;
use std::{sync::Arc, thread::JoinHandle};
pub type ChunkResult = Result<Vec<Arc<InstanceBatch>>, ChunkError>;
/// Providers check cancellation between bounded units of work. Arbitrary provider
/// code cannot be forcibly terminated; shutdown completion must be polled.
pub trait InstanceProvider: Send + Sync + 'static {
    /// Providers must bound their allocations before decoding. The executor also
    /// validates returned payloads, but cannot limit arbitrary provider scratch memory.
    fn load(&self, request: &ChunkRequest, limits: ResultLimits) -> ChunkResult;
}
/// Per-job result allowance, reserved for the entire job including collection delay.
#[derive(Clone, Copy, Debug)]
pub struct ResultLimits {
    pub decoded_bytes: usize,
}
impl ResultLimits {
    fn validate(self, batches: &[Arc<InstanceBatch>], capacity: usize) -> Result<(), ChunkError> {
        if batches.len() > 512 {
            return Err(ChunkError::Capacity);
        }
        let mut bytes = capacity
            .checked_mul(std::mem::size_of::<Arc<InstanceBatch>>())
            .ok_or(ChunkError::Capacity)?;
        let mut records = 0usize;
        for batch in batches {
            bytes = bytes
                .checked_add(batch.decoded_bytes())
                .ok_or(ChunkError::Capacity)?;
            records = records
                .checked_add(batch.records().len())
                .ok_or(ChunkError::Capacity)?;
        }
        if bytes > self.decoded_bytes || records > nico_presentation::MAX_BATCH_INSTANCES {
            return Err(ChunkError::Capacity);
        }
        Ok(())
    }
}
struct Worker {
    request: ChunkRequest,
    handle: JoinHandle<ChunkResult>,
}
/// Bounds running jobs and finished/uncollected results together. No hidden queue:
/// rejected tickets remain the caller's responsibility to retry or fail.
pub struct ChunkWorkers {
    workers: Vec<Worker>,
    limit: usize,
    result_limits: ResultLimits,
    closed: bool,
}
impl ChunkWorkers {
    pub fn new(limit: usize) -> Option<Self> {
        Self::with_byte_budget(limit, 128 * 1024 * 1024)
    }
    /// Splits the total result budget evenly among worker slots. Reservations are
    /// released only by collection, so finished/uncollected results remain bounded.
    /// Excludes provider scratch, shared assets, allocation metadata and collected results.
    pub fn with_byte_budget(limit: usize, decoded_bytes: usize) -> Option<Self> {
        if !(1..=8).contains(&limit) || decoded_bytes < limit {
            return None;
        }
        Some(Self {
            workers: Vec::new(),
            limit,
            result_limits: ResultLimits {
                decoded_bytes: decoded_bytes / limit,
            },
            closed: false,
        })
    }
    pub fn reserved_result_bytes(&self) -> usize {
        self.workers.len() * self.result_limits.decoded_bytes
    }
    pub fn outstanding(&self) -> usize {
        self.workers.len()
    }
    pub fn available(&self) -> usize {
        if self.closed {
            0
        } else {
            self.limit - self.workers.len()
        }
    }
    pub fn submit(
        &mut self,
        request: ChunkRequest,
        provider: Arc<dyn InstanceProvider>,
    ) -> Result<(), ChunkError> {
        if self.closed {
            return Err(ChunkError::Closed);
        }
        if request.cancelled() {
            return Err(ChunkError::Stale);
        }
        if self.workers.len() >= self.limit {
            return Err(ChunkError::Capacity);
        }
        if self.workers.iter().any(|w| w.request.same_ticket(&request)) {
            return Err(ChunkError::Capacity);
        }
        let ticket = request.clone();
        let limits = self.result_limits;
        let handle = std::thread::Builder::new()
            .name("nico-instance-provider".into())
            .spawn(move || {
                if ticket.cancelled() {
                    return Err(ChunkError::Stale);
                }
                let result = provider.load(&ticket, limits).and_then(|batches| {
                    limits.validate(&batches, batches.capacity())?;
                    Ok(batches)
                });
                if ticket.cancelled() {
                    Err(ChunkError::Stale)
                } else {
                    result
                }
            })
            .map_err(|_| ChunkError::ProviderFailed)?;
        self.workers.push(Worker { request, handle });
        Ok(())
    }
    /// Only joins finished threads; panic is reported as provider failure.
    pub fn poll(&mut self) -> Vec<(ChunkRequest, ChunkResult)> {
        let mut ready = Vec::new();
        let mut index = 0;
        while index < self.workers.len() {
            if self.workers[index].handle.is_finished() {
                let worker = self.workers.remove(index);
                let result = worker
                    .handle
                    .join()
                    .unwrap_or(Err(ChunkError::ProviderFailed));
                let result = if worker.request.cancelled() {
                    Err(ChunkError::Stale)
                } else {
                    result
                };
                ready.push((worker.request, result));
            } else {
                index += 1;
            }
        }
        ready
    }
    /// False means workers still exist. Keep this owner and poll until true to
    /// establish shutdown completion. Cancellation alone is not thread exit.
    pub fn shutdown(&mut self) -> bool {
        self.closed = true;
        for worker in &self.workers {
            worker.request.cancel();
        }
        drop(self.poll());
        self.workers.is_empty()
    }
    /// Cancel and join every owned thread, discarding results. This can block
    /// indefinitely for an uncooperative provider. Use only at lifecycle boundaries
    /// when providers are known to finish bounded work and never await this owner.
    /// General interactive hosts should prefer nonblocking `shutdown` polling.
    pub fn shutdown_and_join(&mut self) {
        self.closed = true;
        for worker in &self.workers {
            worker.request.cancel();
        }
        for worker in self.workers.drain(..) {
            let _ = worker.handle.join();
        }
    }
}
impl Drop for ChunkWorkers {
    fn drop(&mut self) {
        // Never block destruction on a broken provider. A live handle may detach;
        // callers needing verified shutdown must poll shutdown before destruction.
        self.shutdown();
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use crate::instances::{ChunkKey, InstanceChunks};
    use std::{
        sync::{Mutex, mpsc},
        time::{Duration, Instant},
    };
    struct Gated {
        started: mpsc::Sender<()>,
        release: Mutex<mpsc::Receiver<()>>,
    }
    impl InstanceProvider for Gated {
        fn load(&self, _: &ChunkRequest, _: ResultLimits) -> ChunkResult {
            self.started.send(()).unwrap();
            self.release
                .lock()
                .unwrap()
                .recv_timeout(Duration::from_secs(5))
                .unwrap();
            Ok(Vec::new())
        }
    }
    #[test]
    fn joined_shutdown_waits_for_exit_and_releases_reservations() {
        let (started_tx, started) = mpsc::channel();
        let (release, release_rx) = mpsc::channel();
        let provider = Arc::new(Gated {
            started: started_tx,
            release: Mutex::new(release_rx),
        });
        let mut chunks = InstanceChunks::new(1, 1, 10).unwrap();
        let ticket = chunks
            .request(ChunkKey {
                provider: 0,
                chunk: 0,
            })
            .unwrap();
        let mut workers = ChunkWorkers::new(1).unwrap();
        workers.submit(ticket.clone(), provider).unwrap();
        started.recv_timeout(Duration::from_secs(5)).unwrap();
        let joiner = std::thread::spawn(move || {
            workers.shutdown_and_join();
            assert_eq!(workers.outstanding(), 0);
            assert_eq!(workers.reserved_result_bytes(), 0);
            assert_eq!(workers.available(), 0);
            assert!(workers.poll().is_empty());
            workers.shutdown_and_join();
        });
        let deadline = Instant::now() + Duration::from_secs(5);
        while !ticket.cancelled() {
            assert!(Instant::now() < deadline);
            std::thread::yield_now();
        }
        assert!(!joiner.is_finished());
        release.send(()).unwrap();
        joiner.join().unwrap();
    }
    #[test]
    fn cancelled_work_retains_capacity_until_thread_exit() {
        let (started_tx, started) = mpsc::channel();
        let (release, release_rx) = mpsc::channel();
        let provider = Arc::new(Gated {
            started: started_tx,
            release: Mutex::new(release_rx),
        });
        let mut chunks = InstanceChunks::new(2, 2, 10).unwrap();
        let ticket = chunks
            .request(ChunkKey {
                provider: 0,
                chunk: 1,
            })
            .unwrap();
        let other = chunks
            .request(ChunkKey {
                provider: 0,
                chunk: 2,
            })
            .unwrap();
        let mut workers = ChunkWorkers::new(1).unwrap();
        workers.submit(ticket.clone(), provider.clone()).unwrap();
        started.recv_timeout(Duration::from_secs(5)).unwrap();
        assert_eq!(workers.submit(other, provider), Err(ChunkError::Capacity));
        assert!(workers.poll().is_empty());
        assert!(!workers.shutdown());
        assert!(ticket.cancelled());
        assert_eq!(workers.outstanding(), 1);
        release.send(()).unwrap();
        let deadline = Instant::now() + Duration::from_secs(5);
        while !workers.shutdown() {
            assert!(Instant::now() < deadline);
            std::thread::yield_now();
        }
        assert_eq!(workers.outstanding(), 0);
    }
    struct Immediate;
    #[test]
    fn result_limits_charge_batch_payload_and_vector_capacity() {
        let batch = crate::instances::tests::batch(3);
        let mut batches = Vec::with_capacity(4);
        batches.push(batch.clone());
        let bytes =
            batch.decoded_bytes() + batches.capacity() * std::mem::size_of::<Arc<InstanceBatch>>();
        assert_eq!(
            ResultLimits {
                decoded_bytes: bytes
            }
            .validate(&batches, batches.capacity()),
            Ok(())
        );
        assert_eq!(
            ResultLimits {
                decoded_bytes: bytes - 1
            }
            .validate(&batches, batches.capacity()),
            Err(ChunkError::Capacity)
        );
        // Shared references consume separate submission groups even with ample bytes.
        let too_many = vec![batch; 513];
        assert_eq!(
            ResultLimits {
                decoded_bytes: usize::MAX
            }
            .validate(&too_many, too_many.capacity()),
            Err(ChunkError::Capacity)
        );
    }
    struct Oversized;
    impl InstanceProvider for Oversized {
        fn load(&self, _: &ChunkRequest, limits: ResultLimits) -> ChunkResult {
            // Even an empty result can retain an oversized allocation.
            Ok(Vec::with_capacity(limits.decoded_bytes + 1))
        }
    }
    #[test]
    fn result_reservations_cover_completed_jobs_and_oversized_payloads_fail() {
        assert!(ChunkWorkers::with_byte_budget(0, 100).is_none());
        assert!(ChunkWorkers::with_byte_budget(2, 1).is_none());
        let mut chunks = InstanceChunks::new(2, 2, 10).unwrap();
        let mut workers = ChunkWorkers::with_byte_budget(2, 128).unwrap();
        let first = chunks
            .request(ChunkKey {
                provider: 0,
                chunk: 0,
            })
            .unwrap();
        let second = chunks
            .request(ChunkKey {
                provider: 0,
                chunk: 1,
            })
            .unwrap();
        workers.submit(first.clone(), Arc::new(Oversized)).unwrap();
        workers.submit(second, Arc::new(Immediate)).unwrap();
        let deadline = Instant::now() + Duration::from_secs(5);
        while workers.workers.iter().any(|w| !w.handle.is_finished()) {
            assert!(Instant::now() < deadline);
            std::thread::yield_now();
        }
        assert_eq!(workers.reserved_result_bytes(), 128);
        let results = workers.poll();
        assert_eq!(results.len(), 2);
        for (ticket, result) in results {
            if ticket.same_ticket(&first) {
                assert!(matches!(result, Err(ChunkError::Capacity)));
                assert_eq!(chunks.complete(&ticket, result), Err(ChunkError::Capacity));
            } else {
                chunks.complete(&ticket, result).unwrap();
            }
        }
        assert_eq!(workers.reserved_result_bytes(), 0);
        assert_eq!(chunks.status().failed, 1);
        assert_eq!(chunks.status().resident, 1);
        assert!(workers.shutdown());
    }
    #[test]
    fn distinct_owners_do_not_alias_and_cancellation_after_finish_discards_result() {
        let mut a = InstanceChunks::new(1, 1, 10).unwrap();
        let mut b = InstanceChunks::new(1, 1, 10).unwrap();
        let key = ChunkKey {
            provider: 0,
            chunk: 1,
        };
        let first = a.request(key).unwrap();
        let second = b.request(key).unwrap();
        assert_eq!(first.generation(), second.generation());
        let mut workers = ChunkWorkers::new(2).unwrap();
        workers.submit(first.clone(), Arc::new(Immediate)).unwrap();
        workers.submit(second.clone(), Arc::new(Immediate)).unwrap();
        let deadline = Instant::now() + Duration::from_secs(5);
        while workers.workers.iter().any(|w| !w.handle.is_finished()) {
            assert!(Instant::now() < deadline);
            std::thread::yield_now();
        }
        a.unload(key);
        let results = workers.poll();
        assert_eq!(results.len(), 2);
        for (ticket, result) in results {
            if ticket.same_ticket(&first) {
                assert!(matches!(result, Err(ChunkError::Stale)));
            } else {
                assert!(ticket.same_ticket(&second));
                b.complete(&ticket, result).unwrap();
            }
        }
        assert!(b.resident(key).is_some());
        assert!(workers.shutdown());
    }
    impl InstanceProvider for Immediate {
        fn load(&self, _: &ChunkRequest, _: ResultLimits) -> ChunkResult {
            Ok(Vec::new())
        }
    }
    #[test]
    fn results_publish_only_when_collected_by_owner() {
        let mut chunks = InstanceChunks::new(1, 1, 10).unwrap();
        let key = ChunkKey {
            provider: 0,
            chunk: 1,
        };
        let ticket = chunks.request(key).unwrap();
        let mut workers = ChunkWorkers::new(1).unwrap();
        workers.submit(ticket, Arc::new(Immediate)).unwrap();
        assert!(chunks.resident(key).is_none());
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            if let Some((ticket, result)) = workers.poll().pop() {
                chunks.complete(&ticket, result).unwrap();
                break;
            }
            assert!(Instant::now() < deadline);
            std::thread::yield_now();
        }
        assert!(chunks.resident(key).is_some());
        assert!(workers.shutdown());
    }
}
