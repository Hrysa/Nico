use super::*;
use glam::{Mat4, Vec3};
use nico_assets::{Mesh, MeshVertex, PbrMaterial};
use nico_presentation::{
    Camera3d, InstanceBatch, InstanceRecord, Scene3d, foliage::FoliageProfile,
};
use nico_render::{InstanceRenderMode, MeshRenderPipeline};

#[test]
#[ignore = "requires graphics; compatible bindings across mixed instance pipelines"]
fn gpu_mixed_instance_paths_preserve_shared_bindings_and_draw_order() {
    let (device, queue, mut target) = setup();
    macro_rules! shader {
        ($name:literal) => {
            builtin_shaders::bootstrap_wgsl(include_bytes!(concat!(
                "../../../../assets/presentation/shaders/generated/wgpu/",
                $name,
                ".wgsl"
            )))
        };
    }
    let make = || {
        let mut renderer =
            MeshRenderPipeline::new(&device, target.format, shader!("meshes"), shader!("quads"))
                .unwrap();
        renderer
            .enable_instancing(&device, shader!("instanced_meshes"))
            .unwrap();
        renderer
            .enable_foliage(&device, shader!("foliage_meshes"))
            .unwrap();
        let visibility = ShaderModuleDescriptor {
            label: None,
            format: ShaderFormat::Wgsl,
            code: include_bytes!(
                "../../../../assets/presentation/shaders/generated/wgpu/instance_visibility.wgsl"
            ),
        };
        renderer
            .enable_gpu_instancing(&device, shader!("instanced_storage"), visibility)
            .unwrap();
        renderer
            .enable_gpu_foliage(&device, shader!("foliage_storage"), visibility)
            .unwrap();
        renderer.set_instance_auto_gpu_min_records(2).unwrap();
        renderer
    };
    let (mut reference, mut actual) = (make(), make());
    let mesh = Arc::new(
        Mesh::triangles(
            [[-0.2, -0.2, 0.], [0.2, -0.2, 0.], [0., 0.2, 0.]]
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
    let material = Arc::new(PbrMaterial {
        double_sided: true,
        ..Default::default()
    });
    let batches: Vec<_> = [
        (false, false, [-0.6, 0.4, 0.], [1., 0., 0., 1.]),
        (true, true, [-0.6, -0.4, 0.], [0., 1., 0., 1.]),
        (false, true, [0.6, 0.4, 0.], [0., 0., 1., 1.]),
        (true, false, [0.6, -0.4, 0.], [1., 1., 0., 1.]),
    ]
    .into_iter()
    .map(|(foliage, indirect, position, tint)| {
        let mut records =
            vec![InstanceRecord::new(0, 0, Mat4::from_translation(position.into()), tint).unwrap()];
        if indirect {
            // The second record makes the whole bound cross the frustum, so Auto
            // must perform GPU culling instead of taking its fully-visible path.
            records.push(
                InstanceRecord::new(1, 0, Mat4::from_translation(Vec3::new(100., 0., 0.)), tint)
                    .unwrap(),
            );
        }
        let mut batch = InstanceBatch::new(mesh.clone(), material.clone(), records, 1000.).unwrap();
        if foliage {
            batch = batch
                .with_foliage(FoliageProfile::new(-0.2, 0.4, 0.1).unwrap())
                .unwrap();
        }
        Arc::new(batch)
    })
    .collect();
    let mut scene = Scene3d {
        instance_batches: batches.clone(),
        camera: Camera3d::looking_at([0., 0., 3.], [0.; 3], [0., 1., 0.]).unwrap(),
        ..Default::default()
    };
    scene.lighting.radiance = [0.; 3];
    scene.lighting.ambient = [1.; 3];
    reference
        .set_instance_mode(&device, &scene, InstanceRenderMode::Cpu)
        .unwrap();
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
    let expected = render(&mut reference, &mut target, &scene);
    assert_eq!(reference.instance_stats().submitted_instances, 4);
    assert!(
        expected
            .chunks_exact(4)
            .any(|pixel| pixel[0] > 200 && pixel[1] < 20)
    );
    assert!(
        expected
            .chunks_exact(4)
            .any(|pixel| pixel[1] > 200 && pixel[0] < 20)
    );
    assert!(
        expected
            .chunks_exact(4)
            .any(|pixel| pixel[2] > 200 && pixel[0] < 20)
    );
    for (frame, order) in [[0, 1, 2, 3], [3, 2, 1, 0], [2, 0, 3, 1]]
        .into_iter()
        .enumerate()
    {
        scene.instance_batches = order.map(|index| batches[index].clone()).into();
        let image = render(&mut actual, &mut target, &scene);
        assert!(
            image
                .iter()
                .zip(&expected)
                .all(|(&a, &b)| a.abs_diff(b) <= 1)
        );
        assert_eq!(actual.instance_stats().submitted_draws, 4);
        assert_eq!(actual.instance_stats().indirect_draws, 2);
        assert_eq!(actual.instance_stats().submitted_instances, 2);
        if frame > 0 {
            assert_eq!(actual.instance_stats().instance_upload_bytes, 0);
            assert_eq!(actual.instance_stats().visibility_upload_bytes, 0);
        }
    }
    device.check_failure().unwrap();
}
