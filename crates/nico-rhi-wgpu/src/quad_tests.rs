//! Opt-in real-GPU validation, separate from portable workspace tests.
use super::*;
use nico_assets::Texture;
use nico_presentation::{Camera2d, Quad, Scene2d, UiScene};
use nico_render::QuadRenderPipeline;
mod affine_normal_tests;
mod arena_measurement;
pub(super) mod gpu_timestamps;
mod instance_bindings;
mod instance_measurement;
mod instance_tile_measurement;
mod visibility_tests;

#[test]
#[ignore = "requires a graphics adapter"]
fn gpu_async_buffer_readback_is_bounded_consumable_and_cancellable() {
    let (device, queue, _) = setup();
    assert!(device.supports_buffer_readback());
    let source = device
        .create_buffer(BufferDescriptor {
            label: None,
            size: 16,
            usages: BufferUsages::COPY_SOURCE | BufferUsages::COPY_DESTINATION,
        })
        .unwrap();
    let destination = device
        .create_buffer(BufferDescriptor {
            label: None,
            size: 16,
            usages: BufferUsages::MAP_READ | BufferUsages::COPY_DESTINATION,
        })
        .unwrap();
    assert!(
        device
            .create_buffer(BufferDescriptor {
                label: None,
                size: 16,
                usages: BufferUsages::MAP_READ | BufferUsages::STORAGE
            })
            .is_err()
    );
    for range in [0..0, 4..8, 0..3, 0..20, 0..MAX_BUFFER_READBACK_BYTES + 4] {
        assert!(device.read_buffer_async(&destination, range).is_err());
    }
    assert!(device.read_buffer_async(&source, 0..4).is_err());
    let submit = |value: u32| {
        queue.write_buffer(&source, 0, &value.to_le_bytes());
        let mut encoder = device.create_command_encoder(None);
        encoder.copy_buffer_to_buffer(&source, 0, &destination, 8, 4);
        queue.submit(vec![encoder.finish()]);
    };
    for value in [123_u32, 456] {
        submit(value);
        let mut ticket = device.read_buffer_async(&destination, 8..12).unwrap();
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(5);
        let bytes = loop {
            if let Some(bytes) = ticket.poll().unwrap() {
                break bytes;
            }
            assert!(
                std::time::Instant::now() < deadline,
                "asynchronous readback timed out"
            );
            std::thread::yield_now();
        };
        assert_eq!(bytes, value.to_le_bytes());
        assert!(ticket.poll().is_err());
    }
    submit(789);
    drop(device.read_buffer_async(&destination, 8..12).unwrap());
    // Dropping a pending or already-ready map must release the staging buffer.
    submit(1011);
    let mut ticket = device.read_buffer_async(&destination, 8..12).unwrap();
    let deadline = std::time::Instant::now() + std::time::Duration::from_secs(5);
    loop {
        if let Some(bytes) = ticket.poll().unwrap() {
            assert_eq!(bytes, 1011_u32.to_le_bytes());
            break;
        }
        assert!(std::time::Instant::now() < deadline);
        std::thread::yield_now();
    }
    device.check_failure().unwrap();
}

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
#[ignore = "requires a real graphics adapter"]
fn gpu_visibility_matches_cpu_ids_counts_and_indirect_arguments_across_resets() {
    for partitioned in [false, true] {
        visibility_reference(partitioned);
    }
}
fn visibility_reference(partitioned: bool) {
    use glam::{Mat4, Vec3};
    use nico_presentation::InstanceBounds;
    use nico_render::visibility::{
        GpuVisibilityPage, VisibilityGroup, VisibilityRecord, visible_ids,
    };
    let (device, queue, _) = setup();
    for (count, group_count) in [
        (0, 1),
        (0, 7),
        (1, 1),
        (129, 1),
        (1025, 1),
        (129, 7),
        (129, 512),
        (1025, 512),
    ] {
        let groups: Vec<_> = (0..group_count)
            .map(|g| VisibilityGroup {
                index_count: 3 + 3 * g,
                first_index: g * 3,
                base_vertex: -(g as i32),
                max_distance: if g % 2 == 0 { 100. } else { 0.8 },
            })
            .collect();
        let records: Vec<_> = (0..count)
            .map(|i| {
                let x = ((i % 17) as f32 - 8.) * 0.22;
                let y = ((i % 11) as f32 - 5.) * 0.21;
                VisibilityRecord {
                    bounds: match i {
                        0 => InstanceBounds::new(
                            [-18.552738, -0.4, -51.624855],
                            [-17.518452, 1.0117071, -50.5307],
                        ),
                        1 => InstanceBounds::new(
                            [-59.165154, -0.4, -25.84423],
                            [-58.178246, 0.7038448, -24.8498],
                        ),
                        _ => InstanceBounds::new([x, y, 0.3], [x + 0.1, y + 0.1, 0.7]),
                    }
                    .unwrap(),
                    group: i % group_count,
                }
            })
            .collect();
        let make_page = || {
            let shader = ShaderModuleDescriptor {
                label: Some("visibility test"),
                format: ShaderFormat::Wgsl,
                code: include_bytes!(
                    "../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl"
                ),
            };
            if partitioned {
                let kernel = Arc::new(
                    nico_render::visibility::GpuVisibilityKernel::new(&device, shader).unwrap(),
                );
                GpuVisibilityPage::with_partitioned_kernel(
                    &device, &queue, kernel, &records, &groups,
                )
            } else {
                GpuVisibilityPage::new(&device, &queue, shader, &records, &groups)
            }
            .unwrap()
        };
        let pages = [make_page(), make_page()];
        let mut duplicate_encoder =
            device.create_command_encoder(Some("duplicate visibility page"));
        assert!(
            GpuVisibilityPage::record_pages(
                &[&pages[0], &pages[0]],
                &queue,
                &mut duplicate_encoder,
                Mat4::IDENTITY,
                Vec3::ZERO,
            )
            .is_err()
        );
        let mut pending = Vec::new();
        for (view, clip) in [
            Mat4::IDENTITY,
            Mat4::from_translation(Vec3::new(100., 0., 0.)),
            Mat4::IDENTITY,
            Mat4::perspective_rh(1.2, 1.3, 0.1, 100.)
                * Mat4::look_at_rh(Vec3::new(0., 1., 3.), Vec3::ZERO, Vec3::Y),
            nico_presentation::Camera3d::looking_at([25., 35., 40.], [0.; 3], [0., 1., 0.])
                .unwrap()
                .view_projection(840. / 764.)
                .unwrap(),
        ]
        .into_iter()
        .enumerate()
        {
            let page = &pages[view % pages.len()];
            let mut encoder = device.create_command_encoder(Some("visibility test"));
            GpuVisibilityPage::record_pages(
                &[&pages[0], &pages[1]],
                &queue,
                &mut encoder,
                clip,
                Vec3::ZERO,
            )
            .unwrap();
            let size = page.layout().bytes();
            let readback = WgpuBuffer(device.inner.create_buffer(&wgpu::BufferDescriptor {
                label: Some("visibility readback"),
                size,
                usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
                mapped_at_creation: false,
            }));
            encoder.copy_buffer_to_buffer(page.output(), 0, &readback, 0, size);
            queue.submit(vec![encoder.finish()]);
            pending.push((view, clip, readback));
        }
        // Submit alternating views and reuse each page before any CPU wait.
        // Queue ordering must preserve each captured count/ID/argument result.
        for (view, clip, readback) in pending {
            let page = &pages[view % pages.len()];
            let (tx, rx) = std::sync::mpsc::channel();
            readback
                .0
                .slice(..)
                .map_async(wgpu::MapMode::Read, move |r| tx.send(r).unwrap());
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
            let bytes = readback.0.slice(..).get_mapped_range().unwrap().to_vec();
            readback.0.unmap();
            let words: Vec<_> = bytes
                .chunks_exact(4)
                .map(|v| u32::from_le_bytes(v.try_into().unwrap()))
                .collect();
            let expected = visible_ids(&records, &groups, clip, Vec3::ZERO).unwrap();
            let layout = page.layout();
            let mut total = 0;
            for (g, ids) in expected.iter().enumerate() {
                assert_eq!(words[layout.counts_word() as usize + g], ids.len() as u32);
                assert_eq!(words[layout.offsets_word() as usize + g], total);
                let start = (layout.ids_word() + total) as usize;
                let mut actual = words[start..start + ids.len()].to_vec();
                actual.sort_unstable();
                assert_eq!(&actual, ids);
                let args = layout.indirect_byte(g as u32).unwrap() as usize / 4;
                assert_eq!(
                    &words[args..args + 5],
                    &[
                        groups[g].index_count,
                        ids.len() as u32,
                        groups[g].first_index,
                        groups[g].base_vertex as u32,
                        0
                    ]
                );
                total += if partitioned {
                    records
                        .iter()
                        .filter(|record| record.group == g as u32)
                        .count() as u32
                } else {
                    ids.len() as u32
                };
            }
            assert!(total <= count);
            device.check_failure().unwrap();
        }
    }
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

#[test]
#[ignore = "requires a graphics adapter"]
fn gpu_instance_upload_pacing_preserves_residency_and_finishes_deferred_chunks() {
    instance_upload_pacing(false, false);
}
#[test]
#[ignore = "requires a graphics adapter"]
fn gpu_indirect_instance_upload_pacing_includes_visibility_sources() {
    instance_upload_pacing(true, false);
}
#[test]
#[ignore = "requires a graphics adapter"]
fn gpu_direct_oversized_batch_splits_without_repeated_uploads() {
    instance_upload_pacing(false, true);
}
#[test]
#[ignore = "requires a graphics adapter"]
fn gpu_indirect_oversized_batch_splits_without_repeated_uploads() {
    instance_upload_pacing(true, true);
}
fn instance_upload_pacing(indirect: bool, split: bool) {
    use nico_presentation::{Camera3d, InstanceBatch, InstanceRecord, Scene3d};
    let (device, queue, mut target) = setup();
    let mut renderer = nico_render::MeshRenderPipeline::new(
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
    renderer
        .enable_instancing(
            &device,
            builtin_shaders::bootstrap_wgsl(include_bytes!(
                "../../../assets/presentation/shaders/generated/wgpu/instanced_meshes.wgsl"
            )),
        )
        .unwrap();
    let mesh = Arc::new(
        nico_assets::Mesh::triangles(
            [[-0.2, -0.2, 0.], [0.2, -0.2, 0.], [0., 0.2, 0.]]
                .into_iter()
                .map(|position| nico_assets::MeshVertex {
                    position,
                    uv: [0.; 2],
                })
                .collect(),
            vec![0, 1, 2],
        )
        .unwrap(),
    );
    let material = Arc::new(nico_assets::PbrMaterial {
        double_sided: true,
        ..Default::default()
    });
    let mut scene = Scene3d {
        camera: Camera3d::looking_at([0., 0., 3.], [0.; 3], [0., 1., 0.]).unwrap(),
        instance_batches: [-0.4, 0.4]
            .into_iter()
            .enumerate()
            .map(|(id, x)| {
                Arc::new(
                    InstanceBatch::new(
                        mesh.clone(),
                        material.clone(),
                        vec![
                            InstanceRecord::new(
                                id as u64,
                                0,
                                glam::Mat4::from_translation(glam::Vec3::new(x, 0., 0.)),
                                [1.; 4],
                            )
                            .unwrap(),
                        ],
                        100.,
                    )
                    .unwrap(),
                )
            })
            .collect(),
        ..Default::default()
    };
    if split {
        let records = scene
            .instance_batches
            .iter()
            .flat_map(|b| b.records().iter().cloned())
            .collect();
        scene.instance_batches = vec![Arc::new(
            InstanceBatch::new(mesh, material, records, 100.).unwrap(),
        )];
    }
    if indirect {
        renderer.enable_gpu_instancing(&device,
            builtin_shaders::bootstrap_wgsl(include_bytes!("../../../assets/presentation/shaders/generated/wgpu/instanced_storage.wgsl")),
            ShaderModuleDescriptor { label: None, format: ShaderFormat::Wgsl,
                code: include_bytes!("../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl") }).unwrap();
    }
    let cost = if indirect { 184 } else { 112 };
    renderer
        .set_instance_source_upload_budget(Some(cost - 1))
        .unwrap();
    assert!(
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64)
            )
            .is_err()
    );
    renderer
        .set_instance_source_upload_budget(Some(cost))
        .unwrap();
    assert!(renderer.set_instance_source_upload_budget(Some(0)).is_err());
    let source = &scene.instance_batches[0];
    let mut oversized_scene = scene.clone();
    oversized_scene.instance_batches = vec![Arc::new(
        InstanceBatch::new(
            source.mesh().clone(),
            source.material().clone(),
            (0..513)
                .map(|id| InstanceRecord::new(id, 0, glam::Mat4::IDENTITY, [1.; 4]).unwrap())
                .collect(),
            100.,
        )
        .unwrap(),
    )];
    let mode = if indirect {
        nico_render::InstanceRenderMode::Gpu
    } else {
        nico_render::InstanceRenderMode::Cpu
    };
    assert!(!renderer.supports_instance_mode(&device, &oversized_scene, mode));
    assert!(
        renderer
            .set_instance_mode(&device, &oversized_scene, mode)
            .is_err()
    );
    assert_eq!(
        renderer.instance_mode(),
        nico_render::InstanceRenderMode::Auto
    );
    drop(oversized_scene);
    if indirect {
        renderer.set_instance_mode(&device, &scene, mode).unwrap();
    }
    let mut captures = Vec::new();
    for frame in 0..3 {
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        let stats = renderer.instance_stats();
        assert_eq!(stats.instance_upload_bytes, if frame < 2 { 112 } else { 0 });
        assert_eq!(
            stats.visibility_upload_bytes,
            if indirect && frame < 2 { 72 } else { 0 }
        );
        assert!(stats.instance_upload_bytes + stats.visibility_upload_bytes <= cost);
        assert_eq!(
            stats.indirect_draws,
            if indirect { stats.submitted_draws } else { 0 }
        );
        assert_eq!(stats.deferred_upload_chunks, u32::from(frame == 0));
        assert_eq!(stats.retained_split_cpu_bytes > 0, split);
        assert_eq!(stats.submitted_draws, if frame == 0 { 1 } else { 2 });
        captures.push(pixels(&device, &queue, &target));
    }
    assert_ne!(captures[0], captures[1]);
    assert_eq!(captures[1], captures[2]);
    if indirect {
        // A split source can fall below the automatic GPU threshold even when
        // the original batch met it. Upload pacing must still make progress.
        renderer
            .set_instance_mode(&device, &scene, nico_render::InstanceRenderMode::Auto)
            .unwrap();
        renderer.set_instance_auto_gpu_min_records(2).unwrap();
        for _ in 0..3 {
            renderer
                .render(
                    &device,
                    &queue,
                    &mut target,
                    &scene,
                    &Scene2d::default(),
                    &UiScene::default(),
                    [64.; 2],
                    Extent3d::surface(64, 64),
                )
                .unwrap();
        }
        assert_eq!(renderer.instance_stats().indirect_draws, 0);
        assert_eq!(renderer.instance_stats().submitted_instances, 2);
        assert_eq!(renderer.instance_stats().instance_upload_bytes, 0);
        assert_eq!(pixels(&device, &queue, &target), captures[2]);
        renderer.set_instance_auto_gpu_min_records(0).unwrap();
        for _ in 0..3 {
            renderer
                .render(
                    &device,
                    &queue,
                    &mut target,
                    &scene,
                    &Scene2d::default(),
                    &UiScene::default(),
                    [64.; 2],
                    Extent3d::surface(64, 64),
                )
                .unwrap();
        }
    }
    if indirect && !split {
        renderer.set_instance_source_upload_budget(None).unwrap();
        for (minimum, indirect_draws, upload_bytes) in
            [(2, 0, 224), (2, 0, 0), (0, 0, 224), (0, 0, 0)]
        {
            renderer.set_instance_auto_gpu_min_records(minimum).unwrap();
            renderer
                .render(
                    &device,
                    &queue,
                    &mut target,
                    &scene,
                    &Scene2d::default(),
                    &UiScene::default(),
                    [64.; 2],
                    Extent3d::surface(64, 64),
                )
                .unwrap();
            let stats = renderer.instance_stats();
            assert_eq!(stats.indirect_draws, indirect_draws);
            assert_eq!(stats.instance_upload_bytes, upload_bytes);
            assert_eq!(pixels(&device, &queue, &target), captures[2]);
        }
        let first = &scene.instance_batches[0];
        let mut mixed = scene.clone();
        mixed.instance_batches[0] = Arc::new(
            InstanceBatch::new(
                first.mesh().clone(),
                first.material().clone(),
                vec![
                    first.records()[0].clone(),
                    InstanceRecord::new(42, 0, first.records()[0].transform(), [1.; 4]).unwrap(),
                ],
                100.,
            )
            .unwrap(),
        );
        renderer.set_instance_auto_gpu_min_records(2).unwrap();
        for frame in 0..2 {
            renderer
                .render(
                    &device,
                    &queue,
                    &mut target,
                    &mixed,
                    &Scene2d::default(),
                    &UiScene::default(),
                    [64.; 2],
                    Extent3d::surface(64, 64),
                )
                .unwrap();
            let stats = renderer.instance_stats();
            // Both complete bounds are visible: Auto draws the resident
            // sources directly even when one owns GPU visibility storage.
            assert_eq!(stats.indirect_draws, 0);
            assert_eq!(stats.gpu_candidate_instances, 0);
            assert_eq!(stats.submitted_instances, 3);
            assert_eq!(stats.submitted_draws, 2);
            if frame == 1 {
                assert_eq!(stats.instance_upload_bytes, 0);
                assert_eq!(stats.visibility_upload_bytes, 0);
            }
            assert_eq!(pixels(&device, &queue, &target), captures[2]);
        }
    }
    if split {
        let camera = scene.camera;
        scene.camera =
            Camera3d::looking_at([2000., 0., 3.], [2000., 0., 0.], [0., 1., 0.]).unwrap();
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        assert_eq!(renderer.instance_stats().submitted_draws, 0);
        scene.camera = camera;
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        assert_eq!(renderer.instance_stats().instance_upload_bytes, 0);
        assert_eq!(renderer.instance_stats().visibility_upload_bytes, 0);
        assert_eq!(pixels(&device, &queue, &target), captures[2]);
        // Increasing the allowance removes obsolete segmented residency instead
        // of keeping both segmented and unsplit GPU copies alive.
        renderer
            .set_instance_source_upload_budget(Some(cost * 2))
            .unwrap();
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        assert_eq!(renderer.instance_stats().retained_batches, 1);
        assert_eq!(renderer.instance_stats().retained_split_cpu_bytes, 0);
        assert_eq!(renderer.instance_stats().submitted_draws, 1);
        assert_eq!(renderer.instance_stats().instance_upload_bytes, 224);
        assert_eq!(pixels(&device, &queue, &target), captures[2]);
        scene.instance_batches.clear();
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        assert_eq!(renderer.instance_stats().retained_batches, 0);
        assert_eq!(renderer.instance_stats().retained_instance_bytes, 0);
        assert_eq!(renderer.instance_stats().retained_split_cpu_bytes, 0);
    }
}

#[test]
#[ignore = "requires a graphics adapter"]
fn gpu_foliage_shader_variants_validate() {
    let (device, _, _) = setup();
    for source in [
        include_str!("../../../assets/presentation/shaders/generated/wgpu/foliage_meshes.wgsl"),
        include_str!("../../../assets/presentation/shaders/generated/wgpu/foliage_storage.wgsl"),
    ] {
        let scope = device.inner.push_error_scope(wgpu::ErrorFilter::Validation);
        let _module = device
            .inner
            .create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("foliage variant validation"),
                source: wgpu::ShaderSource::Wgsl(source.into()),
            });
        let error = pollster::block_on(scope.pop());
        assert!(error.is_none(), "{error:?}");
    }
}

#[test]
#[ignore = "requires a graphics adapter"]
fn gpu_direct_foliage_changes_with_fields_without_reuploading_placements() {
    use nico_presentation::{
        Camera3d, InstanceBatch, InstanceRecord, Scene3d,
        foliage::{FoliageProfile, InfluenceKind, InfluenceSnapshot, WorldInfluence},
    };
    let (device, queue, mut target) = setup();
    let mut renderer = nico_render::MeshRenderPipeline::new(
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
    renderer
        .enable_instancing(
            &device,
            builtin_shaders::bootstrap_wgsl(include_bytes!(
                "../../../assets/presentation/shaders/generated/wgpu/instanced_meshes.wgsl"
            )),
        )
        .unwrap();
    let mesh = Arc::new(
        nico_assets::Mesh::triangles(
            [[-0.2, 0., 0.], [0.2, 0., 0.], [0., 1., 0.]]
                .into_iter()
                .map(|position| nico_assets::MeshVertex {
                    position,
                    uv: [0.; 2],
                })
                .collect(),
            vec![0, 1, 2],
        )
        .unwrap(),
    );
    let batch = Arc::new(
        InstanceBatch::new(
            mesh,
            Arc::new(nico_assets::PbrMaterial {
                double_sided: true,
                ..Default::default()
            }),
            vec![InstanceRecord::new(0, 0, glam::Mat4::IDENTITY, [1.; 4]).unwrap()],
            100.,
        )
        .unwrap()
        .with_foliage(FoliageProfile::new(0., 1., 0.8).unwrap())
        .unwrap(),
    );
    let mut scene = Scene3d {
        instance_batches: vec![batch],
        camera: Camera3d::looking_at([0., 0.5, 3.], [0., 0.5, 0.], [0., 1., 0.]).unwrap(),
        ..Default::default()
    };
    assert!(!renderer.supports_instance_mode(
        &device,
        &scene,
        nico_render::InstanceRenderMode::Cpu
    ));
    let error = renderer
        .set_instance_mode(&device, &scene, nico_render::InstanceRenderMode::Cpu)
        .unwrap_err();
    assert!(
        error
            .to_string()
            .contains("foliage pipelines are not installed")
    );
    assert_eq!(
        renderer.instance_mode(),
        nico_render::InstanceRenderMode::Auto
    );
    renderer
        .enable_foliage(
            &device,
            builtin_shaders::bootstrap_wgsl(include_bytes!(
                "../../../assets/presentation/shaders/generated/wgpu/foliage_meshes.wgsl"
            )),
        )
        .unwrap();
    assert!(renderer.supports_instance_mode(&device, &scene, nico_render::InstanceRenderMode::Cpu));
    scene.lighting.ambient = [1.; 3];
    scene.lighting.radiance = [0.; 3];
    let mut captures = Vec::new();
    for active in [false, true, false] {
        let fields = if active {
            vec![
                WorldInfluence::new(
                    0,
                    InfluenceKind::DirectionalWind,
                    [0.; 3],
                    [1., 0., 0.],
                    10.,
                    1.,
                    0.,
                    10.,
                )
                .unwrap(),
            ]
        } else {
            Vec::new()
        };
        renderer
            .set_foliage_influences(Arc::new(InfluenceSnapshot::new(0., fields).unwrap()))
            .unwrap();
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        captures.push(pixels(&device, &queue, &target));
        if captures.len() > 1 {
            assert_eq!(renderer.instance_stats().instance_upload_bytes, 0);
        }
    }
    assert_ne!(captures[0], captures[1]);
    assert_eq!(captures[0], captures[2]);
    assert!(
        renderer
            .set_instance_mode(&device, &scene, nico_render::InstanceRenderMode::Gpu)
            .is_err()
    );
    assert_eq!(
        renderer.instance_mode(),
        nico_render::InstanceRenderMode::Auto
    );
    renderer
        .enable_gpu_foliage(
            &device,
            builtin_shaders::bootstrap_wgsl(include_bytes!(
                "../../../assets/presentation/shaders/generated/wgpu/foliage_storage.wgsl"
            )),
            ShaderModuleDescriptor {
                label: None,
                format: ShaderFormat::Wgsl,
                code: include_bytes!(
                    "../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl"
                ),
            },
        )
        .unwrap();
    for (index, active) in [false, true, false].into_iter().enumerate() {
        let fields = if active {
            vec![
                WorldInfluence::new(
                    0,
                    InfluenceKind::DirectionalWind,
                    [0.; 3],
                    [1., 0., 0.],
                    10.,
                    1.,
                    0.,
                    10.,
                )
                .unwrap(),
            ]
        } else {
            Vec::new()
        };
        renderer
            .set_foliage_influences(Arc::new(InfluenceSnapshot::new(0., fields).unwrap()))
            .unwrap();
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        let actual = pixels(&device, &queue, &target);
        // Same per-channel tolerance as static direct/indirect parity tests.
        assert!(
            actual
                .iter()
                .zip(&captures[index])
                .all(|(&a, &b)| a.abs_diff(b) <= 1)
        );
        assert_eq!(renderer.instance_stats().indirect_draws, 1);
        assert_eq!(renderer.instance_stats().foliage_upload_bytes, 800);
        assert_eq!(renderer.instance_stats().influence_overflow, 0);
        if index > 0 {
            assert_eq!(renderer.instance_stats().instance_upload_bytes, 0);
            assert_eq!(renderer.instance_stats().visibility_upload_bytes, 0);
        }
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        assert_eq!(renderer.instance_stats().foliage_upload_bytes, 0);
        assert_eq!(pixels(&device, &queue, &target), actual);
    }
    use nico_render::InstanceRenderMode;
    for (index, mode) in [
        InstanceRenderMode::Cpu,
        InstanceRenderMode::Gpu,
        InstanceRenderMode::Gpu,
    ]
    .into_iter()
    .enumerate()
    {
        renderer.set_instance_mode(&device, &scene, mode).unwrap();
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        let actual = pixels(&device, &queue, &target);
        assert!(
            actual
                .iter()
                .zip(&captures[0])
                .all(|(&a, &b)| a.abs_diff(b) <= 1)
        );
        assert_eq!(
            renderer.instance_stats().indirect_draws,
            u32::from(mode == InstanceRenderMode::Gpu)
        );
        if index == 2 {
            assert_eq!(renderer.instance_stats().instance_upload_bytes, 0);
        }
    }
}

#[test]
#[ignore = "requires a graphics adapter"]
fn gpu_foliage_matches_cpu_deformed_positions_and_normals() {
    foliage_reference(false, false);
}

#[test]
#[ignore = "requires a graphics adapter"]
fn gpu_compact_foliage_matches_cpu_deformed_positions_and_normals() {
    foliage_reference(true, false);
}

#[test]
#[ignore = "requires a graphics adapter"]
fn gpu_compact_masked_normal_mapped_foliage_matches_expanded_reference() {
    foliage_reference(true, true);
}

fn foliage_reference(compact: bool, textured: bool) {
    use glam::{Mat4, Quat, Vec3, Vec4};
    use nico_assets::{MaterialTexture, Mesh, MeshVertex, PbrMaterial, model::AlphaMode};
    use nico_presentation::{
        Camera3d, InstanceBatch, InstanceRecord, MeshInstance, Scene3d,
        foliage::{FoliageProfile, InfluenceKind, InfluenceSnapshot, WorldInfluence},
    };
    let (device, queue, mut target) = setup();
    let mut renderer = nico_render::MeshRenderPipeline::new(
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
    renderer
        .enable_instancing(
            &device,
            builtin_shaders::bootstrap_wgsl(include_bytes!(
                "../../../assets/presentation/shaders/generated/wgpu/instanced_meshes.wgsl"
            )),
        )
        .unwrap();
    renderer
        .enable_foliage(
            &device,
            builtin_shaders::bootstrap_wgsl(include_bytes!(
                "../../../assets/presentation/shaders/generated/wgpu/foliage_meshes.wgsl"
            )),
        )
        .unwrap();
    let mesh = Arc::new(
        Mesh::triangles(
            [
                [-0.2, 0., 0.],
                [0.2, 0., 0.],
                [-0.12, 0.5, 0.],
                [0.12, 0.5, 0.],
                [0., 1., 0.],
            ]
            .into_iter()
            .map(|position| MeshVertex {
                position,
                uv: [position[0] / 0.4 + 0.5, 1. - position[1]],
            })
            .collect(),
            vec![0, 1, 2, 2, 1, 3, 2, 3, 4],
        )
        .unwrap(),
    );
    let mut material = PbrMaterial {
        double_sided: true,
        ..Default::default()
    };
    if textured {
        material.alpha = AlphaMode::Mask;
        material.base_color_texture = Some(MaterialTexture::new(Arc::new(
            Texture::rgba8(
                2,
                2,
                vec![
                    255, 255, 255, 0, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 0,
                ],
            )
            .unwrap(),
        )));
        material.normal_texture = Some(MaterialTexture::new(Arc::new(
            Texture::rgba8(1, 1, vec![180, 145, 240, 255]).unwrap(),
        )));
    }
    let material = Arc::new(material);
    let profile = FoliageProfile::new(0., 1., 0.5).unwrap();
    let render = |renderer: &mut nico_render::MeshRenderPipeline<WgpuDevice>,
                  target: &mut Target,
                  scene: &Scene3d| {
        renderer
            .render(
                &device,
                &queue,
                target,
                scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap();
        pixels(&device, &queue, target)
    };
    for indirect in [false, true] {
        if indirect {
            renderer.enable_gpu_foliage(&device,builtin_shaders::bootstrap_wgsl(include_bytes!("../../../assets/presentation/shaders/generated/wgpu/foliage_storage.wgsl")),
                ShaderModuleDescriptor {label:None,format:ShaderFormat::Wgsl,code:include_bytes!("../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl")}).unwrap();
        }
        if compact {
            renderer.enable_compact_instance_records(&device).unwrap();
        }
        for reflection in [1., -1.] {
            let transform = Mat4::from_cols(
                Vec4::new(reflection * 1.4, 0., 0., 0.),
                Vec4::new(0.15, 1.2, 0.1, 0.),
                Vec4::new(0., 0., 0.7, 0.),
                Vec4::W,
            );
            let record = InstanceRecord::new(0, 17, transform, [0.7, 1., 0.8, 1.])
                .unwrap()
                .with_foliage_response(
                    nico_presentation::foliage::FoliageResponse::from_seed(17, 0.8, 0.9).unwrap(),
                );
            let batch = Arc::new(
                InstanceBatch::new(mesh.clone(), material.clone(), vec![record.clone()], 100.)
                    .unwrap()
                    .with_foliage(profile)
                    .unwrap(),
            );
            for time in [0., 0.25, 5., 10.] {
                let snapshot = Arc::new(
                    InfluenceSnapshot::new(
                        time,
                        vec![
                            WorldInfluence::new(
                                0,
                                InfluenceKind::DirectionalWind,
                                [0.; 3],
                                [1., 0., 0.5],
                                5.,
                                0.8,
                                0.,
                                10.,
                            )
                            .unwrap(),
                            WorldInfluence::new(
                                1,
                                InfluenceKind::RadialBend,
                                [-0.4, 0., 0.],
                                [0., 0., 1.],
                                3.,
                                0.6,
                                0.,
                                10.,
                            )
                            .unwrap(),
                        ],
                    )
                    .unwrap(),
                );
                let mut scene = Scene3d {
                    foliage_influences: Some(snapshot.clone()),
                    instance_batches: vec![batch.clone()],
                    camera: Camera3d::looking_at([0., 0.6, 3.], [0., 0.6, 0.], [0., 1., 0.])
                        .unwrap(),
                    ..Default::default()
                };
                if textured {
                    scene.lighting.direction = [0., 0., 1.];
                    scene.lighting.radiance = [1.; 3];
                    scene.lighting.ambient = [0.05; 3];
                }
                renderer
                    .set_instance_mode(
                        &device,
                        &scene,
                        if indirect {
                            nico_render::InstanceRenderMode::Gpu
                        } else {
                            nico_render::InstanceRenderMode::Cpu
                        },
                    )
                    .unwrap();
                let actual = render(&mut renderer, &mut target, &scene);
                assert_eq!(
                    renderer.instance_stats().indirect_draws,
                    u32::from(indirect)
                );
                assert_eq!(
                    renderer.instance_stats().submitted_instances,
                    u32::from(!indirect)
                );
                assert_eq!(
                    renderer.instance_stats().instance_upload_bytes,
                    if time == 0. {
                        if compact { 80 } else { 112 }
                    } else {
                        0
                    }
                );
                assert!(
                    actual.chunks_exact(4).any(|pixel| pixel != &actual[..4]),
                    "foliage fixture must render visible pixels"
                );
                let fields = snapshot.for_chunk(batch.bounds().unwrap());
                let deformed: Vec<_> = mesh
                    .vertices()
                    .iter()
                    .zip(mesh.normals())
                    .map(|(v, n)| {
                        profile
                            .deform_with_response(
                                transform,
                                Vec3::from(v.position),
                                Vec3::from(*n),
                                &fields,
                                record.foliage_response(),
                            )
                            .unwrap()
                    })
                    .collect();
                let expanded = Mesh::triangles(
                    deformed
                        .iter()
                        .zip(mesh.vertices())
                        .map(|((p, _), original)| MeshVertex {
                            position: p.to_array(),
                            uv: original.uv,
                        })
                        .collect(),
                    mesh.indices().to_vec(),
                )
                .unwrap()
                .with_normals(deformed.iter().map(|(_, n)| n.to_array()).collect())
                .unwrap();
                scene.instance_batches.clear();
                scene.meshes = vec![MeshInstance {
                    mesh: Some(Arc::new(expanded)),
                    material: Some(material.clone()),
                    mirrored: record.mirrored(),
                    color: record.tint(),
                    position: [0.; 3],
                    orientation: Quat::IDENTITY,
                    scale: 1.,
                    skin_palette: None,
                    texture: None,
                }];
                let expected = render(&mut renderer, &mut target, &scene);
                // Declared tolerance: at most one 8-bit value in every channel.
                assert!(
                    actual
                        .iter()
                        .zip(&expected)
                        .all(|(&a, &b)| a.abs_diff(b) <= 1),
                    "indirect={indirect}, reflection={reflection}, time={time}"
                );
                if textured && !indirect && reflection == 1. && time == 0. {
                    let mut unmapped = (*material).clone();
                    unmapped.normal_texture = None;
                    scene.meshes[0].material = Some(Arc::new(unmapped));
                    assert!(
                        render(&mut renderer, &mut target, &scene) != expected,
                        "normal map must affect the fixture"
                    );
                    let mut opaque = (*material).clone();
                    opaque.alpha = AlphaMode::Opaque;
                    scene.meshes[0].material = Some(Arc::new(opaque));
                    let unmasked = render(&mut renderer, &mut target, &scene);
                    let coverage =
                        |image: &[u8]| image.chunks_exact(4).filter(|p| *p != &image[..4]).count();
                    assert!(
                        coverage(&unmasked) > coverage(&expected),
                        "alpha mask must remove covered pixels"
                    );
                }
            }
        }
    }
}

fn setup() -> (WgpuDevice, WgpuQueue, Target) {
    setup_with_flags(wgpu::InstanceFlags::debugging())
}

fn setup_with_flags(flags: wgpu::InstanceFlags) -> (WgpuDevice, WgpuQueue, Target) {
    setup_with_compression(flags, false)
}

fn setup_with_compression(flags: wgpu::InstanceFlags, bc: bool) -> (WgpuDevice, WgpuQueue, Target) {
    setup_with_extra_features(flags, bc, wgpu::Features::empty())
}

fn setup_with_extra_features(
    flags: wgpu::InstanceFlags,
    bc: bool,
    extra: wgpu::Features,
) -> (WgpuDevice, WgpuQueue, Target) {
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
        pollster::block_on(adapter.request_device(&wgpu::DeviceDescriptor {
            required_features: extra
                | if bc {
                    wgpu::Features::TEXTURE_COMPRESSION_BC
                } else {
                    wgpu::Features::empty()
                },
            ..Default::default()
        }))
        .unwrap();
    let limits = inner.limits();
    let info = adapter.get_info();
    let device = WgpuDevice {
        inner,
        failure: Arc::new(Mutex::new(None)),
        fail_bind_group: Mutex::new(None),
        disable_buffer_readback: false,
        timestamps: None,
        capabilities: Capabilities {
            compute: adapter
                .get_downlevel_capabilities()
                .flags
                .contains(wgpu::DownlevelFlags::COMPUTE_SHADERS),
            indexed_indirect: adapter
                .get_downlevel_capabilities()
                .flags
                .contains(wgpu::DownlevelFlags::INDIRECT_EXECUTION),
            vertex_storage: adapter
                .get_downlevel_capabilities()
                .flags
                .contains(wgpu::DownlevelFlags::VERTEX_STORAGE),
            texture_compression_bc: bc,
            adapter: AdapterInfo {
                name: info.name,
                api: graphics_api(info.backend),
                kind: adapter_kind(info.device_type),
            },
            limits: Limits {
                max_buffer_size: limits.max_buffer_size,
                max_vertex_buffer_array_stride: limits.max_vertex_buffer_array_stride,
                max_storage_buffers_per_shader_stage: limits.max_storage_buffers_per_shader_stage,
                max_compute_workgroup_size_x: limits.max_compute_workgroup_size_x,
                max_compute_invocations_per_workgroup: limits.max_compute_invocations_per_workgroup,
                max_compute_workgroups_per_dimension: limits.max_compute_workgroups_per_dimension,
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
        foliage_influences: None,
        instance_batches: Vec::new(),
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
        foliage_influences: None,
        instance_batches: Vec::new(),
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
        foliage_influences: None,
        instance_batches: Vec::new(),
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
        foliage_influences: None,
        instance_batches: Vec::new(),
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
        foliage_influences: None,
        instance_batches: Vec::new(),
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
        foliage_influences: None,
        instance_batches: Vec::new(),
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

#[test]
#[ignore = "requires a BC-capable graphics adapter"]
fn gpu_bc3_and_disabled_feature_fallback_render_with_bounded_error() {
    let source: Vec<u8> = (0..64)
        .flat_map(|i| {
            if i % 8 < 4 {
                [180, 75, 35, 255]
            } else {
                [40, 190, 110, 128]
            }
        })
        .collect();
    let texture = Arc::new(
        Texture::rgba8(8, 8, source)
            .unwrap()
            .compress_bc3(|| Ok::<_, ()>(()))
            .unwrap(),
    );
    let mut captures = Vec::new();
    for bc in [true, false] {
        let (device, queue, mut target) =
            setup_with_compression(wgpu::InstanceFlags::debugging(), bc);
        if !bc {
            let result = device.create_texture(TextureDescriptor {
                label: None,
                extent: Extent3d::surface(8, 8),
                mip_levels: 1,
                samples: 1,
                dimension: TextureDimension::Two,
                format: TextureFormat::Bc3RgbaUnorm,
                usages: TextureUsages::SAMPLED | TextureUsages::COPY_DESTINATION,
            });
            assert_eq!(result.err().unwrap().kind(), RhiErrorKind::Unsupported);
        }
        let queue = UploadQueue {
            inner: queue,
            geometry_bytes: std::cell::Cell::new(0),
            texture_bytes: std::cell::Cell::new(0),
        };
        let mut renderer = QuadRenderPipeline::new(
            &device,
            target.format,
            builtin_shaders::bootstrap_wgsl(include_bytes!(
                "../../../assets/presentation/shaders/generated/wgpu/quads.wgsl"
            )),
        )
        .unwrap();
        let baseline = queue.texture_bytes.get();
        let ui = UiScene {
            quads: vec![Quad {
                center: [32., 32.],
                size: [48., 48.],
                color: [1.; 4],
                texture: Some(texture.clone()),
            }],
        };
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &Scene2d::default(),
                &ui,
                [64.; 2],
            )
            .unwrap();
        assert_eq!(
            queue.texture_bytes.get() - baseline,
            if bc { 64 } else { 256 }
        );
        captures.push(pixels(&device, &queue, &target));
        renderer
            .render(
                &device,
                &queue,
                &mut target,
                &Scene2d::default(),
                &ui,
                [64.; 2],
            )
            .unwrap();
        assert_eq!(
            queue.texture_bytes.get() - baseline,
            if bc { 64 } else { 256 }
        );
    }
    let max_error = captures[0]
        .iter()
        .zip(&captures[1])
        .map(|(&a, &b)| a.abs_diff(b))
        .max()
        .unwrap();
    eprintln!(
        "BC3/fallback max channel error: {max_error}; left {:?}/{:?}; right {:?}/{:?}",
        pixel(&captures[0], 16, 32),
        pixel(&captures[1], 16, 32),
        pixel(&captures[0], 48, 32),
        pixel(&captures[1], 48, 32)
    );
    // BC3 is lossy and native interpolation need not match the software
    // decoder byte-for-byte. Bound this fixture to under 2% per channel.
    assert!(max_error <= 5);
    assert_ne!(pixel(&captures[0], 16, 32), pixel(&captures[0], 48, 32));
}

#[test]
#[ignore = "requires a real graphics adapter"]
fn gpu_static_instances_match_expanded_geometry_and_reuse_uploads() {
    static_instances_reference(false, false, false);
}
#[test]
#[ignore = "requires a real graphics adapter"]
fn gpu_static_instances_ordinary_fallback_preserves_affine_rendering() {
    static_instances_reference(true, false, false);
}
#[test]
#[ignore = "requires a real graphics adapter"]
fn gpu_static_instances_indirect_storage_matches_expanded_geometry() {
    static_instances_reference(false, true, false);
}

#[test]
#[ignore = "requires a real graphics adapter"]
fn gpu_indirect_and_cpu_compaction_agree_on_partial_and_zero_visibility() {
    use glam::{Mat4, Vec3};
    use nico_assets::{Mesh, MeshVertex, PbrMaterial};
    use nico_presentation::{Camera3d, InstanceBatch, InstanceRecord, Scene3d};
    use nico_render::MeshRenderPipeline;
    let (device, queue, mut target) = setup();
    let make = |gpu: bool| {
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
        renderer
            .enable_instancing(
                &device,
                builtin_shaders::bootstrap_wgsl(include_bytes!(
                    "../../../assets/presentation/shaders/generated/wgpu/instanced_meshes.wgsl"
                )),
            )
            .unwrap();
        if gpu {
            renderer.enable_gpu_instancing(&device,builtin_shaders::bootstrap_wgsl(include_bytes!("../../../assets/presentation/shaders/generated/wgpu/instanced_storage.wgsl")),ShaderModuleDescriptor {label:None,format:ShaderFormat::Wgsl,code:include_bytes!("../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl")}).unwrap();
        }
        renderer
    };
    let (mut direct, mut gpu) = (make(false), make(true));
    let mesh = Arc::new(
        Mesh::triangles(
            vec![
                MeshVertex {
                    position: [-0.3, -0.3, 0.],
                    uv: [0.; 2],
                },
                MeshVertex {
                    position: [0.3, -0.3, 0.],
                    uv: [0.; 2],
                },
                MeshVertex {
                    position: [0., 0.3, 0.],
                    uv: [0.; 2],
                },
            ],
            vec![0, 1, 2],
        )
        .unwrap(),
    );
    let records = [-0.8, 0.8]
        .into_iter()
        .enumerate()
        .map(|(i, x)| {
            InstanceRecord::new(
                i as u64,
                0,
                Mat4::from_translation(Vec3::new(x, 0., 0.)),
                if i == 0 {
                    [1., 0., 0., 1.]
                } else {
                    [0., 1., 0., 1.]
                },
            )
            .unwrap()
        })
        .collect();
    let batch = Arc::new(
        InstanceBatch::new(
            mesh,
            Arc::new(PbrMaterial {
                double_sided: true,
                ..Default::default()
            }),
            records,
            2.92,
        )
        .unwrap(),
    );
    let mut scene = Scene3d {
        instance_batches: vec![batch],
        ..Default::default()
    };
    scene.lighting.radiance = [0.; 3];
    scene.lighting.ambient = [1.; 3];
    let render =
        |renderer: &mut MeshRenderPipeline<WgpuDevice>, target: &mut Target, scene: &Scene3d| {
            renderer
                .render(
                    &device,
                    &queue,
                    target,
                    scene,
                    &Scene2d::default(),
                    &UiScene::default(),
                    [64.; 2],
                    Extent3d::surface(64, 64),
                )
                .unwrap()
        };
    let mut blank = None;
    for (frame, x) in [-0.8, 0., 0.8, 0.8, 0., -0.8].into_iter().enumerate() {
        scene.camera = Camera3d::looking_at([x, 0., 2.9], [x, 0., 0.], [0., 1., 0.]).unwrap();
        while !gpu.instance_stats().prepared_view.is_multiple_of(8) {
            render(&mut gpu, &mut target, &scene);
        }
        render(&mut direct, &mut target, &scene);
        let cpu = pixels(&device, &queue, &target);
        render(&mut gpu, &mut target, &scene);
        let actual = pixels(&device, &queue, &target);
        let sampled_view = gpu.instance_stats().prepared_view;
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(5);
        let sample = loop {
            gpu.poll_instance_readbacks();
            let stats = gpu.instance_stats().gpu_readback;
            assert_eq!(stats.failed, 0);
            assert!(stats.pending <= 3);
            if let Some(sample) = stats
                .sample
                .filter(|sample| sample.prepared_view == sampled_view)
            {
                break sample;
            }
            assert!(
                std::time::Instant::now() < deadline,
                "renderer count readback timed out"
            );
            std::thread::yield_now();
        };
        assert_eq!(
            sample.visible_instances,
            direct.instance_stats().submitted_instances
        );
        assert_eq!(sample.candidate_instances, 2);
        assert_eq!(sample.indirect_draws, 1);
        assert!(actual.iter().zip(&cpu).all(|(&a, &b)| a.abs_diff(b) <= 1));
        assert_eq!(
            direct.instance_stats().submitted_instances,
            if x == 0. { 0 } else { 1 }
        );
        assert_eq!(gpu.instance_stats().indirect_draws, 1);
        if frame > 0 {
            assert_eq!(gpu.instance_stats().instance_upload_bytes, 0);
            assert_eq!(gpu.instance_stats().visibility_upload_bytes, 0);
            assert_eq!(direct.instance_stats().instance_upload_bytes, 0);
        }
        if frame == 3 {
            assert_eq!(direct.instance_stats().visible_record_upload_bytes, 0);
        }
        if x == 0. {
            if let Some(blank) = &blank {
                assert_eq!(&actual, blank);
            } else {
                blank = Some(actual);
            }
        } else {
            assert!(actual.chunks_exact(4).any(|p| p[0] > 200 || p[1] > 200));
        }
    }
    // Diagnose order sensitivity separately from visibility: coincident opaque
    // surfaces have no unique color winner after unordered GPU compaction.
    let prototype = scene.instance_batches[0].mesh().clone();
    let material = scene.instance_batches[0].material().clone();
    scene.camera = Camera3d::looking_at([0., 0., 2.9], [0.; 3], [0., 1., 0.]).unwrap();
    let mut coplanar = Vec::new();
    for separation in [0., 0.01] {
        for reverse in [false, true] {
            let mut records = vec![
                InstanceRecord::new(0, 0, Mat4::IDENTITY, [1., 0., 0., 1.]).unwrap(),
                InstanceRecord::new(
                    1,
                    0,
                    Mat4::from_translation(Vec3::new(0., 0., separation)),
                    [0., 1., 0., 1.],
                )
                .unwrap(),
            ];
            if reverse {
                records.reverse();
            }
            scene.instance_batches = vec![Arc::new(
                InstanceBatch::new(prototype.clone(), material.clone(), records, 100.).unwrap(),
            )];
            render(&mut direct, &mut target, &scene);
            let cpu = pixels(&device, &queue, &target);
            render(&mut gpu, &mut target, &scene);
            let actual = pixels(&device, &queue, &target);
            if separation == 0. {
                coplanar.push(pixel(&cpu, 32, 32).to_vec());
                let center = pixel(&actual, 32, 32);
                assert!(center[0] > 200 || center[1] > 200);
            } else {
                assert!(actual.iter().zip(&cpu).all(|(&a, &b)| a.abs_diff(b) <= 1));
                assert!(pixel(&actual, 32, 32)[1] > 200);
            }
        }
    }
    assert_ne!(coplanar[0], coplanar[1]);
}
#[test]
#[ignore = "requires a real graphics adapter"]
fn gpu_compact_instances_reconstruct_and_preserve_ordinary_fallback() {
    for (fallback, gpu) in [(false, false), (false, true), (true, false)] {
        static_instances_reference(fallback, gpu, true);
    }
}

fn static_instances_reference(fallback: bool, gpu: bool, compact: bool) {
    use glam::{Mat4, Quat, Vec3};
    use nico_assets::{Mesh, MeshVertex, PbrMaterial};
    use nico_presentation::{Camera3d, InstanceBatch, InstanceRecord, MeshInstance, Scene3d};
    use nico_render::MeshRenderPipeline;
    let (mut device, queue, mut target) = setup();
    if fallback {
        device.capabilities.limits.max_vertex_attributes = 3;
    }
    let queue = UploadQueue {
        inner: queue,
        geometry_bytes: std::cell::Cell::new(0),
        texture_bytes: std::cell::Cell::new(0),
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
    renderer
        .enable_instancing(
            &device,
            builtin_shaders::bootstrap_wgsl(include_bytes!(
                "../../../assets/presentation/shaders/generated/wgpu/instanced_meshes.wgsl"
            )),
        )
        .unwrap();
    if gpu {
        renderer.enable_gpu_instancing(&device,
        builtin_shaders::bootstrap_wgsl(include_bytes!("../../../assets/presentation/shaders/generated/wgpu/instanced_storage.wgsl")),
        ShaderModuleDescriptor {label:Some("visibility"),format:ShaderFormat::Wgsl,code:include_bytes!("../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl")}).unwrap();
    }
    if compact {
        renderer.enable_compact_instance_records(&device).unwrap();
    }
    let record_bytes = if compact { 80u64 } else { 112 };
    let mesh = Arc::new(
        Mesh::triangles(
            vec![
                MeshVertex {
                    position: [-0.3, -0.3, 0.],
                    uv: [0.; 2],
                },
                MeshVertex {
                    position: [0.3, -0.3, 0.],
                    uv: [0.; 2],
                },
                MeshVertex {
                    position: [0., 0.3, 0.],
                    uv: [0.; 2],
                },
            ],
            vec![0, 1, 2],
        )
        .unwrap(),
    );
    let material = Arc::new(PbrMaterial {
        double_sided: true,
        ..Default::default()
    });
    let records: Vec<_> = (0..3)
        .map(|i| {
            InstanceRecord::new(
                i,
                0,
                Mat4::from_scale_rotation_translation(
                    Vec3::new(if i == 2 { -1. } else { 1. }, 1. + i as f32 * 0.2, 1.),
                    Quat::from_rotation_z(i as f32 * 0.2),
                    Vec3::new((i as f32 - 1.) * 0.8, 0., 0.),
                ),
                if i == 0 {
                    [1., 0., 0., 1.]
                } else if i == 1 {
                    [0., 1., 0., 1.]
                } else {
                    [0., 0., 1., 1.]
                },
            )
            .unwrap()
        })
        .collect();
    let mut batches = vec![
        Arc::new(
            InstanceBatch::new(mesh.clone(), material.clone(), records[..2].to_vec(), 100.)
                .unwrap(),
        ),
        Arc::new(
            InstanceBatch::new(mesh.clone(), material.clone(), records[2..].to_vec(), 100.)
                .unwrap(),
        ),
    ];
    let mut scene = Scene3d {
        camera: Camera3d::looking_at([0., 0., 3.], [0.; 3], [0., 1., 0.]).unwrap(),
        instance_batches: batches.clone(),
        ..Default::default()
    };
    if gpu {
        renderer
            .set_instance_mode(&device, &scene, nico_render::InstanceRenderMode::Gpu)
            .unwrap();
    }
    scene.lighting.radiance = [0.; 3];
    scene.lighting.ambient = [1.; 3];
    let render =
        |renderer: &mut MeshRenderPipeline<WgpuDevice>, target: &mut Target, scene: &Scene3d| {
            renderer
                .render(
                    &device,
                    &queue,
                    target,
                    scene,
                    &Scene2d::default(),
                    &UiScene::default(),
                    [64.; 2],
                    Extent3d::surface(64, 64),
                )
                .unwrap()
        };
    render(&mut renderer, &mut target, &scene);
    let instanced = pixels(&device, &queue, &target);
    let uploaded = queue.geometry_bytes.get();
    assert_eq!(
        uploaded,
        3 * 32
            + 3 * 4
            + if fallback {
                0
            } else {
                3 * record_bytes as usize
            }
    );
    let stats = renderer.instance_stats();
    assert_eq!(stats.submitted_instances, if gpu { 0 } else { 3 });
    assert_eq!(stats.visibility_reused_batches, 0);
    assert_eq!(stats.gpu_candidate_instances, if gpu { 3 } else { 0 });
    assert_eq!(stats.indirect_draws, if gpu { 2 } else { 0 });
    assert_eq!(stats.submitted_draws, if fallback { 3 } else { 2 });
    assert_eq!(stats.ordinary_fallback, fallback);
    assert_eq!(
        stats.instance_upload_bytes,
        if fallback { 0 } else { 3 * record_bytes }
    );
    render(&mut renderer, &mut target, &scene);
    assert_eq!(queue.geometry_bytes.get(), uploaded);
    assert_eq!(
        renderer.instance_stats().visibility_reused_batches,
        if fallback { 0 } else { 2 }
    );
    scene.instance_batches.clear();
    render(&mut renderer, &mut target, &scene);
    scene.instance_batches = batches.clone();
    render(&mut renderer, &mut target, &scene);
    assert_eq!(queue.geometry_bytes.get(), uploaded);
    batches[0] = Arc::new(
        InstanceBatch::new(mesh.clone(), material.clone(), records[..2].to_vec(), 100.).unwrap(),
    );
    scene.instance_batches = batches.clone();
    render(&mut renderer, &mut target, &scene);
    assert_eq!(
        renderer.instance_stats().instance_upload_bytes,
        if fallback { 0 } else { 2 * record_bytes }
    );
    assert_eq!(renderer.instance_stats().retained_batches, 2);
    scene.instance_batches.clear();
    scene.meshes = records
        .iter()
        .map(|record| {
            let vertices = mesh
                .vertices()
                .iter()
                .map(|v| MeshVertex {
                    position: record
                        .transform()
                        .transform_point3(Vec3::from(v.position))
                        .to_array(),
                    uv: v.uv,
                })
                .collect();
            let normals = mesh
                .normals()
                .iter()
                .map(|n| {
                    (record.normal_transform() * Vec3::from(*n))
                        .normalize()
                        .to_array()
                })
                .collect();
            MeshInstance {
                mesh: Some(Arc::new(
                    Mesh::triangles(vertices, mesh.indices().to_vec())
                        .unwrap()
                        .with_normals(normals)
                        .unwrap(),
                )),
                material: Some(material.clone()),
                mirrored: record.mirrored(),
                color: record.tint(),
                position: [0.; 3],
                orientation: Quat::IDENTITY,
                scale: 1.,
                skin_palette: None,
                texture: None,
            }
        })
        .collect();
    render(&mut renderer, &mut target, &scene);
    let expanded = pixels(&device, &queue, &target);
    assert!(
        instanced
            .iter()
            .zip(&expanded)
            .all(|(&a, &b)| a.abs_diff(b) <= 1)
    );
    assert!(instanced.chunks_exact(4).any(|p| p[0] > 200 && p[1] < 10));
    assert!(instanced.chunks_exact(4).any(|p| p[1] > 200 && p[0] < 10));
    assert!(instanced.chunks_exact(4).any(|p| p[2] > 200 && p[0] < 10));
    if fallback {
        scene.meshes.clear();
        scene.instance_batches = vec![batches[0].clone(); 129];
        let acquired = target.acquired;
        let error = renderer
            .render(
                &device,
                &queue,
                &mut target,
                &scene,
                &Scene2d::default(),
                &UiScene::default(),
                [64.; 2],
                Extent3d::surface(64, 64),
            )
            .unwrap_err();
        assert_eq!(error.kind(), RhiErrorKind::Unsupported);
        assert_eq!(target.acquired, acquired);
    }
    scene.instance_batches.clear();
    let retained_snapshot = Scene3d {
        meshes: Vec::new(),
        instance_batches: batches.clone(),
        ..scene.clone()
    };
    // An independent device must rebuild all GPU ownership from the same CPU
    // snapshot. No source mutation/revision bump or provider callback is needed.
    recreated_instance_snapshot(&retained_snapshot, &instanced, fallback, gpu, compact);
    drop(retained_snapshot);
    drop(batches);
    render(&mut renderer, &mut target, &scene);
    assert_eq!(renderer.instance_stats().retained_instance_bytes, 0);
    assert_eq!(renderer.instance_stats().retained_batches, 0);
}

fn recreated_instance_snapshot(
    scene: &nico_presentation::Scene3d,
    expected: &[u8],
    fallback: bool,
    gpu: bool,
    compact: bool,
) {
    use nico_render::MeshRenderPipeline;
    let (mut device, queue, mut target) = setup();
    if fallback {
        device.capabilities.limits.max_vertex_attributes = 3;
    }
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
    renderer
        .enable_instancing(
            &device,
            builtin_shaders::bootstrap_wgsl(include_bytes!(
                "../../../assets/presentation/shaders/generated/wgpu/instanced_meshes.wgsl"
            )),
        )
        .unwrap();
    if gpu {
        renderer.enable_gpu_instancing(&device,
            builtin_shaders::bootstrap_wgsl(include_bytes!("../../../assets/presentation/shaders/generated/wgpu/instanced_storage.wgsl")),
            ShaderModuleDescriptor { label: None, format: ShaderFormat::Wgsl,
                code: include_bytes!("../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl") },
        ).unwrap();
        renderer
            .set_instance_mode(&device, scene, nico_render::InstanceRenderMode::Gpu)
            .unwrap();
    }
    if compact {
        renderer.enable_compact_instance_records(&device).unwrap();
    }
    let record_bytes = if compact { 80u64 } else { 112 };
    for frame in 0..2 {
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
        let stats = renderer.instance_stats();
        assert_eq!(stats.retained_batches, 2);
        assert_eq!(
            stats.instance_upload_bytes,
            if frame == 0 && !fallback {
                3 * record_bytes
            } else {
                0
            }
        );
        assert_eq!(stats.indirect_draws, if gpu { 2 } else { 0 });
        assert_eq!(stats.ordinary_fallback, fallback);
        assert_eq!(
            stats.visibility_reused_batches,
            if frame > 0 && !fallback { 2 } else { 0 }
        );
        assert_eq!(pixels(&device, &queue, &target), expected);
    }
}
