//! Immutable game-owned character definitions. No model or renderer dependencies.
use crate::{ActorKind, CombatStats};
use nico_assets::character::CharacterCore;
use serde::{Deserialize, Serialize};
use std::{
    io::Read,
    path::Path,
    sync::{Arc, OnceLock},
};

pub type AssetResult<T> = Result<T, Box<dyn std::error::Error + Send + Sync>>;
pub const DEFAULT_LOGIC_ROOT: &str = "games/arena-arpg/assets/logic/characters";
pub const NAMES: [&str; 3] = ["hero", "grunt", "brute"];

/// Bounded UTF-8 authoring input, also used by the client's visual importer.
pub fn read_definition(path: &Path) -> AssetResult<String> {
    let mut text = String::new();
    std::fs::File::open(path)?
        .take(1024 * 1024 + 1)
        .read_to_string(&mut text)?;
    if text.len() > 1024 * 1024 {
        return Err(format!("{}: definition exceeds 1 MiB", path.display()).into());
    }
    Ok(text)
}

#[derive(Clone, Debug, PartialEq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct CharacterDefinition {
    pub schema_version: u32,
    pub core: CharacterCore,
    pub arena: ArenaRules,
}
#[derive(Clone, Debug, PartialEq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ArenaRules {
    pub stats: Stats,
    pub movement: Movement,
    pub attacks: Attacks,
    pub dodge: Dodge,
}
#[derive(Clone, Debug, PartialEq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Stats {
    pub max_health: u16,
}
#[derive(Clone, Debug, PartialEq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Movement {
    pub speed_mps: f64,
}
#[derive(Clone, Debug, PartialEq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Attacks {
    pub primary: Attack,
}
#[derive(Clone, Debug, PartialEq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Attack {
    pub damage: u16,
    pub range_m: f64,
    pub half_angle_degrees: f64,
    pub windup_ticks: u16,
    pub active_ticks: u16,
    pub recovery_ticks: u16,
}
#[derive(Clone, Debug, PartialEq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Dodge {
    pub speed_mps: f64,
    pub duration_ticks: u16,
    pub invulnerable_ticks: u16,
    pub cooldown_ticks: u16,
    pub buffer_ticks: u16,
}
impl CharacterDefinition {
    pub fn parse(text: &str) -> AssetResult<Self> {
        let value: Self = toml::from_str(text)?;
        value.validate()?;
        Ok(value)
    }
    pub fn validate(&self) -> AssetResult<()> {
        let check = |valid: bool, field: &str| -> AssetResult<()> {
            if valid {
                Ok(())
            } else {
                Err(format!("{}: invalid {field}", self.core.id).into())
            }
        };
        check(self.schema_version == 1, "schema_version (expected 1)")?;
        self.core.validate()?;
        check(self.arena.stats.max_health > 0, "stats.max_health")?;
        check(
            self.arena.movement.speed_mps.is_finite()
                && (0.0..=60.0).contains(&self.arena.movement.speed_mps),
            "movement.speed_mps",
        )?;
        check(
            self.core.collision.radius_m.is_finite()
                && (0.01..=3.0).contains(&self.core.collision.radius_m),
            "collision.radius_m",
        )?;
        let a = &self.arena.attacks.primary;
        check(
            a.damage > 0 && a.damage <= u16::MAX / 3,
            "attacks.primary.damage",
        )?;
        check(
            a.range_m.is_finite() && (0.01..=24.0).contains(&a.range_m),
            "attacks.primary.range_m",
        )?;
        check(
            a.half_angle_degrees.is_finite() && (0.1..=180.0).contains(&a.half_angle_degrees),
            "attacks.primary.half_angle_degrees",
        )?;
        check(
            a.windup_ticks > 0
                && a.active_ticks > 0
                && a.recovery_ticks > 0
                && u32::from(a.windup_ticks)
                    + u32::from(a.active_ticks)
                    + u32::from(a.recovery_ticks)
                    <= u32::from(u16::MAX),
            "attacks.primary phase ticks",
        )?;
        let d = &self.arena.dodge;
        check(
            d.speed_mps.is_finite() && (0.0..=60.0).contains(&d.speed_mps),
            "dodge.speed_mps",
        )?;
        check(
            d.duration_ticks > 0
                && d.invulnerable_ticks <= d.duration_ticks
                && d.cooldown_ticks >= d.duration_ticks
                && d.buffer_ticks <= d.cooldown_ticks,
            "dodge ticks",
        )?;
        Ok(())
    }
    pub fn combat_stats(&self) -> CombatStats {
        let a = &self.arena.attacks.primary;
        CombatStats {
            max_health: self.arena.stats.max_health,
            speed: self.arena.movement.speed_mps,
            windup: a.windup_ticks,
            active: a.active_ticks,
            recovery: a.recovery_ticks,
            range: a.range_m,
            damage: a.damage,
        }
    }
}

/// Fixed game roles resolve to compact catalog indices; shared by snapshots and actors.
/// Fields are private so a published catalog cannot bypass validation.
#[derive(Clone, Debug, PartialEq)]
pub struct CharacterCatalog {
    definitions: [CharacterDefinition; 3],
}
impl CharacterCatalog {
    pub fn new(definitions: [CharacterDefinition; 3]) -> AssetResult<Arc<Self>> {
        for (i, definition) in definitions.iter().enumerate() {
            definition.validate()?;
            if definitions[..i]
                .iter()
                .any(|d| d.core.id == definition.core.id)
            {
                return Err(format!("duplicate character id {}", definition.core.id).into());
            }
        }
        Ok(Arc::new(Self { definitions }))
    }
    pub fn load(root: &Path) -> AssetResult<Arc<Self>> {
        let mut values = Vec::new();
        for name in NAMES {
            let path = root.join(format!("{name}.char.toml"));
            let value = CharacterDefinition::parse(&read_definition(&path)?)
                .map_err(|e| format!("{}: {e}", path.display()))?;
            values.push(value);
        }
        Self::new(values.try_into().unwrap())
    }
    pub fn get(&self, kind: ActorKind) -> &CharacterDefinition {
        &self.definitions[match kind {
            ActorKind::Hero => 0,
            ActorKind::Grunt => 1,
            ActorKind::Brute => 2,
        }]
    }
    pub fn definitions(&self) -> &[CharacterDefinition; 3] {
        &self.definitions
    }
    /// Embedded copies make headless library tests independent of the working directory.
    /// Native hosts explicitly load files at startup instead.
    pub fn builtin() -> Arc<Self> {
        static CATALOG: OnceLock<Arc<CharacterCatalog>> = OnceLock::new();
        CATALOG
            .get_or_init(|| {
                Self::new([
                    CharacterDefinition::parse(include_str!(
                        "../../assets/logic/characters/hero.char.toml"
                    ))
                    .expect("hero definition"),
                    CharacterDefinition::parse(include_str!(
                        "../../assets/logic/characters/grunt.char.toml"
                    ))
                    .expect("grunt definition"),
                    CharacterDefinition::parse(include_str!(
                        "../../assets/logic/characters/brute.char.toml"
                    ))
                    .expect("brute definition"),
                ])
                .expect("character catalog")
            })
            .clone()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{Action, Arena, Level, RunState, TickInput, Vec2};
    #[test]
    fn rejects_unknown_versions_fields_invalid_timing_and_duplicate_ids() {
        let source = include_str!("../../assets/logic/characters/hero.char.toml");
        for invalid in [
            source.replace("schema_version = 1", "schema_version = 2"),
            source.replace("radius_m = 0.4", "radius_m = nan"),
            source.replace("active_ticks = 6", "active_ticks = 65535"),
            source.replace("radius_m = 0.4", "radius_m = 0.4\nraduis = 1"),
            source.replace("invulnerable_ticks = 12", "invulnerable_ticks = 19"),
        ] {
            assert!(CharacterDefinition::parse(&invalid).is_err());
        }
        let mut values = CharacterCatalog::builtin().definitions().clone();
        values[1].core.id = values[0].core.id.clone();
        assert!(CharacterCatalog::new(values).is_err());
    }
    #[test]
    fn disk_and_embedded_catalogs_match_and_share_immutable_content() {
        let catalog = CharacterCatalog::load(
            &Path::new(env!("CARGO_MANIFEST_DIR")).join("../assets/logic/characters"),
        )
        .unwrap();
        assert_eq!(catalog, CharacterCatalog::builtin());
        let arena = Arena::with_characters(Level::default(), catalog.clone()).unwrap();
        assert!(Arc::ptr_eq(
            &arena.snapshot().actors[0].characters,
            &catalog
        ));
        assert!(Arc::ptr_eq(
            &arena.clone().snapshot().actors[3].characters,
            &catalog
        ));
    }
    #[test]
    fn configured_health_movement_attack_dodge_and_restart_drive_simulation() {
        let mut values = CharacterCatalog::builtin().definitions().clone();
        let hero = &mut values[0];
        hero.arena.stats.max_health = 137;
        hero.arena.movement.speed_mps = 6.;
        hero.arena.attacks.primary.windup_ticks = 2;
        hero.arena.attacks.primary.active_ticks = 1;
        hero.arena.attacks.primary.recovery_ticks = 2;
        hero.arena.attacks.primary.damage = 17;
        hero.arena.dodge.duration_ticks = 3;
        hero.arena.dodge.invulnerable_ticks = 1;
        hero.arena.dodge.cooldown_ticks = 7;
        hero.arena.dodge.buffer_ticks = 2;
        hero.arena.dodge.speed_mps = 3.;
        let mut arena = Arena::with_characters(
            Level {
                spawns: [
                    Vec2::new(0., 0.),
                    Vec2::new(0., 1.5),
                    Vec2::new(-5., 5.),
                    Vec2::new(5., 5.),
                ],
            },
            CharacterCatalog::new(values).unwrap(),
        )
        .unwrap();
        assert_eq!(arena.snapshot().actors[0].health, 137);
        let mut input = TickInput::idle(1);
        input.movement = Vec2::new(1., 0.);
        arena.step(input);
        assert!((arena.snapshot().actors[0].position.x - 0.1).abs() < 1e-9);
        let mut input = TickInput::idle(1);
        input.attack_yaw = Some(0.);
        arena.step(input);
        arena.step(TickInput::idle(1));
        assert_eq!(arena.snapshot().actors[1].health, 60);
        arena.step(TickInput::idle(1));
        assert_eq!(arena.snapshot().actors[1].health, 43);
        arena.step(TickInput::idle(1));
        arena.step(TickInput::idle(1));
        assert_eq!(arena.snapshot().actors[0].action, Action::Idle);
        let x = arena.snapshot().actors[0].position.x;
        let mut input = TickInput::idle(1);
        input.dodge = Some(Vec2::new(1., 0.));
        arena.step(input);
        assert_eq!(arena.snapshot().actors[0].dodge_cooldown, 6);
        arena.step(TickInput::idle(1));
        arena.step(TickInput::idle(1));
        assert_eq!(arena.snapshot().actors[0].action, Action::Idle);
        assert!((arena.snapshot().actors[0].position.x - x - 0.15).abs() < 1e-9);
        let mut input = TickInput::idle(1);
        input.restart = true;
        arena.step(input);
        assert_eq!(arena.snapshot().actors[0].health, 137);
        assert_eq!(arena.snapshot().state, RunState::Playing);
    }
    #[test]
    fn configured_radii_control_spawn_validation_actor_contact_and_wall_stop() {
        let mut values = CharacterCatalog::builtin().definitions().clone();
        values[0].core.collision.radius_m = 0.8;
        values[1].core.collision.radius_m = 0.6;
        values[2].core.collision.radius_m = 0.9;
        let catalog = CharacterCatalog::new(values).unwrap();
        let mut level = Level::default();
        level.spawns[0] = Vec2::new(11.2, 0.);
        assert!(Arena::with_characters(level, catalog.clone()).is_err());
        let level = Level {
            spawns: [
                Vec2::new(0., 0.),
                Vec2::new(0., 1.8),
                Vec2::new(-5., 5.),
                Vec2::new(5., 5.),
            ],
        };
        assert!(Arena::with_characters(level, catalog.clone()).is_ok());
        let mut arena = Arena::with_characters(Level::default(), catalog).unwrap();
        arena.collision.sync(&arena.snapshot.actors);
        let wall = arena
            .collision
            .slide(0, arena.snapshot.actors[0].position, Vec2::new(100., 0.));
        assert!((wall.x - (crate::geometry::INNER_FACE - 0.8)).abs() < 0.01);
        let mut actors = arena.snapshot.actors.clone();
        actors[0].position = Vec2::new(0., 0.);
        actors[1].position = Vec2::new(0., 3.);
        arena.collision.sync(&actors);
        let contact = arena
            .collision
            .slide(0, actors[0].position, Vec2::new(0., 10.));
        assert!((contact.z - (3.0 - 2.0 * (0.8_f64 * 0.6).sqrt())).abs() < 0.01);
    }
}
