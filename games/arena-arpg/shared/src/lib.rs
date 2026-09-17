//! Headless arena combat. Hosts submit input at fixed runtime boundaries and read
//! immutable snapshots. No presentation, transport, or platform dependencies.
pub mod characters;
mod collision;
pub mod open_world;
use characters::CharacterCatalog;
use std::sync::Arc;
pub mod geometry;
mod runtime;
pub use runtime::{ArenaPlugin, FIXED_STEP, InputFocusLost};
#[cfg(feature = "tools")]
pub mod tools;

/// Floor-plane world coordinates (X/Z), also used for directions.
#[derive(Clone, Copy, Debug, Default, PartialEq, serde::Serialize, serde::Deserialize)]
#[serde(deny_unknown_fields)]
pub struct Vec2 {
    pub x: f64,
    pub z: f64,
}
impl Vec2 {
    pub const fn new(x: f64, z: f64) -> Self {
        Self { x, z }
    }
    fn finite(self) -> bool {
        self.x.is_finite() && self.z.is_finite()
    }
    fn sub(self, b: Self) -> Self {
        Self::new(self.x - b.x, self.z - b.z)
    }
    fn scale(self, n: f64) -> Self {
        Self::new(self.x * n, self.z * n)
    }
    fn dot(self, b: Self) -> f64 {
        self.x * b.x + self.z * b.z
    }
    fn unit(self) -> Self {
        let length = self.dot(self).sqrt();
        if length > 0.0 {
            self.scale(length.recip())
        } else {
            self
        }
    }
    fn limited(self) -> Self {
        if self.dot(self) > 1.0 {
            self.unit()
        } else {
            self
        }
    }
}

/// Initial actor placement. Actor zero is the hero; IDs one through three are monsters.
#[derive(Clone, Debug, PartialEq)]
pub struct Level {
    pub spawns: [Vec2; 4],
}
impl Default for Level {
    fn default() -> Self {
        Self {
            spawns: [
                Vec2::new(0.0, -6.0),
                Vec2::new(-5.0, 5.0),
                Vec2::new(0.0, 7.0),
                Vec2::new(5.0, 5.0),
            ],
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
/// Invalid authored placement, rejected before the simulation is constructed.
pub enum LevelError {
    OutOfBounds,
    OverlappingSpawns,
}
impl Level {
    pub fn validate(&self) -> Result<(), LevelError> {
        self.validate_with(&CharacterCatalog::builtin())
    }
    pub fn validate_with(&self, characters: &CharacterCatalog) -> Result<(), LevelError> {
        // Slots 1..3 can become brutes in later waves; validate every possible spawn.
        let radius = |i| {
            if i == 0 {
                characters.get(ActorKind::Hero).core.collision.radius_m
            } else {
                characters
                    .get(ActorKind::Grunt)
                    .core
                    .collision
                    .radius_m
                    .max(characters.get(ActorKind::Brute).core.collision.radius_m)
            }
        };
        for (i, p) in self.spawns.iter().enumerate() {
            if !collision::valid_position_with_radius(*p, radius(i)) {
                return Err(LevelError::OutOfBounds);
            }
            if self.spawns[..i].iter().enumerate().any(|(j, other)| {
                p.sub(*other).dot(p.sub(*other)) < (radius(i) + radius(j)).powi(2)
            }) {
                return Err(LevelError::OverlappingSpawns);
            }
        }
        Ok(())
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
/// Terminal states retain the last simulation snapshot until restart.
pub enum RunState {
    Playing,
    Won,
    Lost,
}
/// Elapsed is the zero-based tick that will be evaluated at the next boundary.
#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Action {
    Idle,
    Attack { id: u64, elapsed: u16, hit_mask: u8 },
    Dodge { elapsed: u16, direction: Vec2 },
}
/// Shared combat data used by simulation, telegraphs, and operational snapshots.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ActorKind {
    Hero,
    Grunt,
    Brute,
}
#[derive(Clone, Copy, Debug)]
pub struct CombatStats {
    pub max_health: u16,
    pub speed: f64,
    pub windup: u16,
    pub active: u16,
    pub recovery: u16,
    pub range: f64,
    pub damage: u16,
}
impl ActorKind {
    pub fn stats(self) -> CombatStats {
        CharacterCatalog::builtin().get(self).combat_stats()
    }

    pub fn name(self) -> &'static str {
        match self {
            Self::Hero => "hero",
            Self::Grunt => "grunt",
            Self::Brute => "brute",
        }
    }
}
pub const TOTAL_WAVES: u8 = 3;
pub const INTERMISSION_TICKS: u16 = 180;

#[derive(Clone, Debug, PartialEq)]
/// Owned actor data; mutating a cloned snapshot never changes the simulation.
pub struct Actor {
    pub(crate) characters: Arc<CharacterCatalog>,
    pub id: u8,
    pub kind: ActorKind,
    pub position: Vec2,
    pub facing: Vec2,
    pub health: u16,
    pub action: Action,
    pub dodge_cooldown: u16,
}
impl Actor {
    pub fn definition(&self) -> &characters::CharacterDefinition {
        self.characters.get(self.kind)
    }
    pub fn stats(&self) -> CombatStats {
        self.definition().combat_stats()
    }
}
#[derive(Clone, Debug, PartialEq)]
/// Authoritative state after a complete fixed boundary. Actor IDs are stable.
pub struct Snapshot {
    pub run_id: u64,
    pub tick: u64,
    pub state: RunState,
    pub actors: [Actor; 4],
    /// One-based wave. Actor slots are reused only at a new wave boundary.
    pub wave: u8,
    pub wave_tick: u64,
    pub intermission_ticks: u16,
    pub buffered_dodge: Option<Vec2>,
    pub next_monster_strike_tick: u64,
}
impl Snapshot {
    pub fn monsters_remaining(&self) -> usize {
        self.actors[1..].iter().filter(|a| a.health > 0).count()
    }
}
/// One fixed tick of intent. Hosts must resubmit held movement each tick; attack
/// and dodge are edges, not held buttons. No input persists across a restart.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct TickInput {
    pub run_id: u64,
    pub movement: Vec2,
    pub attack_yaw: Option<f64>,
    pub dodge: Option<Vec2>,
    pub restart: bool,
}
impl TickInput {
    pub fn idle(run_id: u64) -> Self {
        Self {
            run_id,
            movement: Vec2::default(),
            attack_yaw: None,
            dodge: None,
            restart: false,
        }
    }
    fn valid(self) -> bool {
        let direction = |v: Vec2| v.finite() && v.x.abs() <= 1.0 && v.z.abs() <= 1.0;
        direction(self.movement)
            && self
                .attack_yaw
                .is_none_or(|y| y.is_finite() && y.abs() <= std::f64::consts::PI)
            && self.dodge.is_none_or(|d| direction(d) && d.dot(d) > 0.0)
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
/// Semantic input failure; the optional tool adapter reports transport acceptance
/// and command lifecycle separately.
pub enum Rejection {
    InvalidArguments,
    StaleRun,
    RunFinished,
    ActionLocked,
    Cooldown,
    Intermission,
}
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
/// Result of the latest boundary, separate from queued transport acceptance.
pub struct StepReport {
    pub rejection: Option<Rejection>,
    pub restarted: bool,
    pub dodge_buffered: bool,
    pub dodge_started: bool,
}

/// Runtime-owned simulation resource. Snapshots are immutable or cloned for tools.
#[derive(Debug)]
pub struct Arena {
    level: Level,
    characters: Arc<CharacterCatalog>,
    snapshot: Snapshot,
    next_attack: u64,
    collision: collision::CollisionWorld,
}
impl Clone for Arena {
    fn clone(&self) -> Self {
        Self {
            level: self.level.clone(),
            characters: self.characters.clone(),
            snapshot: self.snapshot.clone(),
            next_attack: self.next_attack,
            collision: collision::CollisionWorld::default(),
        }
    }
}
impl Default for Arena {
    fn default() -> Self {
        Self::new(Level::default()).expect("built-in arena is valid")
    }
}
impl Arena {
    pub fn new(level: Level) -> Result<Self, LevelError> {
        Self::with_characters(level, CharacterCatalog::builtin())
    }
    pub fn with_characters(
        level: Level,
        characters: Arc<CharacterCatalog>,
    ) -> Result<Self, LevelError> {
        level.validate_with(&characters)?;
        let snapshot = Self::initial(&level, 1, &characters);
        Ok(Self {
            level,
            characters,
            snapshot,
            next_attack: 1,
            collision: collision::CollisionWorld::default(),
        })
    }
    fn initial(level: &Level, run_id: u64, characters: &Arc<CharacterCatalog>) -> Snapshot {
        Snapshot {
            run_id,
            tick: 0,
            state: RunState::Playing,
            wave: 1,
            wave_tick: 0,
            intermission_ticks: 0,
            buffered_dodge: None,
            next_monster_strike_tick: 0,
            actors: std::array::from_fn(|i| Actor {
                characters: characters.clone(),
                id: i as u8,
                kind: if i == 0 {
                    ActorKind::Hero
                } else {
                    ActorKind::Grunt
                },
                position: level.spawns[i],
                facing: Vec2::new(0.0, if i == 0 { 1.0 } else { -1.0 }),
                health: characters
                    .get(if i == 0 {
                        ActorKind::Hero
                    } else {
                        ActorKind::Grunt
                    })
                    .arena
                    .stats
                    .max_health,
                action: Action::Idle,
                dodge_cooldown: 0,
            }),
        }
    }
    fn next_wave(&mut self) {
        let health = self.snapshot.actors[0]
            .health
            .saturating_add(40)
            .min(self.characters.get(ActorKind::Hero).arena.stats.max_health);
        let wave = self.snapshot.wave + 1;
        let tick = self.snapshot.tick;
        self.snapshot = Self::initial(&self.level, self.snapshot.run_id, &self.characters);
        self.snapshot.tick = tick;
        self.snapshot.wave = wave;
        self.snapshot.actors[0].health = health;
        // Wave two has one brute; wave three has two. Stable slots remain 1..3.
        for i in (5 - wave as usize)..4 {
            let actor = &mut self.snapshot.actors[i];
            actor.kind = ActorKind::Brute;
            actor.health = actor.stats().max_health;
        }
    }
    /// Clear unexecuted input at a runtime-owned focus/takeover/shutdown boundary.
    pub fn clear_buffered_input(&mut self) {
        self.snapshot.buffered_dodge = None;
    }
    fn dodge_wait(&self) -> u16 {
        let hero = &self.snapshot.actors[0];
        let action_wait = match hero.action {
            Action::Idle => 0,
            Action::Attack { elapsed, .. } => {
                (hero.stats().windup + hero.stats().active).saturating_sub(elapsed)
            }
            Action::Dodge { elapsed, .. } => hero
                .definition()
                .arena
                .dodge
                .duration_ticks
                .saturating_sub(elapsed),
        };
        action_wait.max(hero.dodge_cooldown)
    }
    pub fn snapshot(&self) -> &Snapshot {
        &self.snapshot
    }
    fn start_attack(&mut self, index: usize, facing: Vec2) {
        let actor = &mut self.snapshot.actors[index];
        actor.facing = facing;
        actor.action = Action::Attack {
            id: self.next_attack,
            elapsed: 0,
            hit_mask: 0,
        };
        self.next_attack += 1;
    }
    /// Apply one 60 Hz step. Invalid/stale input is ignored while the world continues.
    /// A rejected combat action does not discard otherwise valid movement intent.
    /// A valid restart takes priority and does not advance the restored run.
    pub fn step(&mut self, mut input: TickInput) -> StepReport {
        let mut report = StepReport::default();
        if input.run_id != self.snapshot.run_id {
            report.rejection = Some(Rejection::StaleRun);
        } else if !input.valid() {
            report.rejection = Some(Rejection::InvalidArguments);
        }
        if report.rejection.is_some() {
            input = TickInput::idle(self.snapshot.run_id);
        }
        if input.restart {
            self.snapshot = Self::initial(&self.level, self.snapshot.run_id + 1, &self.characters);
            report.restarted = true;
            return report;
        }
        if self.snapshot.state != RunState::Playing {
            if report.rejection.is_none()
                && (input.movement != Vec2::default()
                    || input.attack_yaw.is_some()
                    || input.dodge.is_some())
            {
                report.rejection = Some(Rejection::RunFinished);
            }
            return report;
        }
        if self.snapshot.intermission_ticks > 0 {
            if input.movement != Vec2::default()
                || input.dodge.is_some()
                || input.attack_yaw.is_some()
            {
                report.rejection.get_or_insert(Rejection::Intermission);
            }
            self.snapshot.intermission_ticks -= 1;
            self.snapshot.tick += 1;
            if self.snapshot.intermission_ticks == 0 {
                self.next_wave();
            }
            return report;
        }
        if let Some(direction) = input.dodge {
            let wait = self.dodge_wait();
            self.snapshot.buffered_dodge = None;
            if wait
                <= self
                    .characters
                    .get(ActorKind::Hero)
                    .arena
                    .dodge
                    .buffer_ticks
            {
                self.snapshot.buffered_dodge = Some(direction.unit());
            } else {
                report.rejection = Some(if self.snapshot.actors[0].action == Action::Idle {
                    Rejection::Cooldown
                } else {
                    Rejection::ActionLocked
                });
            }
        } else if let Some(yaw) = input.attack_yaw {
            // Explicit new combat intent replaces any unexecuted dodge.
            self.clear_buffered_input();
            if self.snapshot.actors[0].action == Action::Idle {
                self.start_attack(0, Vec2::new(yaw.sin(), yaw.cos()));
            } else {
                report.rejection = Some(Rejection::ActionLocked);
            }
        }
        if let Some(direction) = self.snapshot.buffered_dodge {
            if self.dodge_wait() == 0 {
                let hero = &mut self.snapshot.actors[0];
                hero.facing = direction;
                hero.action = Action::Dodge {
                    elapsed: 0,
                    direction,
                };
                hero.dodge_cooldown = hero.definition().arena.dodge.cooldown_ticks;
                self.clear_buffered_input();
                report.dodge_started = true;
            } else {
                report.dodge_buffered = true;
            }
        }
        // AI decisions read beginning-of-tick positions, then stable IDs move in order.
        let mut intents = [Vec2::default(); 4];
        intents[0] = input.movement.limited().scale(
            self.characters
                .get(ActorKind::Hero)
                .arena
                .movement
                .speed_mps
                / 60.0,
        );
        for (i, intent) in intents.iter_mut().enumerate().skip(1) {
            let actor = &self.snapshot.actors[i];
            if actor.health == 0 || actor.action != Action::Idle || self.snapshot.wave_tick < 60 {
                continue;
            }
            let toward = self.snapshot.actors[0].position.sub(actor.position);
            let stats = actor.stats();
            if toward.dot(toward) <= (stats.range - 0.2).powi(2) {
                let strike = self.snapshot.tick + u64::from(stats.windup);
                if strike >= self.snapshot.next_monster_strike_tick {
                    self.start_attack(i, toward.unit());
                    self.snapshot.next_monster_strike_tick = strike + 30;
                }
            } else {
                *intent = toward.unit().scale(stats.speed / 60.0);
            }
        }
        self.collision.sync(&self.snapshot.actors);
        for (i, intent) in intents.into_iter().enumerate() {
            if self.snapshot.actors[i].health == 0 {
                continue;
            }
            let actor = &self.snapshot.actors[i];
            let delta = match actor.action {
                Action::Idle => intent,
                Action::Dodge { direction, .. } => {
                    direction.scale(actor.definition().arena.dodge.speed_mps / 60.0)
                }
                Action::Attack { .. } => Vec2::default(),
            };
            let position = self.collision.slide(i, actor.position, delta);
            let actor = &mut self.snapshot.actors[i];
            if actor.action == Action::Idle && delta.dot(delta) > 0.0 {
                actor.facing = delta.unit();
            }
            actor.position = position;
        }
        self.damage();
        if self.snapshot.actors[0].health == 0 {
            self.snapshot.state = RunState::Lost;
        } else if self.snapshot.monsters_remaining() == 0 {
            if self.snapshot.wave == TOTAL_WAVES {
                self.snapshot.state = RunState::Won;
            } else {
                self.snapshot.intermission_ticks = INTERMISSION_TICKS;
                self.snapshot.actors[0].action = Action::Idle;
            }
        }
        if self.snapshot.state != RunState::Playing || self.snapshot.intermission_ticks > 0 {
            self.clear_buffered_input();
            report.dodge_buffered = false;
        }
        for actor in &mut self.snapshot.actors {
            let stats = actor.stats();
            actor.dodge_cooldown = actor.dodge_cooldown.saturating_sub(1);
            actor.action = if actor.health == 0 {
                Action::Idle
            } else {
                match actor.action {
                    Action::Attack {
                        id,
                        elapsed,
                        hit_mask,
                    } if elapsed + 1 < stats.windup + stats.active + stats.recovery => {
                        Action::Attack {
                            id,
                            elapsed: elapsed + 1,
                            hit_mask,
                        }
                    }
                    Action::Dodge { elapsed, direction }
                        if elapsed + 1 < actor.definition().arena.dodge.duration_ticks =>
                    {
                        Action::Dodge {
                            elapsed: elapsed + 1,
                            direction,
                        }
                    }
                    _ => Action::Idle,
                }
            };
        }
        self.snapshot.tick += 1;
        self.snapshot.wave_tick += 1;
        report
    }
    fn damage(&mut self) {
        let mut damage = [0u16; 4];
        for i in 0..4 {
            let actor = &self.snapshot.actors[i];
            if actor.health == 0 {
                continue;
            }
            let Action::Attack {
                id,
                elapsed,
                mut hit_mask,
            } = actor.action
            else {
                continue;
            };
            let stats = actor.stats();
            if !(stats.windup..stats.windup + stats.active).contains(&elapsed) {
                continue;
            }
            for (j, target) in self.snapshot.actors.iter().enumerate() {
                if (i == 0) == (j == 0) || target.health == 0 || hit_mask & (1 << j) != 0 {
                    continue;
                }
                let delta = target.position.sub(actor.position);
                let range = stats.range;
                if delta.dot(delta) > range * range
                    || actor.facing.dot(delta) + 1e-12
                        < delta.dot(delta).sqrt()
                            * actor
                                .definition()
                                .arena
                                .attacks
                                .primary
                                .half_angle_degrees
                                .to_radians()
                                .cos()
                {
                    continue;
                }
                // A dodged strike is consumed too: the same swing cannot hit later.
                hit_mask |= 1 << j;
                if !matches!(target.action, Action::Dodge { elapsed, .. } if elapsed < target.definition().arena.dodge.invulnerable_ticks)
                {
                    damage[j] += stats.damage;
                }
            }
            self.snapshot.actors[i].action = Action::Attack {
                id,
                elapsed,
                hit_mask,
            };
        }
        for (actor, damage) in self.snapshot.actors.iter_mut().zip(damage) {
            actor.health = actor.health.saturating_sub(damage);
        }
    }
}

#[cfg(test)]
mod tests;
