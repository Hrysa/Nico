//! TOML authoring schema; names are resolved once before playback begins.
use super::*;
use arena_arpg_shared::characters::{CharacterCatalog, read_definition};
pub use nico_assets::character::{PoseOverride, Socket, VisualCore, asset_path};
use serde::{Deserialize, Serialize};
use std::collections::BTreeMap;

pub const DEFAULT_VISUAL_ROOT: &str = "games/arena-arpg/assets/presentation/characters";
pub const MOTIONS: [&str; 5] = ["idle", "run", "attack", "dodge", "death"];

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct VisualDefinition {
    pub schema_version: u32,
    pub core: VisualCore,
    pub arena: ArenaVisualRules,
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ArenaVisualRules {
    pub procedural: Procedural,
    pub weapon: Option<Weapon>,
    #[serde(default)]
    pub animations: BTreeMap<String, ActionAnimationBinding>,
}
pub trait SocketTransform {
    fn transform(&self) -> Transform;
}
impl SocketTransform for Socket {
    fn transform(&self) -> Transform {
        Transform {
            translation: self.translation_model,
            rotation: Quat::from_array(self.rotation_xyzw).normalize().to_array(),
            ..Default::default()
        }
    }
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Weapon {
    pub socket: String,
    pub tip_m: f32,
    pub parts: Vec<WeaponPart>,
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct WeaponPart {
    pub size_m: [f32; 3],
    pub center_m: [f32; 3],
    pub color_rgba: [u8; 4],
}
#[derive(Clone, Copy, Debug, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Playback {
    Loop,
    Once,
}
impl Playback {
    pub fn mode(self) -> PlayMode {
        match self {
            Self::Loop => PlayMode::Loop,
            Self::Once => PlayMode::Once,
        }
    }
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ActionAnimationBinding {
    pub animation: String,
    pub playback: Playback,
    pub speed: f64,
    pub blend_seconds: f64,
    pub contact_seconds: Option<f64>,
    pub authored_speed_mps: Option<f64>,
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Procedural {
    pub parts: ProceduralParts,
    pub body_scale: f32,
    pub torso_scale: f32,
    pub weapon_scale: f32,
    pub color: [f32; 4],
    pub head_color: [f32; 4],
    pub horns: bool,
    pub walk_radians_per_tick: f32,
    pub walk_amplitude_m: f32,
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Primitive {
    pub size_m: [f32; 3],
    pub position_m: [f32; 3],
}
#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ProceduralParts {
    pub body: Primitive,
    pub head: Primitive,
    pub leg: Primitive,
    pub arm: Primitive,
    pub weapon: Primitive,
    pub horn: Primitive,
}
impl ProceduralParts {
    fn all(&self) -> [&Primitive; 6] {
        [
            &self.body,
            &self.head,
            &self.leg,
            &self.arm,
            &self.weapon,
            &self.horn,
        ]
    }
}
impl VisualDefinition {
    pub fn parse(text: &str) -> Result<Self> {
        let value: Self = toml::from_str(text)?;
        value.validate()?;
        Ok(value)
    }
    pub fn load(path: &Path, character_id: &str) -> Result<Self> {
        let value =
            Self::parse(&read_definition(path)?).map_err(|e| format!("{}: {e}", path.display()))?;
        if value.core.character != character_id {
            return Err(format!(
                "{}: character {} does not match {character_id}",
                path.display(),
                value.core.character
            )
            .into());
        }
        Ok(value)
    }
    #[cfg(test)]
    pub fn builtin(index: usize) -> Self {
        Self::parse(
            [
                include_str!("../../../assets/presentation/characters/hero.char-vis.toml"),
                include_str!("../../../assets/presentation/characters/grunt.char-vis.toml"),
                include_str!("../../../assets/presentation/characters/brute.char-vis.toml"),
            ][index],
        )
        .expect("built-in visual definition")
    }
    pub fn profile(&self, name: &str) -> Result<HumanoidProfile> {
        use nico_animation::humanoid::{Bone, BoneBinding};
        Ok(match name {
            "mixamo" => HumanoidProfile::mixamo(),
            "rpg" => HumanoidProfile::rpg(),
            name => {
                let profile = self
                    .core
                    .profiles
                    .get(name)
                    .ok_or_else(|| format!("profiles: unknown {name}"))?;
                HumanoidProfile {
                    bones: Bone::ALL
                        .into_iter()
                        .zip(profile.bones.iter().map(|n| BoneBinding::from(n.as_str())))
                        .collect(),
                    motion_root: Some(profile.motion_root.as_str().into()),
                    ..Default::default()
                }
            }
        })
    }
    pub fn validate(&self) -> Result<()> {
        let check = |ok: bool, field: &str| -> Result<()> {
            if ok {
                Ok(())
            } else {
                Err(format!("{}: invalid {field}", self.core.character).into())
            }
        };
        check(self.schema_version == 1, "schema_version (expected 1)")?;
        let positive = |x: f32| x.is_finite() && x > 0. && x <= 100.;
        let p = &self.arena.procedural;
        for part in p.parts.all() {
            check(
                part.size_m.into_iter().all(positive)
                    && part
                        .position_m
                        .into_iter()
                        .all(|v| v.is_finite() && v.abs() <= 100.),
                "procedural.parts",
            )?;
        }
        check(
            [p.body_scale, p.torso_scale, p.weapon_scale]
                .into_iter()
                .all(positive)
                && p.color
                    .into_iter()
                    .chain(p.head_color)
                    .all(|x| x.is_finite() && (0.0..=1.0).contains(&x))
                && positive(p.walk_radians_per_tick)
                && p.walk_amplitude_m.is_finite()
                && (0.0..=10.0).contains(&p.walk_amplitude_m),
            "procedural",
        )?;
        self.core.validate()?;
        if self.core.model.is_some() {
            check(
                self.arena.animations.len() == MOTIONS.len()
                    && MOTIONS
                        .iter()
                        .all(|n| self.arena.animations.contains_key(*n)),
                "animations (five semantic motions required)",
            )?;
        } else {
            check(
                self.arena.animations.is_empty()
                    && self.core.animations.is_empty()
                    && self.core.profiles.is_empty()
                    && self.core.pose.is_empty()
                    && self.core.sockets.is_empty()
                    && self.arena.weapon.is_none(),
                "procedural definition contains skinned content",
            )?;
        }
        for (name, a) in &self.arena.animations {
            check(
                self.core.animations.contains_key(&a.animation),
                "arena.animations reference",
            )?;
            check(
                a.speed.is_finite()
                    && (0.01..=10.).contains(&a.speed)
                    && a.blend_seconds.is_finite()
                    && (0.0..=5.0).contains(&a.blend_seconds),
                "animation playback",
            )?;
            check(
                a.contact_seconds
                    .is_none_or(|x| name == "attack" && x.is_finite() && x > 0.)
                    && (name != "attack" || a.contact_seconds.is_some()),
                "animation contact_seconds",
            )?;
            check(
                a.authored_speed_mps
                    .is_none_or(|x| name == "run" && x.is_finite() && x > 0. && x <= 60.),
                "animation authored_speed_mps",
            )?;
            check(
                matches!(
                    (name.as_str(), a.playback),
                    ("idle" | "run", Playback::Loop)
                        | ("attack" | "dodge" | "death", Playback::Once)
                ),
                "animation playback mode",
            )?;
            check(
                !matches!(name.as_str(), "attack" | "dodge") || a.speed == 1.,
                "action animation speed (timing is controlled by simulation)",
            )?;
        }
        if let Some(w) = &self.arena.weapon {
            check(
                self.core.sockets.contains_key(&w.socket)
                    && w.tip_m.is_finite()
                    && (0.1..=4.).contains(&w.tip_m)
                    && !w.parts.is_empty()
                    && w.parts.len() <= 16,
                "weapon",
            )?;
            let tip = w
                .parts
                .iter()
                .map(|p| p.center_m[2] + p.size_m[2] * 0.5)
                .fold(f32::NEG_INFINITY, f32::max);
            check(
                (tip - w.tip_m).abs() < 0.0001,
                "weapon.tip_m (must match forward geometry extent)",
            )?;
            for p in &w.parts {
                check(
                    p.size_m.into_iter().all(positive)
                        && p.center_m
                            .into_iter()
                            .all(|v| v.is_finite() && v.abs() <= 10.),
                    "weapon.parts",
                )?;
            }
        }
        Ok(())
    }
}
pub fn load_visuals(root: &Path, logic: &CharacterCatalog) -> Result<[VisualDefinition; 3]> {
    let mut definitions = Vec::new();
    for (name, character) in arena_arpg_shared::characters::NAMES
        .into_iter()
        .zip(logic.definitions())
    {
        let definition = VisualDefinition::load(
            &root.join(format!("{name}.char-vis.toml")),
            &character.core.id,
        )?;
        definitions.push(definition);
    }
    Ok(definitions.try_into().unwrap())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn root() -> std::path::PathBuf {
        Path::new(env!("CARGO_MANIFEST_DIR")).join("../assets/presentation/characters")
    }
    #[test]
    fn all_current_characters_load_with_matching_ids_and_resolved_content() {
        let definitions = load_visuals(&root(), &CharacterCatalog::builtin()).unwrap();
        assert_eq!(definitions[0].arena.animations.len(), MOTIONS.len());
        assert_eq!(definitions[0].core.pose.len(), 15);
        assert!(definitions[1].core.model.is_some() && definitions[2].core.model.is_some());
        let assets =
            CharacterAssets::load_definition(definitions[0].clone(), &root(), None).unwrap();
        assert!((assets.scale * 100.).is_finite());
        assert_eq!(assets.set.clips()[2].name(), "Sword_Regular_C");
    }
    #[test]
    fn bestiary_models_retarget_all_motions_with_independent_players_and_embedded_weapons() {
        for index in [1, 2] {
            let assets =
                CharacterAssets::load_definition(VisualDefinition::builtin(index), &root(), None)
                    .unwrap();
            assert!(assets.weapon.is_none());
            assert_eq!(assets.draw_count(), assets.visual.primitive_count());
            let mut first = AnimationPlayer::new(assets.set.clone());
            let second = AnimationPlayer::new(assets.set.clone());
            for clip in 0..MOTIONS.len() {
                first.play(clip, PlayMode::Once, Duration::ZERO).unwrap();
                for fraction in [0., 0.25, 0.5, 0.75, 1.] {
                    first.seek(first.duration() * fraction).unwrap();
                    let globals = first.pose().globals().unwrap();
                    assert!(globals.iter().all(|m| m.is_finite()));
                    let draws = assets.visual.meshes(&globals).unwrap();
                    assert_eq!(draws.len(), assets.draw_count());
                    assert!(draws.iter().all(|d| d.skin_palette.is_some()));
                    assert!(assets.visual.bounds(&globals, Mat4::IDENTITY).is_ok());
                }
            }
            assert_eq!(second.time(), 0.);
            assert!(first.time() > 0.);
        }
    }
    #[test]
    fn arena_bindings_resolve_arbitrary_library_names_before_playback() {
        let mut definition = VisualDefinition::builtin(0);
        let clip = definition.core.animations.remove("attack").unwrap();
        definition
            .core
            .animations
            .insert("sword_slash".into(), clip);
        definition
            .arena
            .animations
            .get_mut("attack")
            .unwrap()
            .animation = "sword_slash".into();
        // An unused library clip is valid; only the five arena bindings are compiled.
        let extra = definition.core.animations["idle"].clone();
        definition.core.animations.insert("celebrate".into(), extra);
        let assets = CharacterAssets::load_definition(definition.clone(), &root(), None).unwrap();
        assert_eq!(assets.set.clips().len(), MOTIONS.len());
        assert_eq!(assets.set.clips()[2].name(), "Sword_Regular_C");
        definition
            .arena
            .animations
            .get_mut("attack")
            .unwrap()
            .animation = "missing".into();
        assert!(
            definition
                .validate()
                .unwrap_err()
                .to_string()
                .contains("arena.animations")
        );
    }
    #[test]
    fn arena_requires_bindings_but_core_does_not() {
        let mut definition = VisualDefinition::builtin(0);
        definition.arena.animations.clear();
        definition.core.validate().unwrap();
        assert!(definition.validate().is_err());
        let source = include_str!("../../../assets/presentation/characters/hero.char-vis.toml");
        assert!(
            VisualDefinition::parse(&source.replace("[arena.animations.attack]", "[core.dodge]"))
                .is_err()
        );
    }
    #[test]
    fn malformed_visuals_fail_before_playback_with_field_context() {
        let source = include_str!("../../../assets/presentation/characters/hero.char-vis.toml");
        for invalid in [
            source.replace("schema_version = 1", "schema_version = 2"),
            source.replace("target_height_m = 1.8", "target_height_m = nan"),
            source.replace("floor_offset_m = 0.0", "floor_offest_m = 0.0"),
            source.replace("socket = \"sword\"", "socket = \"missing\""),
            source.replace("contact_seconds = 0.686666667", "contact_seconds = -1.0"),
            source.replace("hero/model.glb", "../model.glb"),
            source.replace("tip_m = 1.10", "tip_m = 2.0"),
        ] {
            assert!(VisualDefinition::parse(&invalid).is_err());
        }
        assert!(VisualDefinition::load(&root().join("hero.char-vis.toml"), "arena.grunt").is_err());
        for path in [
            "../model.glb",
            "C:/model.glb",
            "/model.glb",
            "https://example/model.glb",
        ] {
            assert!(asset_path(&root(), path).is_err());
        }
    }
    #[test]
    fn source_bone_and_clip_errors_are_rejected_during_resolution() {
        let mut definition = VisualDefinition::builtin(0);
        definition.core.sockets.get_mut("sword").unwrap().bone = "missing-bone".into();
        assert!(
            CharacterAssets::load_definition(definition, &root(), None)
                .err()
                .unwrap()
                .to_string()
                .contains("missing-bone")
        );
        let mut definition = VisualDefinition::builtin(0);
        definition.core.animations.get_mut("idle").unwrap().clip = "missing-clip".into();
        assert!(
            CharacterAssets::load_definition(definition, &root(), None)
                .err()
                .unwrap()
                .to_string()
                .contains("missing-clip")
        );
    }
    #[test]
    fn visual_height_floor_socket_and_contact_settings_reach_runtime_assets() {
        let mut definition = VisualDefinition::builtin(0);
        definition.core.model.as_mut().unwrap().floor_offset_m = 0.25;
        definition.core.model.as_mut().unwrap().target_height_m = 2.0;
        definition
            .arena
            .animations
            .get_mut("attack")
            .unwrap()
            .contact_seconds = Some(0.6);
        definition
            .core
            .sockets
            .get_mut("sword")
            .unwrap()
            .translation_model[0] = 2.0;
        let assets = CharacterAssets::load_definition(definition, &root(), None).unwrap();
        let mut character = Character::new(assets);
        let state = arena_arpg_shared::Arena::default().snapshot().clone();
        let meshes = character.render(&state, Duration::ZERO, None).unwrap();
        assert!(meshes[0].position[1] > 0.2);
        assert!((character.controller.settings.contact - 0.3).abs() < 1e-6);
        assert_eq!(character.state()["attack_contact_seconds"], 0.6);
    }
}
