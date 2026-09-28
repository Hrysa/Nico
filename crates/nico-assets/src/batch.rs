//! Bounded CPU loading jobs. Workers never access a runtime world or GPU.
use crate::cache::{CacheStats, observe_current_thread, record_activity};
use std::{
    collections::VecDeque,
    io,
    panic::{AssertUnwindSafe, catch_unwind},
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread::{self, JoinHandle},
};

pub type Error = Box<dyn std::error::Error + Send + Sync>;
type Completion<T> = (usize, Result<T, Error>, CacheStats);

/// Owns up to four workers and at most 1024 jobs, including completed results.
/// Poll on the owning thread, or wait during startup. Results carry their input index.
/// Drop cancels pending work and joins workers. Running jobs must check cancellation.
pub struct Batch<T> {
    receiver: mpsc::Receiver<Completion<T>>,
    workers: Vec<JoinHandle<()>>,
    cancelled: Arc<AtomicBool>,
    remaining: usize,
}

impl<T: Send + 'static> Batch<T> {
    pub fn start<I: Send + 'static>(
        inputs: Vec<I>,
        work: impl Fn(I, &Arc<AtomicBool>) -> Result<T, Error> + Send + Sync + 'static,
    ) -> io::Result<Self> {
        let workers = thread::available_parallelism()
            .map_or(1, usize::from)
            .min(4);
        Self::with_workers(inputs, workers, work)
    }

    fn with_workers<I: Send + 'static>(
        inputs: Vec<I>,
        workers: usize,
        work: impl Fn(I, &Arc<AtomicBool>) -> Result<T, Error> + Send + Sync + 'static,
    ) -> io::Result<Self> {
        if inputs.len() > 1024 || workers == 0 || workers > 4 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "invalid loading batch size",
            ));
        }
        let remaining = inputs.len();
        let jobs = Arc::new(Mutex::new(
            inputs.into_iter().enumerate().collect::<VecDeque<_>>(),
        ));
        let work = Arc::new(work);
        let (sender, receiver) = mpsc::channel();
        let mut batch = Self {
            receiver,
            workers: Vec::new(),
            cancelled: Arc::new(AtomicBool::new(false)),
            remaining,
        };
        for index in 0..workers.min(remaining) {
            let jobs = jobs.clone();
            let work = work.clone();
            let sender = sender.clone();
            let cancelled = batch.cancelled.clone();
            batch.workers.push(
                thread::Builder::new()
                    .name(format!("asset-load-{index}"))
                    .spawn(move || {
                        loop {
                            if cancelled.load(Ordering::Acquire) {
                                break;
                            }
                            let Some((index, input)) = jobs.lock().unwrap().pop_front() else {
                                break;
                            };
                            let activity = observe_current_thread();
                            let before = activity.stats();
                            let result = catch_unwind(AssertUnwindSafe(|| work(input, &cancelled)))
                                .unwrap_or_else(|_| {
                                    Err(io::Error::other("asset worker panicked").into())
                                });
                            let failed = result.is_err();
                            if sender
                                .send((index, result, activity.stats().since(before)))
                                .is_err()
                            {
                                break;
                            }
                            if failed {
                                cancelled.store(true, Ordering::Release);
                                break;
                            }
                        }
                    })?,
            );
        }
        Ok(batch)
    }

    /// Returns immediately. `None` means no result is ready, or all results were consumed.
    pub fn try_next(&mut self) -> Option<Result<(usize, T), Error>> {
        match self.receiver.try_recv() {
            Ok(result) => Some(self.accept(result)),
            Err(mpsc::TryRecvError::Empty) => None,
            Err(mpsc::TryRecvError::Disconnected) if self.remaining != 0 => {
                self.remaining = 0;
                Some(Err(io::Error::other("asset batch cancelled").into()))
            }
            Err(_) => None,
        }
    }

    pub fn is_finished(&self) -> bool {
        self.remaining == 0
    }

    pub fn cancel(&self) {
        self.cancelled.store(true, Ordering::Release);
    }

    fn accept(&mut self, (index, result, stats): Completion<T>) -> Result<(usize, T), Error> {
        self.remaining -= 1;
        record_activity(stats);
        result.map(|value| (index, value))
    }

    /// Wait for remaining jobs and return input order, regardless of completion order.
    /// Completion callbacks run on this thread, so UI observers stay on their owner.
    pub fn wait(mut self, mut completed: impl FnMut()) -> Result<Vec<T>, Error> {
        let mut values = Vec::with_capacity(self.remaining);
        while self.remaining != 0 {
            let result = self
                .receiver
                .recv()
                .map_err(|_| io::Error::other("asset batch cancelled"))?;
            let (index, value) = self.accept(result)?;
            values.push((index, value));
            completed();
        }
        values.sort_by_key(|(index, _)| *index);
        Ok(values.into_iter().map(|(_, value)| value).collect())
    }
}

impl<T> Drop for Batch<T> {
    fn drop(&mut self) {
        self.cancelled.store(true, Ordering::Release);
        for worker in self.workers.drain(..) {
            let _ = worker.join();
        }
    }
}

/// Load model files concurrently while reporting completed CPU loads.
#[cfg(feature = "gltf-import")]
pub fn models(
    paths: Vec<std::path::PathBuf>,
    label: impl Into<String>,
    settings: crate::importers::ModelGlbSettings,
    budget: crate::import::ImportBudget,
) -> Result<Vec<Arc<crate::model::Model>>, Error> {
    let progress = crate::progress::ImportProgress::new(label, paths.len());
    let batch = Batch::start(paths, move |path, cancelled| {
        Ok(Arc::new(crate::cache::load_file(
            &path,
            &crate::importers::ModelGlbImporter,
            &settings,
            budget,
            &|| cancelled.load(Ordering::Acquire),
        )?))
    })?;
    let result = batch.wait(|| progress.complete_one())?;
    progress.finish();
    Ok(result)
}

/// Load referenced PNG textures concurrently and share identical images across models.
/// Reserve RGBA bytes before starting workers. Each decoder also enforces its own limit.
#[cfg(all(feature = "png-import", feature = "gltf-import"))]
pub fn textures(
    sources: Vec<(std::path::PathBuf, Arc<crate::model::Model>)>,
    label: impl Into<String>,
    budget: crate::import::ImportBudget,
) -> Result<Vec<Vec<Option<Arc<crate::Texture>>>>, Error> {
    use crate::{
        importers::{PngImporter, PngSettings},
        model::ImageEncoding,
    };
    let mut images = std::collections::BTreeMap::<Vec<u8>, usize>::new();
    let mut jobs = Vec::new();
    let mut maps = Vec::new();
    let mut reserved = 0usize;
    let mut total = 0usize;
    // Traverse material references once before starting workers or reporting progress.
    for (path, model) in &sources {
        let referenced: std::collections::BTreeSet<_> = model
            .data()
            .materials
            .iter()
            .flat_map(|material| material.texture_indices().into_iter().flatten())
            .collect();
        let mut mapping = Vec::new();
        for (index, texture) in model.data().textures.iter().enumerate() {
            if !referenced.contains(&index) {
                mapping.push(None);
                continue;
            }
            total += 1;
            let image = &model.data().images[texture.image];
            if let Some(&existing) = images.get(&image.bytes) {
                mapping.push(Some(existing));
                continue;
            }
            if image.encoding != ImageEncoding::Png || image.bytes.len() > budget.max_input_bytes {
                return Err(io::Error::other("unsupported or oversized texture source").into());
            }
            // Read only dimensions here. Workers validate the complete PNG before publication.
            let header = image
                .bytes
                .get(..24)
                .ok_or_else(|| io::Error::other("missing PNG header"))?;
            if &header[..8] != b"\x89PNG\r\n\x1a\n" || &header[12..16] != b"IHDR" {
                return Err(io::Error::other("invalid PNG header").into());
            }
            let width = u32::from_be_bytes(header[16..20].try_into().unwrap());
            let height = u32::from_be_bytes(header[20..24].try_into().unwrap());
            if width == 0
                || height == 0
                || width > PngSettings::default().max_dimension
                || height > PngSettings::default().max_dimension
            {
                return Err(io::Error::other("texture dimensions exceed limits").into());
            }
            let bytes = (width as usize)
                .checked_mul(height as usize)
                .and_then(|n| n.checked_mul(4))
                .ok_or_else(|| io::Error::other("texture size overflow"))?;
            reserved = reserved
                .checked_add(bytes)
                .filter(|n| *n <= budget.max_decoded_bytes)
                .ok_or_else(|| io::Error::other("material texture budget exceeded"))?;
            let id = jobs.len();
            jobs.push((path.clone(), model.clone(), texture.image, bytes));
            images.insert(image.bytes.clone(), id);
            mapping.push(Some(id));
        }
        maps.push(mapping);
    }
    let progress = crate::progress::ImportProgress::new(label, total);
    let shared = total - jobs.len();
    let batch = Batch::start(jobs, move |(path, model, image, bytes), cancelled| {
        Ok(Arc::new(crate::cache::load_bytes(
            &path,
            &format!("image/{image}"),
            &model.data().images[image].bytes,
            &PngImporter,
            &PngSettings::default(),
            crate::import::ImportBudget {
                max_input_bytes: budget.max_input_bytes,
                max_decoded_bytes: bytes,
            },
            &|| cancelled.load(Ordering::Acquire),
        )?))
    })?;
    let loaded = batch.wait(|| progress.complete_one())?;
    for _ in 0..shared {
        progress.reuse_one();
    }
    progress.finish();
    Ok(maps
        .into_iter()
        .map(|mapping| {
            mapping
                .into_iter()
                .map(|index| index.map(|i| loaded[i].clone()))
                .collect()
        })
        .collect())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::{Duration, Instant};

    #[test]
    fn workers_overlap_poll_without_waiting_and_return_input_order() {
        let (started, received) = mpsc::channel();
        let release = Arc::new(AtomicBool::new(false));
        let gate = release.clone();
        let mut batch = Batch::with_workers(vec![0, 1], 2, move |value, cancelled| {
            started.send(value)?;
            let deadline = Instant::now() + Duration::from_secs(5);
            while !gate.load(Ordering::Acquire) && !cancelled.load(Ordering::Acquire) {
                if Instant::now() >= deadline {
                    return Err(io::Error::other("worker gate timed out").into());
                }
                thread::yield_now();
            }
            Ok(value)
        })
        .unwrap();
        received.recv_timeout(Duration::from_secs(2)).unwrap();
        received.recv_timeout(Duration::from_secs(2)).unwrap();
        assert!(batch.try_next().is_none());
        release.store(true, Ordering::Release);
        assert_eq!(batch.wait(|| {}).unwrap(), vec![0, 1]);
    }

    #[test]
    fn drop_cancels_running_work_and_joins_before_returning() {
        let (started, received) = mpsc::channel();
        let exited = Arc::new(AtomicBool::new(false));
        let finished = exited.clone();
        let batch = Batch::with_workers(vec![0, 1], 1, move |_, cancelled| {
            started.send(())?;
            while !cancelled.load(Ordering::Acquire) {
                thread::yield_now();
            }
            finished.store(true, Ordering::Release);
            Ok(())
        })
        .unwrap();
        received.recv_timeout(Duration::from_secs(2)).unwrap();
        drop(batch);
        assert!(exited.load(Ordering::Acquire));
        assert!(received.try_recv().is_err());
    }

    #[test]
    fn failure_and_panic_stop_pending_jobs_without_losing_the_error() {
        for panic in [false, true] {
            let batch = Batch::<()>::with_workers(vec![0, 1], 1, move |_, _| {
                assert!(!panic, "test panic");
                Err(io::Error::other("test failure").into())
            })
            .unwrap();
            let error = batch.wait(|| {}).unwrap_err().to_string();
            assert_eq!(
                error,
                if panic {
                    "asset worker panicked"
                } else {
                    "test failure"
                }
            );
        }
    }

    #[test]
    fn worker_cache_counts_reach_the_calling_thread() {
        let before = observe_current_thread().stats();
        Batch::with_workers(vec![0, 1], 2, |_, _| {
            record_activity(CacheStats {
                hits: 2,
                ..Default::default()
            });
            Ok(())
        })
        .unwrap()
        .wait(|| {})
        .unwrap();
        assert_eq!(observe_current_thread().stats().hits - before.hits, 4);
    }

    #[test]
    fn empty_and_oversized_batches_are_bounded() {
        let batch = Batch::start(Vec::<()>::new(), |_, _| Ok(())).unwrap();
        assert!(batch.is_finished());
        assert!(batch.wait(|| {}).unwrap().is_empty());
        assert!(Batch::start(vec![(); 1025], |_, _| Ok(())).is_err());
    }

    #[cfg(all(feature = "png-import", feature = "gltf-import"))]
    #[test]
    fn parallel_models_match_serial_loading_and_reuse_cache() {
        use crate::import::ImportBudget;
        use crate::importers::{ModelGlbImporter, ModelGlbSettings};
        let path =
            std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/meshes/cube.glb");
        let settings = ModelGlbSettings::default();
        let budget = ImportBudget::default();
        let serial =
            crate::cache::load_file(&path, &ModelGlbImporter, &settings, budget, &|| false)
                .unwrap();
        let before = observe_current_thread().stats();
        let loaded = models(
            vec![path.clone(), path.clone()],
            "test models",
            settings,
            budget,
        )
        .unwrap();
        assert_eq!(loaded.len(), 2);
        assert_eq!(
            crate::cache::encode(loaded[0].data()).unwrap(),
            crate::cache::encode(serial.data()).unwrap()
        );
        assert_eq!(
            crate::cache::encode(loaded[1].data()).unwrap(),
            crate::cache::encode(serial.data()).unwrap()
        );
        assert_eq!(observe_current_thread().stats().hits - before.hits, 2);
    }
    #[cfg(all(feature = "png-import", feature = "gltf-import"))]
    #[test]
    fn parallel_textures_share_pixels_and_enforce_total_budget() {
        use crate::model::*;
        let bytes = include_bytes!("../tests/fixtures/textures/sample.png").to_vec();
        let texture = ModelTexture {
            image: 0,
            wrap_s: WrapMode::Repeat,
            wrap_t: WrapMode::Repeat,
            min_filter: None,
            mag_filter: None,
        };
        let model = Arc::new(
            Model::new(ModelData {
                images: vec![ModelImage {
                    name: String::new(),
                    encoding: ImageEncoding::Png,
                    bytes,
                }],
                textures: vec![texture.clone(), texture],
                materials: vec![Material {
                    name: String::new(),
                    base_color: [1.; 4],
                    metallic: 0.,
                    roughness: 1.,
                    base_color_texture: Some(0),
                    metallic_roughness_texture: None,
                    normal_texture: None,
                    normal_scale: 1.,
                    occlusion_texture: None,
                    occlusion_strength: 1.,
                    emissive_texture: Some(1),
                    emissive: [0.; 3],
                    alpha: AlphaMode::Opaque,
                    alpha_cutoff: 0.5,
                    double_sided: false,
                }],
                ..Default::default()
            })
            .unwrap(),
        );
        let source = std::path::PathBuf::from("memory-only-model.glb");
        let budget = crate::import::ImportBudget {
            max_input_bytes: 1024,
            max_decoded_bytes: 16,
        };
        let loaded = textures(
            vec![
                (source.clone(), model.clone()),
                (source.clone(), model.clone()),
            ],
            "test textures",
            budget,
        )
        .unwrap();
        let first = loaded[0][0].as_ref().unwrap();
        assert_eq!((first.width(), first.height()), (2, 2));
        assert!(Arc::ptr_eq(first, loaded[0][1].as_ref().unwrap()));
        assert!(Arc::ptr_eq(first, loaded[1][0].as_ref().unwrap()));
        assert!(
            textures(
                vec![(source, model)],
                "too large",
                crate::import::ImportBudget {
                    max_decoded_bytes: 15,
                    ..budget
                }
            )
            .is_err()
        );
    }
}
