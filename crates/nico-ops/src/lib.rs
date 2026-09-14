//! In-process status and orderly-stop control for application hosts.
//!
//! A host owns [`HostEndpoint`]; adapters hold cloneable [`HostControl`] handles.
//! Only the host performs lifecycle operations. The default build has no external
//! dependencies; the optional `mcp` feature adds tool catalogs and handlers. Storage holds one status snapshot
//! and one pending stop signal, regardless of how often a controller polls.

#[cfg(feature = "mcp")]
pub mod mcp;

#[cfg(feature = "bridge")]
pub mod bridge;

pub mod snapshot;

use std::{
    error::Error,
    fmt,
    sync::{
        Arc, Mutex,
        mpsc::{self, Receiver, RecvTimeoutError, SyncSender, TryRecvError, TrySendError},
    },
    time::Duration,
};

/// Lifecycle reported by the host, independently of any operating-system process.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
#[cfg_attr(feature = "bridge", derive(serde::Serialize, serde::Deserialize))]
#[cfg_attr(feature = "bridge", serde(rename_all = "snake_case"))]
pub enum HostState {
    Starting,
    Running,
    Stopping,
    Stopped,
    Failed,
}

/// Backend-neutral result of the latest graphics initialization/render attempt.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
#[cfg_attr(feature = "bridge", derive(serde::Serialize, serde::Deserialize))]
#[cfg_attr(feature = "bridge", serde(rename_all = "snake_case"))]
pub enum GraphicsOutcome {
    #[default]
    NotAttempted,
    Presented,
    ZeroSized,
    Timeout,
    Occluded,
    InitializationFailed,
    RenderFailed,
}

impl GraphicsOutcome {
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::NotAttempted => "not_attempted",
            Self::Presented => "presented",
            Self::ZeroSized => "zero_sized",
            Self::Timeout => "timeout",
            Self::Occluded => "occluded",
            Self::InitializationFailed => "initialization_failed",
            Self::RenderFailed => "render_failed",
        }
    }
}

/// Presentation API successes, not proof of display scanout or GPU completion.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
#[cfg_attr(feature = "bridge", derive(serde::Serialize, serde::Deserialize))]
pub struct GraphicsStatus {
    pub presented_frames: u64,
    pub last_outcome: GraphicsOutcome,
}

/// An owned copy of the latest host report; it never exposes application state.
#[derive(Clone, Debug, Eq, PartialEq)]
#[cfg_attr(feature = "bridge", derive(serde::Serialize, serde::Deserialize))]
pub struct HostStatus {
    pub state: HostState,
    /// Latest host-reported activity; false during client suspension or shutdown.
    /// Independent of readiness and of whether an external bridge is connected.
    pub active: bool,
    /// Successful host steps, not necessarily fixed simulation ticks or GPU frames.
    pub completed_steps: u64,
    /// None for hosts without graphics reporting. Retained across suspension/shutdown.
    #[cfg_attr(feature = "bridge", serde(default))]
    pub graphics: Option<GraphicsStatus>,
    /// Failure reported by the host, or an unexpected endpoint disconnect.
    pub failure: Option<String>,
}

impl HostStatus {
    /// Whether the host has reported readiness and is still running.
    #[must_use]
    pub const fn is_ready(&self) -> bool {
        matches!(self.state, HostState::Running)
    }

    /// Whether the host has finished, successfully or otherwise.
    ///
    /// This does not imply that the containing process has exited.
    #[must_use]
    pub const fn is_finished(&self) -> bool {
        matches!(self.state, HostState::Stopped | HostState::Failed)
    }
}

/// The host is no longer available to accept a stop request.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct HostClosed;

impl fmt::Display for HostClosed {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str("host control endpoint is closed")
    }
}

impl Error for HostClosed {}

/// Controller-side access to status and a bounded, idempotent stop signal.
///
/// Dropping the last controller requests stop through channel disconnection.
/// A host must poll or wait at a safe lifecycle boundary to observe that request.
#[derive(Clone)]
pub struct HostControl {
    stop: Arc<StopSender>,
    status: Arc<Mutex<HostStatus>>,
    snapshots: snapshot::SnapshotControl,
}

type WakeCallback = Arc<dyn Fn() + Send + Sync>;

#[derive(Default)]
struct Wakeup(Mutex<Option<WakeCallback>>);

impl Wakeup {
    fn notify(&self) {
        let callback = self.0.lock().expect("host wakeup lock poisoned").clone();
        if let Some(callback) = callback {
            callback();
        }
    }
}

struct StopSender {
    sender: Option<SyncSender<()>>,
    wakeup: Arc<Wakeup>,
}

impl Drop for StopSender {
    fn drop(&mut self) {
        // Disconnect before waking so the host can observe the last-controller loss.
        self.sender.take();
        self.wakeup.notify();
    }
}

impl HostControl {
    /// Bounded client snapshot requests; only enabled by a supporting host.
    pub fn snapshots(&self) -> snapshot::SnapshotControl {
        self.snapshots.clone()
    }

    /// Returns the latest published snapshot, which may lag a busy host.
    #[must_use]
    pub fn status(&self) -> HostStatus {
        self.status
            .lock()
            .expect("host status lock poisoned")
            .clone()
    }

    /// Requests orderly stop without blocking the caller or counting duplicates.
    ///
    /// Success means the request was queued, already pending, or the host already
    /// stopped successfully. It does not mean shutdown has completed. A failed or
    /// unexpectedly disconnected host returns [`HostClosed`]; inspect its status.
    pub fn request_stop(&self) -> Result<(), HostClosed> {
        match self.status().state {
            HostState::Stopped => return Ok(()),
            HostState::Failed => return Err(HostClosed),
            _ => {}
        }
        match self
            .stop
            .sender
            .as_ref()
            .expect("live stop sender")
            .try_send(())
        {
            Ok(()) => {
                self.stop.wakeup.notify();
                Ok(())
            }
            Err(TrySendError::Full(())) => Ok(()),
            Err(TrySendError::Disconnected(())) => {
                // The host may have completed shutdown since the status read.
                if self.status().state == HostState::Stopped {
                    Ok(())
                } else {
                    Err(HostClosed)
                }
            }
        }
    }
}

/// Host-owned endpoint. It contains no application or world reference.
///
/// Report readiness only after the host-specific readiness condition succeeds.
/// Consume the endpoint with [`Self::finish`] after shutdown; dropping it without
/// a final result publishes a failure so controllers do not retain stale readiness.
pub struct HostEndpoint {
    stop: Receiver<()>,
    status: Arc<Mutex<HostStatus>>,
    snapshots: snapshot::SnapshotControl,
    stop_requested: bool,
    wakeup: Arc<Wakeup>,
}

impl HostEndpoint {
    /// Host-owned snapshot publication boundary.
    pub fn snapshots(&self) -> snapshot::SnapshotControl {
        self.snapshots.clone()
    }

    /// Installs a host wakeup for stop requests and last-controller disconnect.
    ///
    /// The callback runs on the requesting/dropping thread and must return promptly,
    /// never panic, and only signal the host (for example via an event-loop proxy).
    /// It must not capture a controller, which would prevent disconnect detection.
    /// Installation also signals once so requests predating registration are observed.
    /// A replaced callback may still be in flight. Host polling remains authoritative.
    pub fn set_wakeup(&mut self, wakeup: impl Fn() + Send + Sync + 'static) {
        *self.wakeup.0.lock().expect("host wakeup lock poisoned") = Some(Arc::new(wakeup));
        self.wakeup.notify();
    }

    /// Observes and latches an explicit stop or the loss of all controllers.
    pub fn stop_requested(&mut self) -> bool {
        if !self.stop_requested {
            self.stop_requested = match self.stop.try_recv() {
                Ok(()) | Err(TryRecvError::Disconnected) => true,
                Err(TryRecvError::Empty) => false,
            };
        }
        self.stop_requested
    }

    /// Waits up to `timeout` for stop, waking immediately on controller disconnect.
    ///
    /// Returns false only on timeout. Once observed, stop remains latched.
    pub fn wait_for_stop(&mut self, timeout: Duration) -> bool {
        if !self.stop_requested {
            self.stop_requested = match self.stop.recv_timeout(timeout) {
                Ok(()) | Err(RecvTimeoutError::Disconnected) => true,
                Err(RecvTimeoutError::Timeout) => false,
            };
        }
        self.stop_requested
    }

    /// Publishes readiness and progress. Reports after `stopping` are ignored.
    ///
    /// The host defines a successful step. Progress never decreases.
    pub fn running(&mut self, completed_steps: u64) {
        let mut status = self.status.lock().expect("host status lock poisoned");
        if matches!(status.state, HostState::Starting | HostState::Running) {
            status.state = HostState::Running;
            status.active = true;
            status.completed_steps = status.completed_steps.max(completed_steps);
        }
    }

    /// Publishes successful host steps without changing readiness or lifecycle.
    pub fn progress(&mut self, completed_steps: u64) {
        let mut status = self.status.lock().expect("host status lock poisoned");
        if matches!(status.state, HostState::Starting | HostState::Running) {
            status.completed_steps = status.completed_steps.max(completed_steps);
        }
    }

    /// Records one graphics outcome without changing host progress or readiness.
    pub fn graphics(&mut self, outcome: GraphicsOutcome) {
        let mut status = self.status.lock().expect("host status lock poisoned");
        if matches!(status.state, HostState::Starting | HostState::Running) {
            let graphics = status.graphics.get_or_insert_with(GraphicsStatus::default);
            if outcome == GraphicsOutcome::Presented {
                graphics.presented_frames = graphics.presented_frames.saturating_add(1);
            }
            graphics.last_outcome = outcome;
        }
    }

    /// Reports host activity without changing readiness, for example on suspension.
    pub fn activity(&mut self, active: bool) {
        let mut status = self.status.lock().expect("host status lock poisoned");
        if matches!(status.state, HostState::Starting | HostState::Running) {
            status.active = active;
        }
    }

    /// Publishes that shutdown has begun. This is not a completion acknowledgement.
    pub fn stopping(&mut self) {
        let mut status = self.status.lock().expect("host status lock poisoned");
        status.state = HostState::Stopping;
        status.active = false;
    }

    /// Publishes the final lifecycle result and closes the host endpoint.
    pub fn finish(self, result: Result<(), String>) {
        let mut status = self.status.lock().expect("host status lock poisoned");
        status.active = false;
        match result {
            Ok(()) => status.state = HostState::Stopped,
            Err(message) => {
                status.state = HostState::Failed;
                status.failure = Some(message);
            }
        }
    }
}

impl Drop for HostEndpoint {
    fn drop(&mut self) {
        self.snapshots.close();
        self.wakeup
            .0
            .lock()
            .expect("host wakeup lock poisoned")
            .take();
        let mut status = self.status.lock().expect("host status lock poisoned");
        if !status.is_finished() {
            status.state = HostState::Failed;
            status.active = false;
            status.failure = Some("host endpoint dropped without a final result".to_owned());
        }
    }
}

/// Creates an isolated host/controller pair in the Starting state.
///
/// No threads are started. The one-slot stop channel coalesces repeated requests.
#[must_use]
pub fn control_channel() -> (HostControl, HostEndpoint) {
    let (sender, receiver) = mpsc::sync_channel(1);
    let status = Arc::new(Mutex::new(HostStatus {
        state: HostState::Starting,
        active: false,
        completed_steps: 0,
        graphics: None,
        failure: None,
    }));
    let wakeup = Arc::new(Wakeup::default());
    let snapshots = snapshot::SnapshotControl::default();
    (
        HostControl {
            stop: Arc::new(StopSender {
                sender: Some(sender),
                wakeup: wakeup.clone(),
            }),
            status: status.clone(),
            snapshots: snapshots.clone(),
        },
        HostEndpoint {
            stop: receiver,
            status,
            snapshots,
            stop_requested: false,
            wakeup,
        },
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn graphics_counts_only_presentations_and_preserves_terminal_report() {
        let (control, mut host) = control_channel();
        assert!(control.status().graphics.is_none());
        host.graphics(GraphicsOutcome::NotAttempted);
        host.graphics(GraphicsOutcome::Presented);
        let first = control.status();
        host.graphics(GraphicsOutcome::Timeout);
        host.activity(false);
        assert_eq!(control.status().graphics.unwrap().presented_frames, 1);
        assert_eq!(
            first.graphics.unwrap().last_outcome,
            GraphicsOutcome::Presented
        );
        host.graphics(GraphicsOutcome::RenderFailed);
        host.stopping();
        host.graphics(GraphicsOutcome::Presented);
        host.finish(Err("render failed".into()));
        let status = control.status();
        assert_eq!(status.graphics.unwrap().presented_frames, 1);
        assert_eq!(
            status.graphics.unwrap().last_outcome,
            GraphicsOutcome::RenderFailed
        );
        assert_eq!(status.completed_steps, 0);
        assert!(!status.is_ready());
    }

    #[test]
    fn wakeups_coalesce_and_last_disconnect_is_observable_before_wakeup() {
        let (control, mut host) = control_channel();
        let (sender, wakes) = mpsc::channel();
        host.set_wakeup(move || {
            let _ = sender.send(());
        });
        wakes.try_recv().unwrap(); // Installation covers earlier requests.
        let second = control.clone();
        for _ in 0..100 {
            control.request_stop().unwrap();
        }
        wakes.try_recv().unwrap();
        assert!(wakes.try_recv().is_err());
        assert!(host.stop_requested());
        drop(control);
        assert!(wakes.try_recv().is_err());
        drop(second);
        wakes.try_recv().unwrap();
        assert!(matches!(
            host.stop.try_recv(),
            Err(TryRecvError::Disconnected)
        ));
        host.finish(Ok(()));
    }

    #[test]
    fn wakeup_registration_observes_stop_or_disconnect_before_installation() {
        for disconnected in [false, true] {
            let (control, mut host) = control_channel();
            if disconnected {
                drop(control);
            } else {
                control.request_stop().unwrap();
            }
            let (sender, wakes) = mpsc::channel();
            host.set_wakeup(move || {
                let _ = sender.send(());
            });
            wakes.try_recv().unwrap();
            assert!(host.stop_requested());
            host.finish(Ok(()));
        }
    }

    #[test]
    fn finishing_releases_the_wakeup_even_with_live_controllers() {
        let (control, mut host) = control_channel();
        let (sender, wakes) = mpsc::channel();
        host.set_wakeup(move || {
            let _ = sender.send(());
        });
        wakes.try_recv().unwrap();
        host.finish(Ok(()));
        assert!(matches!(wakes.try_recv(), Err(TryRecvError::Disconnected)));
        assert_eq!(control.request_stop(), Ok(()));
    }

    #[test]
    fn progress_does_not_claim_readiness_or_resume_a_stopping_host() {
        let (control, mut host) = control_channel();
        host.progress(3);
        host.progress(2);
        assert_eq!(control.status().completed_steps, 3);
        assert!(!control.status().is_ready());
        host.running(4);
        host.progress(5);
        assert_eq!(control.status().completed_steps, 5);
        host.stopping();
        host.progress(6);
        assert_eq!(control.status().completed_steps, 5);
        assert_eq!(control.status().state, HostState::Stopping);
        host.finish(Ok(()));
    }

    #[test]
    fn activity_is_independent_of_readiness_and_cleared_on_shutdown() {
        let (control, mut host) = control_channel();
        host.activity(true);
        assert!(control.status().active);
        assert!(!control.status().is_ready());
        host.running(1);
        host.activity(false);
        assert!(control.status().is_ready());
        assert!(!control.status().active);
        host.activity(true);
        host.stopping();
        host.activity(true);
        assert!(!control.status().active);
        host.finish(Ok(()));
    }

    #[test]
    fn snapshots_are_owned_and_progress_and_shutdown_are_explicit() {
        let (control, mut host) = control_channel();
        let initial = control.status();
        assert_eq!(initial.state, HostState::Starting);
        assert!(!initial.is_ready());
        host.running(3);
        host.running(2);
        assert_eq!(control.status().completed_steps, 3);
        assert!(control.status().is_ready());
        assert_eq!(initial.completed_steps, 0);
        host.stopping();
        host.running(4);
        assert_eq!(control.status().state, HostState::Stopping);
        assert!(!control.status().is_finished());
        host.finish(Ok(()));
        assert_eq!(control.status().state, HostState::Stopped);
        assert!(control.status().is_finished());
        assert_eq!(control.request_stop(), Ok(()));
    }

    #[test]
    fn stop_requests_coalesce_and_remain_latched() {
        let (control, mut host) = control_channel();
        assert!(!host.stop_requested());
        assert!(!host.wait_for_stop(Duration::ZERO));
        for _ in 0..100 {
            control.request_stop().unwrap();
        }
        assert_eq!(control.status().state, HostState::Starting);
        assert!(host.wait_for_stop(Duration::ZERO));
        assert!(host.stop_requested());
        assert!(host.wait_for_stop(Duration::ZERO));
        host.finish(Ok(()));
    }

    #[test]
    fn only_last_controller_disconnect_requests_stop() {
        let (control, mut host) = control_channel();
        let second = control.clone();
        drop(control);
        assert!(!host.stop_requested());
        drop(second);
        assert!(host.wait_for_stop(Duration::ZERO));
        host.finish(Ok(()));
    }

    #[test]
    fn failure_and_unexpected_disconnect_clear_readiness() {
        let (control, mut host) = control_channel();
        host.running(1);
        host.finish(Err("shutdown failed".to_owned()));
        assert_eq!(control.status().state, HostState::Failed);
        assert_eq!(control.status().failure.as_deref(), Some("shutdown failed"));
        assert_eq!(control.request_stop(), Err(HostClosed));

        let (control, mut host) = control_channel();
        host.running(1);
        drop(host);
        assert_eq!(control.status().state, HostState::Failed);
        assert!(control.status().failure.is_some());
        assert!(!control.status().is_ready());
        assert_eq!(control.request_stop(), Err(HostClosed));
    }

    #[test]
    fn separate_channels_cannot_stop_each_other() {
        let (first, mut first_host) = control_channel();
        let (_second, mut second_host) = control_channel();
        first.request_stop().unwrap();
        assert!(first_host.stop_requested());
        assert!(!second_host.stop_requested());
        first_host.finish(Ok(()));
        second_host.finish(Ok(()));
    }
}
