//! Opt-in real-GPU validation, separate from portable workspace tests.
use super::*;
use nico_assets::Texture;
use nico_presentation::{Camera2d, Quad, Scene2d, UiScene};
use nico_render::QuadRenderPipeline;

struct Target {
    acquired: usize,
    presented: usize,
    skip: Option<nico_render::RenderStatus>,
    fail_acquire: bool,
    format: TextureFormat,
    texture: wgpu::Texture,
}
impl<Q: RhiQueue<WgpuDevice>> RhiSurface<WgpuDevice, Q> for Target {
    type Frame = ();
    fn format(&self) -> TextureFormat {
        self.format
    }
    fn resize(&mut self, _: &WgpuDevice, _: Extent3d) {}
    fn acquire(&mut self, _: &WgpuDevice) -> Result<SurfaceAcquire<(), WgpuTextureView>, RhiError> {
        self.acquired += 1;
        if std::mem::take(&mut self.fail_acquire) {
            return Err(RhiError::new(
                RhiErrorKind::DeviceLost,
                "injected acquisition failure",
            ));
        }
        if let Some(skip) = self.skip.take() {
            return Ok(match skip {
                nico_render::RenderStatus::ZeroSized => SurfaceAcquire::ZeroSized,
                nico_render::RenderStatus::Timeout => SurfaceAcquire::Timeout,
                nico_render::RenderStatus::Occluded => SurfaceAcquire::Occluded,
                nico_render::RenderStatus::Presented => unreachable!(),
            });
        }
        Ok(SurfaceAcquire::Acquired {
            frame: (),
            view: WgpuTextureView(
                self.texture
                    .create_view(&wgpu::TextureViewDescriptor::default()),
            ),
        })
    }
    fn present(&mut self, _: &WgpuDevice, _: &Q, _: ()) {
        self.presented += 1;
    }
}

/// Test-only measurement of uploads; frame uniforms are deliberately excluded.
struct UploadQueue {
    inner: WgpuQueue,
    geometry_bytes: std::cell::Cell<usize>,
    texture_bytes: std::cell::Cell<usize>,
}
impl std::ops::Deref for UploadQueue {
    type Target = WgpuQueue;
    fn deref(&self) -> &WgpuQueue {
        &self.inner
    }
}
impl RhiQueue<WgpuDevice> for UploadQueue {
    fn write_buffer(&self, buffer: &WgpuBuffer, offset: u64, data: &[u8]) {
        if buffer
            .0
            .usage()
            .intersects(wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::INDEX)
        {
            self.geometry_bytes
                .set(self.geometry_bytes.get() + data.len());
        }
        self.inner.write_buffer(buffer, offset, data);
    }
    fn write_texture(
        &self,
        destination: TextureCopy<'_, WgpuTexture>,
        data: &[u8],
        layout: TextureDataLayout,
        extent: Extent3d,
    ) {
        self.texture_bytes
            .set(self.texture_bytes.get() + data.len());
        self.inner.write_texture(destination, data, layout, extent);
    }
    fn submit(&self, commands: Vec<WgpuCommandBuffer>) {
        self.inner.submit(commands);
    }
}

fn pixels(device: &WgpuDevice, queue: &WgpuQueue, target: &Target) -> Vec<u8> {
    let buffer = device.inner.create_buffer(&wgpu::BufferDescriptor {
        label: Some("test readback"),
        size: 256 * 64,
        usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
        mapped_at_creation: false,
    });
    let mut encoder = device
        .inner
        .create_command_encoder(&wgpu::CommandEncoderDescriptor::default());
    encoder.copy_texture_to_buffer(
        wgpu::TexelCopyTextureInfo {
            texture: &target.texture,
            mip_level: 0,
            origin: wgpu::Origin3d::ZERO,
            aspect: wgpu::TextureAspect::All,
        },
        wgpu::TexelCopyBufferInfo {
            buffer: &buffer,
            layout: wgpu::TexelCopyBufferLayout {
                offset: 0,
                bytes_per_row: Some(256),
                rows_per_image: Some(64),
            },
        },
        wgpu::Extent3d {
            width: 64,
            height: 64,
            depth_or_array_layers: 1,
        },
    );
    queue.inner.submit([encoder.finish()]);
    let (tx, rx) = std::sync::mpsc::channel();
    buffer
        .slice(..)
        .map_async(wgpu::MapMode::Read, move |result| {
            tx.send(result).unwrap();
        });
    device
        .inner
        .poll(wgpu::PollType::Wait {
            submission_index: None,
            timeout: Some(std::time::Duration::from_secs(10)),
        })
        .unwrap();
    rx.recv_timeout(std::time::Duration::from_secs(10))
        .unwrap()
        .unwrap();
    let bytes = buffer.slice(..).get_mapped_range().unwrap().to_vec();
    buffer.unmap();
    bytes
}
fn pixel(bytes: &[u8], x: usize, y: usize) -> &[u8] {
    &bytes[(y * 64 + x) * 4..(y * 64 + x + 1) * 4]
}

#[test]
#[ignore = "bounded submission measurement; requires a real graphics adapter"]
fn gpu_dense_scene_submission_measurement() {
    use nico_presentation::{Camera3d, MeshInstance, Quaternion, Scene3d};
    use nico_render::MeshRenderPipeline;
    let (device, queue, mut target) = setup_with_flags(native_instance_flags());
    let mut renderer = MeshRenderPipeline::new(
        &device,
        target.format,
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/meshes.wgsl"
        )),
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/quads.wgsl"
        )),
    )
    .unwrap();
    let images: Vec<_> = (0..32)
        .map(|i| Arc::new(Texture::rgba8(1, 1, vec![i * 8, 255, 255, 255]).unwrap()))
        .collect();
    let draws: Vec<_> = (0..200)
        .map(|i| MeshInstance {
            mirrored: false,
            material: None,
            mesh: None,
            skin_palette: None,
            texture: Some(images[i % images.len()].clone()),
            position: [0., 0., -(i as f32) * 0.01],
            orientation: Quaternion::IDENTITY,
            scale: 1.,
            color: [1.; 4],
        })
        .collect();
    let glyphs: Vec<_> = (0..300)
        .map(|i| Quad {
            center: [(i % 30) as f32 * 2., (i / 30) as f32 * 2.],
            size: [1.; 2],
            color: [1.; 4],
            texture: Some(images[i % images.len()].clone()),
        })
        .collect();
    for (mesh_count, glyph_count) in [(0, 0), (200, 0), (0, 300), (200, 300)] {
        let scene = Scene3d {
            camera: Camera3d::looking_at([0., 0., 3.], [0.; 3], [0., 1., 0.]).unwrap(),
            meshes: draws[..mesh_count].to_vec(),
            ..Default::default()
        };
        let hud = UiScene {
            quads: glyphs[..glyph_count].to_vec(),
        };
        let mut elapsed = std::time::Duration::ZERO;
        for i in 0..70 {
            let start = std::time::Instant::now();
            renderer
                .render(
                    &device,
                    &queue,
                    &mut target,
                    &scene,
                    &Scene2d::default(),
                    &hud,
                    [64.; 2],
                    Extent3d::surface(64, 64),
                )
                .unwrap();
            let submitted = start.elapsed();
            device
                .inner
                .poll(wgpu::PollType::Wait {
                    submission_index: None,
                    timeout: Some(std::time::Duration::from_secs(10)),
                })
                .unwrap();
            if i >= 10 {
                elapsed += submitted;
            }
        }
        eprintln!(
            "{mesh_count} meshes / {glyph_count} glyphs: {:.3} ms mean render-call wall time (60 warm frames; explicit GPU wait excluded)",
            elapsed.as_secs_f64() * 1000. / 60.
        );
    }
    assert_eq!(target.presented, 280);
}

#[test]
#[ignore = "requires a real graphics adapter; run explicitly for rendering changes"]
fn gpu_quads_preserve_texture_orientation_alpha_camera_and_hud() {
    let (device, queue, mut target) = setup();
    let code = include_bytes!("../../../assets/presentation/shaders/generated/wgpu/quads.wgsl");
    let mut renderer = QuadRenderPipeline::new(
        &device,
        TextureFormat::Rgba8UnormSrgb,
        builtin_shaders::bootstrap_wgsl(code),
    )
    .unwrap();
    let texture = Arc::new(
        Texture::rgba8(
            2,
            2,
            vec![
                255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 0,
            ],
        )
        .unwrap(),
    );
    let world = Quad {
        center: [0.0, 0.0],
        size: [2.0, 2.0],
        color: [1.0; 4],
        texture: Some(texture.clone()),
    };
    let hud = Quad {
        center: [8.0, 8.0],
        size: [8.0, 8.0],
        ..world.clone()
    };
    let mut scene = Scene2d {
        camera: Camera2d {
            center: [0.0, 0.0],
            pixels_per_unit: 16.0,
        },
        world: vec![world],
    };
    let mut ui = UiScene { quads: vec![hud] };
    renderer
        .render(&device, &queue, &mut target, &scene, &ui, [64.0, 64.0])
        .unwrap();
    let first = pixels(&device, &queue, &target);
    assert_eq!(pixel(&first, 20, 20), [255, 0, 0, 255]);
    assert_eq!(pixel(&first, 40, 20), [0, 255, 0, 255]);
    assert_eq!(pixel(&first, 20, 40), [0, 0, 255, 255]);
    assert_eq!(pixel(&first, 40, 40), pixel(&first, 60, 60));
    assert_eq!(pixel(&first, 5, 5), [255, 0, 0, 255]);
    scene.camera.center = [1.0, 0.0];
    renderer
        .render(&device, &queue, &mut target, &scene, &ui, [64.0, 64.0])
        .unwrap();
    let moved = pixels(&device, &queue, &target);
    assert_eq!(pixel(&moved, 5, 5), pixel(&first, 5, 5));
    assert_eq!(pixel(&moved, 4, 20), [255, 0, 0, 255]);
    assert_eq!(pixel(&moved, 40, 20), pixel(&moved, 60, 60));
    ui.quads = vec![Quad {
        center: [24.0, 24.0],
        size: [8.0, 8.0],
        color: [1.0, 1.0, 1.0, 0.5],
        texture: Some(Arc::new(
            Texture::rgba8(1, 1, vec![0, 0, 255, 255]).unwrap(),
        )),
    }];
    renderer
        .render(&device, &queue, &mut target, &scene, &ui, [64.0, 64.0])
        .unwrap();
    let blended = pixels(&device, &queue, &target);
    for (&actual, expected) in pixel(&blended, 24, 24).iter().zip([0_i16, 188, 188, 255]) {
        assert!(
            (i16::from(actual) - expected).abs() <= 1,
            "straight alpha HUD over green world: {:?}",
            pixel(&blended, 24, 24)
        );
    }
    // Retire the uploaded content immediately after submitting its last use.
    scene.world.clear();
    ui.quads.clear();
    drop(texture);
    renderer
        .render(&device, &queue, &mut target, &scene, &ui, [64.0, 64.0])
        .unwrap();
    let empty = pixels(&device, &queue, &target);
    assert_eq!(pixel(&empty, 4, 20), pixel(&empty, 60, 60));
    ui.quads.push(Quad {
        center: [8.0, 8.0],
        size: [8.0, 8.0],
        color: [1.0; 4],
        texture: None,
    });
    renderer
        .render(&device, &queue, &mut target, &scene, &ui, [64.0, 64.0])
        .unwrap();
    drop(renderer);
    let fallback = pixels(&device, &queue, &target);
    assert_eq!(pixel(&fallback, 5, 5), [255, 0, 255, 255]);
}

fn setup() -> (WgpuDevice, WgpuQueue, Target) {
    setup_with_flags(wgpu::InstanceFlags::debugging())
}

fn setup_with_flags(flags: wgpu::InstanceFlags) -> (WgpuDevice, WgpuQueue, Target) {
    eprintln!("GPU instance flags: {:?}", flags.with_env());
    let instance = wgpu::Instance::new(
        wgpu::InstanceDescriptor {
            backends: wgpu::Backends::PRIMARY,
            flags,
            ..wgpu::InstanceDescriptor::new_without_display_handle()
        }
        .with_env(),
    );
    let adapter =
        pollster::block_on(instance.request_adapter(&wgpu::RequestAdapterOptions::default()))
            .unwrap();
    eprintln!("GPU rendering validation: {:?}", adapter.get_info());
    let (inner, inner_queue) =
        pollster::block_on(adapter.request_device(&wgpu::DeviceDescriptor::default())).unwrap();
    let limits = inner.limits();
    let info = adapter.get_info();
    let device = WgpuDevice {
        inner,
        failure: Arc::new(Mutex::new(None)),
        capabilities: Capabilities {
            adapter: AdapterInfo {
                name: info.name,
                api: graphics_api(info.backend),
                kind: adapter_kind(info.device_type),
            },
            limits: Limits {
                max_texture_dimension_2d: limits.max_texture_dimension_2d,
                max_bind_groups: limits.max_bind_groups,
                max_uniform_buffer_binding_size: limits.max_uniform_buffer_binding_size,
                max_storage_buffer_binding_size: limits.max_storage_buffer_binding_size,
                max_vertex_buffers: limits.max_vertex_buffers,
                max_vertex_attributes: limits.max_vertex_attributes,
            },
        },
    };
    let queue = WgpuQueue { inner: inner_queue };
    let target = Target {
        acquired: 0,
        presented: 0,
        skip: None,
        fail_acquire: false,
        format: TextureFormat::Rgba8UnormSrgb,
        texture: device.inner.create_texture(&wgpu::TextureDescriptor {
            label: Some("quad validation target"),
            size: wgpu::Extent3d {
                width: 64,
                height: 64,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: wgpu::TextureFormat::Rgba8UnormSrgb,
            usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
            view_formats: &[],
        }),
    };
    (device, queue, target)
}

#[test]
#[ignore = "requires a real graphics adapter; run explicitly for rendering changes"]
fn gpu_meshes_use_depth_perspective_and_shared_hud() {
    use nico_assets::{Mesh, MeshVertex};
    use nico_presentation::{Camera3d, MeshInstance, Scene3d};
    use nico_render::MeshRenderPipeline;
    let (device, queue, mut target) = setup();
    let mut renderer = MeshRenderPipeline::new(
        &device,
        TextureFormat::Rgba8UnormSrgb,
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/meshes.wgsl"
        )),
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/quads.wgsl"
        )),
    )
    .unwrap();
    let mesh = Arc::new(
        Mesh::triangles(
            vec![
                MeshVertex {
                    position: [-0.8, -0.8, 0.0],
                    uv: [0.5, 0.5],
                },
                MeshVertex {
                    position: [0.8, -0.8, 0.0],
                    uv: [0.5, 0.5],
                },
                MeshVertex {
                    position: [0.0, 0.8, 0.0],
                    uv: [0.5, 0.5],
                },
            ],
            vec![0, 1, 2],
        )
        .unwrap(),
    );
    let texture = Arc::new(Texture::rgba8(1, 1, vec![255; 4]).unwrap());
    let near = MeshInstance {
        mirrored: false,
        material: None,
        skin_palette: None,
        mesh: Some(mesh),
        texture: Some(texture.clone()),
        position: [0.0; 3],
        orientation: nico_presentation::Quaternion::IDENTITY,
        scale: 1.0,
        color: [0.0, 1.0, 0.0, 1.0],
    };
    let far = MeshInstance {
        mirrored: false,
        material: None,
        skin_palette: None,
        position: [0.0, 0.0, -1.0],
        color: [1.0, 0.0, 0.0, 1.0],
        ..near.clone()
    };
    let mut scene = Scene3d {
        lighting: nico_presentation::SceneLighting {
            radiance: [0.; 3],
            ambient: [1.; 3],
            ..Default::default()
        },
        camera: Camera3d {
            position: [0.0, 0.0, 3.0],
            orientation: nico_presentation::Quaternion::IDENTITY,
            ..Camera3d::default()
        },
        meshes: vec![near, far],
    };
    let hud = UiScene {
        quads: vec![Quad {
            center: [8.0, 8.0],
            size: [8.0, 8.0],
            color: [0.0, 0.0, 1.0, 1.0],
            texture: Some(texture),
        }],
    };
    let extent = Extent3d::surface(64, 64);
    renderer
        .render(
            &device,
            &queue,
            &mut target,
            &scene,
            &Scene2d::default(),
            &hud,
            [64.0, 64.0],
            extent,
        )
        .unwrap();
    let first = pixels(&device, &queue, &target);
    assert_eq!(
        pixel(&first, 32, 32),
        [0, 255, 0, 255],
        "near triangle must obscure later far draw"
    );
    assert_eq!(pixel(&first, 8, 8), [0, 0, 255, 255]);
    let mut canvas = Scene2d {
        camera: Camera2d {
            center: [0.; 2],
            pixels_per_unit: 16.,
        },
        world: vec![Quad {
            center: [0.; 2],
            size: [1.; 2],
            color: [1., 0., 0., 1.],
            texture: hud.quads[0].texture.clone(),
        }],
    };
    let mut overlay = UiScene {
        quads: vec![Quad {
            center: [32.; 2],
            size: [8.; 2],
            color: [0., 0., 1., 1.],
            texture: hud.quads[0].texture.clone(),
        }],
    };
    renderer
        .render(
            &device,
            &queue,
            &mut target,
            &scene,
            &canvas,
            &overlay,
            [64.; 2],
            extent,
        )
        .unwrap();
    let layers = pixels(&device, &queue, &target);
    assert_eq!(
        pixel(&layers, 26, 32),
        [255, 0, 0, 255],
        "Scene2D draws over 3D"
    );
    assert_eq!(
        pixel(&layers, 32, 32),
        [0, 0, 255, 255],
        "UI draws last, even with the same texture identity"
    );
    canvas.camera.center[0] = 1.;
    canvas.camera.pixels_per_unit = 32.;
    renderer
        .render(
            &device,
            &queue,
            &mut target,
            &scene,
            &canvas,
            &overlay,
            [64.; 2],
            extent,
        )
        .unwrap();
    let moved = pixels(&device, &queue, &target);
    assert_eq!(
        pixel(&moved, 32, 32),
        [0, 0, 255, 255],
        "UI is independent of world camera pan and zoom"
    );
    assert_ne!(pixel(&moved, 26, 32), [255, 0, 0, 255]);
    let acquired = target.acquired;
    overlay.quads[0].center[0] = f32::NAN;
    assert!(
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &canvas,
                &overlay,
                [64.; 2],
                extent
            )
            .is_err()
    );
    assert_eq!(
        target.acquired, acquired,
        "invalid UI must fail before surface acquisition"
    );
    if let Some(directory) = std::env::var_os("NICO_PBR_CAPTURE_DIR") {
        let directory = std::path::PathBuf::from(directory);
        std::fs::create_dir_all(&directory).unwrap();
        let mut ppm = b"P6\n64 64\n255\n".to_vec();
        for pixel in layers.as_chunks::<4>().0 {
            ppm.extend_from_slice(&pixel[..3]);
        }
        std::fs::write(directory.join("scene2d-ui.ppm"), ppm).unwrap();
    }
    scene.meshes.remove(0);
    renderer
        .render(
            &device,
            &queue,
            &mut target,
            &scene,
            &Scene2d::default(),
            &hud,
            [64.0, 64.0],
            extent,
        )
        .unwrap();
    let farther = pixels(&device, &queue, &target);
    assert_eq!(pixel(&farther, 32, 32), [255, 0, 0, 255]);
    let green_count = first
        .as_chunks::<4>()
        .0
        .iter()
        .filter(|p| **p == [0, 255, 0, 255])
        .count();
    let red_count = farther
        .as_chunks::<4>()
        .0
        .iter()
        .filter(|p| **p == [255, 0, 0, 255])
        .count();
    assert!(
        red_count < green_count,
        "perspective must shrink more distant geometry"
    );
    scene.camera.position[0] = 1.0;
    renderer
        .render(
            &device,
            &queue,
            &mut target,
            &scene,
            &Scene2d::default(),
            &hud,
            [64.0, 64.0],
            extent,
        )
        .unwrap();
    let moved = pixels(&device, &queue, &target);
    assert_eq!(pixel(&moved, 8, 8), [0, 0, 255, 255]);
    assert_ne!(pixel(&moved, 32, 32), [255, 0, 0, 255]);
    scene.meshes.clear();
    renderer
        .render(
            &device,
            &queue,
            &mut target,
            &scene,
            &Scene2d::default(),
            &hud,
            [64.0, 64.0],
            extent,
        )
        .unwrap();
    drop(renderer);
    let removed = pixels(&device, &queue, &target);
    assert_eq!(pixel(&removed, 32, 32), pixel(&removed, 60, 60));
    assert_eq!(pixel(&removed, 8, 8), [0, 0, 255, 255]);
}

#[test]
#[ignore = "requires a real graphics adapter"]
fn gpu_snapshot_unpads_rows_and_converts_bgra() {
    let (device, queue, _) = setup();
    let texture = device.inner.create_texture(&wgpu::TextureDescriptor {
        label: Some("snapshot padded BGRA fixture"),
        size: wgpu::Extent3d {
            width: 13,
            height: 7,
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: wgpu::TextureFormat::Bgra8UnormSrgb,
        usage: wgpu::TextureUsages::COPY_SRC | wgpu::TextureUsages::COPY_DST,
        view_formats: &[],
    });
    let bytes: Vec<u8> = (0..91).flat_map(|i| [i, 21, 230, 19]).collect();
    queue.inner.write_texture(
        wgpu::TexelCopyTextureInfo {
            texture: &texture,
            mip_level: 0,
            origin: wgpu::Origin3d::ZERO,
            aspect: wgpu::TextureAspect::All,
        },
        &bytes,
        wgpu::TexelCopyBufferLayout {
            offset: 0,
            bytes_per_row: Some(52),
            rows_per_image: Some(7),
        },
        texture.size(),
    );
    let captured = snapshot::readback(&device, &queue, &texture).unwrap();
    assert_eq!((captured.width, captured.height), (13, 7));
    let expected: Vec<u8> = (0..91).flat_map(|i| [230, 21, i, 255]).collect();
    assert_eq!(captured.rgba, expected);
}

#[test]
#[ignore = "requires a real graphics adapter; run explicitly for rendering changes"]
fn gpu_skinning_matches_cpu_reference_and_updates_shared_geometry_instances() {
    use nico_assets::{Mesh, MeshVertex, SkinWeights, model::IDENTITY};
    use nico_presentation::{Camera3d, MeshInstance, Scene3d};
    use nico_render::MeshRenderPipeline;
    let (device, queue, mut target) = setup();
    let mut renderer = MeshRenderPipeline::new(
        &device,
        TextureFormat::Rgba8UnormSrgb,
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/meshes.wgsl"
        )),
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/quads.wgsl"
        )),
    )
    .unwrap();
    renderer
        .enable_skinning(
            &device,
            builtin_shaders::bootstrap_wgsl(include_bytes!(
                "../../../assets/presentation/shaders/generated/wgpu/skinned_meshes.wgsl"
            )),
        )
        .unwrap();
    let mut reference_renderer = MeshRenderPipeline::new(
        &device,
        TextureFormat::Rgba8UnormSrgb,
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/meshes.wgsl"
        )),
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/quads.wgsl"
        )),
    )
    .unwrap();
    let vertices = vec![
        MeshVertex {
            position: [-0.4, -0.4, 0.],
            uv: [0.5; 2],
        },
        MeshVertex {
            position: [0.4, -0.4, 0.],
            uv: [0.5; 2],
        },
        MeshVertex {
            position: [0., 0.4, 0.],
            uv: [0.5; 2],
        },
    ];
    let mesh = Arc::new(
        Mesh::skinned_triangles(
            vertices.clone(),
            vec![0, 1, 2],
            vec![
                SkinWeights {
                    joints: [0, 1, 0, 0],
                    weights: [0.25, 0.75, 0., 0.]
                };
                3
            ],
            2,
        )
        .unwrap(),
    );
    let white = Arc::new(Texture::rgba8(1, 1, vec![255; 4]).unwrap());
    let mut scene = Scene3d {
        lighting: nico_presentation::SceneLighting {
            radiance: [0.; 3],
            ambient: [1.; 3],
            ..Default::default()
        },
        camera: Camera3d::looking_at([0., 0., 3.], [0.; 3], [0., 1., 0.]).unwrap(),
        meshes: vec![],
    };
    let hud = UiScene::default();
    for translation in [-0.5f32, 0.5] {
        let mut joint = IDENTITY;
        joint[3][0] = translation;
        let palette = Arc::new(vec![IDENTITY, joint]);
        let draw = MeshInstance {
            mirrored: false,
            material: None,
            mesh: Some(mesh.clone()),
            skin_palette: Some(palette),
            texture: Some(white.clone()),
            position: [0.; 3],
            orientation: nico_presentation::Quaternion::IDENTITY,
            scale: 1.,
            color: [0., 1., 0., 1.],
        };
        let mut other = draw.clone();
        other.position[1] = 0.8;
        other.color = [1., 0., 0., 1.];
        other.skin_palette = Some(Arc::new(vec![IDENTITY; 2]));
        let mut static_draw = other.clone();
        static_draw.mesh = Some(Arc::new(
            Mesh::triangles(vertices.clone(), vec![0, 1, 2]).unwrap(),
        ));
        static_draw.skin_palette = None;
        static_draw.position[1] = -0.8;
        static_draw.color = [0., 0., 1., 1.];
        scene.meshes = vec![draw, other, static_draw];
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &Scene2d::default(),
                &hud,
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        let gpu = pixels(&device, &queue, &target);
        assert!(gpu.as_chunks::<4>().0.contains(&[0, 255, 0, 255]));
        assert!(gpu.as_chunks::<4>().0.contains(&[255, 0, 0, 255]));
        // CPU reference uses exactly the same weighted translation, then the instance transform.
        let mut deformed = vertices.clone();
        for v in &mut deformed {
            v.position[0] += translation * 0.75;
        }
        scene.meshes[0].mesh = Some(Arc::new(Mesh::triangles(deformed, vec![0, 1, 2]).unwrap()));
        scene.meshes[0].skin_palette = None;
        scene.meshes[1].mesh = Some(Arc::new(
            Mesh::triangles(vertices.clone(), vec![0, 1, 2]).unwrap(),
        ));
        scene.meshes[1].skin_palette = None;
        reference_renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &Scene2d::default(),
                &hud,
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        assert_eq!(gpu, pixels(&device, &queue, &target));
    }
    scene.meshes.clear();
    renderer
        .render(
            &device,
            &queue,
            &mut target,
            &scene,
            &Scene2d::default(),
            &hud,
            [64.; 2],
            Extent3d::surface(64, 64),
        )
        .unwrap();
}

#[test]
#[ignore = "requires a real graphics adapter; run explicitly for PBR changes"]
fn gpu_pbr_responds_to_material_lighting_normal_maps_and_alpha_modes() {
    use nico_assets::{MaterialTexture, Mesh, MeshVertex, PbrMaterial, model::AlphaMode};
    use nico_presentation::{Camera3d, MeshInstance, Quaternion, Scene3d, SceneLighting};
    use nico_render::MeshRenderPipeline;
    use std::sync::Arc;
    let (device, queue, mut target) = setup();
    let mut renderer = MeshRenderPipeline::new(
        &device,
        TextureFormat::Rgba8UnormSrgb,
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/meshes.wgsl"
        )),
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/quads.wgsl"
        )),
    )
    .unwrap();
    renderer
        .enable_skinning(
            &device,
            builtin_shaders::bootstrap_wgsl(include_bytes!(
                "../../../assets/presentation/shaders/generated/wgpu/skinned_meshes.wgsl"
            )),
        )
        .unwrap();
    let mesh = Arc::new(
        Mesh::triangles(
            vec![
                MeshVertex {
                    position: [-1., -1., 0.],
                    uv: [0., 1.],
                },
                MeshVertex {
                    position: [1., -1., 0.],
                    uv: [1., 1.],
                },
                MeshVertex {
                    position: [0., 1., 0.],
                    uv: [0.5, 0.],
                },
            ],
            vec![0, 1, 2],
        )
        .unwrap(),
    );
    let draw = MeshInstance {
        mirrored: false,
        material: Some(Arc::new(PbrMaterial::default())),
        mesh: Some(mesh.clone()),
        skin_palette: None,
        texture: None,
        position: [0.; 3],
        orientation: Quaternion::IDENTITY,
        scale: 1.,
        color: [1.; 4],
    };
    let mut scene = Scene3d {
        camera: Camera3d::looking_at([0., 0., 3.], [0.; 3], [0., 1., 0.]).unwrap(),
        lighting: SceneLighting {
            direction: [0., 0., 1.],
            radiance: [1.; 3],
            ambient: [0.; 3],
        },
        meshes: vec![draw],
    };
    let mut render = |scene: &Scene3d| {
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        pixels(&device, &queue, &target)
    };
    let rough = render(&scene);
    // Packed metallic/roughness and AO must use raw linear channels, unlike color.
    let channel = 128. / 255.;
    scene.meshes[0].material = Some(Arc::new(PbrMaterial {
        metallic: channel,
        roughness: channel,
        ..Default::default()
    }));
    let factored = render(&scene);
    scene.meshes[0].material = Some(Arc::new(PbrMaterial {
        metallic: 1.,
        roughness: 1.,
        metallic_roughness_texture: Some(MaterialTexture::new(Arc::new(
            Texture::rgba8(1, 1, vec![0, 128, 128, 255]).unwrap(),
        ))),
        ..Default::default()
    }));
    assert_eq!(
        render(&scene),
        factored,
        "metallic B and roughness G must be linear factor multipliers"
    );
    scene.lighting.radiance = [0.; 3];
    scene.lighting.ambient = [1.; 3];
    scene.meshes[0].material = Some(Arc::new(PbrMaterial {
        occlusion_texture: Some(MaterialTexture::new(Arc::new(
            Texture::rgba8(1, 1, vec![128, 0, 0, 255]).unwrap(),
        ))),
        ..Default::default()
    }));
    let occluded = render(&scene);
    assert!(
        (i32::from(pixel(&occluded, 32, 32)[0]) - 188).abs() <= 1,
        "occlusion R must remain linear"
    );
    scene.lighting.ambient = [0.; 3];
    scene.meshes[0].material = Some(Arc::new(PbrMaterial {
        emissive: [1.; 3],
        emissive_texture: Some(MaterialTexture::new(Arc::new(
            Texture::rgba8(1, 1, vec![128, 64, 32, 255]).unwrap(),
        ))),
        ..Default::default()
    }));
    assert_eq!(
        pixel(&render(&scene), 32, 32),
        [128, 64, 32, 255],
        "emissive color must decode sRGB independently of scene lights"
    );
    scene.lighting.radiance = [1.; 3];
    let mut material = PbrMaterial {
        roughness: 0.15,
        ..Default::default()
    };
    scene.meshes[0].material = Some(Arc::new(material.clone()));
    let smooth = render(&scene);
    assert!(
        pixel(&smooth, 32, 32)[0] > pixel(&rough, 32, 32)[0] + 40,
        "smooth dielectric must have a stronger central highlight"
    );
    material.metallic = 1.;
    material.base_color = [1., 0., 0., 1.];
    scene.meshes[0].material = Some(Arc::new(material.clone()));
    let metal = render(&scene);
    assert!(
        pixel(&metal, 32, 32)[0] > 200 && pixel(&metal, 32, 32)[1] < 5,
        "metal specular must inherit base color"
    );
    material = PbrMaterial::default();
    material.normal_texture = Some(MaterialTexture::new(Arc::new(
        Texture::rgba8(1, 1, vec![255, 128, 128, 255]).unwrap(),
    )));
    scene.meshes[0].material = Some(Arc::new(material.clone()));
    let normal = render(&scene);
    assert!(
        pixel(&normal, 32, 32)[0] < pixel(&rough, 32, 32)[0] / 3,
        "tangent normal must turn away from the light"
    );
    scene.lighting.radiance = [0.; 3];
    scene.lighting.ambient = [1.; 3];
    material = PbrMaterial {
        base_color_texture: Some(MaterialTexture::new(Arc::new(
            Texture::rgba8(1, 1, vec![128, 128, 128, 255]).unwrap(),
        ))),
        ..Default::default()
    };
    scene.meshes[0].material = Some(Arc::new(material));
    let srgb = render(&scene);
    assert!(
        (i32::from(pixel(&srgb, 32, 32)[0]) - 128).abs() <= 1,
        "sRGB input must decode and encode exactly once"
    );
    material = PbrMaterial {
        alpha: AlphaMode::Mask,
        base_color: [1., 0., 0., 0.4],
        ..Default::default()
    };
    scene.meshes[0].material = Some(Arc::new(material.clone()));
    let masked = render(&scene);
    assert_eq!(pixel(&masked, 32, 32), pixel(&masked, 0, 0));
    material.alpha = AlphaMode::Opaque;
    scene.meshes[0].material = Some(Arc::new(material.clone()));
    assert_eq!(
        pixel(&render(&scene), 32, 32),
        [255, 0, 0, 255],
        "opaque ignores coverage alpha"
    );
    material.alpha = AlphaMode::Blend;
    material.base_color[3] = 0.5;
    scene.meshes[0].material = Some(Arc::new(material.clone()));
    let mut far = scene.meshes[0].clone();
    far.position[2] = -0.5;
    material.base_color = [0., 1., 0., 0.5];
    far.material = Some(Arc::new(material));
    scene.meshes.push(far);
    let blended = render(&scene);
    scene.meshes.reverse();
    assert_eq!(
        blended,
        render(&scene),
        "transparent ordering must be independent of extraction order"
    );
    assert!(
        pixel(&blended, 32, 32)[0] > pixel(&blended, 32, 32)[1],
        "near red must composite over far green"
    );
    // Imported meshes can share an instance origin despite different geometry/pose depths.
    scene.meshes[0].position = [0.; 3];
    scene.meshes[0].mesh = Some(Arc::new(
        Mesh::triangles(
            mesh.vertices()
                .iter()
                .map(|v| MeshVertex {
                    position: [v.position[0], v.position[1], v.position[2] - 0.5],
                    ..*v
                })
                .collect(),
            mesh.indices().to_vec(),
        )
        .unwrap(),
    ));
    assert_eq!(
        blended,
        render(&scene),
        "baked geometry offsets must affect transparent sorting"
    );
    scene.meshes[0].mesh = Some(Arc::new(
        Mesh::skinned_triangles(
            mesh.vertices().to_vec(),
            mesh.indices().to_vec(),
            vec![
                nico_assets::SkinWeights {
                    joints: [0; 4],
                    weights: [1., 0., 0., 0.]
                };
                3
            ],
            1,
        )
        .unwrap(),
    ));
    let mut translated = nico_assets::model::IDENTITY;
    translated[3][2] = -0.5;
    scene.meshes[0].skin_palette = Some(Arc::new(vec![translated]));
    assert_eq!(
        blended,
        render(&scene),
        "skin pose offsets must affect transparent sorting"
    );
    // Compare lit affine skinning against explicitly transformed CPU geometry.
    scene.meshes.truncate(1);
    scene.meshes[0].position = [0.; 3];
    scene.meshes[0].material = Some(Arc::new(PbrMaterial::default()));
    scene.lighting.radiance = [1.; 3];
    scene.lighting.ambient = [0.1; 3];
    let skin_mesh = Mesh::skinned_triangles(
        mesh.vertices().to_vec(),
        mesh.indices().to_vec(),
        vec![
            nico_assets::SkinWeights {
                joints: [0; 4],
                weights: [1., 0., 0., 0.]
            };
            3
        ],
        1,
    )
    .unwrap()
    .with_normals(vec![[1., 0., 1.]; 3])
    .unwrap();
    scene.meshes[0].mesh = Some(Arc::new(skin_mesh));
    scene.meshes[0].skin_palette = Some(Arc::new(vec![[
        [2., 0., 0., 0.],
        [0.5, 1., 0., 0.],
        [0., 0., 0.5, 0.],
        [0., 0., 0., 1.],
    ]]));
    let skinned = render(&scene);
    let cpu_vertices = mesh
        .vertices()
        .iter()
        .map(|v| MeshVertex {
            position: [
                2. * v.position[0] + 0.5 * v.position[1],
                v.position[1],
                0.5 * v.position[2],
            ],
            ..*v
        })
        .collect();
    scene.meshes[0].mesh = Some(Arc::new(
        Mesh::triangles(cpu_vertices, mesh.indices().to_vec())
            .unwrap()
            .with_normals(vec![[0.5, -0.25, 2.]; 3])
            .unwrap(),
    ));
    scene.meshes[0].skin_palette = None;
    let cpu = render(&scene);
    assert!(
        skinned.iter().zip(&cpu).all(|(a, b)| a.abs_diff(*b) <= 1),
        "lit skin normals must use the inverse transpose, including shear"
    );
    // Mirroring is node metadata, independent of static/skinned geometry layout.
    let skin_source = Arc::new(
        Mesh::skinned_triangles(
            mesh.vertices().to_vec(),
            mesh.indices().to_vec(),
            vec![
                nico_assets::SkinWeights {
                    joints: [0; 4],
                    weights: [1., 0., 0., 0.]
                };
                3
            ],
            1,
        )
        .unwrap(),
    );
    scene.meshes[0].mesh = Some(skin_source.clone());
    scene.meshes[0].skin_palette = Some(Arc::new(vec![nico_assets::model::IDENTITY]));
    let front = render(&scene);
    let mut reflection = nico_assets::model::IDENTITY;
    reflection[0][0] = -1.;
    scene.meshes[0].skin_palette = Some(Arc::new(vec![reflection]));
    let culled = render(&scene);
    assert_eq!(
        pixel(&culled, 32, 32),
        pixel(&culled, 0, 0),
        "single-sided reversed winding must be culled"
    );
    scene.meshes[0].mirrored = true;
    let mirrored = render(&scene);
    assert_eq!(
        mirrored, front,
        "mirrored skinned node preserves its front-face shading"
    );
    let reflected = mesh
        .vertices()
        .iter()
        .map(|v| MeshVertex {
            position: [-v.position[0], v.position[1], v.position[2]],
            ..*v
        })
        .collect();
    scene.meshes[0].mesh = Some(Arc::new(
        Mesh::triangles(reflected, mesh.indices().to_vec())
            .unwrap()
            .with_normals(vec![[0., 0., 1.]; 3])
            .unwrap(),
    ));
    scene.meshes[0].skin_palette = None;
    for alpha in [AlphaMode::Opaque, AlphaMode::Mask, AlphaMode::Blend] {
        scene.meshes[0].material = Some(Arc::new(PbrMaterial {
            alpha,
            double_sided: true,
            ..Default::default()
        }));
        assert_eq!(
            render(&scene),
            front,
            "mirrored double-sided static normals must remain outward for every alpha mode"
        );
        scene.meshes[0].mesh = Some(skin_source.clone());
        scene.meshes[0].skin_palette = Some(Arc::new(vec![reflection]));
        assert_eq!(
            render(&scene),
            front,
            "mirrored double-sided skin normals must remain outward"
        );
        scene.meshes[0].skin_palette = None;
        let reflected = mesh
            .vertices()
            .iter()
            .map(|v| MeshVertex {
                position: [-v.position[0], v.position[1], v.position[2]],
                ..*v
            })
            .collect();
        scene.meshes[0].mesh = Some(Arc::new(
            Mesh::triangles(reflected, mesh.indices().to_vec())
                .unwrap()
                .with_normals(vec![[0., 0., 1.]; 3])
                .unwrap(),
        ));
    }
    if let Some(directory) = std::env::var_os("NICO_PBR_CAPTURE_DIR") {
        let directory = std::path::PathBuf::from(directory);
        std::fs::create_dir_all(&directory).unwrap();
        for (name, pixels) in [
            ("rough", rough),
            ("smooth", smooth),
            ("metal", metal),
            ("normal", normal),
            ("srgb", srgb),
            ("mask", masked),
            ("blend", blended),
            ("skinned", skinned),
            ("mirrored", mirrored),
        ] {
            let mut ppm = b"P6\n64 64\n255\n".to_vec();
            for pixel in pixels.as_chunks::<4>().0 {
                ppm.extend_from_slice(&pixel[..3]);
            }
            std::fs::write(directory.join(format!("{name}.ppm")), ppm).unwrap();
        }
    }
}

#[test]
#[ignore = "requires a real graphics adapter; run explicitly for PBR changes"]
fn gpu_pbr_rejects_invalid_frames_recovers_from_skips_and_retires_sources() {
    use nico_assets::{MaterialTexture, Mesh, MeshVertex, PbrMaterial};
    use nico_presentation::{Camera3d, MeshInstance, Quaternion, Scene3d, SceneLighting};
    use nico_render::{MeshRenderPipeline, RenderStatus};
    let (device, queue, mut target) = setup();
    let queue = UploadQueue {
        inner: queue,
        geometry_bytes: Default::default(),
        texture_bytes: Default::default(),
    };
    let mut renderer = MeshRenderPipeline::new(
        &device,
        target.format,
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/meshes.wgsl"
        )),
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/quads.wgsl"
        )),
    )
    .unwrap();
    let mesh = Arc::new(
        Mesh::triangles(
            vec![
                MeshVertex {
                    position: [-1., -1., 0.],
                    uv: [0.; 2],
                },
                MeshVertex {
                    position: [1., -1., 0.],
                    uv: [0.; 2],
                },
                MeshVertex {
                    position: [0., 1., 0.],
                    uv: [0.; 2],
                },
            ],
            vec![0, 1, 2],
        )
        .unwrap(),
    );
    let image = Arc::new(Texture::rgba8(1, 1, vec![255, 0, 0, 255]).unwrap());
    let material = Arc::new(PbrMaterial {
        base_color_texture: Some(MaterialTexture::new(image.clone())),
        ..Default::default()
    });
    let weak_image = Arc::downgrade(&image);
    let weak_material = Arc::downgrade(&material);
    let weak_mesh = Arc::downgrade(&mesh);
    let mut scene = Scene3d {
        camera: Camera3d::looking_at([0., 0., 3.], [0.; 3], [0., 1., 0.]).unwrap(),
        lighting: SceneLighting {
            radiance: [0.; 3],
            ambient: [1.; 3],
            ..Default::default()
        },
        meshes: vec![MeshInstance {
            mirrored: false,
            material: Some(material.clone()),
            mesh: Some(mesh.clone()),
            skin_palette: None,
            texture: None,
            position: [0.; 3],
            orientation: Quaternion::IDENTITY,
            scale: 1.,
            color: [1.; 4],
        }],
    };
    let canvas = Scene2d::default();
    let ui = UiScene::default();
    let extent = Extent3d::surface(64, 64);
    renderer
        .render(
            &device,
            &queue,
            &mut target,
            &scene,
            &canvas,
            &ui,
            [64.; 2],
            extent,
        )
        .unwrap();
    let first = pixels(&device, &queue, &target);
    assert_eq!(pixel(&first, 32, 32), [255, 0, 0, 255]);
    let geometry_before = queue.geometry_bytes.get();
    let textures_before = queue.texture_bytes.get();
    let hidden = Scene3d {
        meshes: Vec::new(),
        ..scene.clone()
    };
    for _ in 0..8 {
        for frame in [&hidden, &scene] {
            renderer
                .render(
                    &device,
                    &queue,
                    &mut target,
                    frame,
                    &canvas,
                    &ui,
                    [64.; 2],
                    extent,
                )
                .unwrap();
        }
    }
    let geometry_reuploaded = queue.geometry_bytes.get() - geometry_before;
    let textures_reuploaded = queue.texture_bytes.get() - textures_before;
    eprintln!(
        "eight visibility reentries: {geometry_reuploaded} geometry bytes, {textures_reuploaded} texture bytes reuploaded"
    );
    assert_eq!(
        (geometry_reuploaded, textures_reuploaded),
        (0, 0),
        "live assets must survive visibility changes"
    );
    assert_eq!(pixels(&device, &queue, &target), first);
    assert_eq!(
        Arc::strong_count(&mesh),
        2,
        "GPU residency must not pin CPU geometry"
    );
    assert_eq!(
        Arc::strong_count(&material),
        2,
        "GPU residency must not pin CPU materials"
    );
    assert_eq!(
        Arc::strong_count(&image),
        2,
        "GPU residency must not pin decoded pixels"
    );
    let oversized = device.capabilities().limits.max_texture_dimension_2d + 1;
    let large_image =
        Arc::new(Texture::rgba8(oversized, 1, vec![255; oversized as usize * 4]).unwrap());
    {
        let mut reject = |scene: &Scene3d, canvas: &Scene2d, ui: &UiScene, extent| {
            let acquired = target.acquired;
            let presented = target.presented;
            assert!(
                renderer
                    .render(
                        &device,
                        &queue,
                        &mut target,
                        scene,
                        canvas,
                        ui,
                        [64.; 2],
                        extent
                    )
                    .is_err()
            );
            assert_eq!(
                target.acquired, acquired,
                "invalid data must not acquire a surface frame"
            );
            assert_eq!(target.presented, presented);
            assert_eq!(
                pixels(&device, &queue, &target),
                first,
                "rejection must not overwrite the prior frame"
            );
        };
        reject(
            &scene,
            &canvas,
            &ui,
            Extent3d {
                width: 64,
                height: 64,
                depth_or_layers: 2,
            },
        );
        reject(&scene, &canvas, &ui, Extent3d::surface(oversized, 64));
        let mut bad = scene.clone();
        bad.lighting.radiance[0] = f32::NAN;
        reject(&bad, &canvas, &ui, extent);
        bad = scene.clone();
        bad.camera.near = -1.;
        reject(&bad, &canvas, &ui, extent);
        bad = scene.clone();
        bad.meshes[0].scale = f32::MIN_POSITIVE;
        reject(&bad, &canvas, &ui, extent);
        bad = scene.clone();
        bad.meshes[0].material = Some(Arc::new(PbrMaterial {
            roughness: f32::NAN,
            ..Default::default()
        }));
        reject(&bad, &canvas, &ui, extent);
        bad = scene.clone();
        bad.meshes[0].material = Some(Arc::new(PbrMaterial {
            normal_texture: Some(MaterialTexture::new(large_image.clone())),
            ..Default::default()
        }));
        reject(&bad, &canvas, &ui, extent);
        bad.meshes[0].material = None;
        bad.meshes[0].texture = Some(large_image.clone());
        reject(&bad, &canvas, &ui, extent);
        bad = scene.clone();
        bad.meshes[0].mesh = Some(Arc::new(
            Mesh::triangles(vec![mesh.vertices()[0]; 250_001], vec![0, 1, 2]).unwrap(),
        ));
        reject(&bad, &canvas, &ui, extent);
        bad = scene.clone();
        bad.meshes[0].skin_palette = Some(Arc::new(vec![nico_assets::model::IDENTITY]));
        reject(&bad, &canvas, &ui, extent);
        let quad = Quad {
            center: [16.; 2],
            size: [16.; 2],
            color: [1.; 4],
            texture: Some(large_image.clone()),
        };
        reject(
            &scene,
            &Scene2d {
                world: vec![quad.clone()],
                ..Default::default()
            },
            &ui,
            extent,
        );
        reject(&scene, &canvas, &UiScene { quads: vec![quad] }, extent);
    }
    let acquired = target.acquired;
    target.format = TextureFormat::Rgba8Unorm;
    assert!(
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &canvas,
                &ui,
                [64.; 2],
                extent
            )
            .is_err()
    );
    assert_eq!(target.acquired, acquired);
    target.format = TextureFormat::Rgba8UnormSrgb;
    for skip in [
        RenderStatus::ZeroSized,
        RenderStatus::Timeout,
        RenderStatus::Occluded,
    ] {
        target.skip = Some(skip);
        let presented = target.presented;
        assert_eq!(
            renderer
                .render(
                    &device,
                    &queue,
                    &mut target,
                    &scene,
                    &canvas,
                    &ui,
                    [64.; 2],
                    extent
                )
                .unwrap(),
            skip
        );
        assert_eq!(target.presented, presented);
        assert_eq!(pixels(&device, &queue, &target), first);
    }
    target.fail_acquire = true;
    let presented = target.presented;
    assert!(
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &canvas,
                &ui,
                [64.; 2],
                extent
            )
            .is_err()
    );
    assert_eq!(target.presented, presented);
    renderer
        .render(
            &device,
            &queue,
            &mut target,
            &scene,
            &canvas,
            &ui,
            [64.; 2],
            extent,
        )
        .unwrap();
    assert_eq!(
        pixels(&device, &queue, &target),
        first,
        "valid data must render after failed/skipped acquisitions"
    );
    let mut blue = (*material).clone();
    blue.base_color_texture = Some(MaterialTexture::new(Arc::new(
        Texture::rgba8(1, 1, vec![0, 0, 255, 255]).unwrap(),
    )));
    scene.meshes[0].material = Some(Arc::new(blue));
    drop(material);
    drop(image);
    assert!(weak_material.upgrade().is_none() && weak_image.upgrade().is_none());
    renderer
        .render(
            &device,
            &queue,
            &mut target,
            &scene,
            &canvas,
            &ui,
            [64.; 2],
            extent,
        )
        .unwrap();
    assert_eq!(
        pixel(&pixels(&device, &queue, &target), 32, 32),
        [0, 0, 255, 255],
        "replacement materials must not reuse stale texture bindings"
    );
    scene.meshes.clear();
    drop(mesh);
    assert!(weak_mesh.upgrade().is_none());
    renderer
        .render(
            &device,
            &queue,
            &mut target,
            &scene,
            &canvas,
            &ui,
            [64.; 2],
            extent,
        )
        .unwrap();
    let empty = pixels(&device, &queue, &target);
    assert_eq!(pixel(&empty, 32, 32), pixel(&empty, 0, 0));
}

#[test]
#[ignore]
fn gpu_pbr_backface_normal_mapping_matches_reversed_authored_normals() {
    use nico_assets::{MaterialTexture, Mesh, MeshVertex, PbrMaterial};
    use nico_presentation::{Camera3d, MeshInstance, Quaternion, Scene3d, SceneLighting};
    use nico_render::MeshRenderPipeline;
    use std::sync::Arc;
    let (device, queue, mut target) = setup();
    let mut renderer = MeshRenderPipeline::new(
        &device,
        TextureFormat::Rgba8UnormSrgb,
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/meshes.wgsl"
        )),
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/quads.wgsl"
        )),
    )
    .unwrap();
    renderer
        .enable_skinning(
            &device,
            builtin_shaders::bootstrap_wgsl(include_bytes!(
                "../../../assets/presentation/shaders/generated/wgpu/skinned_meshes.wgsl"
            )),
        )
        .unwrap();
    let mesh = Arc::new(
        Mesh::triangles(
            vec![
                MeshVertex {
                    position: [-1., -1., 0.],
                    uv: [0., 1.],
                },
                MeshVertex {
                    position: [1., -1., 0.],
                    uv: [1., 1.],
                },
                MeshVertex {
                    position: [0., 1., 0.],
                    uv: [0.5, 0.],
                },
            ],
            vec![0, 1, 2],
        )
        .unwrap(),
    );
    let draw = MeshInstance {
        mirrored: false,
        material: Some(Arc::new(PbrMaterial::default())),
        mesh: Some(mesh.clone()),
        skin_palette: None,
        texture: None,
        position: [0.; 3],
        orientation: Quaternion::IDENTITY,
        scale: 1.,
        color: [1.; 4],
    };
    let mut scene = Scene3d {
        camera: Camera3d::looking_at([0., 0., 3.], [0.; 3], [0., 1., 0.]).unwrap(),
        lighting: SceneLighting {
            direction: [0., 0., 1.],
            radiance: [1.; 3],
            ambient: [0.; 3],
        },
        meshes: vec![draw],
    };
    let mut render = |scene: &Scene3d| {
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        pixels(&device, &queue, &target)
    };
    scene.meshes[0].material = Some(Arc::new(PbrMaterial {
        double_sided: true,
        normal_texture: Some(MaterialTexture::new(Arc::new(
            Texture::rgba8(1, 1, vec![230, 204, 204, 255]).unwrap(),
        ))),
        ..Default::default()
    }));
    scene.camera = Camera3d::looking_at([0., 0., -3.], [0.; 3], [0., 1., 0.]).unwrap();
    scene.lighting.direction = [-0.8, 0.36, -0.48];
    let actual = render(&scene);
    scene.meshes[0].mesh = Some(Arc::new(
        Mesh::skinned_triangles(
            mesh.vertices().to_vec(),
            mesh.indices().to_vec(),
            vec![
                nico_assets::SkinWeights {
                    joints: [0; 4],
                    weights: [1., 0., 0., 0.]
                };
                3
            ],
            1,
        )
        .unwrap(),
    ));
    scene.meshes[0].skin_palette = Some(Arc::new(vec![nico_assets::model::IDENTITY]));
    assert_eq!(
        render(&scene),
        actual,
        "static and skinned back-face shading must agree"
    );
    scene.meshes[0].skin_palette = None;

    let mapped = [
        230. / 255. * 2. - 1.,
        -(204. / 255. * 2. - 1.),
        204. / 255. * 2. - 1.,
    ];
    scene.meshes[0].mesh = Some(Arc::new(
        Mesh::triangles(mesh.vertices().to_vec(), vec![2, 1, 0])
            .unwrap()
            .with_normals(vec![mapped.map(|x| -x); 3])
            .unwrap(),
    ));
    scene.meshes[0].material = Some(Arc::new(PbrMaterial::default()));
    let expected = render(&scene);
    assert_eq!(
        pixel(&actual, 32, 32),
        pixel(&expected, 32, 32),
        "back face normal mapped vs explicit reversed normal"
    );
}

#[test]
#[ignore]
fn gpu_pbr_skin_normals_are_independent_of_asset_units() {
    use nico_assets::{Mesh, MeshVertex, PbrMaterial};
    use nico_presentation::{Camera3d, MeshInstance, Quaternion, Scene3d, SceneLighting};
    use nico_render::MeshRenderPipeline;
    use std::sync::Arc;
    let (device, queue, mut target) = setup();
    let mut renderer = MeshRenderPipeline::new(
        &device,
        TextureFormat::Rgba8UnormSrgb,
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/meshes.wgsl"
        )),
        builtin_shaders::bootstrap_wgsl(include_bytes!(
            "../../../assets/presentation/shaders/generated/wgpu/quads.wgsl"
        )),
    )
    .unwrap();
    renderer
        .enable_skinning(
            &device,
            builtin_shaders::bootstrap_wgsl(include_bytes!(
                "../../../assets/presentation/shaders/generated/wgpu/skinned_meshes.wgsl"
            )),
        )
        .unwrap();
    let mesh = Arc::new(
        Mesh::triangles(
            vec![
                MeshVertex {
                    position: [-1., -1., 0.],
                    uv: [0., 1.],
                },
                MeshVertex {
                    position: [1., -1., 0.],
                    uv: [1., 1.],
                },
                MeshVertex {
                    position: [0., 1., 0.],
                    uv: [0.5, 0.],
                },
            ],
            vec![0, 1, 2],
        )
        .unwrap(),
    );
    let draw = MeshInstance {
        mirrored: false,
        material: Some(Arc::new(PbrMaterial::default())),
        mesh: Some(mesh.clone()),
        skin_palette: None,
        texture: None,
        position: [0.; 3],
        orientation: Quaternion::IDENTITY,
        scale: 1.,
        color: [1.; 4],
    };
    let mut scene = Scene3d {
        camera: Camera3d::looking_at([0., 0., 3.], [0.; 3], [0., 1., 0.]).unwrap(),
        lighting: SceneLighting {
            direction: [0., 0., 1.],
            radiance: [1.; 3],
            ambient: [0.; 3],
        },
        meshes: vec![draw],
    };
    let mut render = |scene: &Scene3d| {
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        pixels(&device, &queue, &target)
    };
    for unit_scale in [0.0001_f32, 1., 10000.] {
        let vertices = mesh
            .vertices()
            .iter()
            .map(|v| MeshVertex {
                position: v.position.map(|x| x / unit_scale),
                ..*v
            })
            .collect();
        scene.meshes[0].mesh = Some(Arc::new(
            Mesh::skinned_triangles(
                vertices,
                vec![0, 1, 2],
                vec![
                    nico_assets::SkinWeights {
                        joints: [0; 4],
                        weights: [1., 0., 0., 0.]
                    };
                    3
                ],
                1,
            )
            .unwrap()
            .with_normals(vec![[1., 0., 1.]; 3])
            .unwrap(),
        ));
        let mut matrix = nico_assets::model::IDENTITY;
        matrix[0][0] = 2. * unit_scale;
        matrix[1][1] = unit_scale;
        matrix[2][2] = 0.5 * unit_scale;
        scene.meshes[0].skin_palette = Some(Arc::new(vec![matrix]));
        let actual = render(&scene);
        let vertices = mesh
            .vertices()
            .iter()
            .map(|v| MeshVertex {
                position: [v.position[0] * 2., v.position[1], v.position[2] * 0.5],
                ..*v
            })
            .collect();
        scene.meshes[0].mesh = Some(Arc::new(
            Mesh::triangles(vertices, vec![0, 1, 2])
                .unwrap()
                .with_normals(vec![[0.5, 0., 2.]; 3])
                .unwrap(),
        ));
        scene.meshes[0].skin_palette = None;
        let expected = render(&scene);
        assert_eq!(
            pixel(&actual, 32, 32),
            pixel(&expected, 32, 32),
            "skin scale {unit_scale} vs CPU inverse transpose"
        );
    }
}
