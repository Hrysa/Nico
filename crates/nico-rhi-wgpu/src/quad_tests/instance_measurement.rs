//! Opt-in fixed-workload comparison. Wall times include driver/host overhead;
//! completion waits are reported separately and are not GPU timestamp queries.
use super::*;
use glam::{Mat4, Vec3};
use nico_assets::{Mesh, MeshVertex, PbrMaterial};
use nico_presentation::{Camera3d, InstanceBatch, InstanceRecord, Scene3d};
use nico_render::{InstanceRenderMode, MeshRenderPipeline};
use std::time::{Duration, Instant};

#[test]
#[ignore = "manual dense/sparse direct-versus-indirect wall-time measurement"]
fn gpu_instance_dense_sparse_submission_measurement() {
    let moving = std::env::var("NICO_MEASUREMENT_MOVING").as_deref() == Ok("1");
    let (device, queue, mut target) = setup_with_flags(native_instance_flags());
    let mesh = Arc::new(
        Mesh::triangles(
            [
                [-0.02, 0., 0.],
                [0.02, 0., 0.],
                [-0.012, 0.06, -0.012],
                [0.012, 0.06, -0.012],
                [0., 0.12, -0.05],
            ]
            .into_iter()
            .map(|position| MeshVertex {
                position,
                uv: [0.; 2],
            })
            .collect(),
            vec![0, 1, 2, 1, 3, 2, 2, 3, 4],
        )
        .unwrap(),
    );
    let material = Arc::new(PbrMaterial {
        double_sided: true,
        ..Default::default()
    });
    let wait = || {
        device
            .inner
            .poll(wgpu::PollType::Wait {
                submission_index: None,
                timeout: Some(Duration::from_secs(10)),
            })
            .unwrap()
    };
    for (records, chunks) in [
        (64, 1),
        (256, 1),
        (512, 1),
        (1024, 1),
        (2048, 1),
        (4096, 1),
        (8192, 1),
        (16384, 1),
        (16384, 64),
        (262144, 64),
    ] {
        for sparse in [false, true] {
            let per_chunk = records / chunks;
            let batches = (0..chunks)
                .map(|chunk| {
                    let records = (0..per_chunk)
                        .map(|local| {
                            let id = chunk * per_chunk + local;
                            let x = (id % 128) as f32 / 32. - 2.
                                + if sparse && local % 16 != 0 { 100. } else { 0. };
                            let y = ((id / 128) % 128) as f32 / 32. - 2.;
                            InstanceRecord::new(
                                id as u64,
                                id as u32,
                                Mat4::from_translation(Vec3::new(x, y, 0.)),
                                [0.3, 0.7, 0.2, 1.],
                            )
                            .unwrap()
                        })
                        .collect();
                    Arc::new(
                        InstanceBatch::new(mesh.clone(), material.clone(), records, 500.).unwrap(),
                    )
                })
                .collect();
            let mut scene = Scene3d {
                instance_batches: batches,
                camera: Camera3d::looking_at([0., 0., 8.], [0.; 3], [0., 1., 0.]).unwrap(),
                ..Default::default()
            };
            let mut reference = None;
            for mode in [InstanceRenderMode::Cpu, InstanceRenderMode::Gpu] {
                let mut renderer = MeshRenderPipeline::new(
                    &device,
                    target.format,
                    builtin_shaders::bootstrap_wgsl(include_bytes!(
                        "../../../../assets/presentation/shaders/generated/wgpu/meshes.wgsl"
                    )),
                    builtin_shaders::bootstrap_wgsl(include_bytes!(
                        "../../../../assets/presentation/shaders/generated/wgpu/quads.wgsl"
                    )),
                )
                .unwrap();
                renderer.enable_instancing(&device, builtin_shaders::bootstrap_wgsl(include_bytes!("../../../../assets/presentation/shaders/generated/wgpu/instanced_meshes.wgsl"))).unwrap();
                renderer.enable_gpu_instancing(&device,
                    builtin_shaders::bootstrap_wgsl(include_bytes!("../../../../assets/presentation/shaders/generated/wgpu/instanced_storage.wgsl")),
                    ShaderModuleDescriptor { label: None, format: ShaderFormat::Wgsl, code: include_bytes!("../../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl") }).unwrap();
                renderer.set_instance_mode(&device, &scene, mode).unwrap();
                let mut submit = Duration::ZERO;
                let mut complete = Duration::ZERO;
                let mut first = (Duration::ZERO, Duration::ZERO);
                for frame in 0..40 {
                    if moving {
                        scene.camera.position[0] = (frame % 2) as f32 * 0.001;
                    }
                    let start = Instant::now();
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
                    let submitted = start.elapsed();
                    wait();
                    let completed = start.elapsed();
                    if frame == 0 {
                        first = (submitted, completed);
                    }
                    if frame >= 10 {
                        assert_eq!(renderer.instance_stats().instance_upload_bytes, 0);
                        assert_eq!(renderer.instance_stats().visibility_upload_bytes, 0);
                        if moving {
                            assert_eq!(renderer.instance_stats().visibility_reused_batches, 0);
                        }
                        submit += submitted;
                        complete += completed;
                    }
                }
                let image = pixels(&device, &queue, &target);
                if let Some(reference) = &reference {
                    assert_eq!(
                        &image, reference,
                        "fixed-color fixture must agree across paths"
                    );
                } else {
                    reference = Some(image);
                }
                renderer.poll_instance_readbacks();
                let stats = renderer.instance_stats();
                let visible = if mode == InstanceRenderMode::Cpu {
                    stats.submitted_instances
                } else {
                    stats
                        .gpu_readback
                        .sample
                        .expect("completed sampled view")
                        .visible_instances
                };
                assert_eq!(visible, if sparse { records / 16 } else { records } as u32);
                eprintln!(
                    "instance_measurement records={records} chunks={chunks} sparse={sparse} moving={moving} mode={mode:?} visible={visible} first_submit_ms={:.3} first_complete_ms={:.3} warm_submit_ms={:.3} warm_complete_ms={:.3} retained_bytes={} draws={}",
                    first.0.as_secs_f64() * 1000.,
                    first.1.as_secs_f64() * 1000.,
                    submit.as_secs_f64() * 1000. / 30.,
                    complete.as_secs_f64() * 1000. / 30.,
                    stats.retained_instance_bytes,
                    stats.submitted_draws
                );
            }
        }
    }
}
