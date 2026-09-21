//! Bounded rendering commands. Only the host render owner applies mutations.
use crate::{commands::CommandBook, window::RequestState};
use std::{
    sync::{Arc, Mutex},
    time::{Duration, Instant},
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RenderMode {
    Auto,
    Cpu,
    Gpu,
}
/// Dependency-free render-only influence request. The render owner converts and
/// revalidates it before atomic publication; no gameplay world is modified.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RenderInfluence {
    pub id: u64,
    pub radial: bool,
    pub position: [f32; 3],
    pub direction: [f32; 3],
    pub radius: f32,
    pub strength: f32,
    pub start: f64,
    pub end: f64,
}
impl RenderInfluence {
    pub fn is_valid(&self) -> bool {
        let length_squared: f32 = self.direction.iter().map(|v| v * v).sum();
        self.position.iter().all(|v| v.is_finite())
            && length_squared.is_finite()
            && length_squared > 0.
            && self.radius.is_finite()
            && self.radius > 0.
            && (0. ..=1.).contains(&self.strength)
            && self.start.is_finite()
            && self.end.is_finite()
            && self.end > self.start
            && (self.end - self.start).is_finite()
    }
}
pub fn valid_influences(fields: &[RenderInfluence]) -> bool {
    fields.len() <= 256
        && fields.iter().enumerate().all(|(i, field)| {
            field.is_valid() && !fields[..i].iter().any(|other| other.id == field.id)
        })
}
#[derive(Clone, Debug, PartialEq)]
pub enum RenderAction {
    Mode(RenderMode),
    Paused(bool),
    Seek(f64),
    /// Some replaces all diagnostic fields; None restores scene-provided fields.
    Fields(Option<Vec<RenderInfluence>>),
}
/// Enabled backend limits, not adapter maxima or claimed universal support.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[cfg_attr(feature = "mcp", derive(serde::Serialize, serde::Deserialize))]
pub struct RenderingCapabilities {
    pub compute: bool,
    pub indexed_indirect: bool,
    pub vertex_storage: bool,
    pub asynchronous_readback: bool,
    pub max_buffer_size: u64,
    pub max_vertex_buffer_array_stride: u32,
    pub max_vertex_buffers: u32,
    pub max_vertex_attributes: u32,
    pub max_bind_groups: u32,
    pub max_storage_buffers_per_shader_stage: u32,
    pub max_storage_buffer_binding_size: u64,
    pub max_uniform_buffer_binding_size: u64,
    pub max_compute_workgroup_size_x: u32,
    pub max_compute_invocations_per_workgroup: u32,
    pub max_compute_workgroups_per_dimension: u32,
}
#[derive(Clone, Debug, PartialEq)]
pub struct RenderingState {
    /// Automatic GPU selection threshold per rendered segment; forced modes ignore it.
    pub auto_gpu_min_records: u64,
    pub mode: RenderMode,
    pub paused: bool,
    pub visual_seconds: f64,
    /// Whether the currently published scene supports the requested mode.
    pub cpu_supported: bool,
    pub gpu_supported: bool,
    pub cpu_rejection: Option<String>,
    pub gpu_rejection: Option<String>,
    pub capabilities: RenderingCapabilities,
    pub host_frame: u64,
    pub fields_overridden: bool,
    pub influence_count: u32,
}
struct Pending {
    id: u64,
    action: Option<RenderAction>,
}
struct Slot {
    commands: CommandBook<Pending, (u64, RequestState), 1>,
    observed: Option<(RenderingState, Instant)>,
}
impl Default for Slot {
    fn default() -> Self {
        Self {
            commands: CommandBook::new(16),
            observed: None,
        }
    }
}
#[derive(Clone, Default)]
pub struct RenderingControl(Arc<Mutex<Slot>>);

impl RenderingControl {
    pub(crate) fn request(&self, action: RenderAction) -> Result<u64, &'static str> {
        let mut slot = self.0.lock().unwrap();
        if slot.commands.is_closed() {
            return Err("host_closed");
        }
        let state = &slot.observed.as_ref().ok_or("rendering_not_ready")?.0;
        match &action {
            RenderAction::Seek(time)
                if !time.is_finite() || !(0. ..=1_000_000_000.).contains(time) =>
            {
                return Err("invalid_visual_time");
            }
            RenderAction::Mode(RenderMode::Gpu) if !state.gpu_supported => {
                return Err("gpu_mode_unsupported");
            }
            RenderAction::Mode(RenderMode::Cpu) if !state.cpu_supported => {
                return Err("cpu_mode_unsupported");
            }
            RenderAction::Fields(Some(fields)) if !valid_influences(fields) => {
                return Err("invalid_influences");
            }
            _ => {}
        }
        slot.commands
            .submit(0, |id| Pending {
                id,
                action: Some(action),
            })
            .map_err(|error| match error {
                crate::commands::SubmitError::IdExhausted => "request_id_exhausted",
                crate::commands::SubmitError::Closed => "host_closed",
                _ => "busy",
            })
    }
    pub fn state(&self) -> Option<(RenderingState, Duration)> {
        self.0
            .lock()
            .unwrap()
            .observed
            .as_ref()
            .map(|(s, t)| (s.clone(), t.elapsed()))
    }
    pub fn read(&self, id: u64) -> Result<RequestState, &'static str> {
        let slot = self.0.lock().unwrap();
        if slot.commands[0].as_ref().is_some_and(|p| p.id == id) {
            return Ok(RequestState::Pending);
        }
        slot.commands
            .history()
            .iter()
            .find(|(found, _)| *found == id)
            .map(|(_, s)| s.clone())
            .ok_or("unknown_or_expired_request")
    }
    /// Taking reserves the command slot until completion. Host must revalidate
    /// against its current scene; published capability information may be stale.
    pub fn take_request(&self) -> Option<(u64, RenderAction)> {
        let mut slot = self.0.lock().unwrap();
        let pending = slot.commands[0].as_mut()?;
        pending.action.take().map(|action| (pending.id, action))
    }
    pub fn publish(&self, mut state: RenderingState) {
        for value in [&mut state.cpu_rejection, &mut state.gpu_rejection]
            .into_iter()
            .flatten()
        {
            if let Some((end, _)) = value.char_indices().nth(512) {
                value.truncate(end);
            }
        }
        let mut slot = self.0.lock().unwrap();
        if !slot.commands.is_closed() {
            slot.observed = Some((state, Instant::now()));
        }
    }
    pub fn complete(&self, id: u64, result: Result<(), String>) {
        let mut slot = self.0.lock().unwrap();
        if slot.commands[0]
            .as_ref()
            .is_some_and(|p| p.id == id && p.action.is_none())
        {
            slot.commands[0] = None;
            // Bound failure text independently of backend error sizes.
            let state = match result {
                Ok(()) => RequestState::Applied,
                Err(error) => RequestState::Failed(error.chars().take(512).collect()),
            };
            slot.commands.record((id, state));
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
    fn capability_observations_are_owned_and_rejection_text_is_bounded() {
        let control = RenderingControl::default();
        let mut value = state();
        value.gpu_rejection = Some("界".repeat(600));
        value.capabilities.compute = true;
        value.capabilities.max_compute_workgroup_size_x = 64;
        value.capabilities.max_storage_buffer_binding_size = 128 * 1024 * 1024;
        control.publish(value);
        let (mut observed, _) = control.state().unwrap();
        assert_eq!(
            observed.gpu_rejection.as_ref().unwrap().chars().count(),
            512
        );
        assert!(!observed.gpu_supported);
        assert_eq!(observed.auto_gpu_min_records, 4096);
        assert!(observed.capabilities.compute);
        assert_eq!(observed.capabilities.max_compute_workgroup_size_x, 64);
        observed.gpu_rejection = None;
        assert!(control.state().unwrap().0.gpu_rejection.is_some());
    }
    #[test]
    fn invalid_fields_do_not_reserve_the_command_slot() {
        let control = RenderingControl::default();
        control.publish(state());
        let field = RenderInfluence {
            id: 1,
            radial: false,
            position: [0.; 3],
            direction: [1., 0., 0.],
            radius: 5.,
            strength: 1.,
            start: 0.,
            end: 10.,
        };
        assert_eq!(
            control.request(RenderAction::Fields(Some(vec![field, field]))),
            Err("invalid_influences")
        );
        let id = control
            .request(RenderAction::Fields(Some(vec![field])))
            .unwrap();
        assert_eq!(
            control.take_request(),
            Some((id, RenderAction::Fields(Some(vec![field]))))
        );
        assert_eq!(control.request(RenderAction::Fields(None)), Err("busy"));
        control.complete(id, Ok(()));
        assert_eq!(control.read(id), Ok(RequestState::Applied));
        assert!(control.request(RenderAction::Fields(None)).is_ok());
    }
    fn state() -> RenderingState {
        RenderingState {
            auto_gpu_min_records: 4096,
            mode: RenderMode::Auto,
            paused: false,
            visual_seconds: 0.,
            cpu_supported: true,
            gpu_supported: false,
            cpu_rejection: None,
            gpu_rejection: Some("test adapter has no GPU path".into()),
            capabilities: RenderingCapabilities::default(),
            host_frame: 1,
            fields_overridden: false,
            influence_count: 0,
        }
    }
    #[test]
    fn requests_are_reserved_until_completion_and_shutdown_finishes_taken_work() {
        let (control, mut host) = crate::control_channel();
        assert_eq!(
            control.request_rendering(RenderAction::Paused(true)),
            Err("rendering_not_ready")
        );
        let wakes = Arc::new(std::sync::atomic::AtomicUsize::new(0));
        let count = wakes.clone();
        host.set_wakeup(move || {
            count.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
        });
        host.rendering().publish(state());
        assert_eq!(
            control.request_rendering(RenderAction::Mode(RenderMode::Gpu)),
            Err("gpu_mode_unsupported")
        );
        assert_eq!(
            control.request_rendering(RenderAction::Seek(f64::NAN)),
            Err("invalid_visual_time")
        );
        let before = wakes.load(std::sync::atomic::Ordering::SeqCst);
        let id = control
            .request_rendering(RenderAction::Paused(true))
            .unwrap();
        assert_eq!(wakes.load(std::sync::atomic::Ordering::SeqCst), before + 1);
        host.rendering().complete(id, Ok(())); // Not taken: cannot claim application.
        assert_eq!(control.rendering().read(id), Ok(RequestState::Pending));
        assert_eq!(
            host.rendering().take_request(),
            Some((id, RenderAction::Paused(true)))
        );
        assert_eq!(host.rendering().take_request(), None);
        assert_eq!(
            control.request_rendering(RenderAction::Seek(2.)),
            Err("busy")
        );
        host.rendering().complete(id + 1, Ok(()));
        assert_eq!(control.rendering().read(id), Ok(RequestState::Pending));
        drop(host);
        assert_eq!(
            control.rendering().read(id),
            Ok(RequestState::Failed("host_closed".into()))
        );
        assert_eq!(
            control.request_rendering(RenderAction::Seek(1.)),
            Err("host_closed")
        );
    }
    #[test]
    fn terminal_history_and_failure_text_are_bounded() {
        let (control, host) = crate::control_channel();
        host.rendering().publish(state());
        for _ in 0..17 {
            let id = control.request_rendering(RenderAction::Seek(1.)).unwrap();
            host.rendering().take_request().unwrap();
            host.rendering().complete(id, Err("x".repeat(1000)));
        }
        assert_eq!(
            control.rendering().read(1),
            Err("unknown_or_expired_request")
        );
        assert_eq!(
            control.rendering().read(2),
            Ok(RequestState::Failed("x".repeat(512)))
        );
        assert_eq!(
            control.rendering().read(17),
            Ok(RequestState::Failed("x".repeat(512)))
        );
    }
}
