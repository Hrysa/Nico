use super::{ITEM_SWORD, ObjectKind, Vec2, ZONE_LIMIT};
use serde::{Deserialize, Serialize};
use std::path::Path;
pub const DEFAULT_WORLD: &str = "games/arena-arpg/assets/logic/worlds/meadow.world.toml";
pub const DEFAULT_ITEM: &str = "games/arena-arpg/assets/logic/items/iron-sword.item.toml";
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ZoneDefinition {
    pub schema_version: u32,
    pub id: String,
    pub half_extent_m: f64,
    pub settlement: Vec2,
    #[serde(default)]
    pub quest: Option<super::quest::QuestDefinition>,
    #[serde(default)]
    pub obstacles: Vec<Obstacle>,
    #[serde(default)]
    pub monsters: Vec<Spawn>,
}
impl Default for ZoneDefinition {
    fn default() -> Self {
        Self {
            schema_version: 1,
            id: "meadow".into(),
            half_extent_m: ZONE_LIMIT,
            settlement: Vec2::new(0., -20.),
            quest: None,
            obstacles: vec![],
            monsters: vec![],
        }
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Obstacle {
    /// Optional stable identity for client presentation; never a model path.
    #[serde(default)]
    pub id: String,
    pub center: [f64; 3],
    pub size: [f64; 3],
    pub color: [f32; 4],
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Spawn {
    pub kind: ObjectKind,
    pub position: Vec2,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ItemDefinition {
    pub schema_version: u32,
    pub id: String,
    pub name: String,
    pub damage_bonus: u16,
    pub color: [f32; 4],
}
impl Default for ItemDefinition {
    fn default() -> Self {
        Self {
            schema_version: 1,
            id: ITEM_SWORD.into(),
            name: "Iron sword".into(),
            damage_bonus: 5,
            color: [0.7, 0.8, 0.9, 1.],
        }
    }
}
fn valid_color(color: [f32; 4]) -> bool {
    color
        .into_iter()
        .all(|x| x.is_finite() && (0.0..=1.0).contains(&x))
}
impl ItemDefinition {
    pub fn load(path: &Path) -> crate::characters::AssetResult<Self> {
        let value: Self = toml::from_str(&crate::characters::read_definition(path)?)?;
        value.validate()?;
        Ok(value)
    }
    pub fn validate(&self) -> Result<(), &'static str> {
        if self.schema_version != 1
            || self.id != ITEM_SWORD
            || self.name.is_empty()
            || self.name.len() > 64
            || self.damage_bonus > 100
            || !valid_color(self.color)
        {
            return Err("invalid_item_definition");
        }
        Ok(())
    }
}
impl ZoneDefinition {
    pub fn load(path: &Path) -> crate::characters::AssetResult<Self> {
        let value: Self = toml::from_str(&crate::characters::read_definition(path)?)?;
        value.validate()?;
        Ok(value)
    }
    pub fn validate(&self) -> Result<(), &'static str> {
        if self.schema_version != 1
            || self.id.is_empty()
            || self.id.len() > 64
            || !self.half_extent_m.is_finite()
            || !(24.0..=ZONE_LIMIT).contains(&self.half_extent_m)
            || self.obstacles.len() > 32
            || self.monsters.len() > 64
        {
            return Err("invalid_zone_definition");
        }
        let in_zone = |p: Vec2| {
            p.finite() && p.x.abs() < self.half_extent_m - 3. && p.z.abs() < self.half_extent_m - 3.
        };
        if !in_zone(self.settlement) {
            return Err("invalid_settlement");
        }
        if let Some(q) = &self.quest
            && (!in_zone(q.warden)
                || !in_zone(q.camp)
                || self.blocked(q.warden, 1.)
                || super::distance(q.warden, self.settlement) > 8.
                || !q.camp_radius_m.is_finite()
                || !(1.0..=24.0).contains(&q.camp_radius_m))
        {
            return Err("invalid_quest_definition");
        }
        let mut obstacle_ids = std::collections::BTreeSet::new();
        for o in &self.obstacles {
            if o.id.len() > 64
                || (!o.id.is_empty() && !obstacle_ids.insert(&o.id))
                || o.center.iter().any(|x| !x.is_finite())
                || o.size
                    .iter()
                    .any(|x| !x.is_finite() || *x <= 0. || *x > 32.)
                || !valid_color(o.color)
                || o.center[0].abs() + o.size[0] / 2. > self.half_extent_m
                || o.center[2].abs() + o.size[2] / 2. > self.half_extent_m
            {
                return Err("invalid_obstacle");
            }
            if self.blocked(self.settlement, 3.) {
                return Err("blocked_settlement");
            }
        }
        for (i, m) in self.monsters.iter().enumerate() {
            if !matches!(m.kind, ObjectKind::Grunt | ObjectKind::Brute)
                || !in_zone(m.position)
                || self.blocked(m.position, 3.)
                || super::distance(m.position, self.settlement) < 12.
                || self.monsters[..i]
                    .iter()
                    .any(|p| super::distance(p.position, m.position) < 6.)
            {
                return Err("invalid_monster_spawn");
            }
        }
        Ok(())
    }
    pub fn blocked(&self, p: Vec2, radius: f64) -> bool {
        self.obstacles.iter().any(|o| {
            (p.x - o.center[0]).abs() < o.size[0] / 2. + radius
                && (p.z - o.center[2]).abs() < o.size[2] / 2. + radius
        })
    }
}
