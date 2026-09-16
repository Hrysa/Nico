use super::*;
use crate::humanoid::*;
use nico_assets::model::*;
use std::sync::Arc;

pub(super) fn body(size: f32, arm_rotation: Quat) -> Arc<Model> {
    let spec = [
        (Bone::Hips, None, [0., 2., 0.]),
        (Bone::Spine, Some(0), [0., 1., 0.]),
        (Bone::Head, Some(1), [0., 1., 0.]),
        (Bone::LeftUpperArm, Some(1), [1., 0., 0.]),
        (Bone::LeftLowerArm, Some(3), [1., 0., 0.]),
        (Bone::LeftHand, Some(4), [1., 0., 0.]),
        (Bone::RightUpperArm, Some(1), [-1., 0., 0.]),
        (Bone::RightLowerArm, Some(6), [-1., 0., 0.]),
        (Bone::RightHand, Some(7), [-1., 0., 0.]),
        (Bone::LeftUpperLeg, Some(0), [0.5, 0., 0.]),
        (Bone::LeftLowerLeg, Some(9), [0., -1., 0.]),
        (Bone::LeftFoot, Some(10), [0., -1., 0.]),
        (Bone::RightUpperLeg, Some(0), [-0.5, 0., 0.]),
        (Bone::RightLowerLeg, Some(12), [0., -1., 0.]),
        (Bone::RightFoot, Some(13), [0., -1., 0.]),
    ];
    let mut data = ModelData::default();
    for (i, (bone, parent, translation)) in spec.into_iter().enumerate() {
        data.nodes.push(Node {
            name: format!("{bone:?}"),
            children: vec![],
            transform: Transform {
                translation: (Vec3::from(translation) * size).to_array(),
                rotation: if i == 3 {
                    arm_rotation.to_array()
                } else {
                    Quat::IDENTITY.to_array()
                },
                ..Default::default()
            },
            mesh: None,
            skin: None,
        });
        if let Some(parent) = parent {
            data.nodes[parent].children.push(i);
        }
    }
    data.nodes.push(Node {
        name: "helper".into(),
        children: vec![],
        transform: Transform::default(),
        mesh: None,
        skin: None,
    });
    data.nodes[5].children.push(15);
    Arc::new(Model::new(data).unwrap())
}
pub(super) fn profile(model: &Model) -> HumanoidProfile {
    HumanoidProfile {
        bones: Bone::ALL
            .into_iter()
            .filter(|b| {
                model
                    .data()
                    .nodes
                    .iter()
                    .any(|n| n.name == format!("{b:?}"))
            })
            .map(|b| (b, format!("{b:?}").into()))
            .collect(),
        motion_root: Some("Hips".into()),
        ..Default::default()
    }
}
fn near(a: Vec3, b: Vec3) {
    assert!((a - b).length() < 1e-4, "{a:?} != {b:?}");
}
fn same_rotation(a: Quat, b: Quat) {
    assert!(a.dot(b).abs() > 0.99999, "{a:?} != {b:?}");
}
#[test]
fn retarget_preserves_reference_pose_proportions_and_helper_joints() {
    let source = body(1., Quat::IDENTITY);
    let target = body(2., Quat::from_rotation_z(0.4));
    let from = HumanoidRig::new(source.clone(), profile(&source)).unwrap();
    let to = HumanoidRig::new(target.clone(), profile(&target)).unwrap();
    let reference = from.capture(&Pose::rest(&source)).unwrap();
    let result = to.apply(&reference, RootMotion::Preserve).unwrap();
    for (a, b) in result.local().iter().zip(Pose::rest(&target).local()) {
        near(a.translation.into(), b.translation.into());
        same_rotation(Quat::from_array(a.rotation), Quat::from_array(b.rotation));
    }
    let mut pose = Pose::rest(&source).local().to_vec();
    pose[3].rotation = Quat::from_rotation_y(0.6).to_array();
    let changed = from
        .capture(&Pose::from_local(&source, pose).unwrap())
        .unwrap();
    let result = to.apply(&changed, RootMotion::InPlace).unwrap();
    same_rotation(
        result.globals().unwrap()[3]
            .to_scale_rotation_translation()
            .1,
        Quat::from_rotation_y(0.6) * Quat::from_rotation_z(0.4),
    );
    assert_eq!(
        result.local()[4].translation,
        target.data().nodes[4].transform.translation
    );
    assert_eq!(result.local()[15], target.data().nodes[15].transform);
}
#[test]
fn root_motion_is_scaled_or_removed_and_basis_corrects_source_axes() {
    let mut source_data = body(1., Quat::IDENTITY).data().clone();
    source_data.nodes[0].transform.rotation = Quat::from_rotation_y(1.).to_array();
    let source = Arc::new(Model::new(source_data).unwrap());
    let target = body(2., Quat::IDENTITY);
    let mut config = profile(&source);
    config.model_to_canonical = Quat::from_rotation_y(-1.).to_array();
    let from = HumanoidRig::new(source.clone(), config).unwrap();
    let to = HumanoidRig::new(target.clone(), profile(&target)).unwrap();
    let mut local = Pose::rest(&source).local().to_vec();
    local[3].rotation = Quat::from_rotation_x(0.5).to_array();
    local[0].translation = (Vec3::from(local[0].translation)
        + Quat::from_rotation_y(1.) * Vec3::new(3., 0.5, 0.))
    .to_array();
    let motion = from
        .capture(&Pose::from_local(&source, local).unwrap())
        .unwrap();
    let preserve = to.apply(&motion, RootMotion::Preserve).unwrap();
    let inplace = to.apply(&motion, RootMotion::InPlace).unwrap();
    near(
        preserve.local()[0].translation.into(),
        Vec3::new(6., 5., 0.),
    );
    near(inplace.local()[0].translation.into(), Vec3::new(0., 5., 0.));
    same_rotation(
        preserve.globals().unwrap()[3]
            .to_scale_rotation_translation()
            .1,
        Quat::from_rotation_x(0.5),
    );
}
#[test]
fn mapping_reports_missing_bones_duplicates_hierarchy_and_wrong_pose() {
    let model = body(1., Quat::IDENTITY);
    let mut p = profile(&model);
    p.bones.remove(&Bone::LeftHand);
    assert!(HumanoidRig::new(model.clone(), p).is_err());
    let mut p = profile(&model);
    p.bones.insert(Bone::LeftHand, "LeftLowerArm".into());
    assert!(HumanoidRig::new(model.clone(), p).is_err());
    let mut p = profile(&model);
    p.bones.insert(Bone::LeftHand, "RightHand".into());
    p.bones.insert(Bone::RightHand, "LeftHand".into());
    assert!(HumanoidRig::new(model.clone(), p).is_err());
    let rig = HumanoidRig::new(model.clone(), profile(&model)).unwrap();
    assert!(rig.report().missing_optional.contains(&Bone::Neck));
    assert!(rig.report().unmapped_nodes.contains(&15));
    let other = body(1., Quat::IDENTITY);
    assert!(rig.capture(&Pose::rest(&other)).is_err());
    let mut local = Pose::rest(&model).local().to_vec();
    local[0].scale = [2.; 3];
    assert!(
        rig.capture(&Pose::from_local(&model, local).unwrap())
            .is_err()
    );
    let mut data = model.data().clone();
    data.nodes[15].name = "LeftHand".into();
    let duplicate = Arc::new(Model::new(data).unwrap());
    assert!(HumanoidRig::new(duplicate.clone(), profile(&duplicate)).is_err());
    let mut indexed = profile(&duplicate);
    indexed.bones.insert(Bone::LeftHand, BoneBinding::Node(5));
    assert!(HumanoidRig::new(duplicate.clone(), indexed).is_ok());
}
#[test]
fn samples_nonzero_time_origin_step_loop_clamp_and_antipodal_rotations() {
    let mut data = body(1., Quat::IDENTITY).data().clone();
    data.clips.push(Clip {
        name: "test".into(),
        tracks: vec![
            Track {
                node: 0,
                times: vec![1., 3.],
                values: TrackValues::Translation(vec![[0., 2., 0.], [4., 2., 0.]]),
                interpolation: Interpolation::Linear,
            },
            Track {
                node: 3,
                times: vec![1., 3.],
                values: TrackValues::Rotation(vec![[0., 0., 0., 1.], [0., 0., 0., -1.]]),
                interpolation: Interpolation::Linear,
            },
            Track {
                node: 1,
                times: vec![1., 3.],
                values: TrackValues::Scale(vec![[1.; 3], [2.; 3]]),
                interpolation: Interpolation::Step,
            },
        ],
    });
    let model = Arc::new(Model::new(data).unwrap());
    let half = sample(&model, 0, 1., Playback::Clamp).unwrap();
    near(half.local()[0].translation.into(), Vec3::new(2., 2., 0.));
    assert_eq!(half.local()[1].scale, [1.; 3]);
    same_rotation(Quat::from_array(half.local()[3].rotation), Quat::IDENTITY);
    assert_eq!(
        sample(&model, 0, 2., Playback::Loop).unwrap().local()[0].translation,
        [0., 2., 0.]
    );
    assert_eq!(
        sample(&model, 0, 99., Playback::Clamp).unwrap().local()[1].scale,
        [2.; 3]
    );
    assert!(sample(&model, 0, f32::NAN, Playback::Clamp).is_err());
}
#[test]
fn skin_matrices_cancel_mesh_transform_and_use_inverse_bind() {
    let mut data = body(1., Quat::IDENTITY).data().clone();
    data.meshes.push(ModelMesh {
        name: "triangle".into(),
        primitives: vec![Primitive {
            vertices: vec![
                ModelVertex {
                    position: [0.; 3],
                    normal: [0., 1., 0.],
                    uv: [0.; 2],
                    joints: [0; 4],
                    weights: [1., 0., 0., 0.]
                };
                3
            ],
            indices: vec![0, 1, 2],
            material: None,
            skinned: true,
            has_normals: true,
        }],
    });
    data.nodes[15].mesh = Some(0);
    data.nodes[15].skin = Some(0);
    data.skins.push(Skin {
        name: "skin".into(),
        joints: vec![0],
        inverse_bind: vec![Mat4::from_translation(Vec3::new(0., -2., 0.)).to_cols_array_2d()],
        skeleton: Some(0),
    });
    let model = Arc::new(Model::new(data).unwrap());
    let pose = Pose::rest(&model);
    let globals = pose.globals().unwrap();
    let skin = pose.skin_matrices(15).unwrap();
    let world = globals[15] * Mat4::from_cols_array_2d(&skin[0]);
    near(
        world.transform_point3(Vec3::new(1., 0., 0.)),
        Vec3::new(1., 0., 0.),
    );
}

#[test]
fn reusable_pose_buffers_blend_without_growing_and_preserve_pose_on_failure() {
    let model = body(1., Quat::IDENTITY);
    let rest = Pose::rest(&model);
    let mut local = rest.local().to_vec();
    local[0].translation[0] = 4.;
    local[0].rotation = Quat::from_rotation_y(1.).to_array();
    let end = Pose::from_local(&model, local).unwrap();
    let mut output = PoseBuffer::new(model.clone());
    let capacities = (output.local.capacity(), output.scratch.capacity());
    for _ in 0..100 {
        output.blend(&rest, &end, 0.5).unwrap();
        near(
            output.pose().local()[0].translation.into(),
            Vec3::new(2., 2., 0.),
        );
        same_rotation(
            Quat::from_array(output.pose().local()[0].rotation),
            Quat::from_rotation_y(0.5),
        );
    }
    assert_eq!(
        capacities,
        (output.local.capacity(), output.scratch.capacity())
    );
    let before = output.local.clone();
    assert!(output.blend(&rest, &end, f32::NAN).is_err());
    assert!(output.sample(99, 0., Playback::Clamp).is_err());
    let mut opposite = end.local().to_vec();
    opposite[0].scale = [-1.; 3];
    let opposite = Pose::from_local(&model, opposite).unwrap();
    assert!(output.blend(&rest, &opposite, 0.5).is_err());
    assert_eq!(output.local, before);
}

#[test]
fn cached_retarget_workspace_matches_convenience_evaluation() {
    let source = body(1., Quat::IDENTITY);
    let target = body(2., Quat::from_rotation_z(0.3));
    let from = HumanoidRig::new(source.clone(), profile(&source)).unwrap();
    let to = HumanoidRig::new(target.clone(), profile(&target)).unwrap();
    let mut workspace = HumanoidWorkspace::default();
    let mut output = PoseBuffer::new(target.clone());
    let mut globals = Vec::new();
    for angle in [0., 0.3, 1., -0.5] {
        let mut local = Pose::rest(&source).local().to_vec();
        local[3].rotation = Quat::from_rotation_y(angle).to_array();
        let pose = Pose::from_local(&source, local).unwrap();
        let motion = from.capture_into(&pose, &mut workspace).unwrap();
        to.apply_into(&motion, RootMotion::InPlace, &mut output, &mut workspace)
            .unwrap();
        let reference = to
            .apply(&from.capture(&pose).unwrap(), RootMotion::InPlace)
            .unwrap();
        assert_eq!(output.pose().local(), reference.local());
        output.pose().globals_into(&mut globals).unwrap();
        assert_eq!(globals, reference.globals().unwrap());
    }
    let motion = from.capture(&Pose::rest(&source)).unwrap();
    let mut wrong = PoseBuffer::new(source);
    assert!(
        to.apply_into(&motion, RootMotion::InPlace, &mut wrong, &mut workspace)
            .is_err()
    );
}
