//! Bounded native window requests. Only the host invokes platform APIs; application
//! and tooling threads exchange owned data. Applied means submitted to the OS.
use std::sync::{Arc, Mutex};
use std::time::Instant;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum WindowAction {
    Resize { width: u32, height: u32 },
    Maximize,
    Minimize,
    Restore,
    Focus,
    PointerCapture { value: bool },
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum RequestState {
    Pending,
    Applied,
    Failed(String),
}

#[derive(Clone, Debug)]
pub struct WindowState {
    pub physical_size: [u32; 2],
    pub logical_size: [f64; 2],
    pub focused: bool,
    pub minimized: Option<bool>,
    pub maximized: bool,
    pub pointer_captured: bool,
}

struct Pending {
    id: u64,
    action: Option<WindowAction>,
}
struct Slot {
    commands: crate::commands::CommandBook<Pending, (u64, RequestState), 1>,
    observed: Option<(WindowState, Instant)>,
}
impl Default for Slot {
    fn default() -> Self {
        Self {
            commands: crate::commands::CommandBook::new(1),
            observed: None,
        }
    }
}

#[derive(Clone, Default)]
pub struct WindowControl(Arc<Mutex<Slot>>);

impl WindowControl {
    pub(crate) fn request(&self, action: WindowAction) -> Result<u64, &'static str> {
        let mut slot = self.0.lock().unwrap();
        if slot.commands.is_closed() {
            return Err("host_closed");
        }
        if slot.observed.is_none() {
            return Err("window_not_ready");
        }
        if let WindowAction::Resize { width, height } = action
            && (!(320..=3840).contains(&width) || !(240..=2160).contains(&height))
        {
            return Err("invalid_size");
        }
        let id = slot
            .commands
            .submit(0, |id| Pending {
                id,
                action: Some(action),
            })
            .map_err(|error| match error {
                crate::commands::SubmitError::IdExhausted => "request_id_exhausted",
                crate::commands::SubmitError::Closed => "host_closed",
                _ => "busy",
            })?;
        slot.commands.clear_history();
        Ok(id)
    }
    /// Latest request only. Older outcomes expire when another request is accepted.
    pub fn read(&self, id: u64) -> Result<RequestState, &'static str> {
        let slot = self.0.lock().unwrap();
        if slot.commands[0]
            .as_ref()
            .is_some_and(|pending| pending.id == id)
        {
            return Ok(RequestState::Pending);
        }
        slot.commands
            .history()
            .back()
            .filter(|(found, _)| *found == id)
            .map(|(_, state)| state.clone())
            .ok_or("unknown_or_expired_request")
    }
    pub fn state(&self) -> Option<(WindowState, std::time::Duration)> {
        self.0
            .lock()
            .unwrap()
            .observed
            .as_ref()
            .map(|(state, at)| (state.clone(), at.elapsed()))
    }
    /// Consume on the native event thread, independently of redraw/simulation.
    pub fn take_request(&self) -> Option<(u64, WindowAction)> {
        let mut slot = self.0.lock().unwrap();
        let pending = slot.commands[0].as_mut()?;
        pending.action.take().map(|action| (pending.id, action))
    }
    pub fn publish(&self, state: WindowState) {
        let mut slot = self.0.lock().unwrap();
        if !slot.commands.is_closed() {
            slot.observed = Some((state, Instant::now()));
        }
    }
    pub fn complete(&self, id: u64, result: Result<(), String>) {
        let mut slot = self.0.lock().unwrap();
        if slot.commands[0]
            .as_ref()
            .is_some_and(|pending| pending.id == id)
        {
            slot.commands[0] = None;
            slot.commands.record((
                id,
                match result {
                    Ok(()) => RequestState::Applied,
                    Err(error) => RequestState::Failed(error),
                },
            ));
        }
    }
    pub(crate) fn close(&self) {
        let mut slot = self.0.lock().unwrap();
        slot.commands.close();
        if let Some(pending) = slot.commands[0].take() {
            slot.commands
                .record((pending.id, RequestState::Failed("host_closed".into())));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn requests_wake_host_are_bounded_and_close_cancels_pending_work() {
        let (control, mut endpoint) = crate::control_channel();
        assert_eq!(
            control.request_window(WindowAction::Restore),
            Err("window_not_ready")
        );
        endpoint.window().publish(WindowState {
            physical_size: [800, 600],
            logical_size: [800., 600.],
            focused: false,
            minimized: Some(false),
            maximized: false,
            pointer_captured: false,
        });
        let wakes = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let counter = wakes.clone();
        endpoint.set_wakeup(move || {
            counter.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        });
        assert_eq!(
            control.request_window(WindowAction::Resize {
                width: 0,
                height: 600
            }),
            Err("invalid_size")
        );
        let id = control.request_window(WindowAction::Minimize).unwrap();
        assert_eq!(wakes.load(std::sync::atomic::Ordering::SeqCst), 2);
        assert_eq!(control.request_window(WindowAction::Restore), Err("busy"));
        assert_eq!(
            endpoint.window().take_request(),
            Some((id, WindowAction::Minimize))
        );
        assert_eq!(endpoint.window().take_request(), None);
        endpoint.window().complete(id, Ok(()));
        assert_eq!(control.window().read(id), Ok(RequestState::Applied));
        let next = control.request_window(WindowAction::Restore).unwrap();
        assert!(control.window().read(id).is_err());
        endpoint.window().complete(id, Ok(()));
        assert_eq!(control.window().read(next), Ok(RequestState::Pending));
        drop(endpoint);
        assert_eq!(
            control.window().read(next),
            Ok(RequestState::Failed("host_closed".into()))
        );
        assert_eq!(
            control.request_window(WindowAction::Restore),
            Err("host_closed")
        );
    }
}
