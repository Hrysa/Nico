//! Scoped loading progress with separate model and texture phases.
use std::{cell::Cell, sync::Arc, time::Instant};

/// Owned progress notification for a scoped UI observer on the importing thread.
#[derive(Clone, Debug, serde::Serialize)]
pub struct ProgressUpdate {
    pub label: String,
    pub completed: usize,
    pub total: usize,
    pub finished: bool,
    /// Elapsed wall time for this loading phase, including waits; not CPU time.
    pub elapsed_ms: f64,
    /// Counters scoped to this loading thread and phase, excluding other workers.
    pub cache: crate::cache::CacheStats,
    pub shared: usize,
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

/// Reports a fixed total counted before the batch starts.
/// Logs only the initial count and completed-count changes. No timer thread is used.
/// Observers run on the calling thread; unfinished drops never report success.
pub struct ImportProgress {
    started: Instant,
    observer: Option<Observer>,
    completed: Cell<usize>,
    activity: Arc<crate::cache::CacheActivity>,
    initial: crate::cache::CacheStats,
    shared: Cell<usize>,
    total: usize,
    label: String,
    report: Box<dyn Fn(String)>,
}
impl ImportProgress {
    pub fn new(label: impl Into<String>, total: usize) -> Self {
        Self::start(label.into(), total, |line| tracing::info!("{line}"))
    }

    fn start(label: String, total: usize, report: impl Fn(String) + 'static) -> Self {
        let activity = crate::cache::observe_current_thread();
        let progress = Self {
            started: Instant::now(),
            observer: OBSERVER.with(|slot| slot.borrow().clone()),
            completed: Cell::new(0),
            initial: activity.stats(),
            activity,
            shared: Cell::new(0),
            total,
            label,
            report: Box::new(report),
        };
        progress.report();
        progress.notify(false);
        progress
    }

    fn notify(&self, finished: bool) {
        if let Some(observer) = &self.observer {
            observer(ProgressUpdate {
                label: self.label.clone(),
                completed: self.completed.get(),
                total: self.total,
                finished,
                elapsed_ms: self.started.elapsed().as_secs_f64() * 1000.,
                cache: self.activity.stats().since(self.initial),
                shared: self.shared.get(),
            });
        }
    }

    fn report(&self) {
        let stats = self.activity.stats().since(self.initial);
        (self.report)(format!(
            "loading {}/{} ({}; cache hits {}, imported {}, shared {})",
            self.completed.get(),
            self.total,
            self.label,
            stats.hits,
            stats.imports,
            self.shared.get(),
        ));
    }

    fn advance(&self, shared: bool) {
        if self.completed.get() == self.total {
            return;
        }
        self.completed.set(self.completed.get() + 1);
        if shared {
            self.shared.set(self.shared.get() + 1);
        }
        self.report();
        self.notify(false);
    }

    pub fn complete_one(&self) {
        self.advance(false);
    }
    pub fn reuse_one(&self) {
        self.advance(true);
    }

    /// End the observer's phase without repeating its last log line.
    pub fn finish(self) {
        self.notify(true);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::mpsc;

    #[test]
    fn reports_only_count_changes_with_a_fixed_total() {
        let (send, receive) = mpsc::channel();
        let progress = ImportProgress::start("test".into(), 2, move |line| {
            send.send(line).unwrap();
        });
        assert!(
            receive
                .try_recv()
                .unwrap()
                .starts_with("loading 0/2 (test;")
        );
        assert!(receive.try_recv().is_err());
        progress.complete_one();
        assert!(
            receive
                .try_recv()
                .unwrap()
                .starts_with("loading 1/2 (test;")
        );
        progress.reuse_one();
        let line = receive.try_recv().unwrap();
        assert!(line.starts_with("loading 2/2 (test;"));
        assert!(line.ends_with("shared 1)"));
        progress.complete_one();
        progress.reuse_one();
        assert!(receive.try_recv().is_err());
        progress.finish();
        assert_eq!(receive.try_recv(), Err(mpsc::TryRecvError::Disconnected));
    }

    #[test]
    fn dropping_unfinished_progress_does_not_report_completion() {
        let (send, receive) = mpsc::channel();
        let progress = ImportProgress::start("test".into(), 2, move |line| {
            send.send(line).unwrap();
        });
        progress.complete_one();
        drop(progress);
        let lines: Vec<_> = receive.try_iter().collect();
        assert_eq!(lines.len(), 2);
        assert!(lines[1].starts_with("loading 1/2 (test;"));
    }

    #[test]
    fn empty_batch_reports_once() {
        let (send, receive) = mpsc::channel();
        let progress = ImportProgress::start("empty".into(), 0, move |line| {
            send.send(line).unwrap();
        });
        progress.complete_one();
        progress.finish();
        let lines: Vec<_> = receive.try_iter().collect();
        assert_eq!(lines.len(), 1);
        assert!(lines[0].starts_with("loading 0/0 (empty;"));
    }

    #[test]
    fn ui_observer_reports_phase_counts_and_stays_on_the_calling_thread() {
        let (send, receive) = mpsc::channel();
        let observer = observe_progress(move |update| {
            send.send(update).unwrap();
        });
        let progress = ImportProgress::new("models", 2);
        progress.complete_one();
        progress.reuse_one();
        progress.finish();
        let updates: Vec<_> = receive.try_iter().collect();
        assert_eq!(
            updates.iter().map(|p| p.completed).collect::<Vec<_>>(),
            vec![0, 1, 2, 2]
        );
        assert!(updates.iter().all(|p| p.total == 2));
        assert!(updates.last().unwrap().finished);
        std::thread::spawn(|| ImportProgress::new("other", 1).finish())
            .join()
            .unwrap();
        assert!(receive.try_recv().is_err());
        drop(observer);
        ImportProgress::new("unobserved", 1).finish();
        assert_eq!(
            receive.try_recv().unwrap_err(),
            mpsc::TryRecvError::Disconnected
        );
    }
}
