use super::*;
use nico_assets::model::{Clip, Interpolation, ModelData, Node, Track, TrackValues, Transform};
fn model() -> Arc<Model> {
    let mut data = ModelData {
        nodes: vec![Node {
            name: "root".into(),
            children: vec![],
            transform: Transform::default(),
            mesh: None,
            skin: None,
        }],
        ..Default::default()
    };
    for offset in [0., 10., 20.] {
        data.clips.push(Clip {
            name: format!("{offset}"),
            tracks: vec![Track {
                node: 0,
                times: vec![5., 7.],
                interpolation: Interpolation::Linear,
                values: TrackValues::Translation(vec![[offset, 0., 0.], [offset + 2., 0., 0.]]),
            }],
        });
    }
    data.clips.push(Clip {
        name: "still".into(),
        tracks: vec![Track {
            node: 0,
            times: vec![0.],
            interpolation: Interpolation::Step,
            values: TrackValues::Translation(vec![[4., 0., 0.]]),
        }],
    });
    Arc::new(Model::new(data).unwrap())
}
fn set(model: Arc<Model>) -> Arc<AnimationSet> {
    let clips = (0..model.data().clips.len())
        .map(|i| AnimationClip::direct(model.clone(), i, i.to_string()).unwrap())
        .collect();
    Arc::new(AnimationSet::new(model, clips).unwrap())
}
fn player() -> AnimationPlayer {
    AnimationPlayer::new(set(model()))
}
fn seconds(value: f64) -> Duration {
    Duration::from_secs_f64(value)
}
fn x(p: &AnimationPlayer) -> f32 {
    p.pose().local()[0].translation[0]
}
fn near(a: f64, b: f64) {
    assert!((a - b).abs() < 1e-5, "{a} != {b}");
}

#[test]
fn looping_and_one_shots_are_elapsed_based_and_completion_is_emitted_once() {
    for fps in [30, 60, 144] {
        let mut p = player();
        p.play(0, PlayMode::Loop, Duration::ZERO).unwrap();
        for _ in 0..fps * 3 {
            assert!(
                !p.update(seconds(1. / f64::from(fps)))
                    .unwrap()
                    .just_finished
            );
        }
        near(p.time(), 1.);
        near(f64::from(x(&p)), 1.);
    }
    let mut p = player();
    p.play(0, PlayMode::Once, Duration::ZERO).unwrap();
    assert!(p.update(seconds(3.)).unwrap().just_finished);
    assert!(p.finished());
    near(p.time(), 2.);
    near(f64::from(x(&p)), 2.);
    assert!(!p.update(seconds(10.)).unwrap().just_finished);
    p.play(3, PlayMode::Once, Duration::ZERO).unwrap();
    assert!(p.finished());
    near(f64::from(x(&p)), 4.);
    assert!(!p.update(seconds(1.)).unwrap().just_finished);
}
#[test]
fn crossfade_advances_both_clips_and_interruption_is_continuous() {
    let mut p = player();
    p.play(0, PlayMode::Loop, Duration::ZERO).unwrap();
    p.update(seconds(0.5)).unwrap();
    p.play(1, PlayMode::Loop, seconds(1.)).unwrap();
    near(f64::from(x(&p)), 0.5);
    p.update(seconds(0.5)).unwrap();
    near(f64::from(x(&p)), 5.75);
    p.play(2, PlayMode::Once, seconds(1.)).unwrap();
    near(f64::from(x(&p)), 5.75);
    p.update(Duration::ZERO).unwrap();
    near(f64::from(x(&p)), 5.75);
    p.update(seconds(0.5)).unwrap();
    near(f64::from(x(&p)), 13.125);
    p.update(seconds(0.5)).unwrap();
    near(f64::from(x(&p)), 21.);
    assert!(p.fade_weight().is_none());
}
#[test]
fn pause_freezes_clocks_seek_cancels_fades_and_speed_scales_only_clip_time() {
    let mut p = player();
    p.play(1, PlayMode::Loop, seconds(1.)).unwrap();
    p.update(seconds(0.2)).unwrap();
    let value = x(&p);
    p.set_paused(true);
    p.update(seconds(5.)).unwrap();
    assert_eq!(x(&p), value);
    near(p.fade_weight().unwrap(), 0.2);
    p.set_paused(false);
    p.set_speed(0.).unwrap();
    p.update(seconds(0.3)).unwrap();
    near(p.time(), 0.2);
    near(p.fade_weight().unwrap(), 0.5);
    p.seek(1.).unwrap();
    assert!(p.fade_weight().is_none());
    near(f64::from(x(&p)), 11.);
    p.set_speed(2.).unwrap();
    p.update(seconds(0.25)).unwrap();
    near(p.time(), 1.5);
    p.reference_pose();
    assert_eq!(p.clip(), None);
    near(f64::from(x(&p)), 0.);
}
#[test]
fn rejected_controls_and_failed_blend_preserve_visible_pose_and_clocks() {
    let mut p = player();
    p.play(0, PlayMode::Loop, Duration::ZERO).unwrap();
    p.update(seconds(0.5)).unwrap();
    assert!(p.play(99, PlayMode::Loop, seconds(1.)).is_err());
    for t in [-1., f64::NAN, 3.] {
        assert!(p.seek(t).is_err());
    }
    assert!(p.set_speed(f64::INFINITY).is_err());
    near(p.time(), 0.5);
    near(f64::from(x(&p)), 0.5);
    let mut data = model().data().clone();
    data.nodes[0].transform.scale = [-1.; 3];
    data.clips[1].tracks.push(Track {
        node: 0,
        times: vec![5., 7.],
        interpolation: Interpolation::Linear,
        values: TrackValues::Scale(vec![[1.; 3]; 2]),
    });
    let mut p = AnimationPlayer::new(set(Arc::new(Model::new(data).unwrap())));
    p.play(0, PlayMode::Loop, Duration::ZERO).unwrap();
    p.play(1, PlayMode::Loop, seconds(1.)).unwrap();
    let before = p.pose().local().to_vec();
    assert!(p.update(seconds(0.5)).is_err());
    assert_eq!(p.pose().local(), before);
    near(p.time(), 0.);
    near(p.fade_weight().unwrap(), 0.);
}
#[test]
fn players_share_immutable_clips_but_not_state_and_retain_pose_capacity() {
    let set = set(model());
    let mut a = AnimationPlayer::new(set.clone());
    let mut b = AnimationPlayer::new(set);
    a.play(0, PlayMode::Loop, Duration::ZERO).unwrap();
    b.play(1, PlayMode::Once, Duration::ZERO).unwrap();
    let capacities = (
        a.output.local.capacity(),
        a.output.scratch.capacity(),
        a.from.local.capacity(),
        a.to.local.capacity(),
    );
    for _ in 0..1000 {
        a.update(seconds(0.01)).unwrap();
    }
    assert_eq!(
        capacities,
        (
            a.output.local.capacity(),
            a.output.scratch.capacity(),
            a.from.local.capacity(),
            a.to.local.capacity()
        )
    );
    near(b.time(), 0.);
    near(f64::from(x(&b)), 10.);
    assert!(Arc::ptr_eq(a.set(), b.set()));
    let wrong = model();
    assert!(AnimationSet::new(wrong, vec![a.set.clips[0].clone()]).is_err());
}

#[test]
fn retargeted_clips_share_target_pose_and_switch_root_policy_without_moving_game_state() {
    use crate::{
        humanoid::HumanoidRig,
        tests::{body, profile},
    };
    let mut data = body(1., glam::Quat::IDENTITY).data().clone();
    data.clips.push(Clip {
        name: "walk".into(),
        tracks: vec![Track {
            node: 0,
            times: vec![0., 1.],
            interpolation: Interpolation::Linear,
            values: TrackValues::Translation(vec![[0., 2., 0.], [2., 2., 0.]]),
        }],
    });
    let source = Arc::new(Model::new(data).unwrap());
    let target = body(2., glam::Quat::IDENTITY);
    let from = Arc::new(HumanoidRig::new(source.clone(), profile(&source)).unwrap());
    let to = Arc::new(HumanoidRig::new(target.clone(), profile(&target)).unwrap());
    let clip = AnimationClip::humanoid(from, to, 0, "walk").unwrap();
    let set = Arc::new(AnimationSet::new(target, vec![clip]).unwrap());
    let retained = Arc::downgrade(&set);
    let mut player = AnimationPlayer::new(set);
    player.play(0, PlayMode::Once, Duration::ZERO).unwrap();
    player.update(seconds(0.5)).unwrap();
    near(f64::from(x(&player)), 0.);
    player.set_root_motion(RootMotion::Preserve).unwrap();
    near(f64::from(x(&player)), 2.);
    player.set_root_motion(RootMotion::InPlace).unwrap();
    near(f64::from(x(&player)), 0.);
    assert!(player.update(seconds(0.5)).unwrap().just_finished);
    assert!(retained.upgrade().is_some());
    drop(player);
    assert!(retained.upgrade().is_none());
}

#[test]
fn external_clock_preserves_crossfade_and_reports_completion_without_seek() {
    let mut p = player();
    p.play(0, PlayMode::Loop, Duration::ZERO).unwrap();
    p.play(1, PlayMode::Once, seconds(1.)).unwrap();
    p.update_at(seconds(0.25), 1.).unwrap();
    near(p.time(), 1.);
    near(p.fade_weight().unwrap(), 0.25);
    // Outgoing x=.25, destination x=11, mixed at .25.
    near(f64::from(x(&p)), 2.9375);
    let before = x(&p);
    assert!(p.update_at(seconds(0.25), 3.).is_err());
    near(p.time(), 1.);
    assert_eq!(x(&p), before);
    p.set_paused(true);
    p.update_at(seconds(0.5), 2.).unwrap();
    near(p.time(), 1.);
    near(p.fade_weight().unwrap(), 0.25);
    p.set_paused(false);
    assert!(p.update_at(seconds(0.75), 2.).unwrap().just_finished);
    assert!(!p.update_at(seconds(0.1), 2.).unwrap().just_finished);
    assert!(p.fade_weight().is_none());
}
