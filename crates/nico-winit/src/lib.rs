//! Winit-owned native client host for Nico applications.

use std::{
    collections::HashMap,
    error::Error,
    fs, io,
    path::{Path, PathBuf},
    sync::Arc,
    time::Duration,
};

use nico_input::{
    InputControlId, InputDeviceId, InputDeviceKind, InputEvent, InputManager, InputState,
};
use nico_presentation::{Presentation, RenderFrame};
use nico_render::BootstrapRenderPipeline;
use nico_rhi::{Extent3d, RhiSurface};
use nico_rhi_wgpu::{WgpuBackend, WgpuDevice};
use nico_runtime::{App, events::Event};
use winit::{
    application::ApplicationHandler,
    dpi::{LogicalSize, PhysicalSize},
    event::{DeviceEvent, ElementState, MouseButton, MouseScrollDelta, TouchPhase, WindowEvent},
    event_loop::{ActiveEventLoop, ControlFlow, EventLoop},
    keyboard::{KeyCode, PhysicalKey},
    window::{Window, WindowId},
};

/// Result returned by the native client host.
pub type NativeClientResult<T> = Result<T, Box<dyn Error + Send + Sync>>;
type InputDispatcher = dyn FnMut(&InputState, &mut App);

const FRAME_INTERVAL: Duration = Duration::from_nanos(16_666_667);
const POINTER_MOTION: InputControlId = InputControlId::new(1);
const POINTER_SCROLL: InputControlId = InputControlId::new(2);
const POINTER_POSITION: InputControlId = InputControlId::new(3);
/// Keyboard controls currently normalized by the Winit adapter.
pub mod keyboard {
    use nico_input::InputControlId;

    pub const W: InputControlId = InputControlId::new(1);
    pub const A: InputControlId = InputControlId::new(2);
    pub const S: InputControlId = InputControlId::new(3);
    pub const D: InputControlId = InputControlId::new(4);
    pub const UP: InputControlId = InputControlId::new(5);
    pub const LEFT: InputControlId = InputControlId::new(6);
    pub const DOWN: InputControlId = InputControlId::new(7);
    pub const RIGHT: InputControlId = InputControlId::new(8);
}

/// Configuration owned by the concrete native host.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct NativeClientConfig {
    title: String,
    bootstrap_shader_path: PathBuf,
    smoke_frames: Option<u64>,
}

impl NativeClientConfig {
    /// Creates native client configuration with an unbounded event loop.
    #[must_use]
    pub fn new(title: impl Into<String>, bootstrap_shader_path: impl Into<PathBuf>) -> Self {
        Self {
            title: title.into(),
            bootstrap_shader_path: bootstrap_shader_path.into(),
            smoke_frames: None,
        }
    }

    /// Sets an optional presented-frame limit for executable smoke checks.
    #[must_use]
    pub const fn with_smoke_frames(mut self, smoke_frames: Option<u64>) -> Self {
        self.smoke_frames = smoke_frames;
        self
    }
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum SessionState {
    Created,
    Running,
    Stopped,
}

struct ClientSession {
    app: App,
    presentation: Presentation,
    state: SessionState,
    presented_frames: u64,
}

impl ClientSession {
    fn new(app: App) -> Self {
        Self {
            app,
            presentation: Presentation::null(),
            state: SessionState::Created,
            presented_frames: 0,
        }
    }

    fn start(&mut self) -> NativeClientResult<()> {
        match self.state {
            SessionState::Running => return Ok(()),
            SessionState::Stopped => {
                return Err(io::Error::other("client session has already stopped").into());
            }
            SessionState::Created => {}
        }

        self.presentation.start()?;
        if let Err(error) = self.app.start() {
            let _ = self.presentation.shutdown();
            self.state = SessionState::Stopped;
            return Err(error.into());
        }
        self.state = SessionState::Running;
        Ok(())
    }

    fn present(&mut self, delta: Duration) -> NativeClientResult<()> {
        if self.state != SessionState::Running {
            return Err(io::Error::other("client session is not running").into());
        }

        self.app.tick(delta)?;
        self.presentation.present(
            self.app.world(),
            RenderFrame {
                frame_number: self.presented_frames,
                interpolation: 0.0,
            },
        )?;
        self.presented_frames = self.presented_frames.saturating_add(1);
        Ok(())
    }

    fn shutdown(&mut self) -> NativeClientResult<()> {
        match self.state {
            SessionState::Created => {
                self.state = SessionState::Stopped;
                return Ok(());
            }
            SessionState::Stopped => return Ok(()),
            SessionState::Running => {}
        }

        let runtime = self
            .app
            .shutdown()
            .map_err(|error| Box::new(error) as Box<dyn Error + Send + Sync>);
        let presentation = self
            .presentation
            .shutdown()
            .map_err(|error| Box::new(error) as Box<dyn Error + Send + Sync>);
        self.state = SessionState::Stopped;
        runtime.and(presentation)
    }
}

struct NativeClientHost {
    session: ClientSession,
    window: Option<Arc<Window>>,
    graphics: Option<WgpuBackend<Window>>,
    renderer: Option<BootstrapRenderPipeline<WgpuDevice>>,
    active: bool,
    focused: bool,
    size: PhysicalSize<u32>,
    last_frame: Option<std::time::Instant>,
    next_frame: Option<std::time::Instant>,
    smoke_frames: Option<u64>,
    failure: Option<Box<dyn Error + Send + Sync>>,
    input: InputManager,
    input_devices: HashMap<(InputDeviceKind, winit::event::DeviceId), InputDeviceId>,
    next_input_device: u64,
    title: String,
    bootstrap_shader_path: PathBuf,
    dispatch_input: Box<InputDispatcher>,
}

impl NativeClientHost {
    fn new(app: App, config: NativeClientConfig, dispatch_input: Box<InputDispatcher>) -> Self {
        Self {
            session: ClientSession::new(app),
            window: None,
            graphics: None,
            renderer: None,
            active: false,
            focused: false,
            size: PhysicalSize::new(0, 0),
            last_frame: None,
            next_frame: None,
            smoke_frames: config.smoke_frames,
            failure: None,
            input: InputManager::new(),
            input_devices: HashMap::new(),
            next_input_device: 1,
            title: config.title,
            bootstrap_shader_path: resolve_asset_path(&config.bootstrap_shader_path),
            dispatch_input,
        }
    }

    fn owns_window(&self, window_id: WindowId) -> bool {
        self.window
            .as_ref()
            .is_some_and(|window| window.id() == window_id)
    }

    fn stop(&mut self, event_loop: &ActiveEventLoop) {
        self.active = false;
        if let Err(error) = self.session.shutdown() {
            self.record_failure(error);
        }
        event_loop.exit();
    }

    fn fail(&mut self, event_loop: &ActiveEventLoop, error: impl Error + Send + Sync + 'static) {
        self.record_failure(Box::new(error));
        self.stop(event_loop);
    }

    fn record_failure(&mut self, error: Box<dyn Error + Send + Sync>) {
        if self.failure.is_none() {
            self.failure = Some(error);
        } else {
            tracing::error!(error = %error, "additional client host failure");
        }
    }

    fn finish(mut self) -> NativeClientResult<App> {
        if let Err(error) = self.session.shutdown() {
            self.record_failure(error);
        }
        if let Some(error) = self.failure {
            Err(error)
        } else {
            Ok(self.session.app)
        }
    }

    fn input_device(
        &mut self,
        kind: InputDeviceKind,
        provider: winit::event::DeviceId,
    ) -> InputDeviceId {
        if let Some(device) = self.input_devices.get(&(kind, provider)) {
            return *device;
        }

        let device = InputDeviceId::new(self.next_input_device);
        self.next_input_device = self
            .next_input_device
            .checked_add(1)
            .expect("input device identity exhausted");
        self.input_devices.insert((kind, provider), device);
        self.input.handle(InputEvent::Connected { device, kind });
        device
    }

    fn disconnect_provider_device(&mut self, provider: winit::event::DeviceId) {
        let devices = self
            .input_devices
            .iter()
            .filter_map(|((_, candidate), device)| (*candidate == provider).then_some(*device))
            .collect::<Vec<_>>();
        self.input_devices
            .retain(|(_, candidate), _| *candidate != provider);
        for device in devices {
            self.input.handle(InputEvent::Disconnected { device });
        }
    }

    fn map_keyboard(
        &mut self,
        provider: winit::event::DeviceId,
        physical_key: PhysicalKey,
        state: ElementState,
    ) {
        let Some(control) = keyboard_control(physical_key) else {
            return;
        };
        let device = self.input_device(InputDeviceKind::Keyboard, provider);
        self.input.handle(InputEvent::Button {
            device,
            control,
            pressed: state == ElementState::Pressed,
        });
    }

    fn map_pointer_button(
        &mut self,
        provider: winit::event::DeviceId,
        button: MouseButton,
        state: ElementState,
    ) {
        let device = self.input_device(InputDeviceKind::Pointer, provider);
        self.input.handle(InputEvent::Button {
            device,
            control: pointer_button_control(button),
            pressed: state == ElementState::Pressed,
        });
    }
}

fn resolve_asset_path(path: &Path) -> PathBuf {
    if path.is_absolute() {
        return path.to_owned();
    }
    if path.is_file() {
        return path.canonicalize().unwrap_or_else(|_| path.to_owned());
    }
    if let Ok(executable) = std::env::current_exe()
        && let Some(directory) = executable.parent()
    {
        for ancestor in directory.ancestors() {
            let candidate = ancestor.join(path);
            if candidate.is_file() {
                return candidate.canonicalize().unwrap_or(candidate);
            }
        }
    }
    path.to_owned()
}

fn keyboard_control(key: PhysicalKey) -> Option<InputControlId> {
    match key {
        PhysicalKey::Code(KeyCode::KeyW) => Some(keyboard::W),
        PhysicalKey::Code(KeyCode::KeyA) => Some(keyboard::A),
        PhysicalKey::Code(KeyCode::KeyS) => Some(keyboard::S),
        PhysicalKey::Code(KeyCode::KeyD) => Some(keyboard::D),
        PhysicalKey::Code(KeyCode::ArrowUp) => Some(keyboard::UP),
        PhysicalKey::Code(KeyCode::ArrowLeft) => Some(keyboard::LEFT),
        PhysicalKey::Code(KeyCode::ArrowDown) => Some(keyboard::DOWN),
        PhysicalKey::Code(KeyCode::ArrowRight) => Some(keyboard::RIGHT),
        _ => None,
    }
}

fn pointer_button_control(button: MouseButton) -> InputControlId {
    let value = match button {
        MouseButton::Left => 1,
        MouseButton::Right => 2,
        MouseButton::Middle => 3,
        MouseButton::Back => 4,
        MouseButton::Forward => 5,
        MouseButton::Other(value) => u64::from(value) + 16,
    };
    InputControlId::new(value)
}

impl ApplicationHandler for NativeClientHost {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.failure.is_some() {
            event_loop.exit();
            return;
        }

        if self.window.is_none() {
            let attributes = Window::default_attributes()
                .with_title(self.title.clone())
                .with_inner_size(LogicalSize::new(1280.0, 720.0));
            match event_loop.create_window(attributes) {
                Ok(window) => {
                    self.size = window.inner_size();
                    self.window = Some(Arc::new(window));
                }
                Err(error) => {
                    self.fail(event_loop, error);
                    return;
                }
            }
        }

        if self.graphics.is_none() {
            let window = self
                .window
                .as_ref()
                .expect("window exists after successful resume")
                .clone();
            let extent = Extent3d::surface(self.size.width, self.size.height);
            let shader_bytes = match fs::read(&self.bootstrap_shader_path) {
                Ok(shader_bytes) => shader_bytes,
                Err(error) => {
                    let path = self.bootstrap_shader_path.display();
                    self.fail(
                        event_loop,
                        io::Error::new(
                            error.kind(),
                            format!("failed to read bootstrap shader {path}: {error}"),
                        ),
                    );
                    return;
                }
            };
            match pollster::block_on(WgpuBackend::new(
                window,
                event_loop.owned_display_handle(),
                extent,
            )) {
                Ok(graphics) => {
                    let artifact = nico_rhi::builtin_shaders::bootstrap_wgsl(&shader_bytes);
                    match BootstrapRenderPipeline::new(
                        graphics.device(),
                        graphics.surface().format(),
                        artifact,
                    ) {
                        Ok(renderer) => {
                            self.graphics = Some(graphics);
                            self.renderer = Some(renderer);
                        }
                        Err(error) => {
                            self.fail(event_loop, error);
                            return;
                        }
                    }
                }
                Err(error) => {
                    self.fail(event_loop, error);
                    return;
                }
            }
        }

        if let Err(error) = self.session.start() {
            self.record_failure(error);
            self.stop(event_loop);
            return;
        }

        let now = std::time::Instant::now();
        self.active = true;
        self.last_frame = Some(now);
        self.next_frame = Some(now);
        if let Some(window) = &self.window {
            window.request_redraw();
        }
        tracing::info!(
            width = self.size.width,
            height = self.size.height,
            "client resumed"
        );
    }

    fn suspended(&mut self, event_loop: &ActiveEventLoop) {
        self.active = false;
        self.last_frame = None;
        self.next_frame = None;
        event_loop.set_control_flow(ControlFlow::Wait);
        tracing::info!("client suspended");
    }

    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        window_id: WindowId,
        event: WindowEvent,
    ) {
        if !self.owns_window(window_id) {
            return;
        }

        match event {
            WindowEvent::CloseRequested => self.stop(event_loop),
            WindowEvent::Resized(size) => {
                self.size = size;
                if let Some(graphics) = &mut self.graphics {
                    graphics.resize(Extent3d::surface(size.width, size.height));
                }
                tracing::debug!(width = size.width, height = size.height, "client resized");
            }
            WindowEvent::Focused(focused) => {
                self.focused = focused;
                if !focused {
                    self.input.release_all();
                }
                tracing::debug!(focused, "client focus changed");
            }
            WindowEvent::KeyboardInput {
                device_id, event, ..
            } if self.active => {
                self.map_keyboard(device_id, event.physical_key, event.state);
            }
            WindowEvent::MouseInput {
                device_id,
                state,
                button,
            } if self.active => self.map_pointer_button(device_id, button, state),
            WindowEvent::CursorMoved {
                device_id,
                position,
            } if self.active => {
                let device = self.input_device(InputDeviceKind::Pointer, device_id);
                self.input.handle(InputEvent::Vector {
                    device,
                    control: POINTER_POSITION,
                    value: [position.x as f32, position.y as f32],
                });
            }
            WindowEvent::MouseWheel {
                device_id, delta, ..
            } if self.active => {
                let delta = match delta {
                    MouseScrollDelta::LineDelta(x, y) => [x, y],
                    MouseScrollDelta::PixelDelta(position) => {
                        [position.x as f32, position.y as f32]
                    }
                };
                let device = self.input_device(InputDeviceKind::Pointer, device_id);
                self.input.handle(InputEvent::Motion {
                    device,
                    control: POINTER_SCROLL,
                    delta,
                });
            }
            WindowEvent::Touch(touch) if self.active => {
                let device = self.input_device(InputDeviceKind::Touch, touch.device_id);
                let control = InputControlId::new(touch.id);
                self.input.handle(InputEvent::Vector {
                    device,
                    control,
                    value: [touch.location.x as f32, touch.location.y as f32],
                });
                self.input.handle(InputEvent::Button {
                    device,
                    control,
                    pressed: matches!(touch.phase, TouchPhase::Started | TouchPhase::Moved),
                });
            }
            WindowEvent::RedrawRequested if self.active => {
                (self.dispatch_input)(self.input.state(), &mut self.session.app);
                self.input.end_frame();
                let now = std::time::Instant::now();
                let delta = self
                    .last_frame
                    .replace(now)
                    .map_or(Duration::ZERO, |last_frame| {
                        now.saturating_duration_since(last_frame)
                    });
                if let Err(error) = self.session.present(delta) {
                    self.record_failure(error);
                    self.stop(event_loop);
                    return;
                }
                if let (Some(graphics), Some(renderer)) = (&mut self.graphics, &mut self.renderer) {
                    let (device, queue, surface) = graphics.parts();
                    if let Err(error) = renderer.render(device, queue, surface) {
                        self.fail(event_loop, error);
                        return;
                    }
                }
                self.next_frame = now.checked_add(FRAME_INTERVAL);

                if self.session.app.exit_requested()
                    || self
                        .smoke_frames
                        .is_some_and(|limit| self.session.presented_frames >= limit)
                {
                    self.stop(event_loop);
                }
            }
            _ => {}
        }
    }

    fn device_event(
        &mut self,
        _event_loop: &ActiveEventLoop,
        device_id: winit::event::DeviceId,
        event: DeviceEvent,
    ) {
        match event {
            DeviceEvent::MouseMotion { delta } if self.active => {
                let device = self.input_device(InputDeviceKind::Pointer, device_id);
                self.input.handle(InputEvent::Motion {
                    device,
                    control: POINTER_MOTION,
                    delta: [delta.0 as f32, delta.1 as f32],
                });
            }
            DeviceEvent::Removed => self.disconnect_provider_device(device_id),
            _ => {}
        }
    }

    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        if !self.active {
            event_loop.set_control_flow(ControlFlow::Wait);
            return;
        }

        let now = std::time::Instant::now();
        match self.next_frame {
            Some(deadline) if now < deadline => {
                event_loop.set_control_flow(ControlFlow::WaitUntil(deadline));
            }
            _ => {
                if let Some(window) = &self.window {
                    window.request_redraw();
                }
                event_loop.set_control_flow(ControlFlow::Wait);
            }
        }
    }

    fn exiting(&mut self, _event_loop: &ActiveEventLoop) {
        self.active = false;
        if let Err(error) = self.session.shutdown() {
            self.record_failure(error);
        }
    }
}

/// Runs an application on Winit's native event loop.
///
/// `map_input` converts normalized engine input into game-owned runtime events
/// immediately before each application tick.
pub fn run_native_client<C: Event>(
    app: App,
    config: NativeClientConfig,
    mut map_input: impl FnMut(&InputState, &mut Vec<C>) + 'static,
) -> NativeClientResult<App> {
    let mut commands = Vec::new();
    let dispatch_input = move |input: &InputState, app: &mut App| {
        commands.clear();
        map_input(input, &mut commands);
        for command in commands.drain(..) {
            app.send_event(command);
        }
    };
    let event_loop = EventLoop::new()?;
    event_loop.set_control_flow(ControlFlow::Wait);
    let mut host = NativeClientHost::new(app, config, Box::new(dispatch_input));
    let event_loop_result = event_loop.run_app(&mut host);
    if let Err(error) = event_loop_result {
        host.record_failure(Box::new(error));
    }
    host.finish()
}

#[cfg(test)]
mod tests {
    use std::{path::PathBuf, time::Duration};

    use nico_runtime::{AppBuilder, AppState};
    use winit::keyboard::{KeyCode, PhysicalKey};

    use super::{ClientSession, NativeClientConfig, SessionState, keyboard, keyboard_control};

    #[test]
    fn client_session_starts_ticks_and_shuts_down_once()
    -> Result<(), Box<dyn std::error::Error + Send + Sync>> {
        let app = AppBuilder::new().build()?;
        let mut session = ClientSession::new(app);

        session.start()?;
        session.start()?;
        session.present(Duration::from_nanos(16_666_667))?;
        session.present(Duration::from_nanos(16_666_667))?;
        session.shutdown()?;
        session.shutdown()?;

        assert_eq!(session.state, SessionState::Stopped);
        assert_eq!(session.app.state(), AppState::Stopped);
        assert_eq!(session.presented_frames, 2);
        Ok(())
    }

    #[test]
    fn keyboard_adapter_uses_stable_engine_control_ids() {
        assert_eq!(
            keyboard_control(PhysicalKey::Code(KeyCode::KeyW)),
            Some(keyboard::W)
        );
        assert_eq!(
            keyboard_control(PhysicalKey::Code(KeyCode::ArrowRight)),
            Some(keyboard::RIGHT)
        );
        assert_eq!(keyboard_control(PhysicalKey::Code(KeyCode::Escape)), None);
    }

    #[test]
    fn native_client_configuration_keeps_game_owned_title_and_smoke_policy() {
        let config =
            NativeClientConfig::new("Example", "bootstrap.wgsl").with_smoke_frames(Some(3));

        assert_eq!(config.title, "Example");
        assert_eq!(
            config.bootstrap_shader_path,
            PathBuf::from("bootstrap.wgsl")
        );
        assert_eq!(config.smoke_frames, Some(3));
    }
}
