//! Isolated tiny-prototype scheduling experiment, not an Arena acceptance test.
use super::*;
use std::time::{Duration, Instant};

#[test]
#[ignore = "manual tiny-mesh hardware-instance packing comparison"]
fn gpu_instance_tile_measurement() {
    let timestamp_enabled = std::env::var("NICO_MEASUREMENT_GPU_TIMESTAMPS").as_deref() == Ok("1");
    let (device, queue, target) = setup_with_extra_features(
        native_instance_flags(),
        false,
        if timestamp_enabled {
            wgpu::Features::TIMESTAMP_QUERY
        } else {
            wgpu::Features::empty()
        },
    );
    let timestamps = timestamp_enabled.then(|| gpu_timestamps::PassTimestamps::new(&device));
    let wait = || {
        device
            .inner
            .poll(wgpu::PollType::Wait {
                submission_index: None,
                timeout: Some(Duration::from_secs(10)),
            })
            .unwrap();
    };
    // Deliberately not divisible by any tiled case, exercising a partial tail.
    const RECORDS: u32 = 262_145;
    let variants: Vec<_> = [1_u32, 4, 8, 16, 32].into_iter().map(|tile| {
        let code = format!(r#"
const TILE: u32 = {tile}u;
const RECORDS: u32 = {RECORDS}u;
const POSITIONS: array<vec2<f32>, 5> = array<vec2<f32>, 5>(
    vec2<f32>(-0.0015, 0.0), vec2<f32>(0.0015, 0.0),
    vec2<f32>(-0.001, 0.003), vec2<f32>(0.001, 0.003),
    vec2<f32>(0.0, 0.006));
struct Output {{
    @builtin(position) position: vec4<f32>,
    @location(0) color: vec3<f32>,
}}
@vertex fn vertex_main(@builtin(vertex_index) vertex: u32,
                       @builtin(instance_index) instance: u32) -> Output {{
    let record = instance * TILE + vertex / 5u;
    var result: Output;
    result.position = vec4<f32>(2.0, 2.0, 0.0, 1.0);
    result.color = vec3<f32>(0.0);
    if (record < RECORDS) {{
        let grid = vec2<f32>(f32(record % 512u), f32((record / 512u) % 512u)) / 256.0 - vec2<f32>(1.0);
        // The final valid record is conspicuous. If surplus tail records were
        // accidentally emitted, they would overlap it with different colors.
        let tail = record >= RECORDS - 1u;
        let offset = select(grid, vec2<f32>(0.0), tail);
        let scale = select(1.0, 100.0, tail);
        result.position = vec4<f32>(offset + POSITIONS[vertex % 5u] * scale, 0.0, 1.0);
        result.color = vec3<f32>(0.2 + f32(record % 7u) * 0.1, 0.7, 0.2);
    }}
    return result;
}}
@fragment fn fragment_main(input: Output) -> @location(0) vec4<f32> {{
    return vec4<f32>(input.color, 1.0);
}}
"#);
        let shader = device.inner.create_shader_module(wgpu::ShaderModuleDescriptor {
            label: Some("tiny instance tile experiment"),
            source: wgpu::ShaderSource::Wgsl(code.into()),
        });
        let pipeline = device.inner.create_render_pipeline(&wgpu::RenderPipelineDescriptor {
            label: Some("tiny instance tile experiment"),
            layout: None,
            vertex: wgpu::VertexState {
                module: &shader, entry_point: Some("vertex_main"),
                compilation_options: Default::default(), buffers: &[],
            },
            primitive: wgpu::PrimitiveState::default(),
            depth_stencil: None,
            multisample: wgpu::MultisampleState::default(),
            fragment: Some(wgpu::FragmentState {
                module: &shader, entry_point: Some("fragment_main"),
                compilation_options: Default::default(),
                targets: &[Some(wgpu::ColorTargetState {
                    format: wgpu::TextureFormat::Rgba8UnormSrgb,
                    blend: None, write_mask: wgpu::ColorWrites::ALL,
                })],
            }),
            multiview_mask: None, cache: None,
        });
        let indices: Vec<_> = (0..tile).flat_map(|copy| {
            [0_u32, 1, 2, 1, 3, 2, 2, 3, 4].into_iter().map(move |index| copy * 5 + index)
        }).flat_map(u32::to_le_bytes).collect();
        let buffer = device.inner.create_buffer(&wgpu::BufferDescriptor {
            label: Some("tiled shared prototype indices"), size: indices.len() as u64,
            usage: wgpu::BufferUsages::INDEX | wgpu::BufferUsages::COPY_DST,
            mapped_at_creation: false,
        });
        queue.inner.write_buffer(&buffer, 0, &indices);
        (tile, pipeline, buffer)
    }).collect();
    let view = target.texture.create_view(&Default::default());
    let mut reference = None;
    // Warm every pipeline/workload for a full round before reporting trials.
    // This keeps initial device activity from affecting only the one-record case.
    for round in 0..4 {
        for index in 0..variants.len() {
            let (tile, pipeline, indices) = &variants[(index + round) % variants.len()];
            let mut submit = Duration::ZERO;
            let mut complete = Duration::ZERO;
            for frame in 0..310 {
                if let Some(timestamps) = &timestamps {
                    timestamps.select((frame >= 10).then_some(0));
                }
                let stamp = timestamps.as_ref().and_then(|timestamps| {
                    timestamps.allocate(Some("tiny instance tile experiment"))
                });
                let start = Instant::now();
                let mut encoder = device.inner.create_command_encoder(&Default::default());
                {
                    let mut pass = encoder.begin_render_pass(&wgpu::RenderPassDescriptor {
                        label: Some("tiny instance tile experiment"),
                        timestamp_writes: stamp.as_ref().map(|(set, index)| {
                            wgpu::RenderPassTimestampWrites {
                                query_set: set,
                                beginning_of_pass_write_index: Some(*index),
                                end_of_pass_write_index: Some(*index + 1),
                            }
                        }),
                        color_attachments: &[Some(wgpu::RenderPassColorAttachment {
                            view: &view,
                            depth_slice: None,
                            resolve_target: None,
                            ops: wgpu::Operations {
                                load: wgpu::LoadOp::Clear(wgpu::Color::BLACK),
                                store: wgpu::StoreOp::Store,
                            },
                        })],
                        ..Default::default()
                    });
                    pass.set_pipeline(pipeline);
                    pass.set_index_buffer(indices.slice(..), wgpu::IndexFormat::Uint32);
                    pass.draw_indexed(0..9 * tile, 0, 0..RECORDS.div_ceil(*tile));
                }
                queue.inner.submit([encoder.finish()]);
                let submitted = start.elapsed();
                wait();
                if frame >= 10 {
                    submit += submitted;
                    complete += start.elapsed();
                }
            }
            if let Some(timestamps) = &timestamps {
                timestamps.report(
                    &device,
                    &queue,
                    &format!(
                        "benchmark=tiny_tile round={round} warmup={} tile={tile}",
                        round == 0
                    ),
                    [300, 0],
                    ["path=tiled", "unused"],
                );
            }
            let image = pixels(&device, &queue, &target);
            assert!(
                image.chunks_exact(4).any(|pixel| pixel[1] != 0),
                "nonempty reference required"
            );
            if let Some(reference) = &reference {
                assert_eq!(&image, reference, "tile {tile} must preserve exact pixels");
            } else {
                reference = Some(image);
            }
            eprintln!(
                "instance_tile round={round} warmup={} tile={tile} records={RECORDS} submit_ms={:.3} complete_ms={:.3}",
                round == 0,
                submit.as_secs_f64() * 1000. / 300.,
                complete.as_secs_f64() * 1000. / 300.
            );
        }
    }
    device.check_failure().unwrap();
}
