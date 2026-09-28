//! Arena scene components and validation shared by both native hosts.
use crate::{
    Vec2,
    open_world::{
        ObjectKind,
        content::{Obstacle, Spawn, ZoneDefinition},
        quest::QuestDefinition,
    },
};
use nico_assets::definition::DefinitionValidation;
use nico_scene::{
    ComponentRegistry, ComponentScope, SceneComponent, SceneDefinition,
    components::{AmbientLight, Camera, DirectionalLight, OrbitController, Transform},
};
use serde::{Deserialize, Serialize};
use std::{collections::BTreeMap, io, path::PathBuf};
fn error(e: impl ToString) -> io::Error {
    io::Error::other(e.to_string())
}
/// Client entry scene. Headless hosts follow its target without showing the splash.
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Splash {
    pub title: String,
    pub next_scene: PathBuf,
    pub duration_seconds: f64,
}
impl SceneComponent for Splash {
    fn validate(&self) -> io::Result<()> {
        if self.title.is_empty()
            || self.title.len() > 48
            || !self
                .title
                .chars()
                .all(|c| c.is_ascii_alphanumeric() || c == ' ')
            || !self.duration_seconds.is_finite()
            || !(0.0..=30.0).contains(&self.duration_seconds)
        {
            return Err(error("invalid splash title or duration"));
        }
        Ok(())
    }
    fn assets(&self) -> Vec<PathBuf> {
        vec![self.next_scene.clone()]
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct World {
    #[serde(default)]
    pub mode: WorldMode,
    pub rules: PathBuf,
    pub characters: PathBuf,
    pub item: PathBuf,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum WorldMode {
    #[default]
    Multiplayer,
    Arena,
}
impl SceneComponent for World {
    fn validate(&self) -> io::Result<()> {
        Ok(())
    }
    fn assets(&self) -> Vec<PathBuf> {
        vec![
            self.rules.clone(),
            self.characters.clone(),
            self.item.clone(),
        ]
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Environment {
    pub library: PathBuf,
    pub characters: PathBuf,
}
impl SceneComponent for Environment {
    fn validate(&self) -> io::Result<()> {
        Ok(())
    }
    fn assets(&self) -> Vec<PathBuf> {
        vec![self.library.clone(), self.characters.clone()]
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct BoxCollider {
    pub size: [f64; 3],
    pub color: [f32; 4],
}
impl SceneComponent for BoxCollider {
    fn validate(&self) -> io::Result<()> {
        if self
            .size
            .iter()
            .all(|v| v.is_finite() && *v > 0. && *v <= 32.)
            && self
                .color
                .iter()
                .all(|v| v.is_finite() && (0.0..=1.).contains(v))
        {
            Ok(())
        } else {
            Err(error("invalid box collider"))
        }
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct MonsterSpawn {
    pub kind: ObjectKind,
}
impl SceneComponent for MonsterSpawn {
    fn validate(&self) -> io::Result<()> {
        if matches!(self.kind, ObjectKind::Grunt | ObjectKind::Brute) {
            Ok(())
        } else {
            Err(error("invalid monster kind"))
        }
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct PlayerSpawn {}
impl SceneComponent for PlayerSpawn {
    fn validate(&self) -> io::Result<()> {
        Ok(())
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Quest {
    pub warden: String,
    pub camp: String,
    pub camp_radius_m: f64,
}
impl SceneComponent for Quest {
    fn validate(&self) -> io::Result<()> {
        if self.camp_radius_m.is_finite() && (1.0..=24.).contains(&self.camp_radius_m) {
            Ok(())
        } else {
            Err(error("invalid camp radius"))
        }
    }
    fn entity_references(&self) -> Vec<String> {
        vec![self.warden.clone(), self.camp.clone()]
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Scenery {
    pub model: String,
    pub height_m: Option<f32>,
    #[serde(default)]
    pub autumn: bool,
}
impl SceneComponent for Scenery {
    fn validate(&self) -> io::Result<()> {
        if !self.model.is_empty()
            && self
                .height_m
                .is_none_or(|h| h.is_finite() && (0.05..=20.).contains(&h))
        {
            Ok(())
        } else {
            Err(error("invalid scenery"))
        }
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct FollowPlayer {
    pub spawn: String,
    pub offset: [f32; 3],
}
impl SceneComponent for FollowPlayer {
    fn validate(&self) -> io::Result<()> {
        if self.offset.iter().all(|v| v.is_finite()) {
            Ok(())
        } else {
            Err(error("invalid follow offset"))
        }
    }
    fn entity_references(&self) -> Vec<String> {
        vec![self.spawn.clone()]
    }
}
#[derive(Clone, Debug, Serialize, Deserialize, nico_assets::definition::Definition)]
#[serde(deny_unknown_fields)]
pub struct WorldRulesDefinition {
    pub schema_version: u32,
    pub id: String,
    pub half_extent_m: f64,
}
impl DefinitionValidation for WorldRulesDefinition {
    type Error = &'static str;
    fn validate(&self) -> Result<(), Self::Error> {
        if self.schema_version == 1
            && !self.id.is_empty()
            && self.id.len() <= 64
            && self.half_extent_m.is_finite()
            && (1.0..=crate::open_world::ZONE_LIMIT).contains(&self.half_extent_m)
        {
            Ok(())
        } else {
            Err("invalid world rules")
        }
    }
}
pub fn registry() -> io::Result<ComponentRegistry> {
    let mut r = ComponentRegistry::default();
    nico_scene::components::register(&mut r)?;
    use ComponentScope::{Client, Shared};
    r.register::<Splash>("arena.splash", Client)?;
    r.register::<World>("arena.world", Shared)?;
    r.register::<Environment>("arena.environment", Client)?;
    r.register::<BoxCollider>("arena.box_collider", Shared)?;
    r.register::<MonsterSpawn>("arena.monster_spawn", Shared)?;
    r.register::<PlayerSpawn>("arena.player_spawn", Shared)?;
    r.register::<Quest>("arena.quest", Shared)?;
    r.register::<Scenery>("arena.scenery", Client)?;
    r.register::<FollowPlayer>("arena.follow_player", Client)?;
    Ok(r)
}
pub fn single<T: serde::de::DeserializeOwned>(scene: &SceneDefinition, key: &str) -> io::Result<T> {
    let values = scene
        .entities
        .iter()
        .filter_map(|e| e.component::<T>(key).transpose())
        .collect::<io::Result<Vec<_>>>()?;
    if values.len() != 1 {
        return Err(error(format!("scene requires exactly one {key}")));
    }
    Ok(values.into_iter().next().unwrap())
}
/// Cross-component rules are game policy; the engine registry checks each component first.
pub fn validate(scene: &SceneDefinition) -> io::Result<()> {
    single::<World>(scene, "arena.world")?;
    single::<Environment>(scene, "arena.environment")?;
    single::<PlayerSpawn>(scene, "arena.player_spawn")?;
    let camera = single::<Camera>(scene, "nico.camera")?;
    if !camera.active {
        return Err(error("the scene camera must be active"));
    }
    single::<DirectionalLight>(scene, "nico.directional_light")?;
    single::<AmbientLight>(scene, "nico.ambient_light")?;
    let mut camera_count = 0;
    for e in &scene.entities {
        if [
            "arena.world",
            "arena.box_collider",
            "arena.player_spawn",
            "arena.monster_spawn",
            "arena.quest",
        ]
        .iter()
        .any(|key| e.components.contains_key(*key))
            && e.scope != ComponentScope::Shared
        {
            return Err(error("authoritative scene entities must use shared scope"));
        }
        if e.components.contains_key("nico.camera") {
            camera_count += 1;
            e.component::<OrbitController>("nico.orbit")?
                .ok_or_else(|| error("camera requires orbit controller"))?;
            let follow = e
                .component::<FollowPlayer>("arena.follow_player")?
                .ok_or_else(|| error("camera requires follow target"))?;
            if !scene.entities.iter().any(|target| {
                target.id == follow.spawn && target.components.contains_key("arena.player_spawn")
            }) {
                return Err(error("camera target must identify the local player spawn"));
            }
        } else if e.components.contains_key("nico.orbit")
            || e.components.contains_key("arena.follow_player")
        {
            return Err(error("controller requires a camera on the same entity"));
        }
        if let Some(quest) = e.component::<Quest>("arena.quest")? {
            for id in [&quest.warden, &quest.camp] {
                let marker = scene
                    .entities
                    .iter()
                    .find(|target| &target.id == id)
                    .ok_or_else(|| error("missing quest marker"))?;
                let transform = marker
                    .component::<Transform>("nico.transform")?
                    .ok_or_else(|| error("quest marker requires transform"))?;
                if marker.scope != ComponentScope::Shared
                    || transform.position[1] != 0.
                    || transform.rotation_radians != [0.; 3]
                    || transform.scale != 1.
                {
                    return Err(error("quest markers require shared planar unit transforms"));
                }
            }
        }
        let placement = [
            "arena.box_collider",
            "arena.monster_spawn",
            "arena.player_spawn",
            "arena.scenery",
        ]
        .iter()
        .any(|key| e.components.contains_key(*key));
        if placement {
            let t = e
                .component::<Transform>("nico.transform")?
                .ok_or_else(|| error(format!("{} requires transform", e.id)))?;
            if (e.components.contains_key("arena.player_spawn")
                || e.components.contains_key("arena.monster_spawn"))
                && (t.position[1] != 0. || t.rotation_radians != [0.; 3] || t.scale != 1.)
            {
                return Err(error("actor spawns require planar unit transforms"));
            }
            if e.components.contains_key("arena.box_collider")
                && (t.rotation_radians != [0.; 3] || t.scale != 1.)
            {
                return Err(error(
                    "collider placements require axis-aligned unit transforms",
                ));
            }
            if e.components.contains_key("arena.scenery")
                && (t.rotation_radians[0] != 0. || t.rotation_radians[2] != 0. || t.scale != 1.)
            {
                return Err(error("scenery uses Y rotation and height_m"));
            }
        }
    }
    if camera_count != 1 {
        return Err(error("scene requires one active camera"));
    }
    Ok(())
}
pub fn world_definition(
    scene: &SceneDefinition,
    rules: &WorldRulesDefinition,
) -> io::Result<ZoneDefinition> {
    let mut zone = ZoneDefinition {
        id: rules.id.clone(),
        half_extent_m: rules.half_extent_m,
        ..Default::default()
    };
    let transforms = scene
        .entities
        .iter()
        .filter_map(|e| {
            e.component::<Transform>("nico.transform")
                .transpose()
                .map(|t| t.map(|t| (e.id.clone(), t)))
        })
        .collect::<io::Result<BTreeMap<_, _>>>()?;
    for e in &scene.entities {
        if e.components.contains_key("arena.player_spawn") {
            let t = &transforms[&e.id];
            zone.settlement = Vec2::new(t.position[0], t.position[2]);
        }
        if let Some(c) = e.component::<BoxCollider>("arena.box_collider")? {
            zone.obstacles.push(Obstacle {
                id: e.id.clone(),
                center: transforms[&e.id].position,
                size: c.size,
                color: c.color,
            });
        }
        if let Some(s) = e.component::<MonsterSpawn>("arena.monster_spawn")? {
            let t = &transforms[&e.id];
            zone.monsters.push(Spawn {
                kind: s.kind,
                position: Vec2::new(t.position[0], t.position[2]),
            });
        }
        if let Some(q) = e.component::<Quest>("arena.quest")? {
            if zone.quest.is_some() {
                return Err(error("one quest is supported"));
            }
            let position = |id: &str| {
                transforms
                    .get(id)
                    .map(|t| Vec2::new(t.position[0], t.position[2]))
                    .ok_or_else(|| error("quest marker requires transform"))
            };
            zone.quest = Some(QuestDefinition {
                warden: position(&q.warden)?,
                camp: position(&q.camp)?,
                camp_radius_m: q.camp_radius_m,
            });
        }
    }
    if single::<World>(scene, "arena.world")?.mode == WorldMode::Multiplayer {
        zone.validate().map_err(error)?;
    } else {
        arena_level(&zone)?;
    }
    Ok(zone)
}

pub fn arena_level(zone: &ZoneDefinition) -> io::Result<crate::Level> {
    if zone.half_extent_m != crate::geometry::HALF_EXTENT
        || zone.monsters.len() != 3
        || !zone.obstacles.is_empty()
        || zone.quest.is_some()
        || zone
            .monsters
            .iter()
            .map(|spawn| spawn.kind)
            .collect::<Vec<_>>()
            != [ObjectKind::Grunt, ObjectKind::Grunt, ObjectKind::Brute]
    {
        return Err(error(
            "solo arena requires its fixed bounds and three spawns",
        ));
    }
    let level = crate::Level {
        spawns: [
            zone.settlement,
            zone.monsters[0].position,
            zone.monsters[1].position,
            zone.monsters[2].position,
        ],
    };
    level
        .validate()
        .map_err(|e| error(format!("invalid arena spawns: {e:?}")))?;
    Ok(level)
}

#[derive(Deserialize)]
#[serde(deny_unknown_fields)]
struct SceneryLibrary {
    schema_version: u32,
    zone: String,
    models: BTreeMap<String, String>,
}
/// Check library names and source files without importing presentation assets on the server.
pub fn validate_library(
    project: &nico_scene::Project,
    scene: &SceneDefinition,
    zone: &ZoneDefinition,
) -> io::Result<()> {
    let binding: Environment = single(scene, "arena.environment")?;
    let path = project.resolve_asset(&binding.library)?;
    let library: SceneryLibrary =
        toml::from_str(&nico_assets::definition::read_definition(&path).map_err(error)?)
            .map_err(error)?;
    if library.schema_version != 1 || library.models.is_empty() || library.models.len() > 16 {
        return Err(error("invalid scenery library"));
    }
    if single::<World>(scene, "arena.world")?.mode == WorldMode::Multiplayer
        && library.zone != zone.id
    {
        return Err(error(
            "scene rules and scenery library identify different worlds",
        ));
    }
    for asset in library.models.values() {
        let full = nico_assets::character::asset_path(path.parent().unwrap(), asset)
            .map_err(error)?
            .canonicalize()?;
        if !full.starts_with(project.root().join("assets")) {
            return Err(error("scenery asset escapes project"));
        }
    }
    for e in &scene.entities {
        if let Some(scenery) = e.component::<Scenery>("arena.scenery")? {
            if !library.models.contains_key(&scenery.model) {
                return Err(error(format!(
                    "{}: unknown scenery model {}",
                    e.id, scenery.model
                )));
            }
            if !e.components.contains_key("arena.box_collider") && scenery.height_m.is_none() {
                return Err(error("decoration requires height_m"));
            }
        }
    }
    Ok(())
}
