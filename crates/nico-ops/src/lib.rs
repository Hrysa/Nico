//! In-process status and orderly-stop control for application hosts.
//!
//! A host owns [`HostEndpoint`]; adapters hold cloneable [`HostControl`] handles.
//! Only the host performs lifecycle operations. The default build has no external
//! dependencies; the optional `mcp` feature adds a stdio adapter. Storage holds one status snapshot
//! and one pending stop signal, regardless of how often a controller polls.

#[cfg(feature = "mcp")]
pub mod mcp;

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
pub enum HostState {
    Starting,
    Running,
    Stopping,
    Stopped,
    Failed,
}

/// An owned copy of the latest host report; it never exposes application state.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct HostStatus {
    pub state: HostState,
    /// Successful host steps, not necessarily fixed simulation ticks or GPU frames.
    pub completed_steps: u64,
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
    stop: SyncSender<()>,
    status: Arc<Mutex<HostStatus>>,
}

impl HostControl {
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
        match self.stop.try_send(()) {
            Ok(()) | Err(TrySendError::Full(())) => Ok(()),
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
    stop_requested: bool,
}

impl HostEndpoint {
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
            status.completed_steps = status.completed_steps.max(completed_steps);
        }
    }

    /// Publishes that shutdown has begun. This is not a completion acknowledgement.
    pub fn stopping(&mut self) {
        self.status.lock().expect("host status lock poisoned").state = HostState::Stopping;
    }

    /// Publishes the final lifecycle result and closes the host endpoint.
    pub fn finish(self, result: Result<(), String>) {
        let mut status = self.status.lock().expect("host status lock poisoned");
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
        let mut status = self.status.lock().expect("host status lock poisoned");
        if !status.is_finished() {
            status.state = HostState::Failed;
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
        completed_steps: 0,
        failure: None,
    }));
    (
        HostControl {
            stop: sender,
            status: status.clone(),
        },
        HostEndpoint {
            stop: receiver,
            status,
            stop_requested: false,
        },
    )
}

#[cfg(test)]
mod tests {
    use super::*;

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
