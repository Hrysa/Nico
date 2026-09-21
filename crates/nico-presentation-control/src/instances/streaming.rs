//! Presentation-owner composition of scheduling, provider execution and publication.
use super::{
    ChunkError, ChunkKey, ChunkStatus, InstanceChunks, StreamingPolicy,
    workers::{ChunkWorkers, InstanceProvider},
};
use nico_presentation::{InstanceBatch, InstanceBounds};
use std::{collections::BTreeMap, sync::Arc};

pub struct InstanceStreaming {
    chunks: InstanceChunks,
    workers: ChunkWorkers,
    providers: BTreeMap<u32, Arc<dyn InstanceProvider>>,
    closed: bool,
}
#[derive(Debug)]
pub struct ChunkOutcome {
    pub key: ChunkKey,
    pub result: Result<(), ChunkError>,
}
impl InstanceStreaming {
    /// Provider identities remain fixed for this owner's lifetime. Replace the owner
    /// when source content changes, cancelling outstanding work on the old owner.
    pub fn new(
        max_chunks: usize,
        max_records: usize,
        worker_count: usize,
        resident_bytes: usize,
        result_bytes: usize,
        providers: Vec<(u32, Arc<dyn InstanceProvider>)>,
    ) -> Option<Self> {
        if providers.len() > 32 {
            return None;
        }
        let mut registered = BTreeMap::new();
        for (id, provider) in providers {
            if registered.insert(id, provider).is_some() {
                return None;
            }
        }
        Some(Self {
            chunks: InstanceChunks::with_byte_budget(
                max_chunks,
                worker_count,
                max_records,
                resident_bytes,
            )?,
            workers: ChunkWorkers::with_byte_budget(worker_count, result_bytes)?,
            providers: registered,
            closed: false,
        })
    }
    /// Nonblocking owner update. Returns publication outcomes, including rejected
    /// stale results. A complete catalog is required even when all workers are busy:
    /// evictions still cancel pending work, but no unserviceable tickets are created.
    pub fn update(
        &mut self,
        eye: glam::Vec3,
        mut policy: StreamingPolicy,
        catalog: &[(ChunkKey, InstanceBounds)],
    ) -> Result<Vec<ChunkOutcome>, ChunkError> {
        if self.closed {
            return Err(ChunkError::Closed);
        }
        if !eye.is_finite() || catalog.len() > 512 {
            return Err(ChunkError::InvalidSchedule);
        }
        let mut keys = std::collections::BTreeSet::new();
        for (key, _) in catalog {
            if !self.providers.contains_key(&key.provider) || !keys.insert(*key) {
                return Err(ChunkError::InvalidSchedule);
            }
        }
        let mut outcomes = Vec::new();
        // Reconcile before publishing so an evicted completion cannot become resident.
        policy.requests_per_update = policy.requests_per_update.min(self.workers.available());
        let requests = self.chunks.schedule(eye, policy, catalog)?;
        for request in requests {
            if let Err(error) = self.workers.submit(
                request.clone(),
                self.providers[&request.key().provider].clone(),
            ) {
                outcomes.push(ChunkOutcome {
                    key: request.key(),
                    result: self.chunks.complete(&request, Err(error)),
                });
            }
        }
        for (request, result) in self.workers.poll() {
            outcomes.push(ChunkOutcome {
                key: request.key(),
                result: self.chunks.complete(&request, result),
            });
        }
        Ok(outcomes)
    }
    /// Stable chunk-key order; clones only immutable batch references.
    pub fn batches(&self) -> Vec<Arc<InstanceBatch>> {
        self.chunks
            .entries
            .values()
            .filter_map(|entry| entry.resident.as_ref())
            .flat_map(|batches| batches.iter().cloned())
            .collect()
    }
    pub fn status(&self) -> ChunkStatus {
        self.chunks.status()
    }
    pub fn outstanding(&self) -> usize {
        self.workers.outstanding()
    }
    pub fn reserved_result_bytes(&self) -> usize {
        self.workers.reserved_result_bytes()
    }
    /// Keep polling until true before dropping an owner requiring verified exit.
    pub fn shutdown(&mut self) -> bool {
        self.closed = true;
        self.chunks.close();
        self.workers.shutdown()
    }
    /// Blocking lifecycle completion for bounded cooperative providers only.
    /// See `ChunkWorkers::shutdown_and_join` for the provider requirements.
    pub fn shutdown_and_join(&mut self) {
        self.closed = true;
        self.chunks.close();
        self.workers.shutdown_and_join();
    }
}
impl Drop for InstanceStreaming {
    fn drop(&mut self) {
        self.shutdown();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::instances::{
        ChunkRequest,
        workers::{ChunkResult, ResultLimits},
    };
    use std::{
        sync::{Mutex, mpsc},
        time::{Duration, Instant},
    };
    struct Gated {
        started: mpsc::Sender<ChunkKey>,
        release: Mutex<mpsc::Receiver<()>>,
    }
    impl InstanceProvider for Gated {
        fn load(&self, request: &ChunkRequest, _: ResultLimits) -> ChunkResult {
            self.started.send(request.key()).unwrap();
            self.release
                .lock()
                .unwrap()
                .recv_timeout(Duration::from_secs(5))
                .unwrap();
            Ok(vec![crate::instances::tests::batch(1)])
        }
    }
    #[test]
    fn busy_workers_do_not_strand_requests_and_evicted_results_do_not_publish() {
        let (started_tx, started) = mpsc::channel();
        let (release, release_rx) = mpsc::channel();
        let provider = Arc::new(Gated {
            started: started_tx,
            release: Mutex::new(release_rx),
        });
        let mut stream =
            InstanceStreaming::new(2, 10, 1, 10000, 10000, vec![(0, provider)]).unwrap();
        let first = ChunkKey {
            provider: 0,
            chunk: 0,
        };
        let second = ChunkKey {
            provider: 0,
            chunk: 1,
        };
        let bounds = InstanceBounds::new([0.; 3], [1.; 3]).unwrap();
        let policy = StreamingPolicy::new(5., 10., 2).unwrap();
        stream
            .update(glam::Vec3::ZERO, policy, &[(first, bounds)])
            .unwrap();
        assert_eq!(started.recv_timeout(Duration::from_secs(5)).unwrap(), first);
        // Missing provider is rejected before evicting the live request.
        assert!(matches!(
            stream.update(
                glam::Vec3::ZERO,
                policy,
                &[(
                    ChunkKey {
                        provider: 1,
                        chunk: 0
                    },
                    bounds
                )]
            ),
            Err(ChunkError::InvalidSchedule)
        ));
        assert_eq!(stream.status().pending, 1);
        let catalog = [(second, bounds)];
        stream.update(glam::Vec3::ZERO, policy, &catalog).unwrap();
        assert_eq!(stream.outstanding(), 1);
        assert_eq!(stream.status().pending, 0);
        release.send(()).unwrap();
        let deadline = Instant::now() + Duration::from_secs(5);
        while stream.outstanding() != 0 {
            for outcome in stream.update(glam::Vec3::ZERO, policy, &catalog).unwrap() {
                assert_eq!(outcome.key, first);
                assert_eq!(outcome.result, Err(ChunkError::Stale));
            }
            assert!(Instant::now() < deadline);
            std::thread::yield_now();
        }
        assert!(stream.batches().is_empty());
        stream.update(glam::Vec3::ZERO, policy, &catalog).unwrap();
        assert_eq!(
            started.recv_timeout(Duration::from_secs(5)).unwrap(),
            second
        );
        release.send(()).unwrap();
        while stream.status().resident == 0 {
            stream.update(glam::Vec3::ZERO, policy, &catalog).unwrap();
            assert!(Instant::now() < deadline);
            std::thread::yield_now();
        }
        let retained = stream.batches();
        assert_eq!(retained.len(), 1);
        assert_eq!(stream.reserved_result_bytes(), 0);
        assert!(stream.shutdown());
        assert!(stream.batches().is_empty());
        assert_eq!(retained[0].records().len(), 1);
        assert!(matches!(
            stream.update(glam::Vec3::ZERO, policy, &catalog),
            Err(ChunkError::Closed)
        ));
    }
}
