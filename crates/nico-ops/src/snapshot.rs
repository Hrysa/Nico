//! One pending capture and one retained result per host; no application references.
use std::sync::{Arc, Mutex};

#[derive(Clone, Debug)]
pub struct Pixels {
    pub width: u32,
    pub height: u32,
    /// Top-to-bottom RGBA8 color bytes from the render target.
    pub rgba: Vec<u8>,
}

#[derive(Clone, Debug)]
pub enum SnapshotState {
    Pending,
    Ready(Arc<Pixels>),
    Failed(String),
}

#[derive(Default)]
struct Slot {
    enabled: bool,
    closed: bool,
    id: u64,
    taken: bool,
    state: Option<SnapshotState>,
    requested_at: Option<std::time::Instant>,
}

#[derive(Clone, Default)]
pub struct SnapshotControl(Arc<Mutex<Slot>>);

impl SnapshotControl {
    /// Host composition enables captures only when a rendering consumer is installed.
    pub fn enable(&self) {
        self.0.lock().unwrap().enabled = true;
    }
    /// Queue a capture without blocking. Replaces the previous completed result.
    pub fn request(&self) -> Result<u64, &'static str> {
        let mut slot = self.0.lock().unwrap();
        if slot.closed {
            return Err("host_closed");
        }
        if !slot.enabled {
            return Err("unsupported");
        }
        if matches!(slot.state, Some(SnapshotState::Pending)) {
            return Err("busy");
        }
        slot.id = slot.id.checked_add(1).ok_or("request_id_exhausted")?;
        slot.taken = false;
        slot.requested_at = Some(std::time::Instant::now());
        slot.state = Some(SnapshotState::Pending);
        Ok(slot.id)
    }
    /// Read the retained outcome; polling expires an uncompleted five-second request.
    pub fn read(&self, id: u64) -> Result<SnapshotState, &'static str> {
        let mut slot = self.0.lock().unwrap();
        if matches!(slot.state, Some(SnapshotState::Pending))
            && slot
                .requested_at
                .is_some_and(|at| at.elapsed() > std::time::Duration::from_secs(5))
        {
            slot.state = Some(SnapshotState::Failed("capture_timeout".into()));
        }
        if id != slot.id {
            return Err("unknown_or_expired_request");
        }
        slot.state.clone().ok_or("unknown_or_expired_request")
    }
    /// Called by the host at its render boundary, never by a tooling thread.
    pub fn take_request(&self) -> Option<u64> {
        let mut slot = self.0.lock().unwrap();
        if slot.taken || !matches!(slot.state, Some(SnapshotState::Pending)) {
            return None;
        }
        slot.taken = true;
        Some(slot.id)
    }
    /// Publish bounded host-owned pixels. Late results cannot replace a newer request.
    pub fn complete(&self, id: u64, result: Result<Pixels, String>) {
        let mut slot = self.0.lock().unwrap();
        if slot.id == id && matches!(slot.state, Some(SnapshotState::Pending)) {
            slot.state = Some(match result {
                Ok(pixels) => SnapshotState::Ready(Arc::new(pixels)),
                Err(error) => SnapshotState::Failed(error),
            });
        }
    }
    /// Fail pending work and reject future requests, retaining completed content.
    pub fn close(&self) {
        let mut slot = self.0.lock().unwrap();
        slot.closed = true;
        if matches!(slot.state, Some(SnapshotState::Pending)) {
            slot.state = Some(SnapshotState::Failed("host_closed".into()));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn ready_pixels_are_shared_and_shutdown_fails_pending_capture() {
        let (host_control, endpoint) = crate::control_channel();
        let control = host_control.snapshots();
        control.enable();
        let id = control.request().unwrap();
        endpoint.snapshots().complete(
            id,
            Ok(Pixels {
                width: 1,
                height: 1,
                rgba: vec![1, 2, 3, 255],
            }),
        );
        let SnapshotState::Ready(first) = control.read(id).unwrap() else {
            panic!("capture not ready");
        };
        let SnapshotState::Ready(second) = control.read(id).unwrap() else {
            panic!("capture not retained");
        };
        assert!(Arc::ptr_eq(&first, &second));
        let next = control.request().unwrap();
        drop(endpoint);
        assert!(matches!(control.read(next), Ok(SnapshotState::Failed(e)) if e == "host_closed"));
        assert_eq!(first.rgba, [1, 2, 3, 255]);
    }

    #[test]
    fn stalled_capture_times_out_and_late_completion_is_discarded() {
        let control = SnapshotControl::default();
        control.enable();
        let id = control.request().unwrap();
        control.0.lock().unwrap().requested_at =
            Some(std::time::Instant::now() - std::time::Duration::from_secs(6));
        assert!(matches!(control.read(id), Ok(SnapshotState::Failed(e)) if e == "capture_timeout"));
        assert_eq!(control.take_request(), None);
        control.complete(id, Err("late".into()));
        assert!(matches!(control.read(id), Ok(SnapshotState::Failed(e)) if e == "capture_timeout"));
        assert!(control.request().is_ok());
    }

    #[test]
    fn capture_is_bounded_reconciled_by_id_and_closed_on_shutdown() {
        let control = SnapshotControl::default();
        assert_eq!(control.request(), Err("unsupported"));
        control.enable();
        let id = control.request().unwrap();
        assert_eq!(control.request(), Err("busy"));
        assert_eq!(control.take_request(), Some(id));
        assert_eq!(control.take_request(), None);
        control.complete(id, Err("unavailable".into()));
        assert!(matches!(control.read(id), Ok(SnapshotState::Failed(_))));
        let next = control.request().unwrap();
        assert!(control.read(id).is_err());
        control.complete(id, Err("stale".into()));
        assert!(matches!(control.read(next), Ok(SnapshotState::Pending)));
        control.close();
        assert!(matches!(control.read(next), Ok(SnapshotState::Failed(_))));
        assert_eq!(control.request(), Err("host_closed"));
    }
}
