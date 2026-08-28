//! Authoritative gameplay shared by the minimal game's client and server.

use nico_runtime::{AppBuilder, Plugin, RuntimeResult, Stage, SystemContext, events::EventReader};

/// Position of an active simulated entity in minimal-game world units.
#[derive(Clone, Copy, Debug, Default, PartialEq)]
pub struct Position {
    x: f32,
}

impl Position {
    /// Returns the horizontal world position.
    #[must_use]
    pub const fn x(self) -> f32 {
        self.x
    }
}

#[derive(Clone, Copy, Debug, PartialEq)]
struct Velocity {
    units_per_second: f32,
}

const INITIAL_STAMINA: u64 = 10;
const MOVEMENT_QUEST_TARGET: u64 = 2;
const MOVEMENT_QUEST_REWARD: u64 = 5;

/// Authoritative state shared by client and server.
#[derive(Debug, Eq, PartialEq)]
pub struct GameState {
    started: bool,
    fixed_updates: u64,
    frame_updates: u64,
    stamina: u64,
    movement_quest_progress: u64,
    movement_quest_completed: bool,
    coins: u64,
}

impl Default for GameState {
    fn default() -> Self {
        Self {
            started: false,
            fixed_updates: 0,
            frame_updates: 0,
            stamina: INITIAL_STAMINA,
            movement_quest_progress: 0,
            movement_quest_completed: false,
            coins: 0,
        }
    }
}

impl GameState {
    /// Returns whether startup completed.
    #[must_use]
    pub const fn started(&self) -> bool {
        self.started
    }

    /// Returns the number of authoritative fixed updates.
    #[must_use]
    pub const fn fixed_updates(&self) -> u64 {
        self.fixed_updates
    }

    /// Returns the number of host-frame updates.
    #[must_use]
    pub const fn frame_updates(&self) -> u64 {
        self.frame_updates
    }

    /// Returns the stamina remaining after authoritative movement.
    #[must_use]
    pub const fn stamina(&self) -> u64 {
        self.stamina
    }

    /// Returns progress toward the movement quest target.
    #[must_use]
    pub const fn movement_quest_progress(&self) -> u64 {
        self.movement_quest_progress
    }

    /// Returns whether the movement quest has been completed.
    #[must_use]
    pub const fn movement_quest_completed(&self) -> bool {
        self.movement_quest_completed
    }

    /// Returns coins granted by completed quests.
    #[must_use]
    pub const fn coins(&self) -> u64 {
        self.coins
    }
}

/// Authoritative fact emitted after one fixed movement step.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MovementCompleted {
    fixed_tick: u64,
    moved_entities: u64,
}

impl MovementCompleted {
    /// Returns the fixed tick that produced this fact.
    #[must_use]
    pub const fn fixed_tick(self) -> u64 {
        self.fixed_tick
    }

    /// Returns how many entities were moved.
    #[must_use]
    pub const fn moved_entities(self) -> u64 {
        self.moved_entities
    }
}

/// Authoritative fact emitted when the movement quest reaches its target.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct MovementQuestCompleted {
    completed_at_tick: u64,
    reward_coins: u64,
}

impl MovementQuestCompleted {
    /// Returns the fixed tick that completed the quest.
    #[must_use]
    pub const fn completed_at_tick(self) -> u64 {
        self.completed_at_tick
    }

    /// Returns the coin reward carried by this completion fact.
    #[must_use]
    pub const fn reward_coins(self) -> u64 {
        self.reward_coins
    }
}

/// Registers authoritative gameplay in either a client or server runtime.
pub struct MinimalGamePlugin;

impl Plugin for MinimalGamePlugin {
    fn build(&self, app: &mut AppBuilder) -> RuntimeResult<()> {
        app.insert_resource(GameState::default());
        app.add_system(Stage::Startup, "minimal_game::setup", setup);
        app.add_system(Stage::FixedUpdate, "minimal_game::simulate", simulate);
        let mut quest_reader = EventReader::<MovementCompleted>::new();
        app.add_system(
            Stage::Update,
            "minimal_game::advance_movement_quest",
            move |context| {
                let mut moved_entities = 0_u64;
                let mut completed_at_tick = 0_u64;
                for event in context.events.read(&mut quest_reader) {
                    moved_entities = moved_entities.saturating_add(event.moved_entities());
                    completed_at_tick = event.fixed_tick();
                }

                let state = context.world.resource_mut::<GameState>()?;
                state.movement_quest_progress = state
                    .movement_quest_progress
                    .saturating_add(moved_entities)
                    .min(MOVEMENT_QUEST_TARGET);
                let completed = !state.movement_quest_completed
                    && state.movement_quest_progress == MOVEMENT_QUEST_TARGET;
                state.movement_quest_completed |= completed;

                if completed {
                    context.events.send(MovementQuestCompleted {
                        completed_at_tick,
                        reward_coins: MOVEMENT_QUEST_REWARD,
                    });
                }
                Ok(())
            },
        );
        let mut stamina_reader = EventReader::<MovementCompleted>::new();
        app.add_system(
            Stage::Update,
            "minimal_game::spend_movement_stamina",
            move |context| {
                let spent = context
                    .events
                    .read(&mut stamina_reader)
                    .fold(0_u64, |total, event| {
                        total.saturating_add(event.moved_entities())
                    });
                let state = context.world.resource_mut::<GameState>()?;
                state.stamina = state.stamina.saturating_sub(spent);
                Ok(())
            },
        );
        let mut reward_reader = EventReader::<MovementQuestCompleted>::new();
        app.add_system(
            Stage::Update,
            "minimal_game::grant_quest_reward",
            move |context| {
                let reward = context
                    .events
                    .read(&mut reward_reader)
                    .fold(0_u64, |total, event| {
                        total.saturating_add(event.reward_coins())
                    });
                let state = context.world.resource_mut::<GameState>()?;
                state.coins = state.coins.saturating_add(reward);
                Ok(())
            },
        );
        app.add_system(Stage::Update, "minimal_game::update", update);
        Ok(())
    }
}

fn setup(context: &mut SystemContext<'_>) -> RuntimeResult<()> {
    context.world.resource_mut::<GameState>()?.started = true;
    context.commands.spawn((
        Position::default(),
        Velocity {
            units_per_second: 1.0,
        },
    ));
    Ok(())
}

fn simulate(context: &mut SystemContext<'_>) -> RuntimeResult<()> {
    let state = context.world.resource_mut::<GameState>()?;
    state.fixed_updates = state.fixed_updates.saturating_add(1);

    let delta = context.time.delta().as_secs_f32();
    let mut moved_entities = 0_u64;
    for (position, velocity) in context.world.query::<(&mut Position, &Velocity)>().iter() {
        position.x += velocity.units_per_second * delta;
        moved_entities = moved_entities.saturating_add(1);
    }
    context.events.send(MovementCompleted {
        fixed_tick: context.time.fixed_tick(),
        moved_entities,
    });
    Ok(())
}

fn update(context: &mut SystemContext<'_>) -> RuntimeResult<()> {
    let state = context.world.resource_mut::<GameState>()?;
    state.frame_updates = state.frame_updates.saturating_add(1);
    Ok(())
}

#[cfg(test)]
mod tests {
    use std::time::Duration;

    use nico_runtime::{AppBuilder, RuntimeResult};

    use super::{GameState, MinimalGamePlugin, Position};

    #[test]
    fn shared_gameplay_runs_headlessly() -> RuntimeResult<()> {
        let mut app = AppBuilder::new().add_plugin(MinimalGamePlugin).build()?;

        app.start()?;
        app.tick(Duration::from_nanos(16_666_667))?;

        let state = app.world().resource::<GameState>()?;
        assert_eq!(state.movement_quest_progress(), 1);
        assert!(!state.movement_quest_completed());
        assert_eq!(state.stamina(), 9);
        assert_eq!(state.coins(), 0);

        app.tick(Duration::from_nanos(16_666_667))?;
        app.shutdown()?;

        let state = app.world().resource::<GameState>()?;
        assert!(state.started());
        assert_eq!(state.fixed_updates(), 2);
        assert_eq!(state.frame_updates(), 2);
        assert_eq!(state.movement_quest_progress(), 2);
        assert!(state.movement_quest_completed());
        assert_eq!(state.stamina(), 8);
        assert_eq!(state.coins(), 5);

        let positions = app
            .world()
            .query::<&Position>()
            .iter()
            .map(|position| position.x())
            .collect::<Vec<_>>();
        assert_eq!(positions.len(), 1);
        assert!((positions[0] - 0.033_333_335).abs() < f32::EPSILON);
        Ok(())
    }
}
