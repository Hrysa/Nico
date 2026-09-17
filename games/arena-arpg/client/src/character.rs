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
pub mod definition;
mod grip;
use definition::SocketTransform;

// Synthetic one-second fixture marker; real content uses contact_seconds in TOML.
#[cfg(test)]
const ATTACK_CONTACT: f64 = 0.296;
#[cfg(test)]
const CLIPS: [&str; 5] = [
    "Sword_Idle",
    "Run-Forward",
    "Sword_Regular_C",
    "Roll-Forward",
    "Death1",
];
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Motion {
    Idle,
    Run,
    Attack(u64),
    Dodge(u64),
    Death,
}
impl Motion {
    fn clip(self) -> usize {
        match self {
            Self::Idle => 0,
            Self::Run => 1,
            Self::Attack(_) => 2,
            Self::Dodge(_) => 3,
            Self::Death => 4,
        }
    }
    fn name(self) -> &'static str {
        ["idle", "run", "attack", "dodge", "death"][self.clip()]
    }
}

pub struct CharacterAssets {
    set: Arc<AnimationSet>,
    definition: Arc<definition::VisualDefinition>,
    playback: Arc<PlaybackSettings>,
    visual: ModelVisual,
    scale: f32,
    floor: f32,
    weapon: Option<EquippedWeapon>,
}
struct EquippedWeapon {
    hand: Attachment,
    weapon_socket: Attachment,
    weapon_scale: f32,
    weapon_length: f32,
    blade: Arc<Mesh>,
    weapon_texture: Arc<Texture>,
}
impl CharacterAssets {
    /// Maximum submitted meshes, including optional attached equipment before culling.
    pub fn draw_count(&self) -> usize {
        self.visual.primitive_count() + usize::from(self.weapon.is_some())
    }
    pub fn inspection(&self) -> serde_json::Value {
        serde_json::json!({"model_scale":self.scale,"model_floor_translation_m":-self.floor*self.scale,"weapon_bone_index":self.weapon.as_ref().map(|w| w.weapon_socket.node()),"weapon_scale":self.weapon.as_ref().map(|w| w.weapon_scale),"clips":self.set.clips().iter().enumerate().map(|(i,c)| serde_json::json!({"motion":definition::MOTIONS[i],"index":i,"name":c.name(),"duration_seconds":c.duration()})).collect::<Vec<_>>()})
    }
    #[cfg(test)]
    pub fn load(model_path: &Path, animations: &Path) -> Result<Arc<Self>> {
        Self::load_definition(
            definition::VisualDefinition::builtin(0),
            Path::new("."),
            Some((model_path, animations)),
        )
    }
    pub fn load_definition(
        definition: definition::VisualDefinition,
        root: &Path,
        overrides: Option<(&Path, &Path)>,
    ) -> Result<Arc<Self>> {
        definition.validate()?;
        let model_definition = definition.core.model.as_ref().ok_or("model required")?;
        let model_path = definition::asset_path(root, &model_definition.asset)?;
        let model = load(overrides.map_or(model_path.as_path(), |v| v.0))?;
        let target = Arc::new(HumanoidRig::from_reference(
            model.clone(),
            grip::authored_reference(&model, &definition.core.pose)?,
            definition.profile(&model_definition.profile)?,
        )?);
        // Validate even sockets which are currently unused by equipment.
        for (name, socket) in &definition.core.sockets {
            Attachment::new(model.clone(), &socket.bone, socket.transform())
                .map_err(|e| format!("sockets.{name}.bone ({}): {e}", socket.bone))?;
        }
        let mut clips = Vec::new();
        let mut total = 0u64;
        for name in definition::MOTIONS {
            let binding = &definition.arena.animations[name];
            let animation = &definition.core.animations[&binding.animation];
            let path = if let Some((_, directory)) = overrides {
                directory.join(
                    Path::new(&animation.asset)
                        .file_name()
                        .ok_or("animation filename")?,
                )
            } else {
                definition::asset_path(root, &animation.asset)?
            };
            total = total
                .checked_add(std::fs::metadata(&path)?.len())
                .ok_or("animation input overflow")?;
            if total > 256 * 1024 * 1024 {
                return Err("animation source budget exceeded".into());
            }
            let source = load(&path)?;
            let mut matching = source
                .data()
                .clips
                .iter()
                .enumerate()
                .filter(|(_, c)| c.name == animation.clip);
            let index = matching
                .next()
                .ok_or_else(|| format!("{}: missing clip {}", path.display(), animation.clip))?
                .0;
            if matching.next().is_some() {
                return Err(
                    format!("{}: ambiguous clip {}", path.display(), animation.clip).into(),
                );
            }
            let rig = Arc::new(HumanoidRig::new(
                source,
                definition.profile(&animation.profile)?,
            )?);
            clips.push(AnimationClip::humanoid(
                rig,
                target.clone(),
                index,
                &animation.clip,
            )?);
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
        let scale = model_definition.target_height_m / height;
        let weapon = if let Some(weapon_definition) = &definition.arena.weapon {
            let socket = &definition.core.sockets[&weapon_definition.socket];
            let hand = Attachment::new(model.clone(), &socket.bone, Transform::default())?;
            let weapon_socket = Attachment::new(model.clone(), &socket.bone, socket.transform())?;
            let reference_hand =
                hand.matrix(&Pose::rest(&model), Mat4::from_scale(Vec3::splat(scale)))?;
            let weapon_scale = 1. / reference_hand.x_axis.truncate().length();
            if !weapon_scale.is_finite() {
                return Err("invalid hand socket scale".into());
            }
            Some(EquippedWeapon {
                hand,
                weapon_socket,
                weapon_scale,
                weapon_length: weapon_definition.tip_m,
                blade: Arc::new(grip::weapon(&weapon_definition.parts)?),
                weapon_texture: Arc::new(
                    Texture::rgba8(
                        weapon_definition.parts.len() as u32,
                        1,
                        weapon_definition
                            .parts
                            .iter()
                            .flat_map(|p| p.color_rgba)
                            .collect(),
                    )
                    .ok_or("weapon texture")?,
                ),
            })
        } else {
            None
        };
        let set = Arc::new(AnimationSet::new(model.clone(), clips)?);
        if set.clips().iter().any(|clip| clip.duration() <= 0.) {
            return Err("arena clips must have positive duration".into());
        }
        if definition.arena.animations["attack"]
            .contact_seconds
            .unwrap()
            >= set.clips()[2].duration()
        {
            return Err("animations.attack.contact_seconds must be inside the clip".into());
        }
        let playback = Arc::new(PlaybackSettings::new(&definition, &set));
        Ok(Arc::new(Self {
            set,
            playback,
            visual,
            scale,
            floor: low.y - model_definition.floor_offset_m / scale,
            weapon,
            definition: Arc::new(definition),
        }))
    }
}

/// Per-character state driven exclusively by owned snapshots. Presentation never mutates Arena.
#[derive(Clone, Copy)]
struct MotionParameters {
    speed: f64,
    blend: Duration,
    mode: PlayMode,
    authored_speed: Option<f64>,
}
struct PlaybackSettings {
    motions: [MotionParameters; 5],
    contact: f64,
}
impl PlaybackSettings {
    fn new(definition: &definition::VisualDefinition, set: &AnimationSet) -> Self {
        Self {
            contact: definition.arena.animations["attack"]
                .contact_seconds
                .unwrap()
                / set.clips()[2].duration(),
            motions: std::array::from_fn(|i| {
                let a = &definition.arena.animations[definition::MOTIONS[i]];
                MotionParameters {
                    speed: a.speed,
                    blend: Duration::from_secs_f64(a.blend_seconds),
                    mode: a.playback.mode(),
                    authored_speed: a.authored_speed_mps,
                }
            }),
        }
    }
}
/// Per-entity presentation input, independent of arena actor slots.
#[derive(Clone)]
pub struct CharacterFrame {
    pub epoch: u64,
    pub tick: u64,
    pub playing: bool,
    pub position: arena_arpg_shared::Vec2,
    pub facing: arena_arpg_shared::Vec2,
    pub health: u16,
    pub action: Action,
    pub stats: arena_arpg_shared::CombatStats,
    pub dodge_duration: u16,
}
impl CharacterFrame {
    pub fn arena_actor(state: &Snapshot, index: usize) -> Self {
        let actor = &state.actors[index];
        Self {
            epoch: state.run_id.wrapping_mul(4) + u64::from(state.wave),
            tick: state.tick,
            playing: state.state == RunState::Playing,
            position: actor.position,
            facing: actor.facing,
            health: actor.health,
            action: actor.action,
            stats: actor.stats(),
            dodge_duration: actor.definition().arena.dodge.duration_ticks,
        }
    }
}
struct Controller {
    settings: Arc<PlaybackSettings>,
    player: AnimationPlayer,
    motion: Option<Motion>,
    previous: Option<CharacterFrame>,
}
impl Controller {
    fn configured(set: Arc<AnimationSet>, settings: Arc<PlaybackSettings>) -> Self {
        Self {
            settings,
            player: AnimationPlayer::new(set),
            motion: None,
            previous: None,
        }
    }
    #[cfg(test)]
    fn update(&mut self, state: &Snapshot, dt: Duration) -> Result<()> {
        self.update_frame(&CharacterFrame::arena_actor(state, 0), dt)
    }
    fn update_frame(&mut self, state: &CharacterFrame, dt: Duration) -> Result<()> {
        let actor = state;
        let reset = self
            .previous
            .as_ref()
            .is_none_or(|s| s.epoch != state.epoch || s.tick > state.tick);
        let delta_ticks = self
            .previous
            .as_ref()
            .filter(|_| !reset)
            .map_or(0, |s| state.tick - s.tick);
        let elapsed = Duration::from_secs_f64(delta_ticks as f64 / 60.);
        if self.motion == Some(Motion::Run) && delta_ticks > 0 {
            let animation = self.settings.motions[1];
            if let Some(authored) = animation.authored_speed {
                let previous = self.previous.as_ref().unwrap();
                let distance = ((actor.position.x - previous.position.x).powi(2)
                    + (actor.position.z - previous.position.z).powi(2))
                .sqrt();
                self.player.set_speed(
                    (animation.speed * distance / elapsed.as_secs_f64() / authored).clamp(0., 10.),
                )?;
            }
        }
        if reset {
            self.player.reference_pose();
            self.motion = None;
        } else {
            let delta = if !state.playing { dt } else { elapsed };
            let current_action = match actor.action {
                Action::Attack { id, .. } => Some(Motion::Attack(id)),
                Action::Dodge { elapsed, .. } => {
                    Some(Motion::Dodge(state.tick.saturating_sub(u64::from(elapsed))))
                }
                _ => None,
            };
            if current_action.is_some() && current_action == self.motion {
                self.player.update_at(
                    delta,
                    frame_action_position(actor, self.player.duration(), self.settings.contact),
                )?;
            } else {
                self.player.update(delta)?;
            }
        }

        let moving = !reset
            && self
                .previous
                .as_ref()
                .is_some_and(|s| actor.position != s.position);
        let desired = if actor.health == 0 {
            Motion::Death
        } else {
            match actor.action {
                Action::Attack { id, .. } => Motion::Attack(id),
                Action::Dodge { elapsed, .. } => {
                    Motion::Dodge(state.tick.saturating_sub(u64::from(elapsed)))
                }
                Action::Idle
                    if state.playing
                        && (moving || delta_ticks == 0 && self.motion == Some(Motion::Run)) =>
                {
                    Motion::Run
                }
                Action::Idle => Motion::Idle,
            }
        };
        if self.motion != Some(desired) {
            let animation = self.settings.motions[desired.clip()];
            self.player.set_speed(animation.speed)?;
            self.player.play(
                desired.clip(),
                animation.mode,
                if reset {
                    Duration::ZERO
                } else {
                    animation.blend
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
                    frame_action_position(actor, self.player.duration(), self.settings.contact),
                )?;
            }
            self.motion = Some(desired);
        }
        self.previous = Some(state.clone());
        Ok(())
    }
}

fn frame_action_position(actor: &CharacterFrame, duration: f64, contact: f64) -> f64 {
    match actor.action {
        Action::Attack { elapsed, .. } => {
            let stats = actor.stats;
            let phase = if elapsed <= stats.windup {
                contact * f64::from(elapsed) / f64::from(stats.windup)
            } else {
                contact
                    + (1. - contact) * f64::from(elapsed - stats.windup)
                        / f64::from(stats.active + stats.recovery)
            };
            duration * phase.clamp(0., 1.)
        }
        Action::Dodge { elapsed, .. } => {
            duration * (f64::from(elapsed) / f64::from(actor.dodge_duration)).clamp(0., 1.)
        }
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
            controller: Controller::configured(assets.set.clone(), assets.playback.clone()),
            assets,
            globals: Vec::new(),
            hand_matrix: Mat4::IDENTITY,
            weapon_matrix: Mat4::IDENTITY,
            render_bounds: None,
            visible: true,
        }
    }
    #[cfg(test)]
    pub fn render(
        &mut self,
        state: &Snapshot,
        dt: Duration,
        view_projection: Option<Mat4>,
    ) -> Result<Vec<MeshInstance>> {
        self.render_frame(&CharacterFrame::arena_actor(state, 0), dt, view_projection)
    }
    pub fn render_frame(
        &mut self,
        state: &CharacterFrame,
        dt: Duration,
        view_projection: Option<Mat4>,
    ) -> Result<Vec<MeshInstance>> {
        self.controller.update_frame(state, dt)?;
        let actor = state;
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
        self.render_bounds = self.assets.visual.bounds(&self.globals, placement).ok();
        if let Some(equipped) = &self.assets.weapon {
            self.hand_matrix = equipped.hand.matrix(&pose, placement)?;
            // Blade dimensions are in world metres; preserve socket orientation/scale
            // relative to the character's normalized model size.
            let weapon = equipped.weapon_socket.matrix(&pose, placement)?
                * Mat4::from_scale(Vec3::splat(equipped.weapon_scale));
            if !weapon.is_finite() {
                return Err("weapon transform overflow".into());
            }
            self.weapon_matrix = weapon;
            // A failed bound disables culling; it must never hide otherwise valid draws.
            if let Some(bounds) = &mut self.render_bounds {
                let mut low = Vec3::from(bounds.min);
                let mut high = Vec3::from(bounds.max);
                for vertex in equipped.blade.vertices() {
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
                mesh: Some(equipped.blade.clone()),
                skin_palette: Some(Arc::new(vec![weapon.to_cols_array_2d()])),
                texture: Some(equipped.weapon_texture.clone()),
                position: [0.; 3],
                orientation: Quat::IDENTITY,
                scale: 1.,
                color: [1.; 4],
            });
        }
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
        serde_json::json!({"motion":self.controller.motion.map(Motion::name), "clip":self.controller.player.clip(), "time":self.controller.player.time(), "duration":self.controller.player.duration(), "finished":self.controller.player.finished(), "fade_weight":self.controller.player.fade_weight(), "hand_matrix":self.hand_matrix.to_cols_array_2d(), "weapon_matrix":self.weapon_matrix.to_cols_array_2d(), "weapon_length":self.assets.weapon.as_ref().map_or(0., |w| w.weapon_length),"attack_contact_seconds":self.assets.definition.arena.animations["attack"].contact_seconds.unwrap(), "model_draws":self.assets.visual.primitive_count(), "visible":self.visible, "render_bounds":self.render_bounds.map(|b| serde_json::json!({"min":b.min,"max":b.max}))})
    }
}

pub(crate) fn load(path: &Path) -> Result<Arc<Model>> {
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
        "motion":{"enum":[null,"idle","run","attack","dodge","death"]},
        "clip":{"type":["integer","null"],"minimum":0,"maximum":5},
        "time":{"type":"number","minimum":0}, "duration":{"type":"number","minimum":0},
        "finished":{"type":"boolean"}, "fade_weight":{"type":["number","null"],"minimum":0,"maximum":1},
        "hand_matrix":matrix, "weapon_matrix":matrix, "weapon_length":{"type":"number","minimum":0,"maximum":4},"attack_contact_seconds":{"type":"number","minimum":0},
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
        {
            let mut definition = definition::VisualDefinition::builtin(0);
            // Synthetic fixture clips are one second long, unlike the real sword clip.
            definition
                .arena
                .animations
                .get_mut("attack")
                .unwrap()
                .contact_seconds = Some(ATTACK_CONTACT);
            let set = Arc::new(AnimationSet::new(model, clips).unwrap());
            let settings = Arc::new(PlaybackSettings::new(&definition, &set));
            Controller::configured(set, settings)
        }
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
    fn configured_speed_blend_and_stride_follow_observed_motion() {
        let mut controller = controller();
        let settings = Arc::get_mut(&mut controller.settings).unwrap();
        settings.motions[0].speed = 2.;
        settings.motions[1].authored_speed = Some(2.);
        settings.motions[1].blend = Duration::from_millis(250);
        let mut state = state();
        update(&mut controller, &state);
        state.tick += 6;
        update(&mut controller, &state);
        assert!((controller.player.time() - 0.2).abs() < 1e-9);
        state.tick += 1;
        state.actors[0].position.x += 0.1;
        update(&mut controller, &state);
        state.tick += 6;
        state.actors[0].position.x += 0.2;
        update(&mut controller, &state);
        assert!((controller.player.time() - 0.1).abs() < 1e-9);
        assert!((controller.player.fade_weight().unwrap() - 0.4).abs() < 1e-6);
        state.tick += 6;
        state.actors[0].position.x += 0.4;
        update(&mut controller, &state);
        assert!((controller.player.time() - 0.3).abs() < 1e-9);
        let observed = controller.player.time();
        update(&mut controller, &state);
        assert_eq!(controller.player.time(), observed);
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
    fn nonlethal_damage_does_not_interrupt_authoritative_actions_or_movement() {
        for action in [
            Action::Idle,
            Action::Attack {
                id: 7,
                elapsed: 3,
                hit_mask: 0,
            },
            Action::Dodge {
                elapsed: 3,
                direction: arena_arpg_shared::Vec2 { x: 1., z: 0. },
            },
        ] {
            let mut damaged = controller();
            let mut unchanged = controller();
            let mut snapshot = state();
            update(&mut damaged, &snapshot);
            update(&mut unchanged, &snapshot);
            snapshot.tick += 3;
            snapshot.actors[0].action = action;
            snapshot.actors[0].position.x += 0.1;
            update(&mut unchanged, &snapshot);
            snapshot.actors[0].health -= 20;
            update(&mut damaged, &snapshot);
            assert_eq!(damaged.motion, unchanged.motion);
            assert_eq!(damaged.player.time(), unchanged.player.time());
            assert_eq!(
                damaged.player.pose().local(),
                unchanged.player.pose().local()
            );
        }
    }
    #[test]
    fn health_loss_keeps_idle_and_wave_change_resets_playback() {
        let mut c = controller();
        let mut s = state();
        update(&mut c, &s);
        s.tick += 1;
        s.actors[0].health -= 20;
        update(&mut c, &s);
        assert_eq!(c.motion, Some(Motion::Idle));
        assert!(c.player.time() > 0.);
        s.tick += 1;
        s.wave += 1;
        s.actors[0].health = 100;
        update(&mut c, &s);
        assert_eq!(c.motion, Some(Motion::Idle));
        assert_eq!(c.player.time(), 0.);
    }
    #[test]
    fn dodge_death_and_restart_obey_snapshot_precedence() {
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
        assert_eq!(c.motion, Some(Motion::Idle));
        update(&mut c, &s);
        assert_eq!(c.player.time(), 0.); // Repeated snapshot does not restart or advance playback.
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
