//! Server-owned ECS world. Hosts call input/step only at runtime boundaries.
//! Snapshots are owned values; no entity or physics handles cross this boundary.
pub mod content;
pub mod persistence;
pub mod protocol;
pub mod quest;
#[cfg(all(feature = "tools", feature = "network"))]
pub mod runtime;
#[cfg(feature = "network")]
pub mod server;
use crate::{ActorKind, Vec2, characters::CharacterCatalog};
use nico_ecs::{Entity, World};
use nico_physics::{BodyDesc, BodyId, BodyKind, CharacterSettings, PhysicsWorld, Pose, Shape};
use serde::{Deserialize, Serialize};
use std::{
    collections::{BTreeMap, BTreeSet, VecDeque},
    sync::Arc,
};

pub type ObjectId = u64;
pub const MAX_PLAYERS: usize = 16;
pub const MAX_OBJECTS: usize = 256;
pub const INPUT_QUEUE: usize = 64;
pub const VIEW_RADIUS: f64 = 32.;
pub const RESPAWN_TICKS: u64 = 180;
pub const MONSTER_RESPAWN_TICKS: u64 = 900;
pub const ZONE_LIMIT: f64 = 64.;
pub const ITEM_SWORD: &str = "iron_sword";

#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(deny_unknown_fields)]
pub struct CharacterRecord {
    pub schema_version: u32,
    pub name: String,
    pub position: Vec2,
    pub health: u16,
    pub experience: u64,
    pub inventory: Vec<String>,
    pub equipped: Option<String>,
    #[serde(default)]
    pub quest: quest::QuestProgress,
}
impl CharacterRecord {
    pub fn new(name: String, health: u16) -> Self {
        Self {
            schema_version: 1,
            name,
            position: Vec2::new(0., -20.),
            health,
            experience: 0,
            inventory: vec![],
            equipped: None,
            quest: quest::QuestProgress::default(),
        }
    }
    pub fn validate(&self) -> Result<(), &'static str> {
        if self.schema_version != 1
            || self.name.is_empty()
            || self.name.len() > 32
            || !self
                .name
                .bytes()
                .all(|c| c.is_ascii_lowercase() || c.is_ascii_digit() || c == b'_')
        {
            return Err("invalid_character_identity");
        }
        if !self.quest.valid()
            || !valid_position(self.position)
            || self.inventory.len() > 32
            || self.inventory.iter().any(|i| i != ITEM_SWORD)
            || self
                .equipped
                .as_ref()
                .is_some_and(|i| !self.inventory.contains(i))
        {
            return Err("invalid_character_record");
        }
        Ok(())
    }
}
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct PlayerInput {
    pub sequence: u64,
    pub movement: Vec2,
    pub attack_yaw: Option<f64>,
    pub dodge: Option<Vec2>,
    pub pickup: Option<ObjectId>,
    pub equip: Option<String>,
    pub respawn: bool,
    #[serde(default)]
    pub talk: bool,
}
impl PlayerInput {
    pub fn valid(&self) -> bool {
        self.sequence > 0
            && direction(self.movement)
            && self
                .attack_yaw
                .is_none_or(|v| v.is_finite() && v.abs() <= std::f64::consts::PI)
            && self.dodge.is_none_or(|v| direction(v) && v.dot(v) > 0.)
            && self.equip.as_ref().is_none_or(|v| v == ITEM_SWORD)
    }
}
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum WorldAction {
    Idle,
    Attack { id: u64, elapsed: u16 },
    Dodge { elapsed: u16, direction: Vec2 },
    Dead { respawn_tick: u64 },
}
#[derive(Clone, Copy, Debug, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum ObjectKind {
    Player,
    Grunt,
    Brute,
    Loot,
}
impl ObjectKind {
    pub fn character(self) -> ActorKind {
        match self {
            Self::Player => ActorKind::Hero,
            Self::Brute => ActorKind::Brute,
            _ => ActorKind::Grunt,
        }
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ObjectSnapshot {
    pub id: ObjectId,
    pub kind: ObjectKind,
    pub position: Vec2,
    pub facing: Vec2,
    pub health: u16,
    pub max_health: u16,
    pub action: WorldAction,
    pub dodge_cooldown: u16,
    pub name: Option<String>,
    pub equipped: Option<String>,
    pub item: Option<String>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct WorldSnapshot {
    pub tick: u64,
    pub player: ObjectId,
    pub acknowledged_input: u64,
    pub experience: u64,
    pub inventory: Vec<String>,
    pub quest: quest::QuestProgress,
    pub last_error: Option<String>,
    pub objects: Vec<ObjectSnapshot>,
}
#[derive(Clone, Copy)]
struct Identity {
    id: ObjectId,
    kind: ObjectKind,
}
#[derive(Clone, Copy)]
struct Position(Vec2);
#[derive(Clone)]
struct Combat {
    health: u16,
    facing: Vec2,
    action: WorldAction,
    cooldown: u16,
    hit: BTreeSet<ObjectId>,
}
struct Player {
    record: CharacterRecord,
    inputs: VecDeque<PlayerInput>,
    last_received: u64,
    acknowledged: u64,
    last_error: Option<String>,
}
struct Monster {
    home: Vec2,
}
struct Loot {
    item: String,
    expires: u64,
}

pub struct OpenWorld {
    pub zone: content::ZoneDefinition,
    pub item: content::ItemDefinition,
    entities: World,
    ids: BTreeMap<ObjectId, Entity>,
    next_id: ObjectId,
    tick: u64,
    characters: Arc<CharacterCatalog>,
    physics: PhysicsWorld,
    bodies: BTreeMap<ObjectId, BodyId>,
}
fn direction(v: Vec2) -> bool {
    v.finite() && v.dot(v) <= 1.00001
}
fn valid_position(v: Vec2) -> bool {
    v.finite() && v.x.abs() < ZONE_LIMIT && v.z.abs() < ZONE_LIMIT
}
fn distance(a: Vec2, b: Vec2) -> f64 {
    let d = a.sub(b);
    d.dot(d).sqrt()
}
impl OpenWorld {
    pub fn new(characters: Arc<CharacterCatalog>) -> Self {
        Self {
            zone: content::ZoneDefinition::default(),
            item: content::ItemDefinition::default(),
            entities: World::new(),
            ids: BTreeMap::new(),
            next_id: 1,
            tick: 0,
            characters,
            physics: PhysicsWorld::new([0.; 3], MAX_OBJECTS + 32)
                .expect("world physics configuration"),
            bodies: BTreeMap::new(),
        }
    }
    pub fn with_content(
        characters: Arc<CharacterCatalog>,
        zone: content::ZoneDefinition,
        item: content::ItemDefinition,
    ) -> Result<Self, &'static str> {
        zone.validate()?;
        item.validate()?;
        let mut world = Self::new(characters);
        for obstacle in &zone.obstacles {
            world
                .physics
                .insert(BodyDesc::new(
                    BodyKind::Fixed,
                    Shape::Cuboid {
                        half_extents: obstacle.size.map(|x| x / 2.),
                    },
                    Pose::at(obstacle.center),
                ))
                .map_err(|_| "invalid_world_collider")?;
        }
        let spawns = zone.monsters.clone();
        world.zone = zone;
        world.item = item;
        for spawn in spawns {
            world.spawn_monster(spawn.kind, spawn.position)?;
        }
        Ok(world)
    }
    pub fn tick(&self) -> u64 {
        self.tick
    }
    pub fn characters(&self) -> &Arc<CharacterCatalog> {
        &self.characters
    }
    fn allocate(&mut self) -> Result<ObjectId, &'static str> {
        if self.ids.len() >= MAX_OBJECTS {
            return Err("world_full");
        }
        let id = self.next_id;
        self.next_id = id.checked_add(1).ok_or("id_exhausted")?;
        Ok(id)
    }
    pub fn connect(&mut self, mut record: CharacterRecord) -> Result<ObjectId, &'static str> {
        record.validate()?;
        if self.entities.query::<&Player>().iter().count() >= MAX_PLAYERS {
            return Err("server_full");
        }
        if self
            .entities
            .query::<&Player>()
            .iter()
            .any(|p| p.record.name == record.name)
        {
            return Err("character_already_connected");
        }
        let max = self.characters.get(ActorKind::Hero).arena.stats.max_health;
        if record.health > max {
            return Err("invalid_character_health");
        }
        record.position = self
            .free_position(record.position, ObjectKind::Player)
            .ok_or("occupied_spawn")?;
        let id = self.allocate()?;
        let action = if record.health == 0 {
            WorldAction::Dead {
                respawn_tick: self.tick + RESPAWN_TICKS,
            }
        } else {
            WorldAction::Idle
        };
        let entity = self.entities.spawn((
            Identity {
                id,
                kind: ObjectKind::Player,
            },
            Position(record.position),
            Combat {
                health: record.health,
                facing: Vec2::new(0., 1.),
                action,
                cooldown: 0,
                hit: BTreeSet::new(),
            },
            Player {
                record,
                inputs: VecDeque::new(),
                last_received: 0,
                acknowledged: 0,
                last_error: None,
            },
        ));
        self.ids.insert(id, entity);
        self.sync_bodies();
        Ok(id)
    }
    fn free_position(&self, origin: Vec2, kind: ObjectKind) -> Option<Vec2> {
        let radius = self
            .characters
            .get(kind.character())
            .core
            .collision
            .radius_m;
        let objects = self.objects();
        for offset in 0..81 {
            let p = if offset == 0 {
                origin
            } else {
                Vec2::new(
                    origin.x + f64::from(offset % 9 - 4) * 1.2,
                    origin.z + f64::from(offset / 9 - 4) * 1.2,
                )
            };
            if p.x.abs() > self.zone.half_extent_m - radius
                || p.z.abs() > self.zone.half_extent_m - radius
                || self.zone.blocked(p, radius)
            {
                continue;
            }
            if objects.iter().all(|o| {
                o.health == 0
                    || distance(o.position, p)
                        >= radius
                            + self
                                .characters
                                .get(o.kind.character())
                                .core
                                .collision
                                .radius_m
                            + 0.01
            }) {
                return Some(p);
            }
        }
        None
    }
    pub fn record(&self, id: ObjectId) -> Result<CharacterRecord, &'static str> {
        let e = *self.ids.get(&id).ok_or("unknown_player")?;
        let mut record = self
            .entities
            .entities()
            .get::<&Player>(e)
            .map_err(|_| "not_player")?
            .record
            .clone();
        record.position = self.entities.entities().get::<&Position>(e).unwrap().0;
        record.health = self.entities.entities().get::<&Combat>(e).unwrap().health;
        Ok(record)
    }
    pub fn records(&self) -> Vec<(ObjectId, CharacterRecord)> {
        self.ids
            .keys()
            .filter_map(|&id| self.record(id).ok().map(|r| (id, r)))
            .collect()
    }
    pub fn disconnect(&mut self, id: ObjectId) -> Result<CharacterRecord, &'static str> {
        let record = self.record(id)?;
        self.remove(id);
        Ok(record)
    }
    fn remove(&mut self, id: ObjectId) {
        if let Some(body) = self.bodies.remove(&id) {
            self.physics.remove(body).expect("live body");
        }
        if let Some(entity) = self.ids.remove(&id) {
            self.entities.despawn(entity).expect("live entity");
        }
    }
    pub fn submit(&mut self, id: ObjectId, input: PlayerInput) -> Result<(), &'static str> {
        if !input.valid() {
            return Err("invalid_input");
        }
        let e = *self.ids.get(&id).ok_or("unknown_player")?;
        let mut p = self
            .entities
            .entities()
            .get::<&mut Player>(e)
            .map_err(|_| "not_player")?;
        if input.sequence <= p.last_received {
            return Err("stale_input");
        }
        if p.inputs.len() >= INPUT_QUEUE {
            return Err("input_queue_full");
        }
        p.last_received = input.sequence;
        p.inputs.push_back(input);
        Ok(())
    }
    pub fn spawn_monster(
        &mut self,
        kind: ObjectKind,
        position: Vec2,
    ) -> Result<ObjectId, &'static str> {
        if !matches!(kind, ObjectKind::Grunt | ObjectKind::Brute)
            || !valid_position(position)
            || position.x.abs() > self.zone.half_extent_m - 3.
            || position.z.abs() > self.zone.half_extent_m - 3.
            || self.zone.blocked(
                position,
                self.characters
                    .get(kind.character())
                    .core
                    .collision
                    .radius_m,
            )
        {
            return Err("invalid_spawn");
        }
        if self
            .objects()
            .iter()
            .any(|o| o.kind != ObjectKind::Loot && distance(o.position, position) < 2.)
        {
            return Err("occupied_spawn");
        }
        let id = self.allocate()?;
        let health = self.characters.get(kind.character()).arena.stats.max_health;
        let e = self.entities.spawn((
            Identity { id, kind },
            Position(position),
            Combat {
                health,
                facing: Vec2::new(0., -1.),
                action: WorldAction::Idle,
                cooldown: 0,
                hit: BTreeSet::new(),
            },
            Monster { home: position },
        ));
        self.ids.insert(id, e);
        self.sync_bodies();
        Ok(id)
    }
    fn spawn_loot(&mut self, position: Vec2) {
        if let Ok(id) = self.allocate() {
            let e = self.entities.spawn((
                Identity {
                    id,
                    kind: ObjectKind::Loot,
                },
                Position(position),
                Loot {
                    item: ITEM_SWORD.into(),
                    expires: self.tick + 3600,
                },
            ));
            self.ids.insert(id, e);
        }
    }
    pub fn objects(&self) -> Vec<ObjectSnapshot> {
        self.ids
            .iter()
            .map(|(&id, &e)| {
                let identity = self.entities.entities().get::<&Identity>(e).unwrap();
                debug_assert_eq!(id, identity.id);
                let position = self.entities.entities().get::<&Position>(e).unwrap().0;
                let combat = self.entities.entities().get::<&Combat>(e).ok();
                let player = self.entities.entities().get::<&Player>(e).ok();
                let loot = self.entities.entities().get::<&Loot>(e).ok();
                ObjectSnapshot {
                    id,
                    kind: identity.kind,
                    position,
                    facing: combat.as_ref().map_or(Vec2::default(), |c| c.facing),
                    health: combat.as_ref().map_or(0, |c| c.health),
                    max_health: if combat.is_some() {
                        self.characters
                            .get(identity.kind.character())
                            .arena
                            .stats
                            .max_health
                    } else {
                        0
                    },
                    action: combat
                        .as_ref()
                        .map_or(WorldAction::Idle, |c| c.action.clone()),
                    dodge_cooldown: combat.as_ref().map_or(0, |c| c.cooldown),
                    name: player.as_ref().map(|p| p.record.name.clone()),
                    equipped: player.as_ref().and_then(|p| p.record.equipped.clone()),
                    item: loot.as_ref().map(|l| l.item.clone()),
                }
            })
            .collect()
    }
    pub fn snapshot(&self, id: ObjectId) -> Result<WorldSnapshot, &'static str> {
        let e = *self.ids.get(&id).ok_or("unknown_player")?;
        let p = self
            .entities
            .entities()
            .get::<&Player>(e)
            .map_err(|_| "not_player")?;
        let position = self.entities.entities().get::<&Position>(e).unwrap().0;
        Ok(WorldSnapshot {
            tick: self.tick,
            player: id,
            acknowledged_input: p.acknowledged,
            experience: p.record.experience,
            inventory: p.record.inventory.clone(),
            quest: p.record.quest.clone(),
            last_error: p.last_error.clone(),
            objects: self
                .objects()
                .into_iter()
                .filter(|o| o.id == id || distance(o.position, position) <= VIEW_RADIUS)
                .collect(),
        })
    }
    fn sync_bodies(&mut self) {
        for o in self.objects() {
            if o.kind == ObjectKind::Loot {
                continue;
            }
            if o.health == 0 {
                if let Some(body) = self.bodies.remove(&o.id) {
                    self.physics.remove(body).unwrap();
                }
                continue;
            }
            let radius = self
                .characters
                .get(o.kind.character())
                .core
                .collision
                .radius_m;
            let pose = Pose::at([o.position.x, radius, o.position.z]);
            if let Some(&body) = self.bodies.get(&o.id) {
                self.physics.set_pose(body, pose).unwrap();
            } else {
                let body = self
                    .physics
                    .insert(BodyDesc::new(
                        BodyKind::Kinematic,
                        Shape::Ball { radius },
                        pose,
                    ))
                    .unwrap();
                self.bodies.insert(o.id, body);
            }
        }
    }
    /// Exactly one simulation tick. At most one queued input is consumed per player.
    /// Missing input means no movement; packet bursts cannot accelerate simulation.
    pub fn step(&mut self) {
        self.tick += 1;
        let expired: Vec<_> = self
            .entities
            .query::<(&Identity, &Loot)>()
            .iter()
            .filter(|(_, l)| l.expires <= self.tick)
            .map(|(i, _)| i.id)
            .collect();
        for id in expired {
            self.remove(id);
        }
        let before = self.objects();
        for o in &before {
            if o.kind == ObjectKind::Loot {
                continue;
            }
            let e = self.ids[&o.id];
            let input = if o.kind == ObjectKind::Player {
                let mut p = self.entities.entities().get::<&mut Player>(e).unwrap();
                let input = p.inputs.pop_front().unwrap_or_default();
                if input.sequence > 0 {
                    p.acknowledged = input.sequence;
                    p.last_error = None;
                }
                input
            } else {
                self.monster_input(o, &before)
            };
            self.advance_actor(o, &input);
            if o.kind == ObjectKind::Player {
                self.interact(o.id, &input);
            }
        }
        self.sync_bodies();
        self.resolve_hits();
        self.sync_bodies();
    }
    fn monster_input(&self, o: &ObjectSnapshot, objects: &[ObjectSnapshot]) -> PlayerInput {
        let mut input = PlayerInput::default();
        let e = self.ids[&o.id];
        let home = self.entities.entities().get::<&Monster>(e).unwrap().home;
        let target = objects
            .iter()
            .filter(|p| {
                p.kind == ObjectKind::Player && p.health > 0 && distance(p.position, home) <= 14.
            })
            .min_by(|a, b| {
                distance(a.position, o.position).total_cmp(&distance(b.position, o.position))
            });
        if let Some(target) = target {
            let delta = target.position.sub(o.position);
            let range = self
                .characters
                .get(o.kind.character())
                .arena
                .attacks
                .primary
                .range_m;
            if distance(target.position, o.position) <= range {
                input.attack_yaw = Some(delta.x.atan2(delta.z));
            } else {
                input.movement = delta.unit();
            }
        } else if distance(o.position, home) > 0.1 {
            input.movement = home.sub(o.position).unit();
        }
        input
    }
    fn advance_actor(&mut self, o: &ObjectSnapshot, input: &PlayerInput) {
        let e = self.ids[&o.id];
        let def = self.characters.get(o.kind.character());
        let mut c = (*self.entities.entities().get::<&Combat>(e).unwrap()).clone();
        let mut position = o.position;
        c.cooldown = c.cooldown.saturating_sub(1);
        if let WorldAction::Dead { respawn_tick } = c.action {
            if self.tick >= respawn_tick && (o.kind != ObjectKind::Player || input.respawn) {
                let home = if o.kind == ObjectKind::Player {
                    self.zone.settlement
                } else {
                    self.entities.entities().get::<&Monster>(e).unwrap().home
                };
                let Some(free) = self.free_position(home, o.kind) else {
                    return;
                };
                position = free;
                c.health = def.arena.stats.max_health;
                c.action = WorldAction::Idle;
                c.cooldown = 0;
            }
        } else {
            let attack = &def.arena.attacks.primary;
            let cancellable = matches!(c.action, WorldAction::Idle)
                | matches!(c.action,WorldAction::Attack{elapsed,..} if elapsed>=attack.windup_ticks+attack.active_ticks);
            if cancellable
                && c.cooldown == 0
                && let Some(direction) = input.dodge
            {
                c.action = WorldAction::Dodge {
                    elapsed: 0,
                    direction: direction.unit(),
                };
                c.cooldown = def.arena.dodge.cooldown_ticks;
                c.facing = direction.unit();
            } else if matches!(c.action, WorldAction::Idle)
                && let Some(yaw) = input.attack_yaw
            {
                c.facing = Vec2::new(yaw.sin(), yaw.cos());
                c.action = WorldAction::Attack {
                    id: self.tick,
                    elapsed: 0,
                };
                c.hit.clear();
            }
            let travel = match c.action {
                WorldAction::Idle => {
                    if input.movement.dot(input.movement) > 0. {
                        c.facing = input.movement.unit();
                    }
                    input
                        .movement
                        .limited()
                        .scale(def.arena.movement.speed_mps / 60.)
                }
                WorldAction::Dodge { direction, .. } => {
                    direction.scale(def.arena.dodge.speed_mps / 60.)
                }
                _ => Vec2::default(),
            };
            if let Some(&body) = self.bodies.get(&o.id) {
                let result = self
                    .physics
                    .move_character(
                        body,
                        [travel.x, 0., travel.z],
                        crate::FIXED_STEP,
                        CharacterSettings {
                            offset: 0.0001,
                            max_slope_angle: 0.,
                            snap_distance: None,
                        },
                    )
                    .unwrap();
                let limit = self.zone.half_extent_m - def.core.collision.radius_m;
                position = Vec2::new(
                    (position.x + result.translation[0]).clamp(-limit, limit),
                    (position.z + result.translation[2]).clamp(-limit, limit),
                );
                self.physics
                    .set_pose(
                        body,
                        Pose::at([position.x, def.core.collision.radius_m, position.z]),
                    )
                    .unwrap();
            }
            c.action = match c.action {
                WorldAction::Attack { id, elapsed }
                    if elapsed + 1
                        < attack.windup_ticks + attack.active_ticks + attack.recovery_ticks =>
                {
                    WorldAction::Attack {
                        id,
                        elapsed: elapsed + 1,
                    }
                }
                WorldAction::Dodge { elapsed, direction }
                    if elapsed + 1 < def.arena.dodge.duration_ticks =>
                {
                    WorldAction::Dodge {
                        elapsed: elapsed + 1,
                        direction,
                    }
                }
                _ => WorldAction::Idle,
            };
        }
        *self.entities.entities().get::<&mut Combat>(e).unwrap() = c;
        self.entities.entities().get::<&mut Position>(e).unwrap().0 = position;
    }
    fn interact(&mut self, id: ObjectId, input: &PlayerInput) {
        let e = self.ids[&id];
        if self.entities.entities().get::<&Combat>(e).unwrap().health == 0 {
            return;
        }
        if input.talk {
            self.talk_to_warden(id);
        }
        if let Some(drop_id) = input.pickup {
            let position = self.entities.entities().get::<&Position>(e).unwrap().0;
            let item = self.ids.get(&drop_id).and_then(|&d| {
                let p = self.entities.entities().get::<&Position>(d).ok()?.0;
                if distance(position, p) > 2. {
                    return None;
                }
                self.entities
                    .entities()
                    .get::<&Loot>(d)
                    .ok()
                    .map(|l| l.item.clone())
            });
            let mut p = self.entities.entities().get::<&mut Player>(e).unwrap();
            if let Some(item) = item.filter(|_| p.record.inventory.len() < 32) {
                p.record.inventory.push(item);
                drop(p);
                self.remove(drop_id);
            } else {
                p.last_error = Some("pickup_unavailable".into());
            }
        }
        if let Some(item) = &input.equip {
            let mut p = self.entities.entities().get::<&mut Player>(e).unwrap();
            if p.record.inventory.contains(item) {
                p.record.equipped = Some(item.clone());
            } else {
                p.last_error = Some("item_not_owned".into());
            }
        }
    }
    fn resolve_hits(&mut self) {
        let objects = self.objects();
        let mut hits = Vec::new();
        for a in &objects {
            let WorldAction::Attack { elapsed, .. } = a.action else {
                continue;
            };
            let def = self.characters.get(a.kind.character());
            let attack = &def.arena.attacks.primary;
            if a.health == 0
                || !(attack.windup_ticks..attack.windup_ticks + attack.active_ticks)
                    .contains(&elapsed)
            {
                continue;
            }
            for b in &objects {
                if b.kind == ObjectKind::Loot
                    || b.health == 0
                    || a.id == b.id
                    || (a.kind == ObjectKind::Player) == (b.kind == ObjectKind::Player)
                {
                    continue;
                }
                let delta = b.position.sub(a.position);
                let dist = distance(a.position, b.position);
                if dist > attack.range_m
                    || (dist > 0.
                        && a.facing.dot(delta.scale(1. / dist))
                            < attack.half_angle_degrees.to_radians().cos())
                {
                    continue;
                }
                let invulnerable = matches!(b.action,WorldAction::Dodge{elapsed,..} if elapsed<self.characters.get(b.kind.character()).arena.dodge.invulnerable_ticks);
                if invulnerable {
                    continue;
                }
                let mut combat = self
                    .entities
                    .entities()
                    .get::<&mut Combat>(self.ids[&a.id])
                    .unwrap();
                if combat.hit.insert(b.id) {
                    let bonus = if a.equipped.is_some() {
                        self.item.damage_bonus
                    } else {
                        0
                    };
                    hits.push((a.id, b.id, attack.damage.saturating_add(bonus)));
                }
            }
        }
        for (attacker, target, damage) in hits {
            let e = self.ids[&target];
            let kind = self.entities.entities().get::<&Identity>(e).unwrap().kind;
            let mut c = self.entities.entities().get::<&mut Combat>(e).unwrap();
            if c.health == 0 {
                continue;
            }
            c.health = c.health.saturating_sub(damage);
            if c.health == 0 {
                c.action = WorldAction::Dead {
                    respawn_tick: self.tick
                        + if kind == ObjectKind::Player {
                            RESPAWN_TICKS
                        } else {
                            MONSTER_RESPAWN_TICKS
                        },
                };
                drop(c);
                if kind != ObjectKind::Player {
                    if let Ok(mut p) = self
                        .entities
                        .entities()
                        .get::<&mut Player>(self.ids[&attacker])
                    {
                        p.record.experience = p.record.experience.saturating_add(10);
                        let home = self.entities.entities().get::<&Monster>(e).unwrap().home;
                        if self
                            .zone
                            .quest
                            .as_ref()
                            .is_some_and(|q| distance(home, q.camp) <= q.camp_radius_m)
                        {
                            p.record.quest.credit();
                        }
                    }
                    let position = self.entities.entities().get::<&Position>(e).unwrap().0;
                    self.spawn_loot(position);
                }
            }
        }
    }
}
#[cfg(test)]
mod tests;
