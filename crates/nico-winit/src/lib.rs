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
use nico_ops::{GraphicsOutcome, HostEndpoint};
use nico_presentation::{Presentation, RenderFrame};
use nico_render::{BootstrapRenderPipeline, MeshRenderPipeline, QuadRenderPipeline, RenderStatus};
use nico_rhi::{Extent3d, RhiSurface};
use nico_rhi_wgpu::{WgpuBackend, WgpuDevice};
use nico_runtime::{App, events::Event};
use winit::{
    application::ApplicationHandler,
    dpi::{LogicalSize, PhysicalSize},
    event::{DeviceEvent, ElementState, MouseButton, MouseScrollDelta, TouchPhase, WindowEvent},
    event_loop::{ActiveEventLoop, ControlFlow, EventLoop},
    keyboard::{KeyCode, PhysicalKey},
    window::{CursorGrabMode, Window, WindowId},
};

/// Result returned by the native client host.
pub type NativeClientResult<T> = Result<T, Box<dyn Error + Send + Sync>>;
type InputDispatcher = dyn FnMut(&InputState, &mut App);

const FRAME_INTERVAL: Duration = Duration::from_nanos(16_666_667);
const POINTER_MOTION: InputControlId = InputControlId::new(1);
const POINTER_SCROLL: InputControlId = InputControlId::new(2);
const POINTER_POSITION: InputControlId = InputControlId::new(3);
/// Normalized pointer controls; motion is relative device motion in pixels.
pub mod pointer {
    use nico_input::InputControlId;
    pub const LEFT: InputControlId = InputControlId::new(1);
    pub const MOTION: InputControlId = InputControlId::new(1);
}

/// Host-owned window snapshot, refreshed before dispatching input. Coordinates are
/// logical pixels. Capture success is separate from focus and requests.
#[derive(Clone, Debug)]
pub struct NativeWindowState {
    pub focused: bool,
    pub pointer_captured: bool,
    pub logical_size: [f32; 2],
    pub capture_error: Option<String>,
}
impl Default for NativeWindowState {
    fn default() -> Self {
        Self {
            focused: false,
            pointer_captured: false,
            logical_size: [1280.0, 720.0],
            capture_error: None,
        }
    }
}
/// Delivered even when focus is lost and regained between simulation ticks.
#[derive(Clone, Copy, Debug)]
pub struct WindowFocusLost;
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
    pub const SPACE: InputControlId = InputControlId::new(9);
    pub const R: InputControlId = InputControlId::new(10);
    pub const ESCAPE: InputControlId = InputControlId::new(11);
    pub const E: InputControlId = InputControlId::new(12);
    pub const F: InputControlId = InputControlId::new(13);
    pub const Q: InputControlId = InputControlId::new(14);
}

/// Configuration owned by the concrete native host.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct NativeClientConfig {
    title: String,
    bootstrap_shader_path: PathBuf,
    smoke_frames: Option<u64>,
    quad_rendering: bool,
    mesh_shader_path: Option<PathBuf>,
    skin_shader_path: Option<PathBuf>,
    pointer_capture: bool,
    initially_active: bool,
}

impl NativeClientConfig {
    /// Creates native client configuration with an unbounded event loop.
    #[must_use]
    pub fn new(title: impl Into<String>, bootstrap_shader_path: impl Into<PathBuf>) -> Self {
        Self {
            title: title.into(),
            bootstrap_shader_path: bootstrap_shader_path.into(),
            smoke_frames: None,
            quad_rendering: false,
            mesh_shader_path: None,
            skin_shader_path: None,
            pointer_capture: false,
            initially_active: true,
        }
    }

    /// Sets an optional session-frame limit, including skipped GPU presentations.
    #[must_use]
    pub const fn with_smoke_frames(mut self, smoke_frames: Option<u64>) -> Self {
        self.smoke_frames = smoke_frames;
        self
    }

    /// Selects the shared world/HUD quad pipeline with an externally compiled shader.
    #[must_use]
    pub fn with_quad_shader(mut self, path: impl Into<PathBuf>) -> Self {
        self.bootstrap_shader_path = path.into();
        self.quad_rendering = true;
        self.mesh_shader_path = None;
        self.skin_shader_path = None;
        self
    }
    /// Selects perspective meshes with a shared quad/HUD overlay.
    #[must_use]
    pub fn with_mesh_shaders(mut self, mesh: impl Into<PathBuf>, hud: impl Into<PathBuf>) -> Self {
        self.bootstrap_shader_path = hud.into();
        self.quad_rendering = true;
        self.mesh_shader_path = Some(mesh.into());
        self
    }
    /// Enables GPU skinning alongside the mesh pipeline.
    #[must_use]
    pub fn with_skin_shader(mut self, shader: impl Into<PathBuf>) -> Self {
        self.skin_shader_path = Some(shader.into());
        self
    }
    /// Enables click-to-capture and Escape-to-release. Capture clicks are consumed.
    #[must_use]
    pub const fn with_pointer_capture(mut self) -> Self {
        self.pointer_capture = true;
        self
    }
    /// Controls whether window creation requests focus. Useful for isolated tests.
    #[must_use]
    pub const fn with_initial_focus(mut self, active: bool) -> Self {
        self.initially_active = active;
        self
    }
}

enum NativeRenderer {
    Bootstrap(BootstrapRenderPipeline<WgpuDevice>),
    Quads(QuadRenderPipeline<WgpuDevice>),
    Meshes(Box<MeshRenderPipeline<WgpuDevice>>),
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
    renderer: Option<NativeRenderer>,
    quad_rendering: bool,
    mesh_shader_path: Option<PathBuf>,
    skin_shader_path: Option<PathBuf>,
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
    operations: Option<HostEndpoint>,
    pointer_capture: bool,
    window_state: NativeWindowState,
    initially_active: bool,
}

fn capture_allowed(enabled: bool, active: bool, focused: bool) -> Result<(), String> {
    if !enabled {
        Err("pointer_capture_disabled".into())
    } else if !active {
        Err("window_inactive".into())
    } else if !focused {
        Err("window_not_focused".into())
    } else {
        Ok(())
    }
}

impl NativeClientHost {
    fn new(mut app: App, config: NativeClientConfig, dispatch_input: Box<InputDispatcher>) -> Self {
        app.world_mut()
            .insert_resource(NativeWindowState::default());
        Self {
            session: ClientSession::new(app),
            window: None,
            graphics: None,
            renderer: None,
            quad_rendering: config.quad_rendering,
            skin_shader_path: config.skin_shader_path,
            mesh_shader_path: config
                .mesh_shader_path
                .map(|path| resolve_asset_path(&path)),
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
            operations: None,
            pointer_capture: config.pointer_capture,
            window_state: NativeWindowState::default(),
            initially_active: config.initially_active,
        }
    }

    fn capture_pointer(&mut self, capture: bool) -> Result<(), String> {
        let Some(window) = &self.window else {
            return Err("window_unavailable".into());
        };
        if capture {
            capture_allowed(self.pointer_capture, self.active, self.focused)?;
        }
        let desired = capture;
        let result = if desired {
            window
                .set_cursor_grab(CursorGrabMode::Locked)
                .or_else(|_| window.set_cursor_grab(CursorGrabMode::Confined))
        } else {
            window.set_cursor_grab(CursorGrabMode::None)
        };
        self.window_state.capture_error = result.as_ref().err().map(ToString::to_string);
        self.window_state.pointer_captured = desired && result.is_ok();
        window.set_cursor_visible(!self.window_state.pointer_captured);
        if let Err(error) = &result {
            tracing::warn!(%error, "pointer capture request failed");
        }
        result.map_err(|error| error.to_string())
    }
    fn publish_window(&mut self) {
        self.window_state.focused = self.focused;
        if let Some(window) = &self.window {
            let logical = self.size.to_logical::<f32>(window.scale_factor());
            self.window_state.logical_size = [logical.width, logical.height];
        }
        self.session
            .app
            .world_mut()
            .insert_resource(self.window_state.clone());
    }

    fn owns_window(&self, window_id: WindowId) -> bool {
        self.window
            .as_ref()
            .is_some_and(|window| window.id() == window_id)
    }

    // Shared by the Winit callback and lifecycle regression tests. No tick runs here.
    fn suspend_session(&mut self) {
        let _ = self.capture_pointer(false);
        self.input.release_all();
        self.session.app.send_event(WindowFocusLost);
        self.active = false;
        self.last_frame = None;
        if let Some(operations) = &mut self.operations {
            operations.activity(false);
        }
        self.next_frame = None;
    }

    fn stop(&mut self, event_loop: &ActiveEventLoop) {
        self.shutdown();
        event_loop.exit();
    }

    fn shutdown(&mut self) {
        let _ = self.capture_pointer(false);
        self.active = false;
        if let Some(operations) = &mut self.operations {
            operations.stopping();
        }
        if let Err(error) = self.session.shutdown() {
            self.record_failure(error);
        }
    }

    // Called on the host thread, independently of redraws and simulation ticks.
    fn poll_operations(&mut self) -> bool {
        if self.session.state == SessionState::Stopped {
            return true;
        }
        if self
            .operations
            .as_mut()
            .is_some_and(HostEndpoint::stop_requested)
        {
            self.shutdown();
            return true;
        }
        self.poll_window_operations();
        false
    }

    fn poll_window_operations(&mut self) {
        let Some(control) = self.operations.as_ref().map(HostEndpoint::window) else {
            return;
        };
        if let Some((id, action)) = control.take_request() {
            if let nico_ops::window::WindowAction::PointerCapture { value } = action {
                let result = self.capture_pointer(value);
                control.complete(id, result);
            } else if let Some(window) = &self.window {
                use nico_ops::window::WindowAction;
                match action {
                    WindowAction::Resize { width, height } => {
                        let _ = window.request_inner_size(LogicalSize::new(width, height));
                    }
                    WindowAction::Maximize => window.set_maximized(true),
                    WindowAction::Minimize => window.set_minimized(true),
                    WindowAction::Restore => {
                        window.set_minimized(false);
                        window.set_maximized(false);
                    }
                    WindowAction::Focus => window.focus_window(),
                    WindowAction::PointerCapture { .. } => unreachable!(),
                }
                control.complete(id, Ok(()));
            } else {
                control.complete(id, Err("window_unavailable".into()));
            }
        }
        if let Some(window) = &self.window {
            let size = window.inner_size();
            let logical = size.to_logical::<f64>(window.scale_factor());
            self.publish_observed_window(nico_ops::window::WindowState {
                physical_size: [size.width, size.height],
                logical_size: [logical.width, logical.height],
                focused: window.has_focus(),
                minimized: window.is_minimized(),
                maximized: window.is_maximized(),
                pointer_captured: self.window_state.pointer_captured,
            });
        }
    }

    fn publish_observed_window(&self, mut state: nico_ops::window::WindowState) {
        // The runtime mirror may predate focus loss or suspension by many frames.
        state.pointer_captured = self.window_state.pointer_captured;
        if let Some(operations) = &self.operations {
            operations.window().publish(state);
        }
    }

    fn report_render(&mut self, status: RenderStatus) {
        if let Some(operations) = &mut self.operations {
            operations.graphics(match status {
                RenderStatus::Presented => GraphicsOutcome::Presented,
                RenderStatus::ZeroSized => GraphicsOutcome::ZeroSized,
                RenderStatus::Timeout => GraphicsOutcome::Timeout,
                RenderStatus::Occluded => GraphicsOutcome::Occluded,
            });
            if status == RenderStatus::Presented {
                operations.running(self.session.presented_frames);
            }
        }
    }

    fn report_graphics_failure(&mut self, outcome: GraphicsOutcome) {
        if let Some(operations) = &mut self.operations {
            operations.graphics(outcome);
        }
    }

    fn present(&mut self, delta: Duration) -> NativeClientResult<()> {
        self.session.present(delta)?;
        if let Some(operations) = &mut self.operations {
            operations.progress(self.session.presented_frames);
        }
        Ok(())
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
        self.shutdown();
        if let Some(operations) = self.operations.take() {
            operations.finish(
                self.failure
                    .as_ref()
                    .map_or(Ok(()), |error| Err(error.to_string())),
            );
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
        PhysicalKey::Code(KeyCode::Space) => Some(keyboard::SPACE),
        PhysicalKey::Code(KeyCode::KeyR) => Some(keyboard::R),
        PhysicalKey::Code(KeyCode::KeyE) => Some(keyboard::E),
        PhysicalKey::Code(KeyCode::KeyF) => Some(keyboard::F),
        PhysicalKey::Code(KeyCode::KeyQ) => Some(keyboard::Q),
        PhysicalKey::Code(KeyCode::Escape) => Some(keyboard::ESCAPE),
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

fn capture_click(enabled: bool, captured: bool, button: MouseButton, state: ElementState) -> bool {
    enabled && !captured && button == MouseButton::Left && state == ElementState::Pressed
}

#[derive(Debug)]
enum HostEvent {
    Control,
}

impl ApplicationHandler<HostEvent> for NativeClientHost {
    fn user_event(&mut self, event_loop: &ActiveEventLoop, _event: HostEvent) {
        if self.poll_operations() {
            event_loop.exit();
        }
    }

    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        if self.poll_operations() {
            event_loop.exit();
            return;
        }
        if self.failure.is_some() {
            event_loop.exit();
            return;
        }

        if self.window.is_none() {
            let attributes = Window::default_attributes()
                .with_title(self.title.clone())
                .with_active(self.initially_active)
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
                    self.report_graphics_failure(GraphicsOutcome::InitializationFailed);
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
                    let renderer = if let Some(path) = &self.mesh_shader_path {
                        match fs::read(path) {
                            Ok(bytes) => MeshRenderPipeline::new(
                                graphics.device(),
                                graphics.surface().format(),
                                nico_rhi::builtin_shaders::bootstrap_wgsl(&bytes),
                                artifact,
                            )
                            .and_then(|mut renderer| {
                                if let Some(path) = &self.skin_shader_path {
                                    let bytes = fs::read(path).map_err(|error| {
                                        nico_rhi::RhiError::new(
                                            nico_rhi::RhiErrorKind::Backend,
                                            format!(
                                                "failed to read skin shader {}: {error}",
                                                path.display()
                                            ),
                                        )
                                    })?;
                                    renderer.enable_skinning(
                                        graphics.device(),
                                        nico_rhi::builtin_shaders::bootstrap_wgsl(&bytes),
                                    )?;
                                }
                                Ok(NativeRenderer::Meshes(Box::new(renderer)))
                            }),
                            Err(error) => Err(nico_rhi::RhiError::new(
                                nico_rhi::RhiErrorKind::Backend,
                                format!("failed to read mesh shader {}: {error}", path.display()),
                            )),
                        }
                    } else if self.quad_rendering {
                        QuadRenderPipeline::new(
                            graphics.device(),
                            graphics.surface().format(),
                            artifact,
                        )
                        .map(NativeRenderer::Quads)
                    } else {
                        BootstrapRenderPipeline::new(
                            graphics.device(),
                            graphics.surface().format(),
                            artifact,
                        )
                        .map(NativeRenderer::Bootstrap)
                    };
                    match renderer {
                        Ok(renderer) => {
                            self.graphics = Some(graphics);
                            self.renderer = Some(renderer);
                        }
                        Err(error) => {
                            self.report_graphics_failure(GraphicsOutcome::InitializationFailed);
                            self.fail(event_loop, error);
                            return;
                        }
                    }
                }
                Err(error) => {
                    self.report_graphics_failure(GraphicsOutcome::InitializationFailed);
                    self.fail(event_loop, error);
                    return;
                }
            }
        }

        if self.poll_operations() {
            event_loop.exit();
            return;
        }
        if let Err(error) = self.session.start() {
            self.record_failure(error);
            self.stop(event_loop);
            return;
        }

        let now = std::time::Instant::now();
        self.active = true;
        if let Some(operations) = &mut self.operations {
            operations.activity(true);
        }
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
        if self.poll_operations() {
            event_loop.exit();
            return;
        }
        self.suspend_session();
        event_loop.set_control_flow(ControlFlow::Wait);
        tracing::info!("client suspended");
    }

    fn window_event(
        &mut self,
        event_loop: &ActiveEventLoop,
        window_id: WindowId,
        event: WindowEvent,
    ) {
        if self.poll_operations() {
            event_loop.exit();
            return;
        }
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
                    let _ = self.capture_pointer(false);
                    self.session.app.send_event(WindowFocusLost);
                }
                tracing::debug!(focused, "client focus changed");
            }
            WindowEvent::KeyboardInput {
                device_id, event, ..
            } if self.active => {
                if self.pointer_capture
                    && event.physical_key == PhysicalKey::Code(KeyCode::Escape)
                    && event.state == ElementState::Pressed
                {
                    let _ = self.capture_pointer(false);
                }
                self.map_keyboard(device_id, event.physical_key, event.state);
            }
            WindowEvent::MouseInput {
                device_id,
                state,
                button,
            } if self.active => {
                if capture_click(
                    self.pointer_capture,
                    self.window_state.pointer_captured,
                    button,
                    state,
                ) {
                    let _ = self.capture_pointer(true);
                } else {
                    self.map_pointer_button(device_id, button, state);
                }
            }
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
                self.publish_window();
                (self.dispatch_input)(self.input.state(), &mut self.session.app);
                self.input.end_frame();
                let now = std::time::Instant::now();
                let delta = self
                    .last_frame
                    .replace(now)
                    .map_or(Duration::ZERO, |last_frame| {
                        now.saturating_duration_since(last_frame)
                    });
                if let Err(error) = self.present(delta) {
                    self.record_failure(error);
                    self.stop(event_loop);
                    return;
                }
                if let (Some(graphics), Some(renderer)) = (&mut self.graphics, &mut self.renderer) {
                    let snapshot = self.operations.as_ref().map(|host| host.snapshots());
                    let request = snapshot.as_ref().and_then(|slot| slot.take_request());
                    let (device, queue, surface) = graphics.parts();
                    if request.is_some() {
                        surface.request_snapshot();
                    }
                    let scale = self
                        .window
                        .as_ref()
                        .map_or(1.0, |window| window.scale_factor())
                        as f32;
                    let viewport = [
                        self.size.width as f32 / scale,
                        self.size.height as f32 / scale,
                    ];
                    let rendered = match renderer {
                        NativeRenderer::Bootstrap(renderer) => {
                            renderer.render(device, queue, surface)
                        }
                        NativeRenderer::Meshes(renderer) => renderer.render(
                            device,
                            queue,
                            surface,
                            self.session.presentation.scene3d(),
                            self.session.presentation.scene(),
                            viewport,
                            Extent3d::surface(self.size.width, self.size.height),
                        ),
                        NativeRenderer::Quads(renderer) => renderer.render(
                            device,
                            queue,
                            surface,
                            self.session.presentation.scene(),
                            viewport,
                        ),
                    };
                    if let Some(id) = request {
                        snapshot.as_ref().unwrap().complete(
                            id,
                            surface
                                .take_snapshot()
                                .map(|pixels| nico_ops::snapshot::Pixels {
                                    width: pixels.width,
                                    height: pixels.height,
                                    rgba: pixels.rgba,
                                }),
                        );
                    }
                    match rendered {
                        Ok(status) => self.report_render(status),
                        Err(error) => {
                            self.report_graphics_failure(GraphicsOutcome::RenderFailed);
                            self.fail(event_loop, error);
                            return;
                        }
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
            DeviceEvent::MouseMotion { delta }
                if self.active
                    && self.focused
                    && (!self.pointer_capture || self.window_state.pointer_captured) =>
            {
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
        if self.poll_operations() {
            event_loop.exit();
            return;
        }
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
        self.shutdown();
    }
}

/// Runs an application on Winit's native event loop.
///
/// `map_input` converts normalized engine input into game-owned runtime events
/// immediately before each application tick.
pub fn run_native_client<C: Event>(
    app: App,
    config: NativeClientConfig,
    map_input: impl FnMut(&InputState, &mut Vec<C>) + 'static,
) -> NativeClientResult<App> {
    run_client(app, config, map_input, None)
}

/// Runs a native client with in-process status and orderly-stop control.
///
/// Readiness requires App startup and the first `RenderStatus::Presented` result.
/// `completed_steps` counts session frames, including skipped GPU presentations;
/// it can advance before readiness. Readiness remains latched while suspended,
/// until shutdown begins; `active` is false while the host is suspended.
/// Stop and last-controller disconnect wake the event loop even without redraws.
/// Shutdown executes on the host thread and cannot interrupt a blocked callback.
/// Final status remains readable after return and does not imply process exit.
pub fn run_native_client_with_operations<C: Event>(
    app: App,
    config: NativeClientConfig,
    map_input: impl FnMut(&InputState, &mut Vec<C>) + 'static,
    operations: HostEndpoint,
) -> NativeClientResult<App> {
    run_client(app, config, map_input, Some(operations))
}

fn run_client<C: Event>(
    app: App,
    config: NativeClientConfig,
    mut map_input: impl FnMut(&InputState, &mut Vec<C>) + 'static,
    operations: Option<HostEndpoint>,
) -> NativeClientResult<App> {
    let mut commands = Vec::new();
    let dispatch_input = move |input: &InputState, app: &mut App| {
        commands.clear();
        map_input(input, &mut commands);
        for command in commands.drain(..) {
            app.send_event(command);
        }
    };
    let mut host = NativeClientHost::new(app, config, Box::new(dispatch_input));
    host.operations = operations;
    if let Some(operations) = &mut host.operations {
        operations.graphics(GraphicsOutcome::NotAttempted);
    }
    let event_loop = match EventLoop::<HostEvent>::with_user_event().build() {
        Ok(event_loop) => event_loop,
        Err(error) => {
            host.record_failure(Box::new(error));
            return host.finish();
        }
    };
    event_loop.set_control_flow(ControlFlow::Wait);
    if let Some(operations) = &mut host.operations {
        let proxy = event_loop.create_proxy();
        operations.set_wakeup(move || {
            // Closure racing with loop exit is harmless: the stop stays latched.
            let _ = proxy.send_event(HostEvent::Control);
        });
    }
    let event_loop_result = event_loop.run_app(&mut host);
    if let Err(error) = event_loop_result {
        host.record_failure(Box::new(error));
    }
    host.finish()
}

#[cfg(test)]
mod tests {
    use std::{path::PathBuf, sync::mpsc, thread, time::Duration};

    use nico_ops::GraphicsOutcome;
    use nico_ops::{HostState, control_channel};
    use nico_render::RenderStatus;
    use nico_runtime::{App, AppBuilder, AppState, RuntimeError, Stage};
    use winit::keyboard::{KeyCode, PhysicalKey};

    use super::{
        ClientSession, NativeClientConfig, NativeClientHost, NativeClientResult, SessionState,
        keyboard, keyboard_control,
    };

    fn test_host(app: App) -> NativeClientHost {
        NativeClientHost::new(
            app,
            NativeClientConfig::new("test", "unused.wgsl"),
            Box::new(|_, _| {}),
        )
    }

    #[test]
    fn inactive_host_processes_capture_requests_without_a_runtime_tick() {
        let (control, operations) = control_channel();
        let mut host = test_host(AppBuilder::new().build().unwrap());
        host.operations = Some(operations);
        host.session
            .app
            .world_mut()
            .insert_resource(super::NativeWindowState {
                pointer_captured: true,
                ..Default::default()
            });
        host.publish_observed_window(nico_ops::window::WindowState {
            physical_size: [800, 600],
            logical_size: [800., 600.],
            focused: false,
            minimized: Some(true),
            maximized: false,
            pointer_captured: true,
        });
        assert!(!control.window().state().unwrap().0.pointer_captured);
        let id = control
            .request_window(nico_ops::window::WindowAction::PointerCapture { value: false })
            .unwrap();
        assert!(!host.active);
        assert!(!host.poll_operations());
        assert_eq!(
            control.window().read(id),
            Ok(nico_ops::window::RequestState::Failed(
                "window_unavailable".into()
            ))
        );
    }

    #[test]
    fn capture_requires_policy_activity_and_focus() {
        assert_eq!(
            super::capture_allowed(false, true, true),
            Err("pointer_capture_disabled".into())
        );
        assert_eq!(
            super::capture_allowed(true, false, true),
            Err("window_inactive".into())
        );
        assert_eq!(
            super::capture_allowed(true, true, false),
            Err("window_not_focused".into())
        );
        assert_eq!(super::capture_allowed(true, true, true), Ok(()));
    }

    #[test]
    fn graphics_failure_does_not_count_a_presentation_or_claim_readiness() -> NativeClientResult<()>
    {
        for outcome in [
            GraphicsOutcome::InitializationFailed,
            GraphicsOutcome::RenderFailed,
        ] {
            let (control, operations) = control_channel();
            let mut host = test_host(AppBuilder::new().build()?);
            host.operations = Some(operations);
            host.report_graphics_failure(outcome);
            host.record_failure(Box::new(std::io::Error::other("graphics failed")));
            assert!(host.finish().is_err());
            let status = control.status();
            assert_eq!(status.graphics.unwrap().presented_frames, 0);
            assert_eq!(status.graphics.unwrap().last_outcome, outcome);
            assert_eq!(status.state, HostState::Failed);
        }
        Ok(())
    }

    #[test]
    fn client_readiness_requires_gpu_presentation_and_counts_skipped_session_frames()
    -> NativeClientResult<()> {
        let (control, operations) = control_channel();
        let mut host = test_host(AppBuilder::new().build()?);
        host.operations = Some(operations);
        host.session.start()?;
        for status in [
            RenderStatus::ZeroSized,
            RenderStatus::Timeout,
            RenderStatus::Occluded,
        ] {
            host.present(Duration::from_millis(17))?;
            host.report_render(status);
            assert_eq!(control.status().state, HostState::Starting);
        }
        assert_eq!(control.status().completed_steps, 3);
        assert_eq!(control.status().graphics.unwrap().presented_frames, 0);
        host.present(Duration::from_millis(17))?;
        host.report_render(RenderStatus::Presented);
        assert!(control.status().is_ready());
        host.present(Duration::from_millis(17))?;
        host.report_render(RenderStatus::Timeout);
        assert!(control.status().is_ready());
        assert_eq!(control.status().completed_steps, 5);
        let graphics = control.status().graphics.unwrap();
        assert_eq!(graphics.presented_frames, 1);
        assert_eq!(graphics.last_outcome, GraphicsOutcome::Timeout);
        host.finish()?;
        assert_eq!(control.status().graphics, Some(graphics));
        assert_eq!(control.status().state, HostState::Stopped);
        Ok(())
    }

    #[test]
    fn suspended_client_stops_on_wakeup_without_another_tick() -> NativeClientResult<()> {
        for disconnected in [false, true] {
            let (control, mut operations) = control_channel();
            let (sender, wakes) = mpsc::channel();
            operations.set_wakeup(move || {
                let _ = sender.send(());
            });
            wakes.recv_timeout(Duration::from_secs(3))?;
            let mut builder = AppBuilder::new();
            builder.insert_resource(0_u32);
            builder.add_system(Stage::Update, "must not tick", |_| {
                panic!("control must not tick")
            });
            builder.add_system(Stage::Shutdown, "count shutdown", |context| {
                *context.world.resource_mut::<u32>()? += 1;
                Ok(())
            });
            let mut host = test_host(builder.build()?);
            host.operations = Some(operations);
            host.session.start()?;
            host.active = true;
            host.last_frame = Some(std::time::Instant::now());
            host.next_frame = host.last_frame;
            host.suspend_session();
            assert!(!host.active);
            assert!(host.last_frame.is_none() && host.next_frame.is_none());
            let worker = thread::spawn(move || {
                if !disconnected {
                    control.request_stop().unwrap();
                    control.request_stop().unwrap();
                    Some(control)
                } else {
                    drop(control);
                    None
                }
            });
            wakes.recv_timeout(Duration::from_secs(3))?;
            assert!(host.poll_operations());
            host.shutdown(); // Window-close/exit can race with control.
            assert!(host.poll_operations());
            assert_eq!(host.session.presented_frames, 0);
            let control = worker.join().unwrap();
            let app = host.finish()?;
            assert_eq!(*app.world().resource::<u32>()?, 1);
            if let Some(control) = control {
                assert_eq!(control.status().state, HostState::Stopped);
                control.request_stop()?;
            }
        }
        Ok(())
    }

    #[test]
    fn stop_before_client_start_skips_game_startup() -> NativeClientResult<()> {
        let (control, operations) = control_channel();
        control.request_stop()?;
        let mut builder = AppBuilder::new();
        builder.add_system(Stage::Startup, "must not start", |_| {
            panic!("stop precedes startup")
        });
        let mut host = test_host(builder.build()?);
        host.operations = Some(operations);
        assert!(host.poll_operations());
        host.finish()?;
        assert_eq!(control.status().state, HostState::Stopped);
        assert_eq!(control.status().completed_steps, 0);
        Ok(())
    }

    #[test]
    fn client_startup_tick_and_shutdown_failures_publish_failure() -> NativeClientResult<()> {
        for stage in [Stage::Startup, Stage::Update, Stage::Shutdown] {
            let (control, operations) = control_channel();
            let mut builder = AppBuilder::new();
            builder.add_system(stage, "intentional failure", |_| {
                Err(RuntimeError::MissingResource("test failure"))
            });
            let mut host = test_host(builder.build()?);
            host.operations = Some(operations);
            match host.session.start() {
                Err(error) => host.record_failure(error),
                Ok(()) => {
                    if let Err(error) = host.present(Duration::from_millis(17)) {
                        host.record_failure(error);
                    } else {
                        host.report_render(RenderStatus::Presented);
                    }
                }
            }
            assert!(host.finish().is_err());
            assert_eq!(control.status().state, HostState::Failed);
            assert!(control.status().failure.unwrap().contains("test failure"));
        }
        Ok(())
    }

    #[test]
    fn graphics_or_event_loop_failure_is_retained_after_shutdown() -> NativeClientResult<()> {
        let (control, operations) = control_channel();
        let mut host = test_host(AppBuilder::new().build()?);
        host.operations = Some(operations);
        host.record_failure(std::io::Error::other("graphics startup failed").into());
        host.shutdown();
        host.shutdown();
        assert!(host.finish().is_err());
        assert_eq!(
            control.status().failure.as_deref(),
            Some("graphics startup failed")
        );
        Ok(())
    }

    #[test]
    fn client_control_preserves_gameplay_for_identical_tick_sequences() -> NativeClientResult<()> {
        for controlled in [false, true] {
            let (control, operations) = control_channel();
            let mut builder = AppBuilder::new().with_fixed_step(Duration::from_millis(10));
            builder.insert_resource(Vec::<&'static str>::new());
            for (stage, name) in [
                (Stage::FixedUpdate, "fixed"),
                (Stage::Update, "update"),
                (Stage::Shutdown, "shutdown"),
            ] {
                builder.add_system(stage, name, move |context| {
                    context.world.resource_mut::<Vec<&str>>()?.push(name);
                    Ok(())
                });
            }
            let mut host = test_host(builder.build()?);
            if controlled {
                host.operations = Some(operations);
            }
            host.session.start()?;
            for _ in 0..3 {
                assert!(!host.poll_operations());
                host.present(Duration::from_millis(10))?;
                host.report_render(RenderStatus::Presented);
            }
            let app = host.finish()?;
            assert_eq!(
                app.world().resource::<Vec<&str>>()?,
                &[
                    "fixed", "update", "fixed", "update", "fixed", "update", "shutdown"
                ]
            );
            if controlled {
                assert_eq!(control.status().completed_steps, 3);
                assert_eq!(control.status().graphics.unwrap().presented_frames, 3);
            }
        }
        Ok(())
    }

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
        assert_eq!(
            keyboard_control(PhysicalKey::Code(KeyCode::Escape)),
            Some(keyboard::ESCAPE)
        );
        assert_eq!(
            keyboard_control(PhysicalKey::Code(KeyCode::Space)),
            Some(keyboard::SPACE)
        );
        assert_eq!(
            keyboard_control(PhysicalKey::Code(KeyCode::KeyR)),
            Some(keyboard::R)
        );
    }

    #[test]
    fn capture_click_is_consumed_but_subsequent_attack_and_release_are_forwarded() {
        use super::{ElementState, MouseButton, capture_click};
        assert!(capture_click(
            true,
            false,
            MouseButton::Left,
            ElementState::Pressed
        ));
        assert!(!capture_click(
            true,
            true,
            MouseButton::Left,
            ElementState::Pressed
        ));
        assert!(!capture_click(
            true,
            false,
            MouseButton::Left,
            ElementState::Released
        ));
        assert!(!capture_click(
            false,
            false,
            MouseButton::Left,
            ElementState::Pressed
        ));
        assert!(!capture_click(
            true,
            false,
            MouseButton::Right,
            ElementState::Pressed
        ));
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
