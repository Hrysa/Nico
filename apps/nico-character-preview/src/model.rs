//! Preview-owned assembly: shared immutable assets, independent instance playback.
use crate::Result;
use glam::Mat4;
#[cfg(test)]
use glam::Vec3;
use nico_animation::{
    PoseBuffer,
    humanoid::{HumanoidProfile, HumanoidRig},
    playback::{AnimationClip, AnimationPlayer, AnimationSet, PlayMode},
};
use nico_assets::{
    import::{AssetImporter, ImportBudget, ImportContext},
    importers::{ModelGlbImporter, ModelGlbSettings, PngImporter, PngSettings},
    model::{ImageEncoding, Model},
};
use nico_presentation::MeshInstance;
use std::{
    fs::File,
    io::Read,
    path::{Path, PathBuf},
    sync::Arc,
    time::Duration,
};

pub struct CharacterAssets {
    pub model: Arc<Model>,
    pub retargeted: bool,
    pub animations: Arc<AnimationSet>,
    visual: nico_presentation_control::model::ModelVisual,
}
#[derive(Clone)]
pub struct Character {
    pub assets: Arc<CharacterAssets>,
    pub id: usize,
    pub position: [f32; 3],
    pub evaluation_interval: Duration,
    pub pending: Duration,
    cached: Option<Vec<MeshInstance>>,
    pub player: AnimationPlayer,
    pub in_place: bool,
    pub bind_pose: bool,
    pub play_mode: PlayMode,
    pub fade_seconds: f64,
    reference: PoseBuffer,
    globals: Vec<Mat4>,
}
/// Code prefab: each spawn owns its mutable playback state and shares asset data.
pub fn spawn_character(
    world: &mut nico_ecs::World,
    assets: Arc<CharacterAssets>,
) -> Result<nico_ecs::Entity> {
    let reference = PoseBuffer::new(assets.model.clone());
    let mut player = AnimationPlayer::new(assets.animations.clone());
    if !assets.clips().is_empty() {
        player.play(0, PlayMode::Loop, Duration::ZERO)?;
    }
    Ok(world.spawn((Character {
        id: 0,
        position: [0.; 3],
        evaluation_interval: Duration::ZERO,
        pending: Duration::ZERO,
        cached: None,
        reference,
        player,
        assets,
        globals: Vec::new(),
        in_place: true,
        bind_pose: false,
        play_mode: PlayMode::Loop,
        fade_seconds: 0.2,
    },)))
}

fn load(path: &Path) -> Result<Arc<Model>> {
    let mut bytes = Vec::new();
    File::open(path)?
        .take(64 * 1024 * 1024 + 1)
        .read_to_end(&mut bytes)?;
    let mut ctx = ImportContext::new(
        &bytes,
        ImportBudget {
            max_input_bytes: 64 * 1024 * 1024,
            max_decoded_bytes: 128 * 1024 * 1024,
        },
        &|| false,
    )?;
    Ok(Arc::new(ModelGlbImporter.import(
        &mut ctx,
        &ModelGlbSettings {
            allow_material_fallback: true,
            ..Default::default()
        },
    )?))
}
fn validate_clip_labels(clips: &[AnimationClip]) -> Result<()> {
    if clips.len() > 128
        || clips
            .iter()
            .any(|clip| clip.name().len() > 128 || clip.name().chars().any(char::is_control))
    {
        return Err(
            "preview supports at most 128 clips with printable labels up to 128 UTF-8 bytes".into(),
        );
    }
    Ok(())
}
impl CharacterAssets {
    pub fn load(model: &Path, sources: &[PathBuf]) -> Result<Self> {
        let model = load(model)?;
        if sources.len() > 64 {
            return Err("preview accepts at most 64 animation files".into());
        }
        let mut clips = Vec::new();
        if sources.is_empty() {
            for (i, clip) in model.data().clips.iter().enumerate() {
                clips.push(AnimationClip::direct(model.clone(), i, clip.name.clone())?);
            }
        } else {
            let target = Arc::new(HumanoidRig::new(model.clone(), HumanoidProfile::mixamo())?);
            let mut total_bytes = 0u64;
            for path in sources {
                total_bytes = total_bytes
                    .checked_add(std::fs::metadata(path)?.len())
                    .ok_or("animation input size overflow")?;
                if total_bytes > 256 * 1024 * 1024 {
                    return Err("animation set exceeds 256 MiB source budget".into());
                }
                let source = load(path)?;
                let rig = Arc::new(HumanoidRig::new(source.clone(), HumanoidProfile::rpg())?);
                for (i, clip) in source.data().clips.iter().enumerate() {
                    let name = format!(
                        "{} / {}",
                        path.file_stem().unwrap_or_default().to_string_lossy(),
                        clip.name
                    );
                    clips.push(AnimationClip::humanoid(
                        rig.clone(),
                        target.clone(),
                        i,
                        name,
                    )?);
                }
            }
        }
        validate_clip_labels(&clips)?;
        let animations = Arc::new(AnimationSet::new(model.clone(), clips)?);
        let mut textures = Vec::new();
        let mut decoded = 0usize;
        for (index, texture) in model.data().textures.iter().enumerate() {
            if !model
                .data()
                .materials
                .iter()
                .any(|m| m.texture_indices().contains(&Some(index)))
            {
                textures.push(None);
                continue;
            }
            let image = &model.data().images[texture.image];
            if image.encoding != ImageEncoding::Png {
                return Err("preview currently supports PNG material images only".into());
            }
            let remaining = (256usize * 1024 * 1024).saturating_sub(decoded);
            if remaining == 0 {
                return Err("preview material texture budget exceeded".into());
            }
            let mut ctx = ImportContext::new(
                &image.bytes,
                ImportBudget {
                    max_input_bytes: 64 * 1024 * 1024,
                    max_decoded_bytes: remaining,
                },
                &|| false,
            )?;
            let texture = PngImporter.import(&mut ctx, &PngSettings::default())?;
            decoded += texture.pixels().len();
            textures.push(Some(Arc::new(texture)));
        }
        let visual = nico_presentation_control::model::ModelVisual::new(model.clone(), textures)?;
        Ok(Self {
            visual,
            animations,
            model,
            retargeted: !sources.is_empty(),
        })
    }
    pub fn primitive_count(&self) -> usize {
        self.visual.primitive_count()
    }
    pub fn clips(&self) -> &[AnimationClip] {
        self.animations.clips()
    }
}
impl Character {
    pub fn invalidate(&mut self) {
        self.cached = None;
    }
    /// Deliberately hold the last displayed pose between evaluations. Elapsed time
    /// accumulates in full; sampling never changes clip speed. Call flush before edits.
    pub fn scheduled_advance(&mut self, delta: Duration, force: bool) -> Result<bool> {
        if !self.bind_pose && !self.player.paused() {
            self.pending = self
                .pending
                .checked_add(delta)
                .ok_or("animation elapsed overflow")?;
        }
        if !force && self.pending < self.evaluation_interval {
            return Ok(false);
        }
        if self.pending.is_zero() {
            return Ok(false);
        }
        let delta = self.pending;
        let changed = self.advance(delta)?;
        self.pending = Duration::ZERO;
        if changed {
            self.invalidate();
        }
        Ok(true)
    }
    pub fn draws(&mut self) -> Result<&[MeshInstance]> {
        if self.cached.is_none() {
            self.cached = Some(self.meshes()?);
        }
        Ok(self.cached.as_ref().unwrap())
    }
    pub fn duration(&self) -> f64 {
        self.player.duration()
    }
    pub fn advance(&mut self, delta: Duration) -> Result<bool> {
        if self.bind_pose {
            return Ok(false);
        }
        let before = (self.player.time(), self.player.fade_weight());
        self.player.update(delta)?;
        let changed = before != (self.player.time(), self.player.fade_weight());
        if changed {
            self.invalidate();
        }
        Ok(changed)
    }
    pub fn bounds(&self) -> Result<nico_presentation_control::model::ModelBounds> {
        Ok(self
            .assets
            .visual
            .bounds(&self.globals, Mat4::from_translation(self.position.into()))?)
    }
    pub fn meshes(&mut self) -> Result<Vec<MeshInstance>> {
        if self.bind_pose {
            self.reference.pose()
        } else {
            self.player.pose()
        }
        .globals_into(&mut self.globals)?;
        let mut meshes = self.assets.visual.meshes(&self.globals)?;
        for mesh in &mut meshes {
            mesh.position = self.position;
        }
        Ok(meshes)
    }
}

/// Evaluates one GPU input vertex for initial framing and reference checks.
#[cfg(test)]
pub fn vertex_position(instance: &MeshInstance, index: usize) -> Vec3 {
    let mesh = instance.mesh.as_ref().unwrap();
    let p = Vec3::from(mesh.vertices()[index].position);
    let skin = &mesh.skin().unwrap()[index];
    let palette = instance.skin_palette.as_ref().unwrap();
    skin.joints
        .iter()
        .zip(skin.weights)
        .fold(Vec3::ZERO, |a, (&j, w)| {
            a + Mat4::from_cols_array_2d(&palette[j as usize]).transform_point3(p) * w
        })
}

#[cfg(test)]
mod tests {
    use super::*;
    use nico_assets::model::*;
    pub(super) fn fixture() -> Arc<CharacterAssets> {
        let vertex = |x, y| ModelVertex {
            position: [x, y, 0.],
            normal: [0., 0., 1.],
            uv: [0.; 2],
            joints: [0; 4],
            weights: [1., 0., 0., 0.],
        };
        let data = ModelData {
            nodes: vec![
                Node {
                    name: "mesh".into(),
                    children: vec![],
                    transform: Transform::default(),
                    mesh: Some(0),
                    skin: Some(0),
                },
                Node {
                    name: "joint".into(),
                    children: vec![],
                    transform: Transform::default(),
                    mesh: None,
                    skin: None,
                },
                Node {
                    name: "hidden".into(),
                    children: vec![],
                    transform: Transform::default(),
                    mesh: Some(0),
                    skin: Some(0),
                },
            ],
            meshes: vec![ModelMesh {
                name: "triangle".into(),
                primitives: vec![Primitive {
                    vertices: vec![vertex(0., 0.), vertex(1., 0.), vertex(0., 1.)],
                    indices: vec![0, 1, 2],
                    material: None,
                    skinned: true,
                    has_normals: true,
                }],
            }],
            skins: vec![Skin {
                name: "skin".into(),
                joints: vec![1],
                inverse_bind: vec![IDENTITY],
                skeleton: Some(1),
            }],
            clips: vec![Clip {
                name: "move".into(),
                tracks: vec![Track {
                    node: 1,
                    times: vec![0., 2.],
                    values: TrackValues::Translation(vec![[0.; 3], [2., 0., 0.]]),
                    interpolation: Interpolation::Linear,
                }],
            }],
            scenes: vec![Scene {
                name: "visible".into(),
                roots: vec![0, 1],
            }],
            default_scene: Some(0),
            ..Default::default()
        };
        let model = Arc::new(Model::new(data).unwrap());
        let visual =
            nico_presentation_control::model::ModelVisual::new(model.clone(), vec![]).unwrap();
        let animations = Arc::new(
            AnimationSet::new(
                model.clone(),
                vec![AnimationClip::direct(model.clone(), 0, "move").unwrap()],
            )
            .unwrap(),
        );
        Arc::new(CharacterAssets {
            model,
            visual,
            retargeted: false,
            animations,
        })
    }

    #[test]
    fn reduced_evaluation_preserves_elapsed_time_and_pose_after_flush() {
        let mut world = nico_ecs::World::new();
        let entity = spawn_character(&mut world, fixture()).unwrap();
        let base = (*world.entities().get::<&Character>(entity).unwrap()).clone();
        for fps in [30, 60, 144] {
            let mut capped = base.clone();
            let mut full = base.clone();
            capped.evaluation_interval = Duration::from_secs_f64(1. / 15.);
            let mut evaluations = 0;
            for _ in 0..fps * 3 {
                let dt = Duration::from_secs_f64(1. / f64::from(fps));
                evaluations += usize::from(capped.scheduled_advance(dt, false).unwrap());
                full.advance(dt).unwrap();
            }
            assert!(evaluations <= 45);
            assert!(capped.pending < capped.evaluation_interval);
            capped.scheduled_advance(Duration::ZERO, true).unwrap();
            assert!((capped.player.time() - full.player.time()).abs() < 1e-6);
            assert!(
                (vertex_position(&capped.meshes().unwrap()[0], 0)
                    - vertex_position(&full.meshes().unwrap()[0], 0))
                .length()
                    < 1e-5
            );
            assert_eq!(capped.pending, Duration::ZERO);
        }
    }
    #[test]
    fn pending_time_is_flushed_before_pause_and_completion_survives_large_delta() {
        let mut world = nico_ecs::World::new();
        let entity = spawn_character(&mut world, fixture()).unwrap();
        let mut c = world.entities().get::<&mut Character>(entity).unwrap();
        c.evaluation_interval = Duration::from_secs(1);
        assert!(
            !c.scheduled_advance(Duration::from_millis(200), false)
                .unwrap()
        );
        assert_eq!(c.player.time(), 0.);
        c.scheduled_advance(Duration::ZERO, true).unwrap();
        c.player.set_paused(true);
        c.scheduled_advance(Duration::from_secs(5), false).unwrap();
        assert!((c.player.time() - 0.2).abs() < 1e-6);
        assert_eq!(c.pending, Duration::ZERO);
        c.player.set_paused(false);
        c.player.set_mode(PlayMode::Once);
        c.scheduled_advance(Duration::from_secs(5), false).unwrap();
        assert!(c.player.finished());
        assert_eq!(c.player.time(), 2.);
    }
    #[test]
    fn instance_movement_and_reentry_refresh_bounds_without_changing_shared_geometry() {
        let mut world = nico_ecs::World::new();
        let entity = spawn_character(&mut world, fixture()).unwrap();
        let mut a = (*world.entities().get::<&Character>(entity).unwrap()).clone();
        let mut b = a.clone();
        let geometry = a.draws().unwrap()[0].mesh.clone().unwrap();
        assert!(Arc::ptr_eq(
            &geometry,
            b.draws().unwrap()[0].mesh.as_ref().unwrap()
        ));
        let camera = nico_presentation::Camera3d::looking_at([0., 0., 4.], [0.; 3], [0., 1., 0.])
            .unwrap()
            .view_projection(1.)
            .unwrap();
        a.position = [100., 0., 0.];
        a.invalidate();
        a.draws().unwrap();
        assert!(!a.bounds().unwrap().intersects_clip(camera));
        assert!(b.bounds().unwrap().intersects_clip(camera));
        a.evaluation_interval = Duration::from_secs(1);
        a.scheduled_advance(Duration::from_millis(250), false)
            .unwrap();
        a.scheduled_advance(Duration::ZERO, true).unwrap(); // Camera/placement re-entry forces refresh.
        a.position = [0.; 3];
        a.invalidate();
        assert!(Arc::ptr_eq(
            &geometry,
            a.draws().unwrap()[0].mesh.as_ref().unwrap()
        ));
        assert!(a.bounds().unwrap().intersects_clip(camera));
        assert_eq!(a.player.time(), 0.25);
        assert_eq!(b.player.time(), 0.);
    }

    #[test]
    fn despawn_releases_per_instance_palettes_and_shared_assets_after_snapshots_drop() {
        let mut world = nico_ecs::World::new();
        let assets = fixture();
        let weak_assets = Arc::downgrade(&assets);
        let entity = spawn_character(&mut world, assets.clone()).unwrap();
        drop(assets);
        let snapshot = world
            .entities()
            .get::<&mut Character>(entity)
            .unwrap()
            .draws()
            .unwrap()
            .to_vec();
        let weak_mesh = Arc::downgrade(snapshot[0].mesh.as_ref().unwrap());
        let weak_palette = Arc::downgrade(snapshot[0].skin_palette.as_ref().unwrap());
        world.despawn(entity).unwrap();
        assert!(weak_assets.upgrade().is_none());
        assert!(weak_mesh.upgrade().is_some());
        assert!(weak_palette.upgrade().is_some());
        drop(snapshot);
        assert!(weak_mesh.upgrade().is_none());
        assert!(weak_palette.upgrade().is_none());
    }

    #[test]
    fn diagnostic_clip_labels_are_bounded_before_publication() {
        let assets = fixture();
        let binding = |name: String| AnimationClip::direct(assets.model.clone(), 0, name).unwrap();
        assert!(validate_clip_labels(&[binding("x".repeat(128))]).is_ok());
        assert!(validate_clip_labels(&[binding("x".repeat(129))]).is_err());
        assert!(validate_clip_labels(&[binding("bad\nlabel".into())]).is_err());
        assert!(validate_clip_labels(&vec![binding("clip".into()); 129]).is_err());
    }
    #[test]
    fn prefab_instances_share_assets_but_keep_independent_playback() {
        let mut world = nico_ecs::World::new();
        let assets = fixture();
        let a = spawn_character(&mut world, assets.clone()).unwrap();
        let b = spawn_character(&mut world, assets.clone()).unwrap();
        world
            .entities()
            .get::<&mut Character>(a)
            .unwrap()
            .advance(Duration::from_secs_f32(0.5))
            .unwrap();
        let a = world.entities().get::<&Character>(a).unwrap();
        let b = world.entities().get::<&Character>(b).unwrap();
        assert_eq!(a.player.time(), 0.5);
        assert_eq!(b.player.time(), 0.);
        assert!(Arc::ptr_eq(&a.assets, &b.assets));
    }
    #[test]
    fn skinning_changes_vertices_and_respects_selected_scene_and_bind_pose() {
        let mut world = nico_ecs::World::new();
        let e = spawn_character(&mut world, fixture()).unwrap();
        let mut c = world.entities().get::<&mut Character>(e).unwrap();
        c.advance(Duration::from_secs_f32(0.5)).unwrap();
        let meshes = c.meshes().unwrap();
        assert_eq!(meshes.len(), 1);
        assert_eq!(vertex_position(&meshes[0], 0).to_array(), [0.5, 0., 0.]);
        let previous_geometry = meshes[0].mesh.clone().unwrap();
        c.bind_pose = true;
        assert!(Arc::ptr_eq(
            &previous_geometry,
            c.meshes().unwrap()[0].mesh.as_ref().unwrap()
        ));
        assert_eq!(
            vertex_position(&c.meshes().unwrap()[0], 0).to_array(),
            [0.; 3]
        );
    }
    #[test]
    fn playback_speed_is_independent_of_update_rate_across_clip_wraps() {
        let mut world = nico_ecs::World::new();
        let entity = spawn_character(&mut world, fixture()).unwrap();
        let reference = (*world.entities().get::<&Character>(entity).unwrap()).clone();
        for fps in [30, 60, 144] {
            let mut character = reference.clone();
            for _ in 0..fps * 3 {
                character
                    .advance(Duration::from_secs_f64(1. / f64::from(fps)))
                    .unwrap();
            }
            // Three seconds of playback in a two-second clip must end at one
            // second, regardless of how many Update calls divided that time.
            assert!((character.player.time() - 1.).abs() < 0.0001, "{fps} FPS");
            let meshes = character.meshes().unwrap();
            let x = vertex_position(&meshes[0], 0).x;
            assert!((x - 1.).abs() < 0.0001, "{fps} FPS pose");
        }
    }

    #[test]
    fn seeking_pauses_and_invalid_commands_preserve_state() {
        use crate::controls::{Action, Orbit, apply};
        let mut world = nico_ecs::World::new();
        let e = spawn_character(&mut world, fixture()).unwrap();
        let mut c = world.entities().get::<&mut Character>(e).unwrap();
        let mut orbit = Orbit::default();
        apply(Action::Seek(2.), &mut c, &mut orbit).unwrap();
        c.advance(Duration::from_secs(1)).unwrap();
        assert_eq!(c.player.time(), 2.);
        assert!(c.player.paused());
        assert!(apply(Action::Seek(3.), &mut c, &mut orbit).is_err());
        assert!(apply(Action::Clip(1), &mut c, &mut orbit).is_err());
        assert_eq!(c.player.time(), 2.);
        assert_eq!(c.player.clip(), Some(0));
        apply(Action::Playing(true), &mut c, &mut orbit).unwrap();
        c.advance(Duration::from_secs_f32(0.5)).unwrap();
        assert_eq!(c.player.time(), 0.5);
        apply(Action::Clip(0), &mut c, &mut orbit).unwrap();
        assert_eq!(c.player.time(), 0.);
    }
}
