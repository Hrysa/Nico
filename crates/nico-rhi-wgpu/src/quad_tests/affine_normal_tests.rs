//! Read back normals from the actual selected GPU source encoding and shader.
use super::*;

struct SourceQueue {
    inner: WgpuQueue,
    sources: std::cell::RefCell<Vec<WgpuBuffer>>,
}
impl RhiQueue<WgpuDevice> for SourceQueue {
    fn write_buffer(&self, buffer: &WgpuBuffer, offset: u64, data: &[u8]) {
        if buffer
            .0
            .usage()
            .contains(wgpu::BufferUsages::VERTEX | wgpu::BufferUsages::STORAGE)
        {
            self.sources.borrow_mut().push(buffer.clone());
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
        self.inner.write_texture(destination, data, layout, extent);
    }
    fn submit(&self, commands: Vec<WgpuCommandBuffer>) {
        self.inner.submit(commands);
    }
}

#[test]
#[ignore = "requires a graphics adapter"]
fn gpu_affine_normal_encoding_preserves_accepted_anisotropic_transforms() {
    use glam::{Mat4, Vec3};
    use nico_assets::{Mesh, MeshVertex, PbrMaterial};
    use nico_presentation::{Camera3d, InstanceBatch, InstanceRecord, Scene3d};
    use nico_render::{InstanceRenderMode, MeshRenderPipeline};
    let (device, queue, mut target) = setup();
    let queue = SourceQueue {
        inner: queue,
        sources: Default::default(),
    };
    macro_rules! shader {
        ($name:literal) => {
            builtin_shaders::bootstrap_wgsl(include_bytes!(concat!(
                "../../../../assets/presentation/shaders/generated/wgpu/",
                $name,
                ".wgsl"
            )))
        };
    }
    let mut renderer =
        MeshRenderPipeline::new(&device, target.format, shader!("meshes"), shader!("quads"))
            .unwrap();
    renderer
        .enable_instancing(&device, shader!("instanced_meshes"))
        .unwrap();
    renderer.enable_gpu_instancing(&device, shader!("instanced_storage"), ShaderModuleDescriptor {
        label: None, format: ShaderFormat::Wgsl,
        code: include_bytes!("../../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl"),
    }).unwrap();
    renderer.enable_compact_instance_records(&device).unwrap();
    let records: Vec<_> = [
        Vec3::ONE,
        Vec3::new(2., 3., -0.7),
        Vec3::new(1e-20, 1e-20, 100.),
    ]
    .into_iter()
    .enumerate()
    .map(|(id, scale)| InstanceRecord::new(id as u64, 0, Mat4::from_scale(scale), [1.; 4]).unwrap())
    .collect();
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
    let mut scene = Scene3d {
        camera: Camera3d::looking_at([0., 0., 3.], [0.; 3], [0., 1., 0.]).unwrap(),
        instance_batches: records
            .iter()
            .map(|r| {
                Arc::new(
                    InstanceBatch::new(mesh.clone(), material.clone(), vec![r.clone()], 100.)
                        .unwrap(),
                )
            })
            .collect(),
        ..Default::default()
    };
    renderer
        .set_instance_mode(&device, &scene, InstanceRenderMode::Gpu)
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
    let sources = queue.sources.borrow();
    assert_eq!(sources.len(), 3);
    assert_eq!(
        sources.iter().map(|s| s.0.size()).collect::<Vec<_>>(),
        [80, 80, 112]
    );
    let source = include_str!(
        "../../../../assets/presentation/shaders/generated/wgpu/instanced_storage.wgsl"
    );
    for (index, (record, input)) in records.iter().zip(sources.iter()).enumerate() {
        let load = if input.0.size() == 80 {
            "let p = compactInstances_0[0u]; let data = decodeInstance_0(PackedInstanceData_0(p.model0_1, p.model1_1, p.model2_1, p.responseInverseDeterminant_0, p.tint_1));"
        } else {
            "let data = instances_0[0u];"
        };
        let code = format!(
            "{source}\n@group(0) @binding(11) var<storage, read_write> normal_probe: array<vec4<f32>>;\n@compute @workgroup_size(1) fn probe() {{{load} normal_probe[0] = data.normal0_0; normal_probe[1] = data.normal1_0; normal_probe[2] = data.normal2_0; }}"
        );
        let shader = device
            .inner
            .create_shader_module(wgpu::ShaderModuleDescriptor {
                label: Some("selected affine normal representation probe"),
                source: wgpu::ShaderSource::Wgsl(code.into()),
            });
        let pipeline = device
            .inner
            .create_compute_pipeline(&wgpu::ComputePipelineDescriptor {
                label: None,
                layout: None,
                module: &shader,
                entry_point: Some("probe"),
                compilation_options: Default::default(),
                cache: None,
            });
        let output = device.inner.create_buffer(&wgpu::BufferDescriptor {
            label: None,
            size: 48,
            usage: wgpu::BufferUsages::STORAGE | wgpu::BufferUsages::COPY_SRC,
            mapped_at_creation: false,
        });
        let staging = WgpuBuffer(device.inner.create_buffer(&wgpu::BufferDescriptor {
            label: None,
            size: 48,
            usage: wgpu::BufferUsages::MAP_READ | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        }));
        let groups: Vec<_> = (0..4)
            .map(|group| {
                let entries = match group {
                    0 => vec![wgpu::BindGroupEntry {
                        binding: 11,
                        resource: output.as_entire_binding(),
                    }],
                    3 => vec![wgpu::BindGroupEntry {
                        binding: 0,
                        resource: input.0.as_entire_binding(),
                    }],
                    _ => vec![],
                };
                device.inner.create_bind_group(&wgpu::BindGroupDescriptor {
                    label: None,
                    layout: &pipeline.get_bind_group_layout(group),
                    entries: &entries,
                })
            })
            .collect();
        let mut encoder = device.inner.create_command_encoder(&Default::default());
        {
            let mut pass = encoder.begin_compute_pass(&Default::default());
            pass.set_pipeline(&pipeline);
            for (i, group) in groups.iter().enumerate() {
                pass.set_bind_group(i as u32, group, &[]);
            }
            pass.dispatch_workgroups(1, 1, 1);
        }
        encoder.copy_buffer_to_buffer(&output, 0, &staging.0, 0, 48);
        queue.inner.inner.submit([encoder.finish()]);
        let mut ticket = device.read_buffer_async(&staging, 0..48).unwrap();
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(10);
        let bytes = loop {
            if let Some(bytes) = ticket.poll().unwrap() {
                break bytes;
            }
            assert!(
                std::time::Instant::now() < deadline,
                "normal probe readback timed out"
            );
            std::thread::yield_now();
        };
        for row in 0..3 {
            for axis in 0..3 {
                let offset = row * 16 + axis * 4;
                let actual = f32::from_le_bytes(bytes[offset..offset + 4].try_into().unwrap());
                let expected = record.normal_transform().row(row)[axis];
                assert!(
                    actual.is_finite()
                        && (actual - expected).abs() <= (expected.abs() * 1e-4).max(1e-30),
                    "record={index} row={row} axis={axis}: actual={actual} expected={expected}"
                );
            }
        }
    }
    drop(sources);
    let gpu_pixels = pixels(&device, &queue.inner, &target);
    queue.sources.borrow_mut().clear();
    renderer
        .set_instance_mode(&device, &scene, InstanceRenderMode::Cpu)
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
    assert_eq!(renderer.instance_stats().instance_upload_bytes, 272);
    assert_eq!(pixels(&device, &queue.inner, &target), gpu_pixels);
    for mode in [InstanceRenderMode::Cpu, InstanceRenderMode::Gpu] {
        renderer.set_instance_mode(&device, &scene, mode).unwrap();
        for round in 0..2 {
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
            assert_eq!(pixels(&device, &queue.inner, &target), gpu_pixels);
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
            assert_eq!(renderer.instance_stats().retained_batches, 3);
            // Drop source owners, not merely visibility, to exercise retirement.
            scene.instance_batches.clear();
            queue.sources.borrow_mut().clear();
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
            scene.instance_batches = records
                .iter()
                .map(|record| {
                    Arc::new(
                        InstanceBatch::new(
                            mesh.clone(),
                            material.clone(),
                            vec![record.clone()],
                            100.,
                        )
                        .unwrap(),
                    )
                })
                .collect();
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
            assert_eq!(
                renderer.instance_stats().instance_upload_bytes,
                272,
                "{mode:?} re-entry {round}"
            );
            assert_eq!(pixels(&device, &queue.inner, &target), gpu_pixels);
        }
    }
    device.check_failure().unwrap();
}
