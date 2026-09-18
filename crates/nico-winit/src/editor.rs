//! Optional native editor host. Owns the event loop, GPU interop and lifecycle;
//! document policy and runtime composition belong to the editor application.
use crate::NativeClientResult;
use nico_ops::{GraphicsOutcome, HostEndpoint};
use nico_presentation::{Scene2d, Scene3d, UiScene};
use nico_render::MeshRenderPipeline;
use nico_rhi::{Extent3d, RhiSurface, SurfaceAcquire, builtin_shaders};
use nico_rhi_wgpu::{WgpuBackend, WgpuDevice, WgpuOffscreen};
use std::{
    sync::Arc,
    time::{Duration, Instant},
};
use winit::{
    application::ApplicationHandler,
    dpi::LogicalSize,
    event::WindowEvent,
    event_loop::{ActiveEventLoop, ControlFlow, EventLoop},
    window::{Window, WindowId},
};

pub trait EditorApplication {
    /// UI queues document edits. Returns the desired viewport size in logical pixels.
    fn ui(&mut self, ui: &mut egui::Ui, viewport: egui::TextureId) -> egui::Vec2;
    /// Applies queued changes and advances the embedded runtime at its boundary.
    fn update(&mut self, elapsed: Duration) -> NativeClientResult<Scene3d>;
    /// Called after a UI frame has been submitted for presentation.
    fn presented(&mut self) {}
    fn shutdown(&mut self) -> NativeClientResult<()>;
    fn request_close(&mut self) -> bool {
        true
    }
    fn should_close(&self) -> bool {
        false
    }
}

pub struct EditorConfig {
    pub title: String,
    pub smoke_frames: Option<u64>,
    pub background: bool,
}

struct Graphics {
    backend: WgpuBackend<Window>,
    mesh: MeshRenderPipeline<WgpuDevice>,
    target: WgpuOffscreen,
    texture: egui::TextureId,
    gui: egui_wgpu::Renderer,
    input: egui_winit::State,
}
struct Host<A> {
    app: A,
    config: EditorConfig,
    endpoint: HostEndpoint,
    context: egui::Context,
    window: Option<Arc<Window>>,
    graphics: Option<Graphics>,
    frames: u64,
    last: Instant,
    next: Instant,
    active: bool,
    failure: Option<String>,
}

pub fn run<A: EditorApplication + 'static>(
    app: A,
    config: EditorConfig,
    endpoint: HostEndpoint,
) -> NativeClientResult<()> {
    let event_loop = EventLoop::new()?;
    let now = Instant::now();
    let mut host = Host {
        app,
        config,
        endpoint,
        context: egui::Context::default(),
        window: None,
        graphics: None,
        frames: 0,
        last: now,
        next: now,
        active: true,
        failure: None,
    };
    let result = event_loop.run_app(&mut host);
    host.endpoint.stopping();
    if let Err(error) = host.app.shutdown() {
        host.failure.get_or_insert(error.to_string());
    }
    let outcome = result
        .map_err(|e| e.to_string())
        .and_then(|()| host.failure.map_or(Ok(()), Err));
    host.endpoint.finish(outcome.clone());
    outcome.map_err(|e| std::io::Error::other(e).into())
}
impl<A: EditorApplication> Host<A> {
    fn initialize(&mut self, event_loop: &ActiveEventLoop) -> NativeClientResult<()> {
        let window = Arc::new(
            event_loop.create_window(
                Window::default_attributes()
                    .with_title(&self.config.title)
                    .with_inner_size(LogicalSize::new(1440., 900.))
                    .with_active(!self.config.background),
            )?,
        );
        let size = window.inner_size();
        let backend = pollster::block_on(WgpuBackend::new(
            window.clone(),
            event_loop.owned_display_handle(),
            Extent3d {
                width: size.width,
                height: size.height,
                depth_or_layers: 1,
            },
        ))?;
        let format = match backend.surface().format() {
            nico_rhi::TextureFormat::Bgra8UnormSrgb => wgpu::TextureFormat::Bgra8UnormSrgb,
            nico_rhi::TextureFormat::Bgra8Unorm => wgpu::TextureFormat::Bgra8Unorm,
            nico_rhi::TextureFormat::Rgba8UnormSrgb => wgpu::TextureFormat::Rgba8UnormSrgb,
            _ => wgpu::TextureFormat::Rgba8Unorm,
        };
        let mut gui = egui_wgpu::Renderer::new(backend.device().raw(), format, Default::default());
        let target = WgpuOffscreen::new(backend.device(), 800, 600)?;
        let texture = gui.register_native_texture(
            backend.device().raw(),
            target.view(),
            wgpu::FilterMode::Linear,
        );
        let shader_root = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
            .join("../../assets/presentation/shaders");
        let mesh_bytes = std::fs::read(shader_root.join("generated/wgpu/meshes.wgsl"))?;
        let quad_bytes = std::fs::read(shader_root.join("generated/wgpu/quads.wgsl"))?;
        let skin_bytes = std::fs::read(shader_root.join("generated/wgpu/skinned_meshes.wgsl"))?;
        let mut mesh = MeshRenderPipeline::new(
            backend.device(),
            nico_rhi::TextureFormat::Rgba8UnormSrgb,
            builtin_shaders::bootstrap_wgsl(&mesh_bytes),
            builtin_shaders::bootstrap_wgsl(&quad_bytes),
        )?;
        mesh.enable_skinning(
            backend.device(),
            builtin_shaders::bootstrap_wgsl(&skin_bytes),
        )?;
        let input = egui_winit::State::new(
            self.context.clone(),
            egui::ViewportId::ROOT,
            event_loop,
            Some(window.scale_factor() as f32),
            window.theme(),
            None,
        );
        self.graphics = Some(Graphics {
            backend,
            mesh,
            target,
            texture,
            gui,
            input,
        });
        self.window = Some(window);
        self.endpoint.running(self.frames);
        Ok(())
    }
    fn draw(&mut self) -> NativeClientResult<()> {
        let (Some(window), Some(g)) = (&self.window, &mut self.graphics) else {
            return Ok(());
        };
        let now = Instant::now();
        let elapsed = now.duration_since(self.last);
        self.last = now;
        let mut viewport = egui::vec2(800., 600.);
        let mut output = self.context.run_ui(g.input.take_egui_input(window), |ui| {
            viewport = self.app.ui(ui, g.texture);
        });
        for (id, deltas) in &output.textures_delta.set {
            for delta in deltas {
                g.gui.update_texture(
                    g.backend.device().raw(),
                    g.backend.queue().raw(),
                    *id,
                    delta,
                );
            }
        }
        let freed: Vec<_> = output.textures_delta.free.iter().copied().collect();
        output.textures_delta.clear();
        g.input
            .handle_platform_output(window, output.platform_output);
        let scene = self.app.update(elapsed)?;
        let scale = output.pixels_per_point;
        let width = (viewport.x * scale).round().clamp(1., 4096.) as u32;
        let height = (viewport.y * scale).round().clamp(1., 4096.) as u32;
        if g.target.extent().width != width || g.target.extent().height != height {
            g.target = WgpuOffscreen::new(g.backend.device(), width, height)?;
            g.gui.update_egui_texture_from_wgpu_texture(
                g.backend.device().raw(),
                g.target.view(),
                wgpu::FilterMode::Linear,
                g.texture,
            );
        }
        let extent = g.target.extent();
        g.mesh.render(
            g.backend.device(),
            g.backend.queue(),
            &mut g.target,
            &scene,
            &Scene2d::default(),
            &UiScene::default(),
            [viewport.x.max(1.), viewport.y.max(1.)],
            extent,
        )?;
        let primitives = self.context.tessellate(output.shapes, scale);
        let (device, queue, surface) = g.backend.parts();
        let acquired = surface.acquire(device)?;
        let skipped = match &acquired {
            SurfaceAcquire::Acquired { .. } => None,
            SurfaceAcquire::ZeroSized => Some(GraphicsOutcome::ZeroSized),
            SurfaceAcquire::Timeout => Some(GraphicsOutcome::Timeout),
            SurfaceAcquire::Occluded => Some(GraphicsOutcome::Occluded),
        };
        if let SurfaceAcquire::Acquired { frame, view } = acquired {
            let size = window.inner_size();
            let screen = egui_wgpu::ScreenDescriptor {
                size_in_pixels: [size.width, size.height],
                pixels_per_point: scale,
            };
            let mut encoder = device
                .raw()
                .create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
            let callbacks = g.gui.update_buffers(
                device.raw(),
                queue.raw(),
                &mut encoder,
                &primitives,
                &screen,
            );
            {
                let pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                    label: Some("editor UI"),
                    color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                        view: view.raw(),
                        depth_slice: None,
                        resolve_target: None,
                        ops: wgpu::Operations {
                            load: wgpu::LoadOp::Clear(wgpu::Color::BLACK),
                            store: wgpu::StoreOp::Store,
                        },
                    })],
                    ..Default::default()
                });
                g.gui
                    .render(&mut pass.forget_lifetime(), &primitives, &screen);
            }
            queue
                .raw()
                .submit(callbacks.into_iter().chain([encoder.finish()]));
            let capture = self.endpoint.snapshots().take_request();
            if capture.is_some() {
                surface.request_snapshot();
            }
            surface.present(device, queue, frame);
            if let Some(id) = capture {
                self.endpoint.snapshots().complete(
                    id,
                    surface.take_snapshot().map(|p| nico_ops::snapshot::Pixels {
                        width: p.width,
                        height: p.height,
                        rgba: p.rgba,
                    }),
                );
            }
            self.endpoint.graphics(GraphicsOutcome::Presented);
            self.app.presented();
        }
        if let Some(outcome) = skipped {
            self.endpoint.graphics(outcome);
            if let Some(id) = self.endpoint.snapshots().take_request() {
                self.endpoint
                    .snapshots()
                    .complete(id, Err(format!("capture skipped: {}", outcome.as_str())));
            }
        }
        for id in &freed {
            g.gui.free_texture(id);
        }
        self.frames += 1;
        self.endpoint.progress(self.frames);
        Ok(())
    }
    fn fail(&mut self, event_loop: &ActiveEventLoop, error: impl std::fmt::Display) {
        self.failure = Some(error.to_string());
        event_loop.exit();
    }
}
impl<A: EditorApplication> ApplicationHandler for Host<A> {
    fn resumed(&mut self, event_loop: &ActiveEventLoop) {
        self.active = true;
        if self.graphics.is_none()
            && let Err(error) = self.initialize(event_loop)
        {
            self.fail(event_loop, error);
        }
        self.last = Instant::now();
        self.endpoint.activity(true);
    }
    fn suspended(&mut self, _event_loop: &ActiveEventLoop) {
        self.active = false;
        self.endpoint.activity(false);
    }
    fn window_event(&mut self, event_loop: &ActiveEventLoop, id: WindowId, event: WindowEvent) {
        let Some(window) = &self.window else {
            return;
        };
        if window.id() != id {
            return;
        }
        if let Some(g) = &mut self.graphics {
            let _ = g.input.on_window_event(window, &event);
        }
        match event {
            WindowEvent::CloseRequested => {
                if self.app.request_close() {
                    event_loop.exit();
                }
            }
            WindowEvent::Resized(size) => {
                if let Some(g) = &mut self.graphics {
                    g.backend.resize(Extent3d {
                        width: size.width,
                        height: size.height,
                        depth_or_layers: 1,
                    });
                }
            }
            WindowEvent::RedrawRequested if self.active => {
                if let Err(error) = self.draw() {
                    self.fail(event_loop, error);
                }
                if self
                    .config
                    .smoke_frames
                    .is_some_and(|limit| self.frames >= limit)
                {
                    event_loop.exit();
                }
            }
            _ => {}
        }
    }
    fn about_to_wait(&mut self, event_loop: &ActiveEventLoop) {
        if self.endpoint.stop_requested() || self.app.should_close() {
            event_loop.exit();
            return;
        }
        let now = Instant::now();
        if now >= self.next {
            if self.active
                && let Some(window) = &self.window
            {
                window.request_redraw();
            }
            self.next = now + Duration::from_millis(16);
        }
        event_loop.set_control_flow(ControlFlow::WaitUntil(self.next));
    }
}
