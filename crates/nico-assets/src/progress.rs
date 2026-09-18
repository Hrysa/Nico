//! Scoped loading progress with separate model and texture phases.
use std::{
    io,
    sync::{
        Arc,
        atomic::{AtomicUsize, Ordering},
        mpsc,
    },
    thread::{self, JoinHandle},
    time::Duration,
};

/// Owned progress notification for a scoped UI observer on the importing thread.
#[derive(Clone, Debug)]
pub struct ProgressUpdate {
    pub label: String,
    pub completed: usize,
    pub total: usize,
    pub finished: bool,
}
type Observer = Arc<dyn Fn(ProgressUpdate) + Send + Sync>;
thread_local! { static OBSERVER: std::cell::RefCell<Option<Observer>> = const { std::cell::RefCell::new(None) }; }
pub struct ProgressObserver {
    previous: Option<Observer>,
    _thread: std::marker::PhantomData<std::rc::Rc<()>>,
}
/// Observe only imports initiated on this thread; dropping restores the prior observer.
pub fn observe_progress(
    observer: impl Fn(ProgressUpdate) + Send + Sync + 'static,
) -> ProgressObserver {
    ProgressObserver {
        previous: OBSERVER.with(|slot| slot.replace(Some(Arc::new(observer)))),
        _thread: std::marker::PhantomData,
    }
}
impl Drop for ProgressObserver {
    fn drop(&mut self) {
        OBSERVER.with(|slot| slot.replace(self.previous.take()));
    }
}

/// Prints completed/total, cache hits, imports, and in-memory reuse every three
/// seconds. Count model files and referenced textures separately. Drop joins the
/// reporter even on error, without claiming unfinished work completed.
pub struct ImportProgress {
    observer: Option<Observer>,
    completed: Arc<AtomicUsize>,
    activity: Arc<crate::cache::CacheActivity>,
    initial: crate::cache::CacheStats,
    shared: Arc<AtomicUsize>,
    total: usize,
    label: String,
    stop: mpsc::Sender<()>,
    worker: Option<JoinHandle<()>>,
}
impl ImportProgress {
    pub fn new(label: impl Into<String>, total: usize) -> io::Result<Self> {
        Self::start(label.into(), total, Duration::from_secs(3), |line| {
            tracing::info!("{line}")
        })
    }
    fn start(
        label: String,
        total: usize,
        interval: Duration,
        report: impl Fn(String) + Send + 'static,
    ) -> io::Result<Self> {
        let activity = crate::cache::observe_current_thread();
        let initial = activity.stats();
        let observed_activity = activity.clone();
        let completed = Arc::new(AtomicUsize::new(0));
        let observed = completed.clone();
        let shared = Arc::new(AtomicUsize::new(0));
        let observed_shared = shared.clone();
        let name = label.clone();
        let (stop, receiver) = mpsc::channel();
        let worker = thread::Builder::new()
            .name("asset-import-progress".into())
            .spawn(move || {
                while let Err(mpsc::RecvTimeoutError::Timeout) = receiver.recv_timeout(interval) {
                    report(line(
                        observed.load(Ordering::Relaxed),
                        total,
                        &name,
                        initial,
                        observed_activity.stats(),
                        observed_shared.load(Ordering::Relaxed),
                    ));
                }
            })?;
        let observer = OBSERVER.with(|slot| slot.borrow().clone());
        if let Some(observer) = &observer {
            observer(ProgressUpdate {
                label: label.clone(),
                completed: 0,
                total,
                finished: false,
            });
        }
        Ok(Self {
            observer,
            completed,
            initial,
            activity,
            shared,
            total,
            label,
            stop,
            worker: Some(worker),
        })
    }
    fn notify(&self, finished: bool) {
        if let Some(observer) = &self.observer {
            observer(ProgressUpdate {
                label: self.label.clone(),
                completed: self.completed.load(Ordering::Relaxed),
                total: self.total,
                finished,
            });
        }
    }
    pub fn complete_one(&self) {
        let _ = self
            .completed
            .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |n| {
                (n < self.total).then(|| n + 1)
            });
        self.notify(false);
    }
    pub fn reuse_one(&self) {
        if self
            .completed
            .fetch_update(Ordering::Relaxed, Ordering::Relaxed, |n| {
                (n < self.total).then(|| n + 1)
            })
            .is_ok()
        {
            self.shared.fetch_add(1, Ordering::Relaxed);
        }
        self.notify(false);
    }
    /// Stop periodic output and emit the final observed count immediately.
    pub fn finish(mut self) {
        self.stop();
        self.notify(true);
        tracing::info!(
            "{}",
            line(
                self.completed.load(Ordering::Relaxed),
                self.total,
                &self.label,
                self.initial,
                self.activity.stats(),
                self.shared.load(Ordering::Relaxed)
            )
        );
    }
    fn stop(&mut self) {
        let _ = self.stop.send(());
        if let Some(worker) = self.worker.take() {
            let _ = worker.join();
        }
    }
}
fn line(
    completed: usize,
    total: usize,
    label: &str,
    initial: crate::cache::CacheStats,
    now: crate::cache::CacheStats,
    shared: usize,
) -> String {
    format!(
        "loading {completed}/{total} ({label}; cache hits {}, imported {}, shared {shared})",
        now.hits.saturating_sub(initial.hits),
        now.imports.saturating_sub(initial.imports)
    )
}
impl Drop for ImportProgress {
    fn drop(&mut self) {
        self.stop();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn ui_observer_reports_phase_counts_and_is_scoped_to_importing_thread() {
        let (send, receive) = mpsc::channel();
        let observer = observe_progress(move |update| {
            send.send(update).unwrap();
        });
        let progress = ImportProgress::new("models", 2).unwrap();
        progress.complete_one();
        progress.reuse_one();
        progress.finish();
        let updates: Vec<_> = receive.try_iter().collect();
        assert_eq!(
            updates.iter().map(|p| p.completed).collect::<Vec<_>>(),
            vec![0, 1, 2, 2]
        );
        assert!(updates.last().unwrap().finished);
        std::thread::spawn(|| ImportProgress::new("other", 1).unwrap().finish())
            .join()
            .unwrap();
        assert!(receive.try_recv().is_err());
        drop(observer);
        ImportProgress::new("unobserved", 1).unwrap().finish();
        assert_eq!(
            receive.try_recv().unwrap_err(),
            mpsc::TryRecvError::Disconnected
        );
    }
    #[test]
    fn reports_while_work_is_blocked_and_drop_stops_reporter() {
        let (send, receive) = mpsc::channel();
        let progress =
            ImportProgress::start("test".into(), 2, Duration::from_millis(5), move |line| {
                let _ = send.send(line);
            })
            .unwrap();
        assert!(
            receive
                .recv_timeout(Duration::from_secs(2))
                .unwrap()
                .starts_with("loading 0/2 (test;")
        );
        progress.complete_one();
        while !receive
            .recv_timeout(Duration::from_secs(2))
            .unwrap()
            .starts_with("loading 1/2 (test;")
        {}
        drop(progress);
        // Drop joins the worker; queued lines may remain, but no sender survives.
        while receive.try_recv().is_ok() {}
        assert_eq!(receive.try_recv(), Err(mpsc::TryRecvError::Disconnected));
    }
    #[test]
    fn completion_is_bounded_and_does_not_wait_for_the_report_interval() {
        let progress =
            ImportProgress::start("test".into(), 1, Duration::from_secs(3600), |_| {}).unwrap();
        progress.complete_one();
        progress.complete_one();
        assert_eq!(progress.completed.load(Ordering::Relaxed), 1);
        drop(progress);
    }
}
