//! Manual integration measurement using the game's public presentation adapter.
//! Loading and streaming settle before timing; this is not a startup benchmark.
use super::*;
use arena_arpg_presentation::environment::Environment;
use nico_presentation::{Camera3d, Scene3d};
use nico_render::{InstanceRenderMode, MeshRenderPipeline};
use std::time::{Duration, Instant};

#[test]
#[ignore = "manual full Arena scene CPU/GPU/Auto measurement; requires game assets and graphics"]
fn gpu_arena_instance_path_measurement() {
    arena_measurement(false, false);
}

#[test]
#[ignore = "manual expanded-grass versus instancing steady-frame comparison"]
fn gpu_arena_expanded_grass_measurement() {
    arena_measurement(true, false);
}

#[test]
#[ignore = "manual moving-camera expanded-grass versus instancing comparison"]
fn gpu_arena_moving_grass_measurement() {
    arena_measurement(true, true);
}

fn arena_measurement(expanded_baseline: bool, moving: bool) {
    let root = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../../games/arena-arpg");
    let content = arena_arpg_shared::project::ProjectContent::open(&root).unwrap();
    let zone = content.zone;
    let mut environment =
        Environment::load(&root.join("assets/presentation/worlds/meadow.world-vis.toml")).unwrap();
    environment.apply_scene(&content.scene).unwrap();
    environment.bind(&zone).unwrap();
    let subgroup_shader = std::env::var_os("NICO_MEASUREMENT_SUBGROUP_SHADER");
    let timestamps = std::env::var("NICO_MEASUREMENT_GPU_TIMESTAMPS").as_deref() == Ok("1");
    let mut extra = wgpu::Features::empty();
    if subgroup_shader.is_some() {
        extra |= wgpu::Features::SUBGROUP;
    }
    if timestamps {
        extra |= wgpu::Features::TIMESTAMP_QUERY;
        if std::env::var("NICO_MEASUREMENT_DISPATCH_TIMESTAMPS").as_deref() == Ok("1") {
            extra |= wgpu::Features::TIMESTAMP_QUERY_INSIDE_PASSES;
        }
    }
    let (mut device, queue, mut target) =
        setup_with_extra_features(native_instance_flags(), false, extra);
    if timestamps {
        assert!(
            expanded_baseline,
            "timestamps are scoped to the expanded comparison"
        );
        device.timestamps = Some(gpu_timestamps::PassTimestamps::new(&device));
    }
    // Measurement-only capability control; production readback stays enabled.
    device.disable_buffer_readback =
        std::env::var("NICO_MEASUREMENT_NO_READBACK").as_deref() == Ok("1");
    assert!(
        expanded_baseline || !device.disable_buffer_readback,
        "the image/count fixture requires asynchronous count readback"
    );
    eprintln!(
        "Arena measurement count readback: {}",
        !device.disable_buffer_readback
    );
    let extent = Extent3d::surface(840, 764);
    target.texture = device.inner.create_texture(&wgpu::TextureDescriptor {
        label: Some("Arena measurement"),
        size: wgpu::Extent3d {
            width: 840,
            height: 764,
            depth_or_array_layers: 1,
        },
        mip_level_count: 1,
        sample_count: 1,
        dimension: wgpu::TextureDimension::D2,
        format: wgpu::TextureFormat::Rgba8UnormSrgb,
        usage: wgpu::TextureUsages::RENDER_ATTACHMENT | wgpu::TextureUsages::COPY_SRC,
        view_formats: &[],
    });
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
        .enable_skinning(&device, shader!("skinned_meshes"))
        .unwrap();
    renderer
        .enable_instancing(&device, shader!("instanced_meshes"))
        .unwrap();
    renderer
        .enable_foliage(&device, shader!("foliage_meshes"))
        .unwrap();
    let mut visibility_code = include_str!(
        "../../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl"
    )
    .to_owned();
    if let Some(path) = std::env::var_os("NICO_MEASUREMENT_VISIBILITY_SHADER") {
        assert!(
            subgroup_shader.is_none(),
            "choose one visibility shader override"
        );
        visibility_code = std::fs::read_to_string(path).expect("measurement visibility shader");
        eprintln!("Arena measurement visibility shader override: true");
    }
    if let Some(path) = subgroup_shader {
        visibility_code = std::fs::read_to_string(path).expect("subgroup measurement shader");
        // wgpu 30 enables native subgroup builtins through Features::SUBGROUP;
        // Naga does not yet accept the standardized WGSL enable declaration.
        visibility_code = visibility_code.replace("enable subgroups;", "");
        eprintln!("Arena measurement subgroup compaction: true");
    }
    let disable_selection = std::env::var("NICO_MEASUREMENT_ALL_GROUPS").as_deref() == Ok("1");
    if disable_selection {
        // Measurement-only control: retain identical allocation, draw selection,
        // cache keys and shader ABI, but cull every initialized page group.
        let start = visibility_code
            .find("fn selected_0(")
            .expect("generated selection helper");
        let end = start
            + visibility_code[start..]
                .find('}')
                .expect("selection helper end")
            + 1;
        visibility_code.replace_range(
            start..end,
            "fn selected_0(group_1: u32) -> bool { return true; }",
        );
    }
    eprintln!("Arena measurement group selection: {}", !disable_selection);
    renderer
        .enable_gpu_foliage(
            &device,
            shader!("foliage_storage"),
            ShaderModuleDescriptor {
                label: None,
                format: ShaderFormat::Wgsl,
                code: visibility_code.as_bytes(),
            },
        )
        .unwrap();
    let compact_records = std::env::var("NICO_MEASUREMENT_COMPACT_RECORDS").as_deref() == Ok("1");
    if compact_records {
        renderer.enable_compact_instance_records(&device).unwrap();
    }
    eprintln!("Arena compact instance records: {compact_records}");

    let minimum = std::env::var("NICO_MEASUREMENT_GPU_MIN_RECORDS")
        .map(|value| value.parse().expect("numeric measurement GPU threshold"))
        .unwrap_or(1024);
    renderer.set_instance_auto_gpu_min_records(minimum).unwrap();
    // Match native upload pacing: grouped visibility pages are formed from each
    // admitted upload wave, so the warm layout depends on this budget.
    renderer
        .set_instance_source_upload_budget(Some(8 * 1024 * 1024))
        .unwrap();
    eprintln!("Arena measurement Auto GPU minimum records: {minimum}");
    let wait = || {
        device
            .inner
            .poll(wgpu::PollType::Wait {
                submission_index: None,
                timeout: Some(Duration::from_secs(10)),
            })
            .unwrap()
    };
    let editor_eye = (glam::Vec3::new(
        0.5_f32.sin() * 0.4_f32.cos(),
        0.4_f32.sin(),
        0.5_f32.cos() * 0.4_f32.cos(),
    ) * 22.)
        .to_array();
    for (view, eye, far, wind) in [
        ("wide", [25., 35., 40.], 100., true),
        ("near", [0., 2., 8.], 100., true),
        ("editor", editor_eye, 5000., false),
    ] {
        let mut camera = Camera3d::looking_at(eye, [0.; 3], [0., 1., 0.]).unwrap();
        camera.far = far;
        let deadline = Instant::now() + Duration::from_secs(30);
        let mut scene;
        loop {
            scene = Scene3d {
                camera,
                ..Default::default()
            };
            environment.backdrop(camera, &mut scene);
            for index in 0..zone.obstacles.len() {
                environment.obstacle(index, None, &mut scene);
            }
            environment.decorate(None, &mut scene);
            if environment.inspection["grass_streaming"]["resident"].as_u64() == Some(64) {
                break;
            }
            assert!(
                Instant::now() < deadline,
                "streaming did not settle: {}",
                environment.inspection
            );
            std::thread::yield_now();
        }
        assert_eq!(
            scene
                .instance_batches
                .iter()
                .map(|b| b.records().len())
                .sum::<usize>(),
            326175
        );
        if expanded_baseline {
            let start = Instant::now();
            for _ in 0..1000 {
                renderer
                    .validate_instance_mode(
                        &device,
                        std::hint::black_box(&scene),
                        InstanceRenderMode::Auto,
                    )
                    .unwrap();
            }
            eprintln!(
                "instance_mode_validation view={view} batches={} mean_us={:.3}",
                scene.instance_batches.len(),
                start.elapsed().as_secs_f64() * 1000.
            );
            compare_expanded_grass(
                &device,
                &queue,
                &mut target,
                &mut renderer,
                scene,
                view,
                moving,
            );
            continue;
        }
        use nico_presentation::foliage::{InfluenceKind, InfluenceSnapshot, WorldInfluence};
        scene.foliage_influences = Some(Arc::new(
            InfluenceSnapshot::new(
                0.25,
                vec![
                    WorldInfluence::new(
                        1,
                        InfluenceKind::DirectionalWind,
                        [0.; 3],
                        [1., 0., 0.],
                        200.,
                        1.,
                        0.,
                        10.,
                    )
                    .unwrap(),
                ],
            )
            .unwrap(),
        ));
        if !wind {
            scene.foliage_influences = None;
        }
        let modes = [
            InstanceRenderMode::Cpu,
            InstanceRenderMode::Gpu,
            InstanceRenderMode::Auto,
        ];
        let mut expected_visible = None;
        let mut captures = Vec::new();
        for trial in 0..3 {
            for index in 0..3 {
                let mode = modes[(index + trial) % modes.len()];
                renderer.set_instance_mode(&device, &scene, mode).unwrap();
                let mut submit = Duration::ZERO;
                let mut complete = Duration::ZERO;
                let mut first = (Duration::ZERO, Duration::ZERO);
                for frame in 0..40 {
                    let start = Instant::now();
                    renderer
                        .render(
                            &device,
                            &queue,
                            &mut target,
                            &scene,
                            &Scene2d::default(),
                            &UiScene::default(),
                            [840., 764.],
                            extent,
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
                        submit += submitted;
                        complete += completed;
                    }
                }
                renderer.poll_instance_readbacks();
                let stats = renderer.instance_stats();
                let sample = stats
                    .gpu_readback
                    .sample
                    .expect("completed visibility sample");
                assert!(stats.prepared_view - sample.prepared_view < 8);
                let visible = stats.submitted_instances + sample.visible_instances;
                if let Some(expected) = expected_visible {
                    if visible != expected {
                        diagnose_visibility(&device, &queue, &scene);
                    }
                    assert_eq!(visible, expected);
                } else {
                    expected_visible = Some(visible);
                }
                eprintln!(
                    "arena_measurement view={view} trial={trial} mode={mode:?} visible={visible} first_submit_ms={:.3} first_complete_ms={:.3} warm_submit_ms={:.3} warm_complete_ms={:.3} resident_bytes={} draws={} indirect={}",
                    first.0.as_secs_f64() * 1000.,
                    first.1.as_secs_f64() * 1000.,
                    submit.as_secs_f64() * 1000. / 30.,
                    complete.as_secs_f64() * 1000. / 30.,
                    stats.retained_instance_bytes,
                    stats.submitted_draws,
                    stats.indirect_draws
                );
                captures.push((mode, trial, capture_scene(&device, &queue, &target)));
            }
        }
        let mut order_references = Vec::new();
        for ordering in 0..5_u64 {
            let mut reverse = scene.clone();
            reverse.instance_batches = scene
                .instance_batches
                .iter()
                .map(|batch| {
                    let mut records = batch.records().to_vec();
                    if ordering == 0 {
                        records.reverse();
                    } else {
                        records.sort_by_cached_key(|record| {
                            let mut key = record.id() ^ ordering.wrapping_mul(0x9e3779b97f4a7c15);
                            key = (key ^ (key >> 30)).wrapping_mul(0xbf58476d1ce4e5b9);
                            key = (key ^ (key >> 27)).wrapping_mul(0x94d049bb133111eb);
                            key ^ (key >> 31)
                        });
                    }
                    let mut copy = nico_presentation::InstanceBatch::new(
                        batch.mesh().clone(),
                        batch.material().clone(),
                        records,
                        batch.max_draw_distance(),
                    )
                    .unwrap();
                    if let Some(profile) = batch.foliage() {
                        copy = copy.with_foliage(profile).unwrap();
                    }
                    Arc::new(copy)
                })
                .collect();
            // Retire the forward batch residency before loading an independent copy.
            renderer
                .set_instance_mode(&device, &reverse, InstanceRenderMode::Gpu)
                .unwrap();
            renderer
                .set_instance_mode(&device, &reverse, InstanceRenderMode::Cpu)
                .unwrap();
            for frame in 0..128 {
                renderer
                    .render(
                        &device,
                        &queue,
                        &mut target,
                        &reverse,
                        &Scene2d::default(),
                        &UiScene::default(),
                        [840., 764.],
                        extent,
                    )
                    .unwrap();
                if renderer.instance_stats().deferred_upload_chunks == 0 {
                    break;
                }
                assert!(frame < 127, "CPU ordering reference did not finish uploads");
            }
            assert_eq!(
                Some(renderer.instance_stats().submitted_instances),
                expected_visible,
                "CPU ordering reference must contain the full visible set"
            );
            wait();
            order_references.push(capture_scene(&device, &queue, &target));
        }
        let reverse = &order_references[0];
        let cpu = &captures
            .iter()
            .find(|(mode, _, _)| *mode == InstanceRenderMode::Cpu)
            .unwrap()
            .2;
        let order_pixels = cpu
            .chunks_exact(4)
            .zip(reverse.chunks_exact(4))
            .filter(|(a, b)| a != b)
            .count();
        for (mode, trial, capture) in &captures {
            let mut changed = 0;
            let mut outside_order = 0;
            let mut matches_reverse = 0;
            let mut unexplained = 0;
            for (pixel, ((reference, reversed), actual)) in cpu
                .chunks_exact(4)
                .zip(reverse.chunks_exact(4))
                .zip(capture.chunks_exact(4))
                .enumerate()
            {
                if actual != reference {
                    changed += 1;
                    outside_order += usize::from(reference == reversed);
                    matches_reverse += usize::from(actual == reversed);
                    unexplained += usize::from(
                        !order_references
                            .iter()
                            .any(|r| &r[pixel * 4..pixel * 4 + 4] == actual),
                    );
                }
            }
            if *mode == InstanceRenderMode::Cpu {
                assert_eq!(changed, 0, "CPU repeats must be exact");
            }
            // No numeric pixel tolerance: every differing GPU pixel must be an
            // exact color produced at that pixel by another CPU record order.
            assert_eq!(
                unexplained, 0,
                "GPU colors must match a CPU ordering: {view} {mode:?}"
            );
            eprintln!(
                "arena_image_order view={view} trial={trial} mode={mode:?} changed={changed} reverse_changed={order_pixels} outside_order={outside_order} matches_reverse={matches_reverse} unexplained={unexplained}"
            );
        }
    }
    device.check_failure().unwrap();
}

fn expanded_grass(scene: &Scene3d) -> Scene3d {
    use nico_assets::{MaterialTexture, Mesh, MeshVertex, PbrMaterial, Texture};
    use nico_presentation::MeshInstance;
    let colors = [
        [107, 164, 25, 255],
        [139, 186, 37, 255],
        [161, 192, 42, 255],
        [184, 170, 40, 255],
        [207, 173, 42, 255],
        [96, 145, 29, 255],
    ];
    let material = Arc::new(PbrMaterial {
        double_sided: true,
        base_color_texture: Some(MaterialTexture::new(Arc::new(
            Texture::rgba8(6, 1, colors.iter().flatten().copied().collect()).unwrap(),
        ))),
        ..Default::default()
    });
    let mut expanded = scene.clone();
    expanded.instance_batches.clear();
    for batch in &scene.instance_batches {
        let mut vertices = Vec::new();
        let mut normals = Vec::new();
        let mut indices = Vec::new();
        for record in batch.records() {
            let rgb = record.tint().map(|v| {
                let srgb = if v <= 0.0031308 {
                    v * 12.92
                } else {
                    1.055 * v.powf(1. / 2.4) - 0.055
                };
                (srgb * 255.).round() as u8
            });
            let color = colors
                .iter()
                .position(|c| *c == rgb)
                .expect("original six-color grass palette");
            let base = vertices.len() as u32;
            for (vertex, normal) in batch.mesh().vertices().iter().zip(batch.mesh().normals()) {
                vertices.push(MeshVertex {
                    position: record
                        .transform()
                        .transform_point3(vertex.position.into())
                        .to_array(),
                    uv: [(color as f32 + 0.5) / 6., 0.5],
                });
                normals.push(
                    (record.normal_transform() * glam::Vec3::from(*normal))
                        .normalize()
                        .to_array(),
                );
            }
            indices.extend(batch.mesh().indices().iter().map(|i| base + i));
        }
        expanded.meshes.push(MeshInstance {
            mesh: Some(Arc::new(
                Mesh::triangles(vertices, indices)
                    .unwrap()
                    .with_normals(normals)
                    .unwrap(),
            )),
            material: Some(material.clone()),
            texture: None,
            skin_palette: None,
            mirrored: false,
            position: [0.; 3],
            orientation: glam::Quat::IDENTITY,
            scale: 1.,
            color: [1.; 4],
        });
    }
    assert!(expanded.meshes.len() <= 256);
    expanded
}

#[allow(clippy::too_many_arguments)]
fn compare_expanded_grass(
    device: &WgpuDevice,
    queue: &WgpuQueue,
    target: &mut Target,
    renderer: &mut MeshRenderPipeline<WgpuDevice>,
    mut scene: Scene3d,
    view: &str,
    moving: bool,
) {
    // Original grass scope: the newly added shrub consumer and wind are absent
    // from BOTH paths. Keep the same scenery, placements, palette and camera.
    scene
        .instance_batches
        .retain(|b| b.records().first().is_some_and(|r| r.id() < 1_u64 << 32));
    scene.foliage_influences = None;
    // Diagnostic isolation only. The default full-scene comparison remains the
    // acceptance workload; both paths remove identical ordinary scenery here.
    let grass_only = std::env::var("NICO_MEASUREMENT_GRASS_ONLY").as_deref() == Ok("1");
    if grass_only {
        scene.meshes.clear();
    }
    eprintln!("expanded_comparison_scope view={view} grass_only={grass_only}");
    assert_eq!(
        scene
            .instance_batches
            .iter()
            .map(|b| b.records().len())
            .sum::<usize>(),
        323871
    );
    let all_expanded = expanded_grass(&scene);
    eprintln!(
        "fully_visible view={view} batches={} records={}",
        scene
            .instance_batches
            .iter()
            .filter(|batch| batch.bounds().unwrap().fully_visible(
                scene.camera.view_projection(840. / 764.).unwrap(),
                scene.camera.position.into(),
                batch.max_draw_distance()
            ))
            .count(),
        scene
            .instance_batches
            .iter()
            .filter(|batch| batch.bounds().unwrap().fully_visible(
                scene.camera.view_projection(840. / 764.).unwrap(),
                scene.camera.position.into(),
                batch.max_draw_distance()
            ))
            .map(|batch| batch.records().len())
            .sum::<usize>()
    );
    let mut shifted = scene.clone();
    shifted.camera.position[0] += 0.1;
    let mut scenes = [scene.clone(), shifted];
    let expanded: Vec<_> = scenes
        .iter()
        .map(|input| {
            let mut output = input.clone();
            output.instance_batches.clear();
            let clip = input.camera.view_projection(840. / 764.).unwrap();
            output.meshes.extend(
                input
                    .instance_batches
                    .iter()
                    .zip(all_expanded.meshes.iter().skip(scene.meshes.len()))
                    .filter(|(batch, _)| batch.bounds().unwrap().intersects_clip(clip))
                    .map(|(_, draw)| draw.clone()),
            );
            output
        })
        .collect();
    // The historical expanded draw list is selected before render timing, while
    // instance chunk selection occurs inside render. Quantify that asymmetry
    // separately; do not subtract it from the recorded rendering comparison.
    let selection_start = Instant::now();
    let mut selected_draws = 0;
    for frame in 0..10_000 {
        let input = std::hint::black_box(&scenes[frame % scenes.len()]);
        let clip = input.camera.view_projection(840. / 764.).unwrap();
        let selected: Vec<_> = input
            .instance_batches
            .iter()
            .zip(all_expanded.meshes.iter().skip(scene.meshes.len()))
            .filter(|(batch, _)| batch.bounds().unwrap().intersects_clip(clip))
            .map(|(_, draw)| draw.clone())
            .collect();
        assert_eq!(
            selected.len(),
            expanded[frame % scenes.len()].meshes.len() - scene.meshes.len()
        );
        selected_draws += std::hint::black_box(selected).len();
    }
    eprintln!(
        "expanded_chunk_selection view={view} iterations=10000 mean_us={:.3} selected_draws={selected_draws}",
        selection_start.elapsed().as_secs_f64() * 100.,
    );
    if let Ok(value) = std::env::var("NICO_MEASUREMENT_COALESCE_GRASS") {
        // Layout-only experiment outside timing: retain the original expanded
        // chunk baseline while testing fewer same-prototype instance batches.
        // This does not model streaming, eviction or production source uploads.
        let limit = value
            .parse::<usize>()
            .expect("numeric coalesced batch size");
        assert!((1..=nico_presentation::MAX_BATCH_INSTANCES).contains(&limit));
        let prototype = &scene.instance_batches[0];
        assert!(scene.instance_batches.iter().all(|batch| {
            Arc::ptr_eq(batch.mesh(), prototype.mesh())
                && Arc::ptr_eq(batch.material(), prototype.material())
                && batch.foliage() == prototype.foliage()
                && batch.max_draw_distance() == prototype.max_draw_distance()
        }));
        let records: Vec<_> = scene
            .instance_batches
            .iter()
            .flat_map(|batch| batch.records().iter().cloned())
            .collect();
        let batches: Vec<_> = records
            .chunks(limit)
            .map(|records| {
                let mut batch = nico_presentation::InstanceBatch::new(
                    prototype.mesh().clone(),
                    prototype.material().clone(),
                    records.to_vec(),
                    prototype.max_draw_distance(),
                )
                .unwrap();
                if let Some(profile) = prototype.foliage() {
                    batch = batch.with_foliage(profile).unwrap();
                }
                Arc::new(batch)
            })
            .collect();
        eprintln!(
            "coalesced_grass view={view} records={} batches={} limit={limit}",
            records.len(),
            batches.len()
        );
        scene.instance_batches = batches.clone();
        for input in &mut scenes {
            input.instance_batches = batches.clone();
        }
    }
    if let Ok(order) = std::env::var("NICO_MEASUREMENT_RECORD_ORDER") {
        assert!(matches!(order.as_str(), "front" | "back" | "morton"));
        // Diagnostic only: sort immutable source records before all uploads and
        // timing. The original expanded baseline and chunk boundaries stay fixed.
        // This does not measure runtime GPU sorting or camera-dependent repacking.
        let forward = scene.camera.orientation * -glam::Vec3::Z;
        let eye = glam::Vec3::from(scene.camera.position);
        let batches: Vec<_> = scene
            .instance_batches
            .iter()
            .map(|batch| {
                let mut indices: Vec<_> = (0..batch.records().len()).collect();
                let depth: Vec<_> = indices
                    .iter()
                    .map(|&index| (batch.record_bounds(index).unwrap().center() - eye).dot(forward))
                    .collect();
                let centers: Vec<_> = indices
                    .iter()
                    .map(|&i| batch.record_bounds(i).unwrap().center())
                    .collect();
                let minimum = centers
                    .iter()
                    .copied()
                    .fold(glam::Vec3::splat(f32::INFINITY), glam::Vec3::min);
                let maximum = centers
                    .iter()
                    .copied()
                    .fold(glam::Vec3::splat(f32::NEG_INFINITY), glam::Vec3::max);
                let span = (maximum - minimum).max_element().max(1e-6);
                let morton: Vec<_> = centers
                    .iter()
                    .map(|center| {
                        let grid = (((*center - minimum) / span) * 1023.)
                            .clamp(glam::Vec3::ZERO, glam::Vec3::splat(1023.))
                            .as_uvec3()
                            .to_array();
                        let mut code = 0u32;
                        for bit in 0..10 {
                            for (axis, coordinate) in grid.iter().enumerate() {
                                code |= ((coordinate >> bit) & 1) << (bit * 3 + axis);
                            }
                        }
                        code
                    })
                    .collect();
                indices.sort_by(|&a, &b| {
                    if order == "morton" {
                        morton[a].cmp(&morton[b])
                    } else if order == "front" {
                        depth[a].total_cmp(&depth[b])
                    } else {
                        depth[b].total_cmp(&depth[a])
                    }
                });
                let mut sorted = nico_presentation::InstanceBatch::new(
                    batch.mesh().clone(),
                    batch.material().clone(),
                    indices
                        .iter()
                        .map(|&i| batch.records()[i].clone())
                        .collect(),
                    batch.max_draw_distance(),
                )
                .unwrap();
                if let Some(profile) = batch.foliage() {
                    sorted = sorted.with_foliage(profile).unwrap();
                }
                assert_eq!(sorted.bounds(), batch.bounds());
                assert_eq!(sorted.records().len(), batch.records().len());
                Arc::new(sorted)
            })
            .collect();
        scene.instance_batches = batches.clone();
        for input in &mut scenes {
            input.instance_batches = batches.clone();
        }
        eprintln!("source_record_order view={view} order={order} sorting_outside_timing=true");
    }
    let mode = match std::env::var("NICO_MEASUREMENT_INSTANCE_MODE").as_deref() {
        Ok("cpu") => InstanceRenderMode::Cpu,
        Ok("gpu") => InstanceRenderMode::Gpu,
        Ok("auto") | Err(_) => InstanceRenderMode::Auto,
        Ok(value) => panic!("invalid measurement instance mode: {value}"),
    };
    renderer.set_instance_mode(device, &scene, mode).unwrap();
    let upload_wave = std::env::var("NICO_MEASUREMENT_UPLOAD_WAVE_BATCHES")
        .ok()
        .map(|value| value.parse::<usize>().expect("integer upload wave size"));
    if let Some(wave) = upload_wave {
        assert!((1..=128).contains(&wave));
        // Reproduce small immutable publication waves without including provider
        // scheduling or source uploads in the steady-frame measurements below.
        // Clear prior-view residency so every view uses the requested cohorts.
        let other = if mode == InstanceRenderMode::Gpu {
            InstanceRenderMode::Cpu
        } else {
            InstanceRenderMode::Gpu
        };
        renderer.set_instance_mode(device, &scene, other).unwrap();
        renderer.set_instance_mode(device, &scene, mode).unwrap();
        let mut published = scene.clone();
        published.instance_batches.clear();
        for batches in scene.instance_batches.chunks(wave) {
            published.instance_batches.extend_from_slice(batches);
            renderer
                .render(
                    device,
                    queue,
                    target,
                    &published,
                    &Scene2d::default(),
                    &UiScene::default(),
                    [840., 764.],
                    Extent3d::surface(840, 764),
                )
                .unwrap();
        }
        device
            .inner
            .poll(wgpu::PollType::Wait {
                submission_index: None,
                timeout: Some(Duration::from_secs(10)),
            })
            .unwrap();
    }
    let measured_frames = std::env::var("NICO_MEASUREMENT_FRAMES")
        .map(|value| {
            value
                .parse::<u32>()
                .expect("numeric measurement frame count")
        })
        .unwrap_or(30);
    assert!((1..=10_000).contains(&measured_frames));
    let timestamp_frame_limit = if device
        .inner
        .features()
        .contains(wgpu::Features::TIMESTAMP_QUERY_INSIDE_PASSES)
    {
        400
    } else {
        500
    };
    assert!(
        device.timestamps.is_none() || measured_frames <= timestamp_frame_limit,
        "timestamp round exceeds its query budget"
    );
    let warm_rounds = std::env::var("NICO_MEASUREMENT_WARM_ROUNDS")
        .map(|value| value.parse::<u32>().expect("numeric warm-round count"))
        .unwrap_or(0);
    assert!(warm_rounds <= 3);
    eprintln!(
        "expanded_comparison_samples view={view} warm=10 measured={measured_frames} warm_rounds={warm_rounds}"
    );
    let paired = std::env::var("NICO_MEASUREMENT_PAIRED").as_deref() == Ok("1");
    for round in 0..warm_rounds + 3 {
        let trial = round.saturating_sub(warm_rounds);
        let label = if round < warm_rounds {
            "expanded_comparison_warmup"
        } else {
            "expanded_comparison"
        };
        let schedule: Vec<_> = if paired {
            (0..10 + measured_frames)
                .flat_map(|frame| {
                    (0..2).map(move |index| ((frame + index + round) % 2 == 0, frame))
                })
                .collect()
        } else {
            (0..2)
                .flat_map(|index| {
                    (0..10 + measured_frames).map(move |frame| ((index + round) % 2 == 0, frame))
                })
                .collect()
        };
        let mut submissions = [Duration::ZERO; 2];
        let mut completions = [Duration::ZERO; 2];
        let mut counters = [(0, 0, 0); 2];
        for (legacy, frame) in schedule {
            let slot = usize::from(!legacy);
            let camera = if moving { (frame % 2) as usize } else { 0 };
            let input = if legacy {
                &expanded[camera]
            } else {
                &scenes[camera]
            };
            if let Some(timestamps) = &device.timestamps {
                timestamps.select((frame >= 10).then_some(slot));
            }
            let start = Instant::now();
            renderer
                .render(
                    device,
                    queue,
                    target,
                    input,
                    &Scene2d::default(),
                    &UiScene::default(),
                    [840., 764.],
                    Extent3d::surface(840, 764),
                )
                .unwrap();
            let submitted = start.elapsed();
            device
                .inner
                .poll(wgpu::PollType::Wait {
                    submission_index: None,
                    timeout: Some(Duration::from_secs(10)),
                })
                .unwrap();
            if frame >= 10 {
                submissions[slot] += submitted;
                completions[slot] += start.elapsed();
                assert_eq!(renderer.instance_stats().instance_upload_bytes, 0);
                assert_eq!(renderer.instance_stats().visibility_upload_bytes, 0);
                assert_eq!(renderer.instance_stats().foliage_upload_bytes, 0);
                if moving {
                    assert_eq!(renderer.instance_stats().visibility_reused_batches, 0);
                }
            }
            let stats = renderer.instance_stats();
            counters[slot] = (
                stats.submitted_draws,
                stats.visibility_dispatched_pages,
                stats.visibility_dispatches,
            );
        }
        if let Some(timestamps) = &device.timestamps {
            timestamps.report(
                device,
                queue,
                &format!(
                    "view={view} round={round} warmup={} grass_only={grass_only} mode={mode:?} moving={moving} paired={paired}",
                    round < warm_rounds
                ),
                [measured_frames; 2],
                ["expanded=true", "expanded=false"],
            );
        }
        for slot in 0..2 {
            let legacy = slot == 0;
            eprintln!(
                "{label} view={view} trial={trial} expanded={legacy} moving={moving} mode={mode:?} upload_wave={upload_wave:?} submit_ms={:.3} complete_ms={:.3} ordinary_draws={} instance_draws={} visibility_pages={} visibility_dispatches={} paired={paired}",
                submissions[slot].as_secs_f64() * 1000. / f64::from(measured_frames),
                completions[slot].as_secs_f64() * 1000. / f64::from(measured_frames),
                if legacy {
                    expanded[0].meshes.len()
                } else {
                    scene.meshes.len()
                },
                counters[slot].0,
                counters[slot].1,
                counters[slot].2
            );
        }
    }
}

fn capture_scene(device: &WgpuDevice, queue: &WgpuQueue, target: &Target) -> Vec<u8> {
    const ROW: u32 = 3584; // 840 RGBA pixels rounded up to COPY_BYTES_PER_ROW_ALIGNMENT.
    let buffer = device.inner.create_buffer(&wgpu::BufferDescriptor {
        label: None,
        size: ROW as u64 * 764,
        usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
        mapped_at_creation: false,
    });
    let mut encoder = device.inner.create_command_encoder(&Default::default());
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
                bytes_per_row: Some(ROW),
                rows_per_image: Some(764),
            },
        },
        wgpu::Extent3d {
            width: 840,
            height: 764,
            depth_or_array_layers: 1,
        },
    );
    queue.inner.submit([encoder.finish()]);
    let (tx, rx) = std::sync::mpsc::channel();
    buffer
        .slice(..)
        .map_async(wgpu::MapMode::Read, move |r| tx.send(r).unwrap());
    device
        .inner
        .poll(wgpu::PollType::Wait {
            submission_index: None,
            timeout: Some(Duration::from_secs(10)),
        })
        .unwrap();
    rx.recv_timeout(Duration::from_secs(10)).unwrap().unwrap();
    let pixels = buffer
        .slice(..)
        .get_mapped_range()
        .unwrap()
        .chunks_exact(ROW as usize)
        .flat_map(|row| row[..840 * 4].iter().copied())
        .collect();
    buffer.unmap();
    pixels
}

fn diagnose_visibility(device: &WgpuDevice, queue: &WgpuQueue, scene: &Scene3d) {
    use nico_render::visibility::{GpuVisibilityPage, VisibilityGroup, VisibilityRecord};
    let clip = scene.camera.view_projection(840. / 764.).unwrap();
    let eye = glam::Vec3::from(scene.camera.position);
    let mut records = Vec::new();
    let mut identities = Vec::new();
    let mut groups = Vec::new();
    for batch in &scene.instance_batches {
        let Some(bounds) = batch.bounds() else {
            continue;
        };
        if !bounds.intersects_clip(clip) || !bounds.within_distance(eye, batch.max_draw_distance())
        {
            continue;
        }
        let group = groups.len() as u32;
        groups.push(VisibilityGroup {
            index_count: 3,
            first_index: 0,
            base_vertex: 0,
            max_distance: batch.max_draw_distance(),
        });
        for (i, record) in batch.records().iter().enumerate() {
            records.push(VisibilityRecord {
                bounds: batch.record_bounds(i).unwrap(),
                group,
            });
            identities.push(record.id());
        }
    }
    let page = GpuVisibilityPage::new(
        device,
        queue,
        ShaderModuleDescriptor {
            label: None,
            format: ShaderFormat::Wgsl,
            code: include_bytes!(
                "../../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl"
            ),
        },
        &records,
        &groups,
    )
    .unwrap();
    let mut encoder = device.create_command_encoder(None);
    page.record(queue, &mut encoder, clip, eye).unwrap();
    let bytes = records.len() as u64 * 4;
    let buffer = WgpuBuffer(device.inner.create_buffer(&wgpu::BufferDescriptor {
        label: None,
        size: bytes,
        usage: wgpu::BufferUsages::COPY_DST | wgpu::BufferUsages::MAP_READ,
        mapped_at_creation: false,
    }));
    encoder.copy_buffer_to_buffer(page.output(), 0, &buffer, 0, bytes);
    queue.submit(vec![encoder.finish()]);
    let (tx, rx) = std::sync::mpsc::channel();
    buffer
        .0
        .slice(..)
        .map_async(wgpu::MapMode::Read, move |r| tx.send(r).unwrap());
    device
        .inner
        .poll(wgpu::PollType::Wait {
            submission_index: None,
            timeout: Some(Duration::from_secs(10)),
        })
        .unwrap();
    rx.recv_timeout(Duration::from_secs(10)).unwrap().unwrap();
    let mapped = buffer.0.slice(..).get_mapped_range().unwrap();
    for (i, bytes) in mapped.chunks_exact(4).enumerate() {
        let record = records[i];
        let gpu = u32::from_le_bytes(bytes.try_into().unwrap()) != 0;
        let cpu = record.bounds.intersects_clip(clip)
            && record
                .bounds
                .within_distance(eye, groups[record.group as usize].max_distance);
        if cpu != gpu {
            let mut margins = [f32::NEG_INFINITY; 6];
            for corner in 0..8 {
                let min = record.bounds.min();
                let max = record.bounds.max();
                let p = clip
                    * glam::Vec3::new(
                        if corner & 1 == 0 { min[0] } else { max[0] },
                        if corner & 2 == 0 { min[1] } else { max[1] },
                        if corner & 4 == 0 { min[2] } else { max[2] },
                    )
                    .extend(1.);
                for (out, value) in margins.iter_mut().zip([
                    p.x + p.w,
                    p.w - p.x,
                    p.y + p.w,
                    p.w - p.y,
                    p.z,
                    p.w - p.z,
                ]) {
                    *out = out.max(value);
                }
            }
            eprintln!(
                "visibility_disagreement id={} cpu={cpu} gpu={gpu} bounds={:?} plane_margins={margins:?}",
                identities[i], record.bounds
            );
        }
    }
}
