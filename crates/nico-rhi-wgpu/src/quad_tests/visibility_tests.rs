//! Isolate page/dispatch overhead before changing production resource ownership.
use super::*;
use glam::{Mat4, Vec3};
use nico_presentation::InstanceBounds;
use nico_render::visibility::{
    GpuVisibilityKernel, GpuVisibilityPage, VisibilityGroup, VisibilityRecord,
};
use std::time::{Duration, Instant};

#[test]
#[ignore = "requires graphics; selected groups and zero-count resets across submissions"]
fn gpu_visibility_selection_resets_excluded_groups_at_word_boundaries() {
    let (device, queue, _) = setup();
    let kernel = Arc::new(GpuVisibilityKernel::new(&device, ShaderModuleDescriptor {
        label: None, format: ShaderFormat::Wgsl,
        code: include_bytes!("../../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl"),
    }).unwrap());
    let records: Vec<_> = (0..512)
        .map(|group| VisibilityRecord {
            bounds: InstanceBounds::new([0., 0., 0.2], [0.1, 0.1, 0.3]).unwrap(),
            group,
        })
        .collect();
    let groups = [VisibilityGroup {
        index_count: 9,
        first_index: 3,
        base_vertex: -2,
        max_distance: 100.,
    }; 512];
    let mut boundary = [0; 16];
    for group in [0, 31, 32, 127, 128, 511] {
        boundary[group / 32] |= 1 << (group % 32);
    }
    for mode in 0..3 {
        let page = match mode {
            0 => GpuVisibilityPage::with_kernel(&device, &queue, kernel.clone(), &records, &groups),
            1 => GpuVisibilityPage::with_partitioned_kernel(
                &device,
                &queue,
                kernel.clone(),
                &records,
                &groups,
            ),
            _ => GpuVisibilityPage::with_reusable_capacity(
                &device,
                &queue,
                kernel.clone(),
                &records,
                &groups,
                nico_render::visibility::VisibilityLayout::new(512, 512).unwrap(),
            ),
        }
        .unwrap();
        let layout = page.layout();
        let mut pending = Vec::new();
        for selection in [[u32::MAX; 16], boundary, [0; 16], [u32::MAX; 16]] {
            let staging = device
                .create_buffer(BufferDescriptor {
                    label: None,
                    size: layout.bytes(),
                    usages: BufferUsages::COPY_DESTINATION | BufferUsages::MAP_READ,
                })
                .unwrap();
            let mut encoder = device.create_command_encoder(None);
            GpuVisibilityPage::record_selected_pages(
                &[(&page, selection)],
                &queue,
                &mut encoder,
                Mat4::IDENTITY,
                Vec3::ZERO,
            )
            .unwrap();
            encoder.copy_buffer_to_buffer(page.output(), 0, &staging, 0, layout.bytes());
            queue.submit(vec![encoder.finish()]);
            pending.push((selection, staging));
        }
        // Reusing one page before CPU waits must preserve each submission's mask.
        for (selection, staging) in pending {
            let mut ticket = device
                .read_buffer_async(&staging, 0..layout.bytes())
                .unwrap();
            let deadline = Instant::now() + Duration::from_secs(10);
            let bytes = loop {
                if let Some(bytes) = ticket.poll().unwrap() {
                    break bytes;
                }
                assert!(Instant::now() < deadline);
                std::thread::yield_now();
            };
            let words: Vec<_> = bytes
                .chunks_exact(4)
                .map(|bytes| u32::from_le_bytes(bytes.try_into().unwrap()))
                .collect();
            for group in 0..512 {
                let selected = u32::from(selection[group / 32] & (1 << (group % 32)) != 0);
                assert_eq!(words[layout.counts_word() as usize + group], selected);
                let args = layout.indirect_byte(group as u32).unwrap() as usize / 4;
                assert_eq!(
                    &words[args..args + 5],
                    &[9, selected, 3, (-2_i32) as u32, 0]
                );
                if selected != 0 {
                    let offset = words[layout.offsets_word() as usize + group];
                    assert_eq!(words[(layout.ids_word() + offset) as usize], group as u32);
                }
            }
        }
    }
    device.check_failure().unwrap();
}

#[test]
#[ignore = "requires graphics; appendable visibility capacity and submission ordering"]
fn gpu_visibility_appends_without_moving_existing_groups() {
    visibility_append_and_replace(false);
    visibility_append_and_replace(true);
}

fn visibility_append_and_replace(reusable: bool) {
    use nico_render::visibility::{VisibilityLayout, visible_ids};
    let (device, queue, _) = setup();
    let kernel = Arc::new(GpuVisibilityKernel::new(&device, ShaderModuleDescriptor {
        label: None, format: ShaderFormat::Wgsl,
        code: include_bytes!("../../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl"),
    }).unwrap());
    let group = VisibilityGroup {
        index_count: 9,
        first_index: 3,
        base_vertex: -2,
        max_distance: 100.,
    };
    let record = |x: f32, group| VisibilityRecord {
        bounds: InstanceBounds::new([x, 0., 0.2], [x + 0.1, 0.1, 0.3]).unwrap(),
        group,
    };
    let initial = [record(0., 0), record(if reusable { 0.4 } else { 3. }, 0)];
    let constructor = if reusable {
        GpuVisibilityPage::with_reusable_capacity
    } else {
        GpuVisibilityPage::with_partitioned_capacity
    };
    let page = constructor(
        &device,
        &queue,
        kernel.clone(),
        &initial,
        &[group],
        VisibilityLayout::new(8, 4).unwrap(),
    )
    .unwrap();
    assert_eq!(page.active_counts(), (2, 1));
    let layout = page.layout();
    let count_word = |g: u32| layout.counts_word() as usize + g as usize;
    let read_counts = |words: &[u32]| (0..4).map(|g| words[count_word(g)]).collect::<Vec<_>>();
    let submit = |clip| {
        let staging = device
            .create_buffer(BufferDescriptor {
                label: None,
                size: layout.bytes(),
                usages: BufferUsages::COPY_DESTINATION | BufferUsages::MAP_READ,
            })
            .unwrap();
        let mut encoder = device.create_command_encoder(None);
        assert_eq!(
            GpuVisibilityPage::record_pages(&[&page], &queue, &mut encoder, clip, Vec3::ZERO)
                .unwrap(),
            2
        );
        encoder.copy_buffer_to_buffer(page.output(), 0, &staging, 0, layout.bytes());
        queue.submit(vec![encoder.finish()]);
        staging
    };
    // Append after submission, before waiting for the previous GPU consumer.
    let first = submit(Mat4::IDENTITY);
    if reusable {
        assert!(
            page.append(&queue, &[record(0., 1), record(0., 0)], &[group; 2])
                .is_err()
        );
        assert_eq!(page.active_counts(), (2, 1));
    }
    let added = if reusable {
        [record(0.5, 0), record(-0.5, 1), record(4., 1)]
    } else {
        [record(-0.5, 1), record(0.5, 0), record(4., 1)]
    };
    assert_eq!(
        page.append(&queue, &added, &[group, group]).unwrap(),
        (2, 1)
    );
    assert_eq!(page.active_counts(), (5, 3));
    assert!(page.append(&queue, &[record(0., 0); 4], &[group]).is_err());
    assert!(page.append(&queue, &[record(0., 1)], &[group]).is_err());
    assert_eq!(page.active_counts(), (5, 3));
    assert_eq!(page.layout().indirect_byte(0), layout.indirect_byte(0));
    assert_eq!(page.append(&queue, &[], &[group]).unwrap(), (5, 3));
    assert_eq!(page.active_counts(), (5, 4));
    assert!(page.append(&queue, &[record(0., 0)], &[group]).is_err());
    assert_eq!(page.active_counts(), (5, 4));
    let populated = submit(Mat4::IDENTITY);
    let hidden = submit(Mat4::from_translation(Vec3::new(20., 0., 0.)));
    let restored = submit(Mat4::IDENTITY);
    let read = |buffer: &WgpuBuffer| {
        let mut ticket = device.read_buffer_async(buffer, 0..layout.bytes()).unwrap();
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            if let Some(bytes) = ticket.poll().unwrap() {
                break bytes
                    .chunks_exact(4)
                    .map(|b| u32::from_le_bytes(b.try_into().unwrap()))
                    .collect::<Vec<_>>();
            }
            assert!(Instant::now() < deadline);
            std::thread::yield_now();
        }
    };
    let first = read(&first);
    assert_eq!(first[count_word(0)], if reusable { 2 } else { 1 });
    assert_eq!(first[layout.ids_word() as usize], 0);
    let populated = read(&populated);
    let mut all = initial.to_vec();
    all.extend(added.into_iter().map(|mut record| {
        record.group += 1;
        record
    }));
    let expected = visible_ids(&all, &[group; 4], Mat4::IDENTITY, Vec3::ZERO).unwrap();
    for (g, expected) in expected.iter().enumerate() {
        let count = populated[count_word(g as u32)] as usize;
        assert_eq!(count, expected.len());
        let offset = populated[layout.offsets_word() as usize + g] as usize;
        let mut ids = populated
            [layout.ids_word() as usize + offset..layout.ids_word() as usize + offset + count]
            .to_vec();
        ids.sort_unstable();
        assert_eq!(&ids, expected);
        let args = layout.indirect_byte(g as u32).unwrap() as usize / 4;
        assert_eq!(
            &populated[args..args + 5],
            &[9, count as u32, 3, (-2_i32) as u32, 0]
        );
    }
    let hidden = read(&hidden);
    assert_eq!(read_counts(&hidden), &[0; 4]);
    let restored = read(&restored);
    for g in 0..4 {
        assert_eq!(
            restored[count_word(g as u32)],
            populated[count_word(g as u32)]
        );
    }
    if reusable {
        assert!(
            page.replace_group(&queue, 0, 0..1, &initial, group)
                .is_err()
        );
        assert!(page.replace_group(&queue, 0, 0..9, &[], group).is_err());
        assert!(page.replace_group(&queue, 4, 0..1, &[], group).is_err());
        assert!(
            page.replace_group(&queue, 0, 0..1, &[record(0., 1)], group)
                .is_err()
        );
        page.replace_group(&queue, 0, 0..2, &[record(0.2, 0)], group)
            .unwrap();
        page.replace_group(&queue, 1, 2..3, &[], group).unwrap();
        let shrunk = submit(Mat4::IDENTITY);
        // Reuse a retired tail and the adjacent disabled group's record slot.
        page.replace_group(&queue, 1, 1..3, &[record(0.1, 0), record(0.2, 0)], group)
            .unwrap();
        let reused = submit(Mat4::IDENTITY);
        assert_eq!(page.active_counts(), (5, 4));
        let shrunk = read(&shrunk);
        let reused = read(&reused);
        assert_eq!(read_counts(&shrunk), &[1, 0, 1, 0]);
        assert_eq!(read_counts(&reused), &[1, 2, 1, 0]);
        let base = layout.ids_word() as usize;
        assert_eq!(reused[base], 0);
        let mut new_ids = reused[base + 1..base + 3].to_vec();
        new_ids.sort_unstable();
        assert_eq!(new_ids, [1, 2]);
        assert!(page.disable_groups(&queue, &[4]).is_err());
        assert_eq!(page.disable_groups(&queue, &[1]).unwrap(), 4);
        page.replace_group(&queue, 0, 1..2, &[record(0.1, 0)], group)
            .unwrap();
        let moved = read(&submit(Mat4::IDENTITY));
        assert_eq!(read_counts(&moved), &[1, 0, 1, 0]);
        assert_eq!(moved[base + 1], 1);
    } else {
        assert!(page.replace_group(&queue, 0, 0..2, &[], group).is_err());
    }
    assert!(
        GpuVisibilityPage::with_partitioned_capacity(
            &device,
            &queue,
            kernel,
            &initial,
            &[group],
            VisibilityLayout::new(1, 1).unwrap()
        )
        .is_err()
    );
    device.check_failure().unwrap();
}

#[test]
#[ignore = "manual visibility camera upload cost; no compute or rasterization"]
fn gpu_visibility_camera_upload_measurement() {
    let (device, queue, _) = setup_with_flags(native_instance_flags());
    let buffers: Vec<_> = (0..64)
        .map(|_| {
            device
                .create_buffer(BufferDescriptor {
                    label: Some("camera upload measurement"),
                    size: 96,
                    usages: BufferUsages::UNIFORM | BufferUsages::COPY_DESTINATION,
                })
                .unwrap()
        })
        .collect();
    for trial in 0..3 {
        for order in 0..3 {
            let writes = [1, 16, 64][(trial + order) % 3];
            let mut submit = Duration::ZERO;
            let mut complete = Duration::ZERO;
            for frame in 0_u32..40 {
                let mut bytes = [0_u8; 96];
                bytes[..4].copy_from_slice(&frame.to_le_bytes());
                let start = Instant::now();
                for buffer in &buffers[..writes] {
                    queue.write_buffer(buffer, 0, &bytes);
                }
                queue.submit(Vec::new());
                let submitted = start.elapsed();
                device
                    .inner
                    .poll(wgpu::PollType::Wait {
                        submission_index: None,
                        timeout: Some(Duration::from_secs(10)),
                    })
                    .unwrap();
                if frame >= 10 {
                    submit += submitted;
                    complete += start.elapsed();
                }
            }
            eprintln!(
                "camera_upload trial={trial} buffers={writes} submit_us={:.3} complete_us={:.3}",
                submit.as_secs_f64() * 1e6 / 30.,
                complete.as_secs_f64() * 1e6 / 30.
            );
        }
    }
    device.check_failure().unwrap();
}

#[test]
#[ignore = "requires graphics; grouped renderer lifetime and camera regression"]
fn gpu_grouped_chunks_revalidate_shared_view_and_release_after_last_owner() {
    for separate_pages in [false, true] {
        grouped_chunks_lifetime(separate_pages, false, 1);
        grouped_chunks_lifetime(separate_pages, true, 1);
    }
    grouped_chunks_lifetime(true, false, 1024);
    grouped_chunks_lifetime(true, true, 1024);
}

fn grouped_chunks_lifetime(separate_pages: bool, auto: bool, record_count: u32) {
    grouped_chunks_lifetime_with_limit(separate_pages, auto, record_count, None);
}

#[test]
#[ignore = "requires graphics; large reservations and compact device-limit fallback"]
fn gpu_large_visibility_reservations_share_waves_and_respect_storage_limits() {
    grouped_chunks_lifetime_with_limit(true, false, 8192, None);
    grouped_chunks_lifetime_with_limit(true, true, 8192, None);
    grouped_chunks_lifetime_with_limit(true, false, 8192, Some(1024 * 1024));
}

fn grouped_chunks_lifetime_with_limit(
    separate_pages: bool,
    auto: bool,
    record_count: u32,
    storage_limit: Option<u64>,
) {
    use nico_assets::{Mesh, MeshVertex, PbrMaterial};
    use nico_presentation::{Camera3d, InstanceBatch, InstanceRecord, Scene3d};
    use nico_render::{InstanceRenderMode, MeshRenderPipeline};
    let (mut device, queue, mut target) = setup();
    if let Some(limit) = storage_limit {
        device.capabilities.limits.max_storage_buffer_binding_size = limit;
    }
    let mesh = Arc::new(
        Mesh::triangles(
            [[-0.5, -0.5, 0.], [0.5, -0.5, 0.], [0., 0.5, 0.]]
                .into_iter()
                .map(|position| MeshVertex {
                    position,
                    uv: [0.; 2],
                })
                .collect(),
            vec![0, 1, 2],
        )
        .unwrap(),
    );
    let material = Arc::new(PbrMaterial::default());
    let mut scene = Scene3d {
        instance_batches: [0., 8.]
            .into_iter()
            .enumerate()
            .map(|(id, x)| {
                Arc::new(
                    InstanceBatch::new(
                        mesh.clone(),
                        material.clone(),
                        (0..record_count)
                            .map(|record_id| {
                                InstanceRecord::new(
                                    id as u64 * u64::from(record_count) + u64::from(record_id),
                                    0,
                                    Mat4::from_translation(Vec3::new(x, 0., 0.)),
                                    [1.; 4],
                                )
                                .unwrap()
                            })
                            .collect(),
                        100.,
                    )
                    .unwrap(),
                )
            })
            .collect(),
        camera: Camera3d::looking_at([4., 0., 14.], [4., 0., 0.], [0., 1., 0.]).unwrap(),
        ..Default::default()
    };
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
    renderer
        .enable_instancing(
            &device,
            builtin_shaders::bootstrap_wgsl(include_bytes!(
                "../../../../assets/presentation/shaders/generated/wgpu/instanced_meshes.wgsl"
            )),
        )
        .unwrap();
    renderer.enable_gpu_instancing(&device, builtin_shaders::bootstrap_wgsl(include_bytes!("../../../../assets/presentation/shaders/generated/wgpu/instanced_storage.wgsl")),
        ShaderModuleDescriptor { label:None, format:ShaderFormat::Wgsl, code:include_bytes!("../../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl") }).unwrap();
    renderer
        .set_instance_mode(
            &device,
            &scene,
            if auto {
                InstanceRenderMode::Auto
            } else {
                InstanceRenderMode::Gpu
            },
        )
        .unwrap();
    renderer.set_instance_auto_gpu_min_records(0).unwrap();
    if separate_pages {
        renderer
            .set_instance_source_upload_budget(Some(u64::from(record_count) * 144 + 40))
            .unwrap();
    }
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
                .unwrap();
            pixels(&device, &queue, target)
        };
    render(&mut renderer, &mut target, &scene);
    if separate_pages {
        render(&mut renderer, &mut target, &scene);
    }
    assert_eq!(
        renderer.instance_stats().indirect_draws,
        if auto { 0 } else { 2 }
    );
    assert_eq!(
        renderer.instance_stats().visibility_dispatched_pages,
        u32::from(!auto)
    );
    assert_eq!(
        renderer.instance_stats().visibility_dispatches,
        if auto { 0 } else { 2 }
    );
    let initial_bytes = renderer.instance_stats().retained_instance_bytes;
    if record_count == 8192 {
        let capacity = if storage_limit.is_some() {
            32768
        } else {
            131072
        };
        // Reserve only supported capacity; separately paced uploads share it.
        assert_eq!(
            initial_bytes,
            2 * 8192 * 112
                + 2 * 16
                + capacity * 40
                + 64 * 48
                + nico_render::visibility::VISIBILITY_VIEW_BYTES
        );
    }
    assert_eq!(
        renderer.instance_stats().visibility_reused_batches,
        u32::from(separate_pages && record_count == 1 && !auto)
    );
    if record_count == 1024 {
        // Both separately paced batches share one 4096-record / 64-group page.
        assert_eq!(
            initial_bytes,
            2 * 1024 * 112
                + 2 * 16
                + 4096 * 40
                + 64 * 48
                + nico_render::visibility::VISIBILITY_VIEW_BYTES
        );
        if !auto {
            let both = render(&mut renderer, &mut target, &scene);
            assert_eq!(renderer.instance_stats().visibility_dispatches, 0);
            let neighbor = scene.instance_batches.pop().unwrap();
            let one = render(&mut renderer, &mut target, &scene);
            assert_ne!(one, both);
            assert_eq!(renderer.instance_stats().visibility_dispatches, 2);
            assert_eq!(renderer.instance_stats().instance_upload_bytes, 0);
            scene.instance_batches.push(neighbor);
            assert_eq!(render(&mut renderer, &mut target, &scene), both);
            assert_eq!(renderer.instance_stats().visibility_dispatches, 2);
            assert_eq!(renderer.instance_stats().instance_upload_bytes, 0);
            assert_eq!(render(&mut renderer, &mut target, &scene), both);
            assert_eq!(renderer.instance_stats().visibility_dispatches, 0);
            assert_eq!(renderer.instance_stats().visibility_reused_batches, 2);

            // Fail the second new selection allocation on an existing page.
            // The first temporary binding must not publish a partial cohort.
            renderer.set_instance_source_upload_budget(None).unwrap();
            for _ in 0..2 {
                scene.instance_batches.push(Arc::new(
                    InstanceBatch::new(
                        scene.instance_batches[0].mesh().clone(),
                        scene.instance_batches[0].material().clone(),
                        scene.instance_batches[0].records()[..512].to_vec(),
                        100.,
                    )
                    .unwrap(),
                ));
            }
            *device.fail_bind_group.lock().unwrap() = Some(("instance source and visible IDs", 2));
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
            assert_eq!(error.kind(), RhiErrorKind::OutOfMemory);
            assert!(device.fail_bind_group.lock().unwrap().is_none());
            let pending = scene.instance_batches.split_off(2);
            assert_eq!(render(&mut renderer, &mut target, &scene), both);
            assert_eq!(
                renderer.instance_stats().retained_instance_bytes,
                initial_bytes
            );
            assert_eq!(renderer.instance_stats().instance_upload_bytes, 0);
            scene.instance_batches.extend(pending);
            assert_eq!(render(&mut renderer, &mut target, &scene), both);
            assert_eq!(renderer.instance_stats().instance_upload_bytes, 1024 * 112);
            assert_eq!(
                renderer.instance_stats().retained_instance_bytes,
                initial_bytes + 1024 * 112 + 32
            );
            assert_eq!(render(&mut renderer, &mut target, &scene), both);
            assert_eq!(renderer.instance_stats().instance_upload_bytes, 0);
            scene.instance_batches.truncate(2);
            assert_eq!(render(&mut renderer, &mut target, &scene), both);
            assert_eq!(
                renderer.instance_stats().retained_instance_bytes,
                initial_bytes
            );
        }
    }
    let mut original = None;
    let views: &[f32] = if auto {
        &[0., 0.8, 0., 0.8]
    } else {
        &[0., 8., 0.]
    };
    for (visit, &x) in views.iter().enumerate() {
        scene.camera = Camera3d::looking_at([x, 0., 2.], [x, 0., 0.], [0., 1., 0.]).unwrap();
        let capture = render(&mut renderer, &mut target, &scene);
        let direct = auto && x == 0.;
        assert_eq!(renderer.instance_stats().indirect_draws, u32::from(!direct));
        assert_eq!(
            renderer.instance_stats().submitted_instances,
            u32::from(direct) * record_count
        );
        // Changing another page's camera does not invalidate this page's
        // previously computed visibility output.
        let reused = u32::from(if auto {
            visit == 3
        } else {
            separate_pages && record_count == 1 && visit == 2
        });
        let dispatched = u32::from(!direct) - reused;
        assert_eq!(renderer.instance_stats().visibility_reused_batches, reused);
        assert_eq!(
            renderer.instance_stats().visibility_dispatched_pages,
            dispatched
        );
        assert_eq!(
            renderer.instance_stats().visibility_dispatches,
            2 * dispatched
        );
        assert_eq!(renderer.instance_stats().instance_upload_bytes, 0);
        assert_eq!(renderer.instance_stats().visibility_upload_bytes, 0);
        if x == 0. {
            if let Some(original) = &original {
                assert_eq!(&capture, original);
            } else {
                original = Some(capture);
            }
        }
    }
    if record_count == 1024 {
        renderer.set_instance_source_upload_budget(None).unwrap();
        let reference = render(&mut renderer, &mut target, &scene);
        for count in [512, 1536, 256, 1280, 1024] {
            let source = &scene.instance_batches[0];
            scene.instance_batches[0] = Arc::new(
                InstanceBatch::new(
                    source.mesh().clone(),
                    source.material().clone(),
                    (0..count)
                        .map(|id| InstanceRecord::new(id, 0, Mat4::IDENTITY, [1.; 4]).unwrap())
                        .collect(),
                    100.,
                )
                .unwrap(),
            );
            assert_eq!(render(&mut renderer, &mut target, &scene), reference);
            let stats = renderer.instance_stats();
            assert_eq!(
                stats.retained_instance_bytes,
                initial_bytes - 1024 * 112 + count * 112
            );
            assert_eq!(stats.retained_batches, 2);
            assert_eq!(stats.instance_upload_bytes, count * 112);
            assert_eq!(stats.visibility_upload_bytes, count * 32 + 40);
            assert_eq!(render(&mut renderer, &mut target, &scene), reference);
            assert_eq!(renderer.instance_stats().instance_upload_bytes, 0);
            assert_eq!(renderer.instance_stats().visibility_upload_bytes, 0);
            assert_eq!(
                renderer.instance_stats().visibility_retirement_upload_bytes,
                0
            );
        }
    }
    scene.instance_batches.remove(1);
    render(&mut renderer, &mut target, &scene);
    assert_eq!(renderer.instance_stats().retained_batches, 1);
    assert_eq!(renderer.instance_stats().visibility_reused_batches, 1);
    assert_eq!(renderer.instance_stats().visibility_dispatched_pages, 0);
    assert_eq!(renderer.instance_stats().visibility_dispatches, 0);
    // A separately owned page releases its bounds, group metadata, camera,
    // and output with its source owner.
    assert_eq!(
        renderer.instance_stats().retained_instance_bytes,
        initial_bytes
            - u64::from(record_count) * 112
            - 16
            - if separate_pages && record_count == 1 {
                40 + 48 + nico_render::visibility::VISIBILITY_VIEW_BYTES
            } else {
                0
            }
    );
    scene.instance_batches.clear();
    render(&mut renderer, &mut target, &scene);
    assert_eq!(renderer.instance_stats().retained_instance_bytes, 0);
}

#[test]
#[ignore = "manual visibility page grouping comparison; requires graphics"]
fn gpu_visibility_page_grouping_measurement() {
    let (device, queue, _) = setup_with_flags(native_instance_flags());
    let kernel = Arc::new(GpuVisibilityKernel::new(&device, ShaderModuleDescriptor {
        label: None,
        format: ShaderFormat::Wgsl,
        code: include_bytes!("../../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl"),
    }).unwrap());
    let wait = || {
        device
            .inner
            .poll(wgpu::PollType::Wait {
                submission_index: None,
                timeout: Some(Duration::from_secs(10)),
            })
            .unwrap()
    };
    for per_chunk in [256, 4096] {
        for sparse in [false, true] {
            let groups = vec![
                VisibilityGroup {
                    index_count: 9,
                    first_index: 0,
                    base_vertex: 0,
                    max_distance: 100.,
                };
                64
            ];
            let records: Vec<_> = (0..64 * per_chunk)
                .map(|id| {
                    let x = if sparse && id % 16 != 0 { 10. } else { 0. };
                    VisibilityRecord {
                        bounds: InstanceBounds::new([x, 0., 0.3], [x + 0.1, 0.1, 0.5]).unwrap(),
                        group: (id / per_chunk) as u32,
                    }
                })
                .collect();
            let combined = vec![
                GpuVisibilityPage::with_kernel(&device, &queue, kernel.clone(), &records, &groups)
                    .unwrap(),
            ];
            let separate: Vec<_> = records
                .chunks(per_chunk)
                .map(|chunk| {
                    let local: Vec<_> = chunk
                        .iter()
                        .map(|record| VisibilityRecord {
                            bounds: record.bounds,
                            group: 0,
                        })
                        .collect();
                    GpuVisibilityPage::with_kernel(
                        &device,
                        &queue,
                        kernel.clone(),
                        &local,
                        &groups[..1],
                    )
                    .unwrap()
                })
                .collect();
            wait();
            for trial in 0..3 {
                for index in 0..2 {
                    let grouped = (trial + index) % 2 == 1;
                    let pages: Vec<_> =
                        if grouped { &combined } else { &separate }.iter().collect();
                    let mut submit = Duration::ZERO;
                    let mut complete = Duration::ZERO;
                    for frame in 0..40 {
                        let start = Instant::now();
                        let mut encoder =
                            device.create_command_encoder(Some("visibility page measurement"));
                        GpuVisibilityPage::record_pages(
                            &pages,
                            &queue,
                            &mut encoder,
                            Mat4::IDENTITY,
                            Vec3::new((frame % 2) as f32 * 0.001, 0., 0.),
                        )
                        .unwrap();
                        queue.submit(vec![encoder.finish()]);
                        let submitted = start.elapsed();
                        wait();
                        if frame >= 10 {
                            submit += submitted;
                            complete += start.elapsed();
                        }
                    }
                    // Counts from each group must agree; readback is outside the timing.
                    let readback = device
                        .create_buffer(BufferDescriptor {
                            label: None,
                            size: 64 * 4,
                            usages: BufferUsages::MAP_READ | BufferUsages::COPY_DESTINATION,
                        })
                        .unwrap();
                    let mut encoder = device.create_command_encoder(None);
                    let mut destination = 0;
                    for page in &pages {
                        let size = u64::from(page.layout().group_count()) * 4;
                        encoder.copy_buffer_to_buffer(
                            page.output(),
                            u64::from(page.layout().counts_word()) * 4,
                            &readback,
                            destination,
                            size,
                        );
                        destination += size;
                    }
                    assert_eq!(destination, 64 * 4);
                    queue.submit(vec![encoder.finish()]);
                    let mut ticket = device.read_buffer_async(&readback, 0..destination).unwrap();
                    wait();
                    let deadline = Instant::now() + Duration::from_secs(5);
                    let bytes = loop {
                        if let Some(bytes) = ticket.poll().unwrap() {
                            break bytes;
                        }
                        assert!(Instant::now() < deadline);
                        std::thread::yield_now();
                    };
                    for word in bytes.chunks_exact(4) {
                        assert_eq!(
                            u32::from_le_bytes(word.try_into().unwrap()),
                            (if sparse { per_chunk / 16 } else { per_chunk }) as u32
                        );
                    }
                    eprintln!(
                        "visibility_pages per_chunk={per_chunk} sparse={sparse} trial={trial} grouped={grouped} submit_ms={:.3} complete_ms={:.3}",
                        submit.as_secs_f64() * 1000. / 30.,
                        complete.as_secs_f64() * 1000. / 30.
                    );
                }
            }
        }
    }
    device.check_failure().unwrap();
}

#[test]
#[ignore = "manual selected-group culling cost; requires graphics"]
fn gpu_visibility_selected_groups_measurement() {
    let (device, queue, _) = setup_with_flags(native_instance_flags());
    let kernel = Arc::new(GpuVisibilityKernel::new(&device, ShaderModuleDescriptor {
        label: None, format: ShaderFormat::Wgsl,
        code: include_bytes!("../../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl"),
    }).unwrap());
    let groups = [VisibilityGroup {
        index_count: 9,
        first_index: 0,
        base_vertex: 0,
        max_distance: 100.,
    }; 64];
    let records: Vec<_> = (0..64 * 4096)
        .map(|id| VisibilityRecord {
            bounds: InstanceBounds::new([0., 0., 0.2], [0.1, 0.1, 0.3]).unwrap(),
            group: id / 4096,
        })
        .collect();
    let page = GpuVisibilityPage::with_reusable_capacity(
        &device,
        &queue,
        kernel,
        &records,
        &groups,
        nico_render::visibility::VisibilityLayout::new(records.len(), groups.len()).unwrap(),
    )
    .unwrap();
    let wait = || {
        device
            .inner
            .poll(wgpu::PollType::Wait {
                submission_index: None,
                timeout: Some(Duration::from_secs(10)),
            })
            .unwrap()
    };
    wait();
    for trial in 0..3 {
        for step in 0..2 {
            let selected = (trial + step) % 2 == 0;
            let mut selection = [u32::MAX; 16];
            if selected {
                selection = [0; 16];
                selection[0] = 0xffff;
            }
            let mut elapsed = Duration::ZERO;
            for frame in 0..110 {
                let start = Instant::now();
                let mut encoder = device.create_command_encoder(None);
                GpuVisibilityPage::record_selected_pages(
                    &[(&page, selection)],
                    &queue,
                    &mut encoder,
                    Mat4::IDENTITY,
                    Vec3::new((frame % 2) as f32 * 0.001, 0., 0.),
                )
                .unwrap();
                queue.submit(vec![encoder.finish()]);
                wait();
                if frame >= 10 {
                    elapsed += start.elapsed();
                }
            }
            // Reads occur outside timing. All selected groups must retain the
            // same visible records; the remaining groups must have zero counts.
            let staging = device
                .create_buffer(BufferDescriptor {
                    label: None,
                    size: 64 * 4,
                    usages: BufferUsages::COPY_DESTINATION | BufferUsages::MAP_READ,
                })
                .unwrap();
            let mut encoder = device.create_command_encoder(None);
            encoder.copy_buffer_to_buffer(
                page.output(),
                u64::from(page.layout().counts_word()) * 4,
                &staging,
                0,
                64 * 4,
            );
            queue.submit(vec![encoder.finish()]);
            let mut ticket = device.read_buffer_async(&staging, 0..64 * 4).unwrap();
            let deadline = Instant::now() + Duration::from_secs(10);
            let bytes = loop {
                if let Some(bytes) = ticket.poll().unwrap() {
                    break bytes;
                }
                assert!(Instant::now() < deadline);
                std::thread::yield_now();
            };
            for (group, bytes) in bytes.chunks_exact(4).enumerate() {
                assert_eq!(
                    u32::from_le_bytes(bytes.try_into().unwrap()),
                    if !selected || group < 16 { 4096 } else { 0 }
                );
            }
            eprintln!(
                "selected_groups_measurement trial={trial} selected={selected} input_records=262144 selected_groups={} mean_complete_ms={:.3}",
                if selected { 16 } else { 64 },
                elapsed.as_secs_f64() * 10.
            );
        }
    }
    device.check_failure().unwrap();
}
