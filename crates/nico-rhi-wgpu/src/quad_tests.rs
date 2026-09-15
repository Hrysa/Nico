//! Opt-in real-GPU validation, separate from portable workspace tests.
use super::*;
use nico_assets::Texture;
use nico_presentation::{Camera2d, Quad, Scene2d};
use nico_render::QuadRenderPipeline;

struct Target {
    texture: wgpu::Texture,
}
impl RhiSurface<WgpuDevice, WgpuQueue> for Target {
    type Frame = ();
    fn format(&self) -> TextureFormat {
        TextureFormat::Rgba8UnormSrgb
    }
    fn resize(&mut self, _: &WgpuDevice, _: Extent3d) {}
    fn acquire(&mut self, _: &WgpuDevice) -> Result<SurfaceAcquire<(), WgpuTextureView>, RhiError> {
        Ok(SurfaceAcquire::Acquired {
            frame: (),
            view: WgpuTextureView(
                self.texture
                    .create_view(&wgpu::TextureViewDescriptor::default()),
            ),
        })
    }
    fn present(&mut self, _: &WgpuDevice, _: &WgpuQueue, _: ()) {}
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
        hud: vec![hud],
    };
    renderer
        .render(&device, &queue, &mut target, &scene, [64.0, 64.0])
        .unwrap();
    let first = pixels(&device, &queue, &target);
    assert_eq!(pixel(&first, 20, 20), [255, 0, 0, 255]);
    assert_eq!(pixel(&first, 40, 20), [0, 255, 0, 255]);
    assert_eq!(pixel(&first, 20, 40), [0, 0, 255, 255]);
    assert_eq!(pixel(&first, 40, 40), pixel(&first, 60, 60));
    assert_eq!(pixel(&first, 5, 5), [255, 0, 0, 255]);
    scene.camera.center = [1.0, 0.0];
    renderer
        .render(&device, &queue, &mut target, &scene, [64.0, 64.0])
        .unwrap();
    let moved = pixels(&device, &queue, &target);
    assert_eq!(pixel(&moved, 5, 5), pixel(&first, 5, 5));
    assert_eq!(pixel(&moved, 4, 20), [255, 0, 0, 255]);
    assert_eq!(pixel(&moved, 40, 20), pixel(&moved, 60, 60));
    scene.hud = vec![Quad {
        center: [24.0, 24.0],
        size: [8.0, 8.0],
        color: [1.0, 1.0, 1.0, 0.5],
        texture: Some(Arc::new(
            Texture::rgba8(1, 1, vec![0, 0, 255, 255]).unwrap(),
        )),
    }];
    renderer
        .render(&device, &queue, &mut target, &scene, [64.0, 64.0])
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
    scene.hud.clear();
    drop(texture);
    renderer
        .render(&device, &queue, &mut target, &scene, [64.0, 64.0])
        .unwrap();
    let empty = pixels(&device, &queue, &target);
    assert_eq!(pixel(&empty, 4, 20), pixel(&empty, 60, 60));
    scene.hud.push(Quad {
        center: [8.0, 8.0],
        size: [8.0, 8.0],
        color: [1.0; 4],
        texture: None,
    });
    renderer
        .render(&device, &queue, &mut target, &scene, [64.0, 64.0])
        .unwrap();
    drop(renderer);
    let fallback = pixels(&device, &queue, &target);
    assert_eq!(pixel(&fallback, 5, 5), [255, 0, 255, 255]);
}

fn setup() -> (WgpuDevice, WgpuQueue, Target) {
    let instance = wgpu::Instance::new(
        wgpu::InstanceDescriptor {
            backends: wgpu::Backends::PRIMARY,
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
        mesh: Some(mesh),
        texture: Some(texture.clone()),
        position: [0.0; 3],
        orientation: nico_presentation::Quaternion::IDENTITY,
        scale: 1.0,
        color: [0.0, 1.0, 0.0, 1.0],
    };
    let far = MeshInstance {
        position: [0.0, 0.0, -1.0],
        color: [1.0, 0.0, 0.0, 1.0],
        ..near.clone()
    };
    let mut scene = Scene3d {
        camera: Camera3d {
            position: [0.0, 0.0, 3.0],
            orientation: nico_presentation::Quaternion::IDENTITY,
            ..Camera3d::default()
        },
        meshes: vec![near, far],
    };
    let hud = Scene2d {
        hud: vec![Quad {
            center: [8.0, 8.0],
            size: [8.0, 8.0],
            color: [0.0, 0.0, 1.0, 1.0],
            texture: Some(texture),
        }],
        ..Scene2d::default()
    };
    let extent = Extent3d::surface(64, 64);
    renderer
        .render(
            &device,
            &queue,
            &mut target,
            &scene,
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
    scene.meshes.remove(0);
    renderer
        .render(
            &device,
            &queue,
            &mut target,
            &scene,
            &hud,
            [64.0, 64.0],
            extent,
        )
        .unwrap();
    let farther = pixels(&device, &queue, &target);
    assert_eq!(pixel(&farther, 32, 32), [255, 0, 0, 255]);
    let green_count = first
        .chunks_exact(4)
        .filter(|p| *p == [0, 255, 0, 255])
        .count();
    let red_count = farther
        .chunks_exact(4)
        .filter(|p| *p == [255, 0, 0, 255])
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
