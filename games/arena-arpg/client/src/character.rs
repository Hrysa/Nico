//! Local content composition for the arena; reusable evaluation lives in nico-animation.
use arena_arpg_shared::{Action, RunState, Snapshot};
use glam::{Mat4, Quat, Vec3};
use nico_animation::{
    Pose,
    attachment::Attachment,
    humanoid::{HumanoidProfile, HumanoidRig},
    playback::{AnimationClip, AnimationPlayer, AnimationSet, PlayMode},
};
use nico_assets::{
    Mesh, SkinWeights, Texture,
    import::{AssetImporter, ImportBudget, ImportContext},
    importers::{ModelGlbImporter, ModelGlbSettings, PngImporter, PngSettings},
    model::{ImageEncoding, Model, Transform},
};
use nico_presentation::MeshInstance;
use nico_presentation_control::model::{ModelBounds, ModelVisual};
use std::{fs::File, io::Read, path::Path, sync::Arc, time::Duration};
type Result<T> = std::result::Result<T, Box<dyn std::error::Error + Send + Sync>>;

// Contact calibrated from the retargeted RPG right-hand clip: peak forward extension.
const ATTACK_CONTACT: f64 = 49. / 120.;
const CLIPS: [&str; 6] = [
    "Idle",
    "Run-Forward",
    "Attack-R1",
    "Roll-Forward",
    "GetHit-F1",
    "Death1",
];
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Motion {
    Idle,
    Run,
    Attack(u64),
    Dodge(u64),
    Hit(u64),
    Death,
}
impl Motion {
    fn clip(self) -> usize {
        match self {
            Self::Idle => 0,
            Self::Run => 1,
            Self::Attack(_) => 2,
            Self::Dodge(_) => 3,
            Self::Hit(_) => 4,
            Self::Death => 5,
        }
    }
    fn name(self) -> &'static str {
        ["idle", "run", "attack", "dodge", "hit", "death"][self.clip()]
    }
    fn mode(self) -> PlayMode {
        if matches!(self, Self::Idle | Self::Run) {
            PlayMode::Loop
        } else {
            PlayMode::Once
        }
    }
}

pub struct CharacterAssets {
    set: Arc<AnimationSet>,
    visual: ModelVisual,
    hand: Attachment,
    scale: f32,
    floor: f32,
    weapon_scale: f32,
    weapon_length: f32,
    blade: Arc<Mesh>,
    white: Arc<Texture>,
}
impl CharacterAssets {
    pub fn load(model_path: &Path, animations: &Path) -> Result<Arc<Self>> {
        let model = load(model_path)?;
        let target = Arc::new(HumanoidRig::new(model.clone(), HumanoidProfile::mixamo())?);
        let mut clips = Vec::new();
        let mut total = 0u64;
        for name in CLIPS {
            let path = animations.join(format!("RPG-Character@Unarmed-{name}.glb"));
            total = total
                .checked_add(std::fs::metadata(&path)?.len())
                .ok_or("animation input overflow")?;
            if total > 256 * 1024 * 1024 {
                return Err("animation source budget exceeded".into());
            }
            let source = load(&path)?;
            if source.data().clips.len() != 1 {
                return Err(format!("{} must contain exactly one clip", path.display()).into());
            }
            let rig = Arc::new(HumanoidRig::new(source, HumanoidProfile::rpg())?);
            clips.push(AnimationClip::humanoid(rig, target.clone(), 0, name)?);
        }
        let mut textures = Vec::new();
        let mut decoded = 0usize;
        for (index, texture) in model.data().textures.iter().enumerate() {
            if !model
                .data()
                .materials
                .iter()
                .any(|m| m.base_color_texture == Some(index))
            {
                textures.push(None);
                continue;
            }
            let image = &model.data().images[texture.image];
            if image.encoding != ImageEncoding::Png {
                return Err("arena currently supports PNG base-color images only".into());
            }
            let remaining = (128usize * 1024 * 1024).saturating_sub(decoded);
            if remaining == 0 {
                return Err("arena base-color texture budget exceeded".into());
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
        let visual = ModelVisual::new(model.clone(), textures)?;
        // Reserve enough of the renderer's 256 draw slots for arena geometry and telegraphs.
        if visual.primitive_count() > 32 {
            return Err("arena hero supports at most 32 primitives".into());
        }
        let reference = visual.meshes(&Pose::rest(&model).globals()?)?;
        let mut low = Vec3::splat(f32::INFINITY);
        let mut high = Vec3::splat(f32::NEG_INFINITY);
        for draw in &reference {
            let mesh = draw.mesh.as_ref().unwrap();
            let palette = draw.skin_palette.as_ref().unwrap();
            for (vertex, weights) in mesh.vertices().iter().zip(mesh.skin().unwrap()) {
                let point =
                    weights
                        .joints
                        .iter()
                        .zip(weights.weights)
                        .fold(Vec3::ZERO, |p, (&j, w)| {
                            p + Mat4::from_cols_array_2d(&palette[j as usize])
                                .transform_point3(vertex.position.into())
                                * w
                        });
                low = low.min(point);
                high = high.max(point);
            }
        }
        let height = high.y - low.y;
        if !height.is_finite() || height < 1e-5 {
            return Err("invalid character bounds".into());
        }
        let scale = 1.8 / height;
        let hand = Attachment::new(model.clone(), "mixamorig:RightHand", Transform::default())?;
        let reference_hand =
            hand.matrix(&Pose::rest(&model), Mat4::from_scale(Vec3::splat(scale)))?;
        let weapon_scale = 1. / reference_hand.x_axis.truncate().length();
        if !weapon_scale.is_finite() {
            return Err("invalid hand socket scale".into());
        }
        let set = Arc::new(AnimationSet::new(model.clone(), clips)?);
        if set.clips().iter().any(|clip| clip.duration() <= 0.) {
            return Err("arena clips must have positive duration".into());
        }
        let mut contact = AnimationPlayer::new(set.clone());
        contact.play(2, PlayMode::Once, Duration::ZERO)?;
        contact.seek(contact.duration() * ATTACK_CONTACT)?;
        let socket = hand.matrix(&contact.pose(), Mat4::from_scale(Vec3::splat(scale)))?;
        let point = socket.w_axis.truncate();
        let direction = socket.transform_vector3(Vec3::Y) * weapon_scale;
        let a = direction.x * direction.x + direction.z * direction.z;
        let b = point.x * direction.x + point.z * direction.z;
        let reach = arena_arpg_shared::ActorKind::Hero.stats().range as f32;
        let c = point.x * point.x + point.z * point.z - reach * reach;
        let weapon_length = (-b + (b * b - a * c).sqrt()) / a;
        if !weapon_length.is_finite() || !(0.1..=4.).contains(&weapon_length) {
            return Err("attack socket cannot be calibrated to hero reach".into());
        }
        // Single-palette skinning preserves the full socket matrix, including hierarchy shear.
        let blade = crate::visuals::box_mesh([0.08, 0.08, weapon_length]);
        let blade = Arc::new(
            Mesh::skinned_triangles(
                blade.vertices().to_vec(),
                blade.indices().to_vec(),
                vec![
                    SkinWeights {
                        joints: [0; 4],
                        weights: [1., 0., 0., 0.]
                    };
                    blade.vertices().len()
                ],
                1,
            )
            .ok_or("invalid weapon mesh")?,
        );
        Ok(Arc::new(Self {
            set,
            visual,
            hand,
            scale,
            floor: low.y,
            weapon_scale,
            weapon_length,
            blade,
            white: Arc::new(Texture::rgba8(1, 1, vec![255; 4]).unwrap()),
        }))
    }
}

/// Per-hero state driven exclusively by owned snapshots. Presentation never mutates Arena.
struct Controller {
    player: AnimationPlayer,
    motion: Option<Motion>,
    previous: Option<Snapshot>,
}
impl Controller {
    fn new(set: Arc<AnimationSet>) -> Self {
        Self {
            player: AnimationPlayer::new(set),
            motion: None,
            previous: None,
        }
    }
    fn update(&mut self, state: &Snapshot, dt: Duration) -> Result<()> {
        let actor = &state.actors[0];
        let reset = self.previous.as_ref().is_none_or(|s| {
            s.run_id != state.run_id || s.wave != state.wave || s.tick > state.tick
        });
        let delta_ticks = self
            .previous
            .as_ref()
            .filter(|_| !reset)
            .map_or(0, |s| state.tick - s.tick);
        let elapsed = Duration::from_secs_f64(delta_ticks as f64 / 60.);
        if reset {
            self.player.reference_pose();
            self.motion = None;
        } else {
            let delta = if state.state != RunState::Playing {
                dt
            } else {
                elapsed
            };
            let current_action = match actor.action {
                Action::Attack { id, .. } => Some(Motion::Attack(id)),
                Action::Dodge { elapsed, .. } => {
                    Some(Motion::Dodge(state.tick.saturating_sub(u64::from(elapsed))))
                }
                _ => None,
            };
            if current_action.is_some() && current_action == self.motion {
                self.player
                    .update_at(delta, action_position(actor, self.player.duration()))?;
            } else {
                self.player.update(delta)?;
            }
        }

        let hurt = !reset
            && self
                .previous
                .as_ref()
                .is_some_and(|s| actor.health < s.actors[0].health);
        let moving = !reset
            && self
                .previous
                .as_ref()
                .is_some_and(|s| actor.position != s.actors[0].position);
        let desired = if actor.health == 0 {
            Motion::Death
        } else if hurt {
            Motion::Hit(state.tick)
        } else if let Some(hit @ Motion::Hit(_)) = self.motion.filter(|_| !self.player.finished()) {
            hit
        } else {
            match actor.action {
                Action::Attack { id, .. } => Motion::Attack(id),
                Action::Dodge { elapsed, .. } => {
                    Motion::Dodge(state.tick.saturating_sub(u64::from(elapsed)))
                }
                Action::Idle
                    if state.state == RunState::Playing
                        && (moving || delta_ticks == 0 && self.motion == Some(Motion::Run)) =>
                {
                    Motion::Run
                }
                Action::Idle => Motion::Idle,
            }
        };
        if self.motion != Some(desired) {
            self.player.set_speed(1.)?;
            self.player.play(
                desired.clip(),
                desired.mode(),
                if reset {
                    Duration::ZERO
                } else {
                    Duration::from_millis(80)
                },
            )?;
            if matches!(desired, Motion::Attack(_) | Motion::Dodge(_)) {
                self.player.update_at(
                    Duration::from_secs_f64(match actor.action {
                        Action::Attack { elapsed, .. } | Action::Dodge { elapsed, .. } => {
                            f64::from(elapsed) / 60.
                        }
                        _ => 0.,
                    }),
                    action_position(actor, self.player.duration()),
                )?;
            }
            self.motion = Some(desired);
        }
        self.previous = Some(state.clone());
        Ok(())
    }
}

fn action_position(actor: &arena_arpg_shared::Actor, duration: f64) -> f64 {
    match actor.action {
        Action::Attack { elapsed, .. } => {
            let stats = actor.kind.stats();
            let phase = if elapsed <= stats.windup {
                ATTACK_CONTACT * f64::from(elapsed) / f64::from(stats.windup)
            } else {
                ATTACK_CONTACT
                    + (1. - ATTACK_CONTACT) * f64::from(elapsed - stats.windup)
                        / f64::from(stats.active + stats.recovery)
            };
            duration * phase.clamp(0., 1.)
        }
        Action::Dodge { elapsed, .. } => duration * (f64::from(elapsed) / 18.).clamp(0., 1.),
        _ => 0.,
    }
}

pub struct Character {
    assets: Arc<CharacterAssets>,
    controller: Controller,
    globals: Vec<Mat4>,
    hand_matrix: Mat4,
    weapon_matrix: Mat4,
    render_bounds: Option<ModelBounds>,
    visible: bool,
}
impl Character {
    pub fn new(assets: Arc<CharacterAssets>) -> Self {
        Self {
            controller: Controller::new(assets.set.clone()),
            assets,
            globals: Vec::new(),
            hand_matrix: Mat4::IDENTITY,
            weapon_matrix: Mat4::IDENTITY,
            render_bounds: None,
            visible: true,
        }
    }
    pub fn render(
        &mut self,
        state: &Snapshot,
        dt: Duration,
        view_projection: Option<Mat4>,
    ) -> Result<Vec<MeshInstance>> {
        self.controller.update(state, dt)?;
        let actor = &state.actors[0];
        let yaw = actor.facing.x.atan2(actor.facing.z) as f32;
        let rotation = Quat::from_rotation_y(yaw);
        let position = Vec3::new(
            actor.position.x as f32,
            -self.assets.floor * self.assets.scale,
            actor.position.z as f32,
        );
        let placement = Mat4::from_scale_rotation_translation(
            Vec3::splat(self.assets.scale),
            rotation,
            position,
        );
        let pose = self.controller.player.pose();
        pose.globals_into(&mut self.globals)?;
        let mut meshes = self.assets.visual.meshes(&self.globals)?;
        for mesh in &mut meshes {
            mesh.position = position.to_array();
            mesh.orientation = rotation;
            mesh.scale = self.assets.scale;
        }
        self.hand_matrix = self.assets.hand.matrix(&pose, placement)?;
        // Blade dimensions are in world metres; preserve socket orientation/scale
        // relative to the character's normalized model size.
        let weapon = self.hand_matrix
            * Mat4::from_scale(Vec3::splat(self.assets.weapon_scale))
            * Mat4::from_rotation_x(-std::f32::consts::FRAC_PI_2)
            * Mat4::from_translation(Vec3::new(0., 0., self.assets.weapon_length * 0.5));
        if !weapon.is_finite() {
            return Err("weapon transform overflow".into());
        }
        self.weapon_matrix = weapon;
        // A failed bound disables culling; it must never hide otherwise valid draws.
        self.render_bounds = self.assets.visual.bounds(&self.globals, placement).ok();
        if let Some(bounds) = &mut self.render_bounds {
            let mut low = Vec3::from(bounds.min);
            let mut high = Vec3::from(bounds.max);
            for vertex in self.assets.blade.vertices() {
                let point = weapon.transform_point3(vertex.position.into());
                low = low.min(point);
                high = high.max(point);
            }
            let margin = low.abs().max(high.abs()).max(Vec3::ONE) * 1e-4;
            bounds.min = (low - margin).to_array();
            bounds.max = (high + margin).to_array();
            if !low.is_finite()
                || !high.is_finite()
                || !Vec3::from(bounds.min).is_finite()
                || !Vec3::from(bounds.max).is_finite()
            {
                self.render_bounds = None;
            }
        }
        meshes.push(MeshInstance {
            mesh: Some(self.assets.blade.clone()),
            skin_palette: Some(Arc::new(vec![weapon.to_cols_array_2d()])),
            texture: Some(self.assets.white.clone()),
            position: [0.; 3],
            orientation: Quat::IDENTITY,
            scale: 1.,
            color: [0.72, 0.91, 0.98, 1.],
        });
        self.visible = self
            .render_bounds
            .zip(view_projection)
            .is_none_or(|(bounds, matrix)| bounds.intersects_clip(matrix));
        if !self.visible {
            meshes.clear();
        }
        Ok(meshes)
    }
    pub fn state(&self) -> serde_json::Value {
        serde_json::json!({"motion":self.controller.motion.map(Motion::name), "clip":self.controller.player.clip(), "time":self.controller.player.time(), "duration":self.controller.player.duration(), "finished":self.controller.player.finished(), "fade_weight":self.controller.player.fade_weight(), "hand_matrix":self.hand_matrix.to_cols_array_2d(), "weapon_matrix":self.weapon_matrix.to_cols_array_2d(), "weapon_length":self.assets.weapon_length,"attack_contact_seconds":self.assets.set.clips()[2].duration()*ATTACK_CONTACT, "model_draws":self.assets.visual.primitive_count(), "visible":self.visible, "render_bounds":self.render_bounds.map(|b| serde_json::json!({"min":b.min,"max":b.max}))})
    }
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

/// Discoverable animation observation; null means procedural visuals are selected.
pub fn state_schema() -> serde_json::Value {
    use serde_json::json;
    let matrix = json!({"type":"array","minItems":4,"maxItems":4,"items":{"type":"array","minItems":4,"maxItems":4,"items":{"type":"number"}}});
    json!({"anyOf":[{"type":"null"},{"type":"object","additionalProperties":false,
    "required":["motion","clip","time","duration","finished","fade_weight","hand_matrix","weapon_matrix","weapon_length","attack_contact_seconds","model_draws","render_bounds","visible"],
    "properties":{
        "motion":{"enum":[null,"idle","run","attack","dodge","hit","death"]},
        "clip":{"type":["integer","null"],"minimum":0,"maximum":5},
        "time":{"type":"number","minimum":0}, "duration":{"type":"number","minimum":0},
        "finished":{"type":"boolean"}, "fade_weight":{"type":["number","null"],"minimum":0,"maximum":1},
        "hand_matrix":matrix, "weapon_matrix":matrix, "weapon_length":{"type":"number","minimum":0.1,"maximum":4},"attack_contact_seconds":{"type":"number","minimum":0},
            "visible":{"type":"boolean"},
            "render_bounds":{"anyOf":[{"type":"null"},{"type":"object","required":["min","max"],"additionalProperties":false,"properties":{"min":{"type":"array","minItems":3,"maxItems":3,"items":{"type":"number"}},"max":{"type":"array","minItems":3,"maxItems":3,"items":{"type":"number"}}}}]}, "model_draws":{"type":"integer","minimum":1,"maximum":32}
    }}]})
}

#[cfg(test)]
mod tests {
    use super::*;
    use nico_assets::model::*;
    fn controller() -> Controller {
        let model = Arc::new(
            Model::new(ModelData {
                nodes: vec![Node {
                    name: "joint".into(),
                    children: vec![],
                    transform: Transform::default(),
                    mesh: None,
                    skin: None,
                }],
                clips: CLIPS
                    .iter()
                    .map(|name| Clip {
                        name: (*name).into(),
                        tracks: vec![Track {
                            node: 0,
                            times: vec![0., 1.],
                            values: TrackValues::Translation(vec![[0.; 3], [1., 0., 0.]]),
                            interpolation: Interpolation::Linear,
                        }],
                    })
                    .collect(),
                ..Default::default()
            })
            .unwrap(),
        );
        let clips = CLIPS
            .iter()
            .enumerate()
            .map(|(i, name)| AnimationClip::direct(model.clone(), i, *name).unwrap())
            .collect();
        Controller::new(Arc::new(AnimationSet::new(model, clips).unwrap()))
    }
    fn state() -> Snapshot {
        arena_arpg_shared::Arena::default().snapshot().clone()
    }
    fn update(c: &mut Controller, s: &Snapshot) {
        c.update(s, Duration::from_secs_f64(1. / 60.)).unwrap();
    }
    #[test]
    fn snapshots_select_idle_run_stop_and_do_not_advance_twice() {
        let mut c = controller();
        let mut s = state();
        update(&mut c, &s);
        assert_eq!(c.motion, Some(Motion::Idle));
        s.tick += 1;
        s.actors[0].position.x += 0.1;
        update(&mut c, &s);
        assert_eq!(c.motion, Some(Motion::Run));
        let time = c.player.time();
        update(&mut c, &s);
        assert_eq!(c.motion, Some(Motion::Run));
        assert_eq!(c.player.time(), time);
        s.tick += 1;
        update(&mut c, &s);
        assert_eq!(c.motion, Some(Motion::Idle));
    }
    #[test]
    fn attack_phase_tracks_authoritative_elapsed_across_skipped_render_updates() {
        let mut a = controller();
        let mut b = controller();
        let mut s = state();
        update(&mut a, &s);
        update(&mut b, &s);
        for elapsed in 0..24 {
            s.tick = 100 + u64::from(elapsed);
            s.actors[0].action = Action::Attack {
                id: 1,
                elapsed,
                hit_mask: 0,
            };
            update(&mut a, &s);
            if elapsed == 0 || elapsed == 23 {
                update(&mut b, &s);
            }
        }
        assert_eq!(a.motion, Some(Motion::Attack(1)));
        assert!(
            (a.player.time() - (ATTACK_CONTACT + (1. - ATTACK_CONTACT) * 11. / 24.)).abs() < 1e-6
        );
        assert!((a.player.time() - b.player.time()).abs() < 1e-6);
        assert_eq!(s.actors[0].health, 100); // Presentation never changes the source snapshot.
        s.tick += 1;
        s.actors[0].action = Action::Attack {
            id: 2,
            elapsed: 0,
            hit_mask: 0,
        };
        update(&mut a, &s);
        assert_eq!(a.motion, Some(Motion::Attack(2)));
        assert_eq!(a.player.time(), 0.);
    }

    #[test]
    fn late_attack_observation_reaches_contact_at_active_boundary_without_a_delayed_fade() {
        let mut c = controller();
        let mut s = state();
        update(&mut c, &s);
        s.tick = 100;
        s.actors[0].action = Action::Attack {
            id: 5,
            elapsed: 12,
            hit_mask: 0,
        };
        update(&mut c, &s);
        assert!((c.player.time() - ATTACK_CONTACT).abs() < 1e-6);
        assert!(c.player.fade_weight().is_none());
        assert!(
            (f64::from(c.player.pose().local()[0].translation[0]) - ATTACK_CONTACT).abs() < 1e-6
        );
    }
    #[test]
    fn wave_change_discards_old_hit_reaction() {
        let mut c = controller();
        let mut s = state();
        update(&mut c, &s);
        s.tick += 1;
        s.actors[0].health -= 20;
        update(&mut c, &s);
        assert!(matches!(c.motion, Some(Motion::Hit(_))));
        s.tick += 1;
        s.wave += 1;
        s.actors[0].health = 100;
        update(&mut c, &s);
        assert_eq!(c.motion, Some(Motion::Idle));
        assert_eq!(c.player.time(), 0.);
    }
    #[test]
    fn dodge_hit_death_and_restart_obey_snapshot_precedence() {
        let mut c = controller();
        let mut s = state();
        update(&mut c, &s);
        s.tick = 10;
        s.actors[0].action = Action::Dodge {
            elapsed: 6,
            direction: arena_arpg_shared::Vec2 { x: 0., z: 1. },
        };
        update(&mut c, &s);
        assert_eq!(c.motion, Some(Motion::Dodge(4)));
        assert!((c.player.time() - 1. / 3.).abs() < 1e-6);
        s.tick += 1;
        s.actors[0].action = Action::Idle;
        s.actors[0].health -= 20;
        update(&mut c, &s);
        assert_eq!(c.motion, Some(Motion::Hit(11)));
        update(&mut c, &s);
        assert_eq!(c.player.time(), 0.); // Repeated snapshot does not restart or advance hit.
        s.tick += 61;
        update(&mut c, &s);
        assert_eq!(c.motion, Some(Motion::Idle));
        s.tick += 1;
        s.actors[0].health = 0;
        s.state = RunState::Lost;
        update(&mut c, &s);
        assert_eq!(c.motion, Some(Motion::Death));
        c.update(&s, Duration::from_secs(2)).unwrap();
        assert!(c.player.finished()); // Terminal simulation stops ticking.
        let mut fresh = state();
        fresh.run_id = s.run_id + 1;
        update(&mut c, &fresh);
        assert_eq!(c.motion, Some(Motion::Idle));
        assert!(!c.player.finished());
        assert_eq!(c.player.time(), 0.);
    }
}
